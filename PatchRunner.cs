using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Blueprinter.Ops;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Blueprinter
{
    public class PatchRunner(BundleRegistry bundles, Action<LoadedBundle, int, int> reportProgress = null)
    {
        private class Progress
        {
            public int Current;
            public int Total;
        }

        public readonly List<(LoadedBundle Bundle, AssetPatch Patch)> DeferredPatches = [];
        private readonly Dictionary<LoadedBundle, Progress> progress = [];

        public IEnumerator ApplyAllPatchesCoroutine()
        {
            DeferredPatches.Clear();
            progress.Clear();
            foreach (var loadedBundle in bundles.Bundles)
            {
                var manifest = loadedBundle.Manifest;
                int patchLocationCount = GetPatchLocationCount(manifest);
                int totalWork = GetTotalWork(manifest);
                if (totalWork == 0)
                    continue;

                Plugin.Log.LogDebug($"[PatchRunner] {manifest.modName} work: {patchLocationCount} locations, {manifest?.Ops?.Count ?? 0} ops");
                progress[loadedBundle] = new Progress { Total = totalWork };
                ReportProgress(loadedBundle);
                if (patchLocationCount == 0)
                    continue;

                Plugin.Log.LogDebug($"[PatchRunner] Applying patches {manifest.modName} {manifest.modVersion}");

                int appliedLocations = 0;
                int processedLocations = 0;

                foreach (var patch in manifest.Patches)
                {
                    var locations = patch?.PatchLocations;
                    int locationCount = locations?.Count ?? 0;
                    if (locationCount == 0)
                        continue;

                    if (patch.GameAsset?.asset == null)
                    {
                        Plugin.Log.LogWarning($"[PatchRunner] Patch {patch.GameAsset?.id ?? "unknown"} missing game asset");
                        progress[loadedBundle].Current += locationCount;
                        continue;
                    }

                    var gameLocation = patch.GameAsset;
                    var gameAssetRoot = ResourcesAssetResolver.ResolveGameAsset(gameLocation.asset, false, out var missingGameAsset);
                    if (gameAssetRoot == null)
                    {
                        if (missingGameAsset)
                        {
                            Plugin.Log.LogDebug($"[PatchRunner] Game asset {gameLocation.asset.name ?? gameLocation.asset.locator} not available, deferring patch {gameLocation.id}");
                            DeferredPatches.Add((loadedBundle, patch));
                        }
                        else
                        {
                            progress[loadedBundle].Current += locationCount;
                        }
                        continue;
                    }

                    var gameAsset = ResourcesAssetResolver.ResolveBundleTargetObject(gameAssetRoot, gameLocation);
                    if (gameAsset == null)
                    {
                        progress[loadedBundle].Current += locationCount;
                        continue;
                    }

                    foreach (var loc in locations)
                    {
                        processedLocations++;
                        progress[loadedBundle].Current++;
                        if (ApplySingleLocation(loadedBundle, gameLocation.id, gameAsset, loc))
                            appliedLocations++;

                        if (processedLocations % 0x400 == 0)
                        {
                            ReportProgress(loadedBundle);
                            yield return null;
                        }
                    }
                }

                Plugin.Log.LogDebug($"[PatchRunner] {manifest.modName} {appliedLocations}/{patchLocationCount} locations applied initially");
                ReportProgress(loadedBundle);
                yield return null;
            }
        }

        public IEnumerator RetryDeferredPatchesCoroutine()
        {
            var patches = DeferredPatches.ToArray();
            DeferredPatches.Clear();

            if (patches.Length == 0)
                yield break;

            Plugin.Log.LogDebug($"[PatchRunner] Retrying {patches.Length} deferred patches");

            int processedLocations = 0;
            foreach (var item in patches)
            {
                var patch = item.Patch;
                var locations = patch?.PatchLocations;
                int locationCount = locations?.Count ?? 0;
                if (locationCount == 0)
                    continue;

                var bundle = item.Bundle;
                var gameLocation = patch.GameAsset;
                var gameAssetRoot = ResourcesAssetResolver.ResolveGameAsset(gameLocation.asset);
                if (gameAssetRoot == null)
                {
                    DeferredPatches.Add(item);
                    progress[bundle].Current += locationCount;
                    processedLocations += locationCount;
                    ReportProgress(bundle);
                    continue;
                }

                var gameAsset = ResourcesAssetResolver.ResolveBundleTargetObject(gameAssetRoot, gameLocation);
                if (gameAsset == null)
                {
                    progress[bundle].Current += locationCount;
                    processedLocations += locationCount;
                    ReportProgress(bundle);
                    continue;
                }

                foreach (var loc in locations)
                {
                    ApplySingleLocation(bundle, gameLocation.id, gameAsset, loc);
                    progress[bundle].Current++;
                    processedLocations++;

                    if (processedLocations % 0x400 == 0)
                    {
                        ReportProgress(bundle);
                        yield return null;
                    }
                }

                ReportProgress(bundle);
            }
        }

        public void ApplyAllOps(Encyclopedia encyclopedia)
        {
            EncyclopediaLoader.Load(encyclopedia, bundles.Bundles);

            foreach (var loadedBundle in bundles.Bundles)
            {
                var manifest = loadedBundle.Manifest;
                var ops = manifest?.Ops;
                if (ops == null || ops.Count == 0)
                    continue;

                Plugin.Log.LogDebug($"[PatchRunner] Applying ops {manifest.modName} {manifest.modVersion}");

                foreach (var op in ops)
                {
                    if (op?.opId != EncyclopediaLoader.OpId)
                        ApplySingleOp(loadedBundle, manifest, op);

                    progress[loadedBundle].Current++;
                }

                ReportProgress(loadedBundle);
            }
        }

        private void ReportProgress(LoadedBundle bundle)
        {
            var value = progress[bundle];
            reportProgress?.Invoke(bundle, value.Current, value.Total);
        }

        public static int GetTotalWork(PatchManifest manifest)
        {
            return GetPatchLocationCount(manifest) + (manifest?.Ops?.Count ?? 0);
        }

        private static int GetPatchLocationCount(PatchManifest manifest)
        {
            int total = 0;
            if (manifest?.Patches == null)
                return total;

            foreach (var patch in manifest.Patches)
                total += patch?.PatchLocations?.Count ?? 0;

            return total;
        }

        private void ApplySingleOp(LoadedBundle bundle, PatchManifest manifest, Op op)
        {
            if (op == null)
                return;

            try
            {
                switch (op.opId)
                {
                    case OpAddAircraftToHangarsHandler.OpId:
                        ApplyOp<OpAddAircraftToHangarsPayload>(bundle, op, OpAddAircraftToHangarsHandler.Handle);
                        break;
                    case OpAddToHangarHandler.OpId:
                        ApplyOp<OpAddToHangarPayload>(bundle, op, OpAddToHangarHandler.Handle);
                        break;
                    case OpAddLoadingScreensHandler.OpId:
                        ApplyOp<OpAddLoadingScreensPayload>(bundle, op, OpAddLoadingScreensHandler.Handle);
                        break;
                    case OpAddWeaponToHardpointHandler.OpId:
                        ApplyOp<OpAddWeaponToHardpointPayload>(bundle, op, OpAddWeaponToHardpointHandler.Handle);
                        break;
                    case OpAddWeaponMountToWeaponManagerHandler.OpId:
                        ApplyOp<OpAddWeaponMountPayload>(bundle, op, OpAddWeaponMountToWeaponManagerHandler.Handle);
                        break;
                    case OpAddMissionsHandler.OpId:
                        ApplyOp<OpAddMissionsPayload>(bundle, op, OpAddMissionsHandler.Handle);
                        break;
                    case OpFindAircraftToHangarHandler.OpId:
                        ApplyOp<OpFindAircraftToHangarPayload>(bundle, op, OpFindAircraftToHangarHandler.Handle);
                        break;
                    default:
                        Plugin.Log.LogWarning($"[PatchRunner] Unsupported op {op.opId} in {manifest.modName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Op {op.opId} failed in {manifest.modName}");
                Plugin.Log.LogWarning(ex);
            }
        }

        private static void ApplyOp<TPayload>(LoadedBundle bundle, Op op, Action<LoadedBundle, TPayload> handle)
        {
            if (string.IsNullOrEmpty(op.payloadJson))
            {
                Plugin.Log.LogWarning($"[PatchRunner] Empty payload {op.opId}");
                return;
            }

            TPayload payload;
            try
            {
                payload = JsonUtilities.Deserialize<TPayload>(op.payloadJson);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Invalid payload {op.opId}");
                Plugin.Log.LogWarning(ex);
                return;
            }

            if (payload == null)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Null payload {op.opId}");
                return;
            }

            handle(bundle, payload);
        }

        private bool ApplySingleLocation(LoadedBundle loadedBundle, string patchId, Object gameAsset, LocationRef loc)
        {
            if (loc == null)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Null location in patch {patchId}");
                return false;
            }
            var locationId = loc.id;

            if (loc.asset == null)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Missing asset reference {patchId} {locationId}");
                return false;
            }

            var bundleAssetRoot = ResourcesAssetResolver.ResolveBundleAsset(loadedBundle, loc.asset);
            if (bundleAssetRoot == null)
                return false;

            var bundleTargetObject = ResourcesAssetResolver.ResolveBundleTargetObject(bundleAssetRoot, loc);
            if (bundleTargetObject == null)
                return false;

            if (string.IsNullOrEmpty(loc.memberPath))
            {
                Plugin.Log.LogWarning($"[PatchRunner] Empty member path {patchId} {locationId}");
                return false;
            }

            var memberPath = loc.memberPath;
            try
            {
                // SPECIAL CASE #0: Camera rendererIndex (URP)
                if (string.Equals(memberPath, "rendererIndex", StringComparison.Ordinal) || string.Equals(memberPath, "m_RendererIndex", StringComparison.Ordinal))
                    return ApplyCameraRendererIndexPatch(bundleTargetObject, gameAsset, patchId, locationId);

                // SPECIAL CASE #1: AudioMixer -> AudioMixerGroup wiring via outputAudioMixerGroup::<groupName>
                if (memberPath.StartsWith("outputAudioMixerGroup", StringComparison.Ordinal))
                {
                    if (gameAsset is not UnityEngine.Audio.AudioMixer mixer)
                    {
                        Plugin.Log.LogWarning($"[PatchRunner] Asset {gameAsset.GetType().FullName} is not AudioMixer patch {patchId} location {locationId}");
                        return false;
                    }

                    const string Sep = "::";
                    var sepIndex = memberPath.IndexOf(Sep, StringComparison.Ordinal);
                    if (sepIndex < 0 || sepIndex + Sep.Length >= memberPath.Length)
                    {
                        Plugin.Log.LogWarning($"[PatchRunner] Missing AudioMixer group in path {memberPath} patch {patchId} location {locationId}");
                        return false;
                    }

                    var groupHint = memberPath[(sepIndex + Sep.Length)..];
                    memberPath = memberPath[..sepIndex];
                    var groups = mixer.FindMatchingGroups(groupHint);
                    if (groups == null || groups.Length == 0)
                    {
                        Plugin.Log.LogWarning($"[PatchRunner] AudioMixer {mixer.name} missing group {groupHint} patch {patchId} location {locationId}");
                        return false;
                    }

                    if (!MemberPathSetter.TryApply(bundleTargetObject, memberPath, groups[0]))
                    {
                        Plugin.Log.LogWarning($"[PatchRunner] Could not set {memberPath} patch {patchId} location {locationId}");
                        return false;
                    }
                    return true;
                }

                // SPECIAL CASE #2: Renderer.materials[] / sharedMaterials[]
                if (memberPath.StartsWith("sharedMaterials[", StringComparison.Ordinal) || memberPath.StartsWith("materials[", StringComparison.Ordinal))
                    return ApplyRendererMaterialArrayPatch(bundleTargetObject, gameAsset, memberPath, patchId, locationId);

                // SPECIAL CASE #3: Material texture property
                if (bundleTargetObject is Material material && memberPath.StartsWith("materialTexture::", StringComparison.Ordinal))
                {
                    if (gameAsset is not Texture texture)
                    {
                        Plugin.Log.LogWarning($"[PatchRunner] Asset {gameAsset?.GetType().FullName} is not Texture patch {patchId} location {locationId}");
                        return false;
                    }

                    string propertyName = memberPath.Substring("materialTexture::".Length);

                    if (!material.HasProperty(propertyName))
                    {
                        Plugin.Log.LogWarning($"[PatchRunner] Material {material.name} missing texture property {propertyName} patch {patchId} location {locationId}");
                        return false;
                    }

                    material.SetTexture(propertyName, texture);
                    return true;
                }

                // Default behaviour: assign the resolved GameAsset directly.
                if (!MemberPathSetter.TryApply(bundleTargetObject, memberPath, gameAsset))
                {
                    Plugin.Log.LogWarning($"[PatchRunner] Could not set {memberPath} patch {patchId} location {locationId}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Patch {patchId} location {locationId} member {loc.memberPath} failed");
                Plugin.Log.LogWarning(ex);
                return false;
            }
        }
        private bool ApplyCameraRendererIndexPatch(Object bundleTargetObject, Object gameAsset, string patchId, string locationId)
        {
            var rpAsset = GraphicsSettings.currentRenderPipeline as ScriptableObject;
            if (rpAsset == null)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Missing render pipeline patch {patchId} location {locationId}");
                return false;
            }

            var rpType = rpAsset.GetType();
            var field = rpType.GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic) ?? rpType.GetField("m_RendererData", BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Missing renderer list on {rpAsset.name} patch {patchId} location {locationId}");
                return false;
            }

            var listObj = field.GetValue(rpAsset);
            if (listObj is not Array arr)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Renderer list on {rpAsset.name} is not an array patch {patchId} location {locationId}");
                return false;
            }

            var rendererAsset = gameAsset;
            var idx = -1;
            for (int i = 0; i < arr.Length; i++)
            {
                var elem = arr.GetValue(i) as Object;
                if (elem == rendererAsset)
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Renderer {rendererAsset.name} not found in {rpAsset.name} patch {patchId} location {locationId}");
                return false;
            }

            var t = Traverse.Create(bundleTargetObject);
            var field2 = t.Field("m_RendererIndex");
            if (!field2.FieldExists())
            {
                Plugin.Log.LogWarning($"[PatchRunner] Missing m_RendererIndex on {bundleTargetObject.GetType().FullName} patch {patchId} location {locationId}");
                return false;
            }

            field2.SetValue(idx);
            return true;
        }

        private bool ApplyRendererMaterialArrayPatch(Object target, Object gameAsset, string memberPath, string patchId, string locationId)
        {
            if (target is not Renderer renderer)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Target {target.GetType().FullName} is not a Renderer patch {patchId} location {locationId}");
                return false;
            }

            if (gameAsset is not Material mat)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Asset {gameAsset.GetType().FullName} is not a Material patch {patchId} location {locationId}");
                return false;
            }

            const string SharedPrefix = "sharedMaterials[";
            const string InstPrefix = "materials[";
            var useShared = memberPath.StartsWith(SharedPrefix, StringComparison.Ordinal);
            var prefix = useShared ? SharedPrefix : InstPrefix;

            int closeBracket = memberPath.IndexOf(']', prefix.Length);
            if (closeBracket < 0)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Malformed member path {memberPath} patch {patchId} location {locationId}");
                return false;
            }

            string indexStr = memberPath[prefix.Length..closeBracket];
            if (!int.TryParse(indexStr, out var idx) || idx < 0)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Invalid material index {indexStr} path {memberPath} patch {patchId} location {locationId}");
                return false;
            }

            var arr = useShared ? renderer.sharedMaterials : renderer.materials;
            var length = arr?.Length ?? 0;

            if (arr == null || length == 0 || idx >= length)
            {
                Plugin.Log.LogWarning($"[PatchRunner] Material index {idx} out of range {length} on {renderer.gameObject.name} patch {patchId} location {locationId}");
                return false;
            }

            arr[idx] = mat;

            if (useShared)
                renderer.sharedMaterials = arr;
            else
                renderer.materials = arr;

            return true;
        }
    }
}
