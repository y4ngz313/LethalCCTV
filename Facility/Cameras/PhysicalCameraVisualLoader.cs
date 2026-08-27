using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    internal static class PhysicalCameraVisualLoader
    {
        internal const string BundleFileName = "lethalcctv-camera.meshbundle";

        private static bool _loadAttempted;
        private static GameObject _cameraPrefab;
        private static AssetBundle _bundle;

        internal static GameObject TryLoad()
        {
            EnsureLoaded();
            return _cameraPrefab;
        }

        private static void EnsureLoaded()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;

            try
            {
                string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dllDir))
                {
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][CameraVisual] Cannot resolve plugin directory; using generated fallback cameras.");
                    return;
                }

                string bundlePath = Path.Combine(dllDir, BundleFileName);
                if (!File.Exists(bundlePath))
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][CameraVisual] Optional bundle '{BundleFileName}' not found next to the active plugin DLL; using generated fallback cameras.");
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][CameraVisual] AssetBundle.LoadFromFile returned null for '{bundlePath}'; using generated fallback cameras.");
                    return;
                }

                UnityEngine.Object[] all = _bundle.LoadAllAssets(typeof(GameObject));
                _cameraPrefab = PickPrefab(all, requiredTerm: "camera", preferredTerm: "security");
                if (_cameraPrefab == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][CameraVisual] Bundle loaded but contained no GameObject prefab. Assets: [{string.Join(", ", _bundle.GetAllAssetNames())}]. Using generated fallback cameras.");
                    return;
                }

                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][CameraVisual] Loaded physical camera prefab '{_cameraPrefab.name}' from '{bundlePath}'.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][CameraVisual] Bundle load failed: {ex.GetType().Name}: {ex.Message}. Using generated fallback cameras.");
            }
        }

        private static GameObject PickPrefab(UnityEngine.Object[] assets, string requiredTerm, string preferredTerm)
        {
            if (assets == null || assets.Length == 0) return null;

            GameObject first = null;
            for (int i = 0; i < assets.Length; i++)
            {
                GameObject candidate = assets[i] as GameObject;
                if (candidate == null) continue;
                if (first == null) first = candidate;

                string name = candidate.name ?? string.Empty;
                if (name.IndexOf(requiredTerm, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    name.IndexOf(preferredTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }

            for (int i = 0; i < assets.Length; i++)
            {
                GameObject candidate = assets[i] as GameObject;
                if (candidate == null) continue;
                string name = candidate.name ?? string.Empty;
                if (name.IndexOf(requiredTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                    return candidate;
            }

            return string.Equals(requiredTerm, "camera", StringComparison.OrdinalIgnoreCase) ? first : null;
        }
    }
}
