using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace Blueprinter;

internal static class BundleCache
{
    private const string CacheFormat = "BP-Cache-v1";
    private const string BundleLabel = "bundle:";
    private const long FreeSpaceReserve = 256L * 1024 * 1024;
    private const float MinRecompressTimeoutSeconds = 180f;
    private const double RecompressSecondsPerGiB = 300.0;
    private static readonly TimeSpan MaxIdle = TimeSpan.FromDays(7);
    private static readonly Dictionary<string, string> PreparedPaths = new(StringComparer.Ordinal);
    private static ConfigEntry<bool> _enabled;
    private static ConfigEntry<float> _maxSizeGiB;
    private static string _directory;
    private static bool _disabledThisRun;
    
    internal static bool Enabled => _enabled is { Value: true } && !_disabledThisRun;
    internal static string Warning { get; private set; }
    
    internal static void Configure(ConfigEntry<bool> enabled, ConfigEntry<float> maxSizeGiB)
    {
        _enabled = enabled;
        _maxSizeGiB = maxSizeGiB;
        _directory = Path.Combine(Paths.BepInExRootPath, "Blueprinter_Cache");
        _disabledThisRun = false;
        Warning = null;
        PreparedPaths.Clear();
    }
    
    internal static void PrepareCaching()
    {
        if (!Enabled || !Directory.Exists(_directory))
            return;
        
        try
        {
            TrimCache(0);
            foreach (var temp in Directory.EnumerateFiles(_directory, "*.writing"))
                SafeDelete(temp);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Initial cleanup skipped: {ex.Message}");
        }
    }
    
    internal static bool TryGetPreparedPath(string source, out string path)
    {
        path = null;
        return Enabled && PreparedPaths.TryGetValue(source, out path) && File.Exists(path);
    }
    
    internal static void RejectPreparedPath(string source, string path)
    {
        PreparedPaths.Remove(source);
        SafeDelete(path + ".meta");
    }
    
    // Cache gets removed when it's either >7 days old and hasn't been used, or when a different version of the
    // same bundle is loaded (either upgrading/downgrading mod and loading that)
    // This is so that cache remains if you temporarily move BP mods out of plugin folder to play without them,
    // then re-enable them later
    internal static void RemoveRedundantCache(IReadOnlyList<LoadedBundle> loaded)
    {
        if (!Enabled || loaded == null)
            return;
        
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bundle in loaded)
        {
            if (bundle == null || string.IsNullOrEmpty(bundle.bundleName))
                continue;
            
            var bundleName = bundle.bundleName.Replace('\r', '_').Replace('\n', '_');
            names.Add(bundleName);
            if (!PreparedPaths.TryGetValue(bundle.source, out var path))
                continue;
            
            current.Add(path);
            try
            {
                UpdateBundleMetadata(path, bundleName);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BundleCache] Cache metadata update failed for {bundle.bundleName}: {ex.Message}");
            }
        }
        
        try
        {
            foreach (var meta in Directory.EnumerateFiles(_directory, "*.lz4.bundle.meta"))
                try
                {
                    var path = meta[..^".meta".Length];
                    if (current.Contains(path))
                        continue;
                    
                    var entryName = ReadBundleName(meta);
                    if (entryName != null && names.Contains(entryName))
                        RemoveEntry(path);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[BundleCache] Cache version cleanup skipped {meta}: {ex.Message}");
                }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Cache version scan failed: {ex.Message}");
        }
        
        try
        {
            TrimCache(0);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Cache trim failed: {ex.Message}");
        }
    }
    
    private static void UpdateBundleMetadata(string cachePath, string bundleName)
    {
        var metaPath = cachePath + ".meta";
        if (!File.Exists(metaPath))
            throw new FileNotFoundException("Cache metadata missing", metaPath);
        if (string.Equals(ReadBundleName(metaPath), bundleName, StringComparison.Ordinal))
        {
            File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow);
            return;
        }
        
        var metadata = File.ReadAllText(metaPath);
        var labelPosition = metadata.IndexOf("\n" + BundleLabel, StringComparison.Ordinal);
        if (labelPosition >= 0)
            metadata = metadata[..(labelPosition + 1)];
        else if (!metadata.EndsWith("\n", StringComparison.Ordinal))
            metadata += "\n";
        File.WriteAllText(metaPath, metadata + BundleLabel + bundleName + "\n", Encoding.UTF8);
    }
    
    internal static IEnumerator PrepareCandidate(string source, Assembly assembly, string resourceName)
    {
        if (!Enabled)
            yield break;
        
        var prepare = CreatePreparation(source, assembly, resourceName);
        if (prepare == null || prepare.Hit)
            yield break;
        
        AssetBundleRecompressOperation op = null;
        long inputLength = 0;
        try
        {
            inputLength = new FileInfo(prepare.InputPath).Length;
            if (!CheckCapacity(inputLength))
                DisableOnStorageFailure("Insufficient disk space to prepare the converted bundle");
            else
                op = AssetBundle.RecompressAssetBundleAsync(prepare.InputPath, prepare.TempOutputPath,
                    BuildCompression.LZ4Runtime);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Could not start conversion for {source}: {ex}");
            if (IsDiskFull(ex))
                DisableOnStorageFailure(ex.Message);
        }
        finally
        {
            if (op == null)
                Cleanup(prepare);
        }
        
        if (op == null)
            yield break;
        
        // Handle failsafe in case LZ4 preparation gets indefinitely stuck (unity decompress/recompress never returns
        // errors if the bundle is corrupt, so it leaves game forever stuck in "preparing LZ4 cache")
        // This times it out if it's still working after timeoutSeconds on the same cache and actually show
        // the error message + try to fall back using regular/non-cache for it
        // timeoutSeconds is set to 180s as minimum, and has additional scaling of 300s per GiB bundle size, whichever
        // is larger, to account for a possible super large multi GiB bundle taking longer
        var recompressStarted = Time.realtimeSinceStartup;
        var timeoutSeconds = Math.Max(MinRecompressTimeoutSeconds,
            RecompressSecondsPerGiB * inputLength / (1024d * 1024d * 1024d));
        while (!op.isDone)
        {
            if (Time.realtimeSinceStartup - recompressStarted >= timeoutSeconds)
            {
                Plugin.Log.LogError($"[BundleCache] Recompression timed out after {timeoutSeconds:0}s for "
                                    + $"{source}. Trying the original asset bundle instead, check Player.log if it "
                                    + "still can't load.");
                Warning = "One bundle's cache conversion timed out, loading its original asset bundles. Check "
                          + " Player.log for details.";
                // Can't cancel this, unity doesn't have a cancellation API for this native operation, so no deleting
                // input or .writing output (next startup removes abandoned .writing temp files)
                yield break;
            }
            
            yield return null;
        }
        
        try
        {
            if (!op.success || !File.Exists(prepare.TempOutputPath) || new FileInfo(prepare.TempOutputPath).Length == 0)
            {
                var message = op.humanReadableResult ?? "Unknown recompression failure";
                if (LooksLikeDiskFull(message) || IsCriticallyLowOnSpace())
                    DisableOnStorageFailure(message);
                throw new IOException("Unity recompression failed: " + message);
            }
            
            var outputSize = new FileInfo(prepare.TempOutputPath).Length;
            if (!CheckFinalCapacity(outputSize, prepare.CachePath))
                throw new IOException("Cache size limit reached");
            
            SafeDelete(prepare.CachePath);
            File.Move(prepare.TempOutputPath, prepare.CachePath);
            File.WriteAllText(prepare.TempMetaPath, prepare.Metadata, Encoding.UTF8);
            SafeDelete(prepare.MetaPath);
            File.Move(prepare.TempMetaPath, prepare.MetaPath);
            PreparedPaths[source] = prepare.CachePath;
            Plugin.Log.LogInfo($"[BundleCache] Cache done for source {source}, in LZ4 format, cache size: {outputSize}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Conversion not cached for {source}: {ex.Message}");
            if (IsDiskFull(ex))
                DisableOnStorageFailure(ex.Message);
        }
        finally
        {
            Cleanup(prepare);
        }
    }
    
    private static Preparation CreatePreparation(string source, Assembly assembly, string resourceName)
    {
        var p = new Preparation();
        try
        {
            var original = assembly == null ? source : assembly.Location;
            if (string.IsNullOrWhiteSpace(original))
                throw new IOException("Assembly has no file path");
            original = Path.GetFullPath(original);
            var info = new FileInfo(original);
            if (!info.Exists)
                throw new FileNotFoundException("Bundle source missing", original);
            
            var identity = assembly == null ? "file: " + original : "resource: " + original + ":" + resourceName;
            var moduleId = assembly == null ? "" : assembly.ManifestModule.ModuleVersionId.ToString("N");
            p.Metadata = $"{CacheFormat}\nLZ4\n{identity}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{moduleId}\n";
            var key = HashName(identity.ToUpperInvariant());
            p.CachePath = Path.Combine(_directory, key + ".lz4.bundle");
            p.MetaPath = p.CachePath + ".meta";
            p.TempOutputPath = p.CachePath + ".writing";
            p.TempMetaPath = p.MetaPath + ".writing";
            if (File.Exists(p.CachePath) && new FileInfo(p.CachePath).Length > 0 && File.Exists(p.MetaPath) &&
                File.ReadAllText(p.MetaPath).StartsWith(p.Metadata, StringComparison.Ordinal))
            {
                PreparedPaths[source] = p.CachePath;
                p.Hit = true;
                Plugin.Log.LogInfo($"[BundleCache] Cache hit from \"{source}\", size: {new FileInfo(p.CachePath).Length} bytes");
                return p;
            }
            
            Directory.CreateDirectory(_directory);
            SafeDelete(p.TempOutputPath);
            SafeDelete(p.TempMetaPath);
            if (assembly == null)
            {
                p.InputPath = original;
            }
            else
            {
                p.TempInputPath = Path.Combine(_directory, key + ".input.writing");
                using var input = assembly.GetManifestResourceStream(resourceName);
                if (input == null)
                    throw new FileNotFoundException("Embedded resource missing", resourceName);
                
                using (var output = new FileStream(p.TempInputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                           128 * 1024))
                {
                    input.CopyTo(output, 128 * 1024);
                }
                
                p.InputPath = p.TempInputPath;
            }
            
            Plugin.Log.LogInfo(
                $"[BundleCache] Cache convert from source: \"{source}\" to LZ4, size: {new FileInfo(p.InputPath).Length} bytes");
            return p;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Cache preparation failed for {source}: {ex}");
            if (IsDiskFull(ex))
                DisableOnStorageFailure(ex.Message);
            Cleanup(p);
            return null;
        }
    }
    
    private static bool CheckCapacity(long sourceLength)
    {
        var guess = sourceLength > long.MaxValue / 2 ? long.MaxValue : sourceLength * 2;
        guess = Math.Max(guess, 64L * 1024 * 1024);
        TrimCache(guess);
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_directory));
            var free = new DriveInfo(root).AvailableFreeSpace;
            return free >= FreeSpaceReserve + guess;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }
    
    private static bool CheckFinalCapacity(long outputBytes, string targetPath)
    {
        var limit = LimitBytes();
        if (limit == long.MaxValue)
            return true;
        
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.lz4.bundle"))
            if (!string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                total += new FileInfo(path).Length;
        return total + outputBytes <= limit;
    }
    
    private static void TrimCache(long incoming)
    {
        if (!Directory.Exists(_directory))
            return;
        
        var now = DateTime.UtcNow;
        var pinned = new HashSet<string>(PreparedPaths.Values, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string path, long size, DateTime touched)>();
        long totalCacheSize = 0;
        foreach (var bundle in Directory.EnumerateFiles(_directory, "*.lz4.bundle"))
            try
            {
                var cacheSize = new FileInfo(bundle).Length;
                var metadata = bundle + ".meta";
                var touched = File.Exists(metadata) ? File.GetLastWriteTimeUtc(metadata) : DateTime.MinValue;
                totalCacheSize += cacheSize;
                if (pinned.Contains(bundle))
                    continue;
                
                if (now - touched > MaxIdle || !File.Exists(metadata))
                {
                    if (RemoveEntry(bundle))
                        totalCacheSize -= cacheSize;
                }
                else
                {
                    candidates.Add((bundle, cacheSize, touched));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BundleCache] Cache cleanup skipped for {bundle}: {ex.Message}");
            }
        
        var cacheLimit = LimitBytes();
        foreach (var candidate in candidates.OrderBy(c => c.touched))
        {
            if (totalCacheSize + incoming <= cacheLimit)
                break;
            
            if (RemoveEntry(candidate.path)) totalCacheSize -= candidate.size;
        }
    }
    
    private static long LimitBytes()
    {
        var gb = _maxSizeGiB?.Value ?? 8f;
        if (gb <= 0)
            return long.MaxValue;
        return (long)Math.Min(gb * 1024d * 1024d * 1024d, long.MaxValue);
    }
    
    private static string ReadBundleName(string meta)
    {
        var lines = File.ReadAllLines(meta);
        foreach (var line in lines)
            if (line.StartsWith(BundleLabel, StringComparison.Ordinal))
                return line[BundleLabel.Length..];
        return null;
    }
    
    private static bool RemoveEntry(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            SafeDelete(path + ".meta");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Cannot remove cache at {path}: {ex.Message}");
            return false;
        }
    }
    
    private static void DisableOnStorageFailure(string details)
    {
        if (_disabledThisRun)
            return;
        
        _disabledThisRun = true;
        Warning = "Bundle cache disabled: insufficient disk space, loading original asset bundles.";
        Plugin.Log.LogError($"[BundleCache] Cache disabled: {details}, clearing only own cache files.");
        if (_enabled is { Value: true })
            try
            {
                _enabled.Value = false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[BundleCache] Could not set cache setting to off in BepInEx config (disk may be full): {ex.Message}");
            }
        
        PreparedPaths.Clear();
        try
        {
            if (!Directory.Exists(_directory))
                return;
            
            foreach (var path in Directory.EnumerateFiles(_directory, "*.lz4.bundle"))
                RemoveEntry(path);
            foreach (var path in Directory.EnumerateFiles(_directory, "*.writing"))
                SafeDelete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Could not completely clear cache: {ex.Message}");
        }
    }
    
    private static bool IsCriticallyLowOnSpace()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_directory)));
            return drive.AvailableFreeSpace < FreeSpaceReserve;
        }
        catch
        {
            return false;
        }
    }
    
    private static bool IsDiskFull(Exception ex) =>
        // Windows ERROR_DISK_FULL (112), ERROR_HANDLE_DISK_FULL (39), and Unix text
        (ex.HResult & 0xffff) == 112 || (ex.HResult & 0xffff) == 39 || LooksLikeDiskFull(ex.Message);
    
    private static bool LooksLikeDiskFull(string message)
    {
        if (message == null)
            return false;
        
        message = message.ToLowerInvariant();
        return message.Contains("disk full") || message.Contains("disk is full") || message.Contains("diskfull") ||
               message.Contains("disk space") || message.Contains("no space left") ||
               message.Contains("not enough space") || message.Contains("not enough free") ||
               message.Contains("enospc");
    }
    
    private static void Cleanup(Preparation p)
    {
        SafeDelete(p.TempInputPath);
        SafeDelete(p.TempOutputPath);
        SafeDelete(p.TempMetaPath);
    }
    
    private static void SafeDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[BundleCache] Cache cleanup skipped {path}: {ex.Message}");
        }
    }
    
    private static string HashName(string identity)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity)), 0, 16).Replace("-", "")
            .ToLowerInvariant();
    }
    
    private sealed class Preparation
    {
        internal bool Hit;
        internal string InputPath, TempInputPath, CachePath, MetaPath, TempOutputPath, TempMetaPath, Metadata;
    }
}