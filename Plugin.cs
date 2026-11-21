using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Blueprinter
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static string BundlesSignature = "NOBUNDLES";
        public static ManualLogSource Log => Instance?.Logger;
        public static Plugin Instance;
        internal static int LoadingScreenImageLimit { get; private set; } = 3;
        internal static int HintLimit { get; private set; } = 3;
        public string GameVersion;
        private bool fastLoad;
        private bool skipAdditionalAssets;
        private BepInEx.Configuration.ConfigEntry<bool> bundleCacheEnabled;
        private BepInEx.Configuration.ConfigEntry<float> maxCacheSizeGiB;

        private void Awake()
        {
            Instance = this;
            GameVersion = Application.version;
            fastLoad = Config.Bind("General", "FastLoad", false).Value;
            skipAdditionalAssets = Config.Bind("General", "SkipAdditionalAssets", false).Value;
            LoadingScreenImageLimit = Config.Bind("Loading Screens", "Max Images Per Mod", 5,
                "Maximum number of loading screen images each mod can add. Set 0 to disable mod-added loading screens.").Value;
            HintLimit = Config.Bind("Hints", "Max Hints Per Mod", 10,
                "Maximum number of hints each mod can add. Set 0 to disable mod-added hints.").Value;
            bundleCacheEnabled = Config.Bind("Bundle Cache", "Enabled", true,
                "Cache loaded asset bundles into LZ4 format on disk for faster subsequent startups " +
                "(original mods are not modified).");
            maxCacheSizeGiB = Config.Bind("Bundle Cache", "Max Size GiB", 10f,
                "Maximum bundle cache size in GiB, older/unused entries are removed first. " +
                "(Set 0 for unlimited)\n\nCache folder is at Nuclear Option\\BepInEx\\Blueprinter_Cache and " +
                "can be safely manually deleted at any time too.");
            BundleCache.Configure(bundleCacheEnabled, maxCacheSizeGiB);
            GameObject mgr = Chainloader.ManagerObject;
            if (mgr != null)
            {
                mgr.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(mgr);
            }

            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            try
            {
                harmony.PatchAll();
            }
            catch (Exception ex)
            {
                Log.LogError("[Plugin] Failed to apply Harmony patches");
                Log.LogError(ex);
                harmony.UnpatchSelf();
                Api.SetState(Api.State.Failed);
                BlueprinterIssuePopup.ShowError(
                    "Harmony patches failed to apply.\n" +
                    "This may be caused by a game update or incompatible mod.\n" +
                    "Check the log for details.");
            }
        }

        private async Task<GameObject> LoadAdditionalAssets()
        {
            GameObject rewired = null;
            try
            {
                while (NetworkManagerNuclearOption.i == null)
                    await Task.Delay(10);

                await ResourcesAsyncLoader.LoadPrefab("Rewired", destroyCancellationToken, go => rewired = go);
                if (rewired == null)
                {
                    Log.LogWarning("[Plugin] Could not load Rewired");
                    return null;
                }

                var missionKey = MissionGroup.Default.First();
                if (!missionKey.TryLoad(out var mission, out var error))
                {
                    Log.LogWarning($"[Plugin] Could not load mission {error}");
                    DestroyImmediate(rewired);
                    return null;
                }

                MissionManager.SetMission(mission, false);
                await NetworkManagerNuclearOption.i.StartHostAsync(new HostOptions(SocketType.Offline, GameState.SinglePlayer, mission.MapKey));
                return rewired;
            }
            catch (Exception ex)
            {
                if (rewired != null)
                    DestroyImmediate(rewired);

                Log.LogWarning("[Plugin] Additional asset setup failed");
                Log.LogWarning(ex);
                return null;
            }
        }

        private async Task<bool> StopAdditionalAssets(GameObject rewired)
        {
            try
            {
                await NetworkManagerNuclearOption.i.StopAsync(setDisconnectReason: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError("[Plugin] Could not stop host");
                Log.LogError(ex);
                return false;
            }
            finally
            {
                if (rewired != null)
                    DestroyImmediate(rewired);
            }
        }

        public IEnumerator RunRoutine(Encyclopedia encyclopedia)
        {
            Api.SetState(Api.State.LoadingBundles);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var completed = false;
            var gameAssetNameIndexStarted = false;
            BundleRegistry bundleRegistry = null;
            BlueprinterLoadingScreen loadingScreen = null;
            string startupFailure = null;
            try
            {
                GameAssetNameIndex.Start();
                gameAssetNameIndexStarted = true;
                bundleRegistry = new BundleRegistry();
                var noYield = GameManager.IsHeadless;
                loadingScreen = noYield ? null : BlueprinterLoadingScreen.Create();
                Action<LoadedBundle, int, int> reportProgress = (loadedBundle, current, total) => loadingScreen?.SetBundleProgress(loadedBundle, current, total);
                if (BundleCache.Enabled)
                {
                    if (loadingScreen != null)
                        loadingScreen.Status = "Preparing LZ4 asset bundle cache...";
                    
                    var cacheEnumerator = bundleRegistry.PrepareCacheCoroutine();
                    while (true)
                    {
                        bool moveNext;
                        try
                        {
                            moveNext = cacheEnumerator.MoveNext();
                        }
                        catch (Exception ex)
                        {
                            Log.LogError("[Plugin] Cache preparation interrupted, continuing with available cached/original bundles: " + ex);
                            break;
                        }
                        
                        if (!moveNext)
                            break;
                        
                        yield return cacheEnumerator.Current;
                    }
                    if (loadingScreen != null && BundleCache.Warning != null)
                        loadingScreen.Warning = BundleCache.Warning;
                }

                if (fastLoad)
                {
                    try
                    {
                        bundleRegistry.FastLoad();
                    }
                    catch (Exception ex)
                    {
                        startupFailure = ex.Message;
                        Log.LogError("[Plugin] Bundle loading failed");
                        Log.LogError(ex);
                        yield break;
                    }
                }
                else
                {
                    var scanEnum = bundleRegistry.ScanAndLoadCoroutine(status => { if (loadingScreen != null) loadingScreen.Status = status; }, reportProgress);
                    while (true)
                    {
                        bool moveNext;
                        try
                        {
                            moveNext = scanEnum.MoveNext();
                        }
                        catch (Exception ex)
                        {
                            Log.LogError("[Plugin] Bundle loading failed");
                            Log.LogError(ex);
                            yield break;
                        }

                        if (!moveNext)
                            break;

                        if (!noYield)
                            yield return scanEnum.Current;
                    }
                }

                if (!bundleRegistry.ScanSucceeded)
                    yield break;

                BundleCache.RemoveRedundantCache(bundleRegistry.Bundles);

                BundlesSignature = bundleRegistry.Bundles.Count == 0 ? "NOBUNDLES" : string.Join("_", bundleRegistry.Bundles.Select(bundle => $"--{bundle.bundleName}-v{bundle.Manifest.modVersion}"));

                if (loadingScreen != null)
                {
                    loadingScreen.Status = "Preparing prefabs";
                    if (!fastLoad)
                        yield return null;
                }

                try
                {
                    PrefabHashAssigner.AssignFromBundles(bundleRegistry.Bundles);
                }
                catch (Exception ex)
                {
                    Log.LogError("[Plugin] Prefab preparation failed");
                    Log.LogError(ex);
                    yield break;
                }

                Api.SetState(Api.State.ApplyingPatches);
                if (loadingScreen != null)
                {
                    loadingScreen.Status = "Applying patches";
                    if (!fastLoad)
                        yield return null;
                }

                var runner = new PatchRunner(bundleRegistry, reportProgress);
                IEnumerator patchEnum = runner.ApplyAllPatchesCoroutine();

                while (true)
                {
                    bool moveNext;
                    try
                    {
                        moveNext = patchEnum.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        Log.LogError("[Plugin] Patching failed");
                        Log.LogError(ex);
                        yield break;
                    }

                    if (!moveNext)
                        break;
                    
                    // Don't yield with Fast Load on, the yield empty frames add up to be upwards of several seconds
                    if (!noYield && !fastLoad)
                        yield return patchEnum.Current;
                }
                
                if (runner.DeferredPatches.Count > 0 && !skipAdditionalAssets && GameManager.IsHeadless)
                    Log.LogWarning("Skipping additional asset loading, not yet supported on dedicated servers. " +
                                   $"({runner.DeferredPatches.Count} deferred patch groups)");
                
                if (runner.DeferredPatches.Count > 0 && !skipAdditionalAssets && !GameManager.IsHeadless)
                {
                    Log.LogInfo("[Plugin] Loading additional assets");
                    if (loadingScreen != null)
                        loadingScreen.Status = "Loading additional assets";

                    var loadTask = LoadAdditionalAssets();
                    yield return new WaitUntil(() => loadTask.IsCompleted);

                    GameObject rewired = loadTask.Result;
                    if (loadingScreen != null)
                    {
                        loadingScreen.Status = "Applying patches";
                        if (!fastLoad)
                            yield return null;
                    }

                    Exception retryException = null;
                    GameAssetNameIndex.Clear();
                    var retryEnum = runner.RetryDeferredPatchesCoroutine();

                    while (true)
                    {
                        bool moveNext;
                        try
                        {
                            moveNext = retryEnum.MoveNext();
                        }
                        catch (Exception ex)
                        {
                            retryException = ex;
                            break;
                        }

                        if (!moveNext)
                            break;

                        if (!noYield && !fastLoad)
                            yield return retryEnum.Current;
                    }

                    if (rewired != null)
                    {
                        if (SceneSingleton<GameplayUI>.i != null)
                            SceneSingleton<GameplayUI>.i.ResumeGame();

                        var stopTask = StopAdditionalAssets(rewired);
                        yield return new WaitUntil(() => stopTask.IsCompleted);

                        if (!stopTask.Result)
                            yield break;
                    }

                    if (retryException != null)
                    {
                        Log.LogError("[Plugin] Deferred patching failed");
                        Log.LogError(retryException);
                        yield break;
                    }

                    if (runner.DeferredPatches.Count > 0)
                    {
                        Log.LogWarning($"[Plugin] {runner.DeferredPatches.Count} deferred patches unresolved");
                        foreach (var item in runner.DeferredPatches)
                            Log.LogWarning($"[Plugin] ({item.Bundle.bundleName}) Patch {item.Patch?.GameAsset?.id ?? "unknown"} unresolved");
                    }
                }

                Api.SetState(Api.State.ApplyingOps);
                if (loadingScreen != null)
                {
                    loadingScreen.Status = "Applying ops";
                    if (!fastLoad)
                        yield return null;
                }

                try
                {
                    runner.ApplyAllOps(encyclopedia);
                    RegisterLiveries(bundleRegistry.Bundles);
                }
                catch (Exception ex)
                {
                    Log.LogError("[Plugin] Finalization failed");
                    Log.LogError(ex);
                    yield break;
                }

                if (loadingScreen != null && !fastLoad)
                    yield return null;
                completed = true;
            }
            finally
            {
                var cleanupCompleted = !gameAssetNameIndexStarted;
                try
                {
                    // Clean up temporary unity references after load complete
                    if (gameAssetNameIndexStarted)
                    {
                        GameAssetNameIndex.End();
                        cleanupCompleted = true;
                    }
                    // Keep loading screen present until all loading is complete, to prevent possible interaction going into
                    // a match with BP not fully finished yet
                    if (completed)
                        Log.LogInfo($"[Plugin] Done in {stopwatch.Elapsed.TotalSeconds:F2}s");
                    else
                        Log.LogError($"[Plugin] Startup ended before completion after {stopwatch.Elapsed.TotalSeconds:F2}s");

                    BlueprinterLoadingScreen.DestroyInstance();

                    var canShowPopup = !Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
                    if (!completed && canShowPopup)
                    {
                        BlueprinterIssuePopup.ShowError(
                            startupFailure ?? "Startup did not complete.\nCheck the log for details.");
                    }
                    else if (completed && bundleRegistry != null &&
                        (bundleRegistry.DuplicateMods.Count > 0 || bundleRegistry.OutdatedMods.Count > 0) && canShowPopup)
                    {
                        BlueprinterIssuePopup.Show(bundleRegistry.DuplicateMods, bundleRegistry.OutdatedMods);
                    }

                }
                finally
                {
                    Api.SetState(completed && cleanupCompleted ? Api.State.Ready : Api.State.Failed);
                }
            }
        }

        private void RegisterLiveries(IReadOnlyList<LoadedBundle> bundles)
        {
            var locator = new LiveryAddressableLocator(bundles);
            if (locator.Count == 0)
            {
                Log.LogDebug("[Plugin] No livery overrides");
                return;
            }

            Addressables.ResourceManager.ResourceProviders.Add(new LiveryAddressableProvider());
            Addressables.AddResourceLocator(locator);
            Log.LogDebug($"[Plugin] Registered {locator.Count} liveries");
        }
    }
}
