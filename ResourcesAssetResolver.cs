using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blueprinter
{
    public static class ResourcesAssetResolver
    {
        private static readonly Dictionary<(Type, string), Object> BaseCache = [];
        private static readonly Dictionary<(AssetBundle, string, Type), Object> BundleAssetCache = [];
        private static readonly Dictionary<string, Type> ResolvedTypeCache = new(StringComparer.Ordinal);
        
        public static Object ResolveGameAsset(AssetRef assetRef, bool logMissing = true)
        {
            return ResolveGameAsset(assetRef, logMissing, out _);
        }

        public static Object ResolveGameAsset(AssetRef assetRef, bool logMissing, out bool missing)
        {
            missing = false;
            if (assetRef == null)
                return null;

            Type type = ResolveType(assetRef.type);
            if (type == null)
            {
                Log?.LogWarning($"[ResourcesAssetResolver] Unknown type {assetRef.type}");
                return null;
            }

            string key = assetRef.name ?? assetRef.locator;
            if (string.IsNullOrEmpty(key))
            {
                Log?.LogWarning($"[ResourcesAssetResolver] Missing locator for type {assetRef.type}");
                return null;
            }

            if (BaseCache.TryGetValue((type, key), out var cached))
                return cached;

            if (GameAssetNameIndex.TryGet(type, key, out var indexedMatch, out var indexedCount))
            {
                if (indexedCount > 1)
                    Log?.LogWarning($"[ResourcesAssetResolver] Game asset {key} type {assetRef.type} has {indexedCount} matches, using first");
                BaseCache[(type, key)] = indexedMatch;
                return indexedMatch;
            }

            var objects = Resources.FindObjectsOfTypeAll(type);
            Object match = null;
            int matchCount = 0;

            foreach (Object obj in objects)
            {
                if (obj == null)
                    continue;
                if (obj is GameObject go && go.transform.parent != null)
                    continue;

                bool matches = string.Equals(obj.name, key, StringComparison.OrdinalIgnoreCase);
                if (!matches && obj is Component comp)
                {
                    string rootName = comp.transform?.root?.name;
                    matches = !string.IsNullOrEmpty(rootName) && string.Equals(rootName, key, StringComparison.OrdinalIgnoreCase);
                }

                if (!matches)
                    continue;

                match ??= obj;
                matchCount++;
            }

            GameAssetNameIndex.Build(type, objects);

            if (match == null)
            {
                missing = true;
                if (logMissing)
                    Log?.LogWarning($"[ResourcesAssetResolver] Game asset {key} type {assetRef.type} not found");
                return null;
            }

            if (matchCount > 1)
                Log?.LogWarning($"[ResourcesAssetResolver] Game asset {key} type {assetRef.type} has {matchCount} matches, using first");

            BaseCache[(type, key)] = match;
            return match;
        }

        public static Object ResolveBundleAsset(LoadedBundle bundle, AssetRef targetRef)
        {
            if (bundle == null || bundle.AssetBundle == null || targetRef == null)
                return null;

            string key = targetRef.locator ?? targetRef.name;
            if (string.IsNullOrEmpty(key))
            {
                Log?.LogWarning($"[ResourcesAssetResolver] ({bundle.bundleName}) Missing locator");
                return null;
            }

            Type type = ResolveType(targetRef.type);
            if (!string.IsNullOrEmpty(targetRef.type) && type == null)
            {
                Log?.LogWarning($"[ResourcesAssetResolver] ({bundle.bundleName}) Unknown type {targetRef.type} for {key}");
                return null;
            }

            try
            {
                (AssetBundle AssetBundle, string key, Type type) cacheKey = (bundle.AssetBundle, key, type);
                if (BundleAssetCache.TryGetValue(cacheKey, out var cachedAsset))
                    return cachedAsset;

                Object asset = type != null ? bundle.AssetBundle.LoadAsset(key, type) : bundle.AssetBundle.LoadAsset(key);
                
                if (key.Contains(".fbx"))
                {
                    var objects = type != null ? bundle.AssetBundle.LoadAssetWithSubAssets(key, type) : bundle.AssetBundle.LoadAssetWithSubAssets(key);
                    foreach (var obj in objects)
                    {
                        if (obj.name == targetRef.name)
                        {
                            asset = obj;
                        }
                    }
                }
                
                if (asset == null)
                    Log?.LogWarning($"[ResourcesAssetResolver] ({bundle.bundleName}) Could not load {key} type {targetRef.type}");
                else
                    NormalizeBundleAssetShaders(asset, bundle.bundleName, key);

                BundleAssetCache[cacheKey] = asset;
                return asset;
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[ResourcesAssetResolver] ({bundle.bundleName}) Load failed {key} from {bundle.source}");
                Log?.LogWarning(ex);
                return null;
            }
        }

        public static Object ResolveBundleTargetObject(Object targetAsset, LocationRef loc)
        {
            if (targetAsset == null || loc == null)
                return null;

            if (string.IsNullOrEmpty(loc.hierarchyPath) && string.IsNullOrEmpty(loc.componentType))
                return targetAsset;

            GameObject rootGo;
            if (targetAsset is GameObject go)
                rootGo = go;
            else if (targetAsset is Component comp)
                rootGo = comp.gameObject;
            else
            {
                Log?.LogWarning($"[ResourcesAssetResolver] Target {targetAsset.name} requires GameObject or Component");
                return null;
            }

            Transform current = rootGo.transform;
            if (!string.IsNullOrEmpty(loc.hierarchyPath))
            {
                Transform target = rootGo.transform.Find(loc.hierarchyPath);
                if (target == null)
                {
                    Log?.LogWarning($"[ResourcesAssetResolver] Hierarchy {loc.hierarchyPath} not found under {rootGo.name}");
                    return null;
                }
                current = target;
            }

            if (string.IsNullOrEmpty(loc.componentType))
                return current.gameObject;

            Type compType = ResolveType(loc.componentType);
            if (compType == null)
            {
                Log?.LogWarning($"[ResourcesAssetResolver] Unknown component type {loc.componentType}");
                return null;
            }

            Component[] comps = current.GetComponents(compType);
            if (comps == null || comps.Length == 0)
            {
                Log?.LogWarning($"[ResourcesAssetResolver] Component {loc.componentType} not found on {current.gameObject.name}");
                return null;
            }

            int idx = loc.componentIndex;
            if (idx < 0 || idx >= comps.Length)
            {
                Log?.LogWarning($"[ResourcesAssetResolver] Component index {idx} out of range {comps.Length} on {current.gameObject.name}");
                return null;
            }

            return comps[idx];
        }

        private static ManualLogSource Log => Plugin.Log;

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            if (ResolvedTypeCache.TryGetValue(typeName, out var cached))
                return cached;

            var result = Type.GetType(typeName) ?? typeof(GameObject).Assembly.GetType(typeName);
            if (result != null)
                ResolvedTypeCache[typeName] = result;

            return result;
        }

        private static void NormalizeBundleAssetShaders(Object asset, string bundleName, string assetKey)
        {
            try
            {
                int fixedCount = 0;

                switch (asset)
                {
                    case Material material:
                        if (NormalizeMaterialShader(material))
                            fixedCount++;
                        break;

                    case GameObject go:
                        foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
                            fixedCount += NormalizeRendererMaterials(renderer);
                        break;

                    case Renderer renderer:
                        fixedCount += NormalizeRendererMaterials(renderer);
                        break;

                    case Component component:
                        foreach (Renderer renderer in component.GetComponentsInChildren<Renderer>(true))
                            fixedCount += NormalizeRendererMaterials(renderer);
                        break;
                }

                if (fixedCount > 0)
                {
                    Log?.LogDebug($"[ResourcesAssetResolver] Rebound {fixedCount} shaders in {assetKey} from {bundleName}");
                }
            }
            catch (Exception ex)
            {
                Log?.LogWarning($"[ResourcesAssetResolver] Shader normalization failed in {assetKey} from {bundleName}");
                Log?.LogWarning(ex);
            }
        }

        private static int NormalizeRendererMaterials(Renderer renderer)
        {
            if (renderer == null)
                return 0;

            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                return 0;

            int fixedCount = 0;

            foreach (Material material in materials)
            {
                if (NormalizeMaterialShader(material))
                    fixedCount++;
            }

            return fixedCount;
        }

        private static bool NormalizeMaterialShader(Material material)
        {
            if (material == null || material.shader == null)
                return false;

            Shader currentShader = material.shader;
            string shaderName = currentShader.name;

            Shader runtimeShader = Shader.Find(shaderName);
            if (runtimeShader == null)
                return false;

            if (ReferenceEquals(currentShader, runtimeShader))
                return false;

            string[] keywords = material.shaderKeywords;
            int rawRenderQueue = material.rawRenderQueue;
            MaterialGlobalIlluminationFlags giFlags = material.globalIlluminationFlags;
            bool enableInstancing = material.enableInstancing;
            bool doubleSidedGI = material.doubleSidedGI;

            material.shader = runtimeShader;

            material.shaderKeywords = keywords;
            material.renderQueue = rawRenderQueue;
            material.globalIlluminationFlags = giFlags;
            material.enableInstancing = enableInstancing;
            material.doubleSidedGI = doubleSidedGI;

            return true;
        }
    }
}
