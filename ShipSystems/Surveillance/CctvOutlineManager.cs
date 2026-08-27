using System.Collections.Generic;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.ShipSystems.Surveillance.OutlineEffect;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// CCTV-only real outlines for dynamic targets. Objective-like targets remain
    /// boxed by CctvScreenOutlineOverlay.
    /// </summary>
    internal static class CctvOutlineManager
    {
        private const float UpdateIntervalSeconds = 0.20f;
        private const float OutlineRequestDurationSeconds = 0.70f;
        private const float OutlineRadiusMeters = 58f;

        private static readonly Dictionary<int, GameObject> _activeRoots = new Dictionary<int, GameObject>(128);
        private static readonly Dictionary<int, OutlineCandidate> _candidatesByRoot = new Dictionary<int, OutlineCandidate>(128);
        private static readonly HashSet<int> _liveRootIds = new HashSet<int>();
        private static readonly List<int> _rootsToClear = new List<int>(128);

        private static float _nextUpdateAt;
        private static bool _initialized;
        private static string _lastOutlineSummary;
        private static string _lastSkipState;
        private static float _nextSkipLogAt;

        private enum OutlineKind
        {
            Item,
            Hostile,
            Player,
        }

        private sealed class OutlineCandidate
        {
            public GameObject Root;
            public OutlineKind Kind;
            public int RendererCount;
            public float NearestDistanceSq;
        }

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] CCTV OutlineEffect manager initialized.");
        }

        internal static void Shutdown()
        {
            ClearAll();
            CctvTargetCache.Clear();
            _initialized = false;
        }

        internal static void ClearAll()
        {
            foreach (GameObject root in _activeRoots.Values)
                CctvOutlineEffectBridge.Clear(root);

            _activeRoots.Clear();
            _candidatesByRoot.Clear();
            _liveRootIds.Clear();
            _rootsToClear.Clear();
            _lastOutlineSummary = null;
            CctvOutlineEffectBridge.DisableActiveEffect();
        }

        internal static void Tick(CCTVCamera activeCamera, bool isActive, bool force = false)
        {
            if (!_initialized)
                Initialize();

            if (!isActive || activeCamera == null || MonitorFocus.IsTurretPageActive)
            {
                if (isActive && activeCamera == null)
                    LogOutlineDiagnostic("skip active=true activeCamera=null");
                ClearAll();
                return;
            }

            Camera camera = activeCamera.Cam;
            if (camera == null)
            {
                LogOutlineDiagnostic($"skip active=true holder='{DescribeCamera(activeCamera)}' camera=null");
                ClearAll();
                return;
            }

            if (!force && Time.unscaledTime < _nextUpdateAt)
                return;

            _nextUpdateAt = Time.unscaledTime + UpdateIntervalSeconds;
            CctvTargetCache.RefreshIfDue(force);
            RefreshOutlineTargets(camera);
        }

        private static void RefreshOutlineTargets(Camera camera)
        {
            _candidatesByRoot.Clear();
            _liveRootIds.Clear();

            Vector3 cameraPosition = camera.transform.position;
            IReadOnlyList<CctvTargetCache.RendererTarget> rendererTargets = CctvTargetCache.RendererTargets;
            int candidateRenderers = 0;
            int nearRenderers = 0;

            for (int i = 0; i < rendererTargets.Count; i++)
            {
                CctvTargetCache.RendererTarget target = rendererTargets[i];
                if (!ShouldUseRealOutline(target.Kind))
                    continue;

                Renderer renderer = target.Renderer;
                if (renderer == null || !renderer.enabled)
                    continue;

                candidateRenderers++;
                float distanceSq = (renderer.bounds.center - cameraPosition).sqrMagnitude;
                if (distanceSq > OutlineRadiusMeters * OutlineRadiusMeters)
                    continue;

                GameObject root = ResolveOutlineRoot(renderer, target.Kind);
                if (root == null || !root.activeInHierarchy)
                    continue;

                nearRenderers++;
                UpsertCandidate(root, ToOutlineKind(target.Kind), distanceSq);
            }

            int shown = 0;
            foreach (OutlineCandidate candidate in _candidatesByRoot.Values)
            {
                if (candidate.Root == null)
                    continue;

                int id = candidate.Root.GetInstanceID();
                if (CctvOutlineEffectBridge.Show(camera, candidate.Root, ToChannel(candidate.Kind), OutlineRequestDurationSeconds))
                {
                    shown++;
                    _liveRootIds.Add(id);
                    _activeRoots[id] = candidate.Root;
                }
            }

            _rootsToClear.Clear();
            foreach (KeyValuePair<int, GameObject> pair in _activeRoots)
            {
                if (!_liveRootIds.Contains(pair.Key))
                    _rootsToClear.Add(pair.Key);
            }

            for (int i = 0; i < _rootsToClear.Count; i++)
            {
                int id = _rootsToClear[i];
                if (_activeRoots.TryGetValue(id, out GameObject root))
                    CctvOutlineEffectBridge.Clear(root);
                _activeRoots.Remove(id);
            }

            LogOutlineSummary(candidateRenderers, nearRenderers, shown);
            if (_activeRoots.Count == 0)
                CctvOutlineEffectBridge.DisableActiveEffect();
        }

        private static void UpsertCandidate(GameObject root, OutlineKind kind, float distanceSq)
        {
            int id = root.GetInstanceID();
            if (_candidatesByRoot.TryGetValue(id, out OutlineCandidate existing))
            {
                existing.RendererCount++;
                existing.NearestDistanceSq = Mathf.Min(existing.NearestDistanceSq, distanceSq);
                if (Priority(kind) > Priority(existing.Kind))
                    existing.Kind = kind;
                return;
            }

            _candidatesByRoot[id] = new OutlineCandidate
            {
                Root = root,
                Kind = kind,
                RendererCount = 1,
                NearestDistanceSq = distanceSq
            };
        }

        private static bool ShouldUseRealOutline(CctvTargetCache.TargetKind kind)
        {
            return kind == CctvTargetCache.TargetKind.Player
                   || kind == CctvTargetCache.TargetKind.Hostile
                   || kind == CctvTargetCache.TargetKind.Item;
        }

        private static GameObject ResolveOutlineRoot(Renderer renderer, CctvTargetCache.TargetKind kind)
        {
            if (renderer == null)
                return null;

            switch (kind)
            {
                case CctvTargetCache.TargetKind.Player:
                    return renderer.GetComponentInParent<PlayerControllerB>()?.gameObject;
                case CctvTargetCache.TargetKind.Hostile:
                    return renderer.GetComponentInParent<EnemyAI>()?.gameObject;
                case CctvTargetCache.TargetKind.Item:
                    return renderer.GetComponentInParent<GrabbableObject>()?.gameObject;
                default:
                    return null;
            }
        }

        private static OutlineKind ToOutlineKind(CctvTargetCache.TargetKind kind)
        {
            switch (kind)
            {
                case CctvTargetCache.TargetKind.Player: return OutlineKind.Player;
                case CctvTargetCache.TargetKind.Hostile: return OutlineKind.Hostile;
                default: return OutlineKind.Item;
            }
        }

        private static CctvOutlineChannel ToChannel(OutlineKind kind)
        {
            switch (kind)
            {
                case OutlineKind.Player: return CctvOutlineChannel.Player;
                case OutlineKind.Hostile: return CctvOutlineChannel.Hostile;
                default: return CctvOutlineChannel.Item;
            }
        }

        private static int Priority(OutlineKind kind)
        {
            switch (kind)
            {
                case OutlineKind.Hostile: return 3;
                case OutlineKind.Player: return 2;
                default: return 1;
            }
        }

        private static void LogOutlineSummary(int candidateRenderers, int nearRenderers, int shown)
        {
            if (!ShouldLogDiagnostics()) return;

            int players = 0;
            int hostiles = 0;
            int items = 0;
            foreach (OutlineCandidate candidate in _candidatesByRoot.Values)
            {
                switch (candidate.Kind)
                {
                    case OutlineKind.Player:
                        players++;
                        break;
                    case OutlineKind.Hostile:
                        hostiles++;
                        break;
                    default:
                        items++;
                        break;
                }
            }

            string summary = $"candidateRenderers={candidateRenderers} nearRenderers={nearRenderers} roots={_candidatesByRoot.Count} shown={shown} players={players} hostiles={hostiles} items={items}";
            if (summary == _lastOutlineSummary) return;
            _lastOutlineSummary = summary;
            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV OutlineEffect summary {summary}.");
        }

        private static void LogOutlineDiagnostic(string state)
        {
            if (!ShouldLogDiagnostics()) return;

            float now = Time.unscaledTime;
            if (_lastSkipState == state && now < _nextSkipLogAt) return;

            _lastSkipState = state;
            _nextSkipLogAt = now + 3f;
            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV OutlineEffect diagnostic {state}.");
        }

        private static bool ShouldLogDiagnostics()
        {
            return SurveillanceBootstrap.Config?.ReconLoggingEnabled?.Value == true;
        }

        private static string DescribeCamera(CCTVCamera camera)
        {
            if (camera == null) return "<null>";
            string label = !string.IsNullOrEmpty(camera.DisplayLabel) ? camera.DisplayLabel : camera.ResolvedLabel;
            Transform cameraTransform = camera.Cam != null ? camera.Cam.transform : camera.transform;
            Vector3 pos = cameraTransform != null ? cameraTransform.position : Vector3.zero;
            return $"{label}#{camera.CameraIndex}@{pos.x:0.0},{pos.y:0.0},{pos.z:0.0}";
        }
    }
}
