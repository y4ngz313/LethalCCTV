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

                if (!TryBindRig(_cameraPrefab, out _, out _, out _, out _, out _))
                {
                    _cameraPrefab = null;
                    return;
                }

                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][CameraVisual] Loaded physical camera prefab '{_cameraPrefab.name}' from '{bundlePath}'.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][CameraVisual] Bundle load failed: {ex.GetType().Name}: {ex.Message}. Using generated fallback cameras.");
            }
        }

        internal static bool TryBindRig(GameObject root, out Transform bracket, out Transform yoke,
            out Transform head, out Transform led, out Transform lens)
        {
            bracket = root != null ? root.transform.Find("Bracket") : null;
            yoke = bracket != null ? bracket.Find("Yoke") : null;
            head = yoke != null ? yoke.Find("Head") : null;
            led = head != null ? head.Find("LED_Socket") : null;
            lens = head != null ? head.Find("Lens_Socket") : null;
            if (bracket != null && yoke != null && head != null && led != null && lens != null)
                return true;

            Transform[] nodes = root != null ? root.GetComponentsInChildren<Transform>(true) : Array.Empty<Transform>();
            string[] names = new string[nodes.Length];
            for (int i = 0; i < nodes.Length; i++) names[i] = nodes[i].name;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV][CameraVisual] Invalid named rig; expected Bracket/Yoke/Head with LED_Socket and Lens_Socket; found [{string.Join(", ", names)}]. Using generated fallback cameras.");
            return false;
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
