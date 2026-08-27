using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance.OutlineEffect
{
    internal static class OutlineRuntimeAssets
    {
        // NOT ".lethalbundle": LethalLevelLoader auto-loads every *.lethalbundle in the
        // plugins folder, which double-loads this bundle and hard-fails the game load.
        internal const string BundleName = "y4ngz-outlineeffect.bundle";

        private static readonly Dictionary<string, Shader> ShaderCache = new Dictionary<string, Shader>();
        private static AssetBundle _bundle;
        private static bool _loadAttempted;

        internal static Shader LoadShader(string shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
                return null;

            if (ShaderCache.TryGetValue(shaderName, out Shader cached) && cached != null)
                return cached;

            Shader shader = Resources.Load<Shader>(shaderName);
            if (shader == null)
                shader = LoadShaderFromBundles(shaderName);

            if (shader != null)
            {
                ShaderCache[shaderName] = shader;
            }
            else
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] OutlineEffect shader '{shaderName}' could not be loaded. Missing {BundleName}?");
            }

            return shader;
        }

        private static Shader LoadShaderFromBundles(string shaderName)
        {
            foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (loaded == null)
                    continue;

                Shader shader = loaded.LoadAsset<Shader>(shaderName);
                if (shader != null)
                    return shader;
            }

            EnsureBundleLoaded();
            return _bundle != null ? _bundle.LoadAsset<Shader>(shaderName) : null;
        }

        private static void EnsureBundleLoaded()
        {
            if (_loadAttempted)
                return;

            _loadAttempted = true;
            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string bundlePath = Path.Combine(dllDir ?? string.Empty, BundleName);
            if (!File.Exists(bundlePath))
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] OutlineEffect bundle is missing at {bundlePath}.");
                return;
            }

            _bundle = AssetBundle.LoadFromFile(bundlePath);
            if (_bundle == null)
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to load outline effect bundle at {bundlePath}.");
        }
    }
}
