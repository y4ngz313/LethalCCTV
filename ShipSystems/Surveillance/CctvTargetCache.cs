using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using Y4NGZCompany.Core.Compat;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CctvTargetCache
    {
        private const float FallbackRefreshIntervalSeconds = 4f;
        private const float StalePruneIntervalSeconds = 1f;
        private const int MaxLabelCharacters = 18;
        // Renderer resolution (GetComponentsInChildren + eligibility walks) over every
        // item/enemy/player in a level cost 60-142ms when done in one pass
        // (y4ngz313/Y4NGZCompany#279), so a rebuild processes at most this many roots
        // per RefreshIfDue call and publishes only when complete.
        private const int MaxRootsPerRefreshSlice = 32;
        // Even one-collector-per-pass left CollectItemTargets spiking 55-94ms
        // (#279): it paid a scene-wide FindObjectsOfType<GrabbableObject> AND the
        // per-item classification of every result in the same pass. The scan now
        // gets a pass to itself and classification runs this many items per pass.
        private const int MaxItemsPerRefreshSlice = 64;

        private static List<RendererTarget> _rendererTargets = new List<RendererTarget>(320);
        private static List<ScreenTarget> _screenTargets = new List<ScreenTarget>(180);
        private static List<GroundTarget> _groundTargets = new List<GroundTarget>(32);
        private static Dictionary<int, int> _rendererIndexById = new Dictionary<int, int>(320);
        private static Dictionary<int, int> _screenIndexByRoot = new Dictionary<int, int>(180);
        private static List<RendererTarget> _stagingRendererTargets = new List<RendererTarget>(320);
        private static List<ScreenTarget> _stagingScreenTargets = new List<ScreenTarget>(180);
        private static List<GroundTarget> _stagingGroundTargets = new List<GroundTarget>(32);
        private static Dictionary<int, int> _stagingRendererIndexById = new Dictionary<int, int>(320);
        private static Dictionary<int, int> _stagingScreenIndexByRoot = new Dictionary<int, int>(180);
        private static readonly List<Renderer> _scratchRenderers = new List<Renderer>(16);
        private static readonly List<ObjectiveMarkerInfo> _objectiveMarkers = new List<ObjectiveMarkerInfo>(32);
        private static readonly List<PendingRootWork> _pendingRoots = new List<PendingRootWork>(256);
        private static GrabbableObject[] _pendingItems;
        private static int _pendingItemIndex;
        private static int _pendingRootIndex;
        private static int _pendingCollectorIndex;
        private static bool _slicedRefreshActive;
        private static bool _writeToStaging;
        private static bool _enqueueRootTargets;
        private static bool _loggedSlicedRefresh;

        private static Type _objectiveApiType;
        private static MethodInfo _getActiveMarkersMethod;
        private static bool _objectiveApiResolveAttempted;
        private static float _nextRefreshAt;
        private static float _nextStalePruneAt;
        private static int _nextSyntheticSourceId = -1;

        internal enum TargetKind
        {
            Item,
            Hostile,
            Player,
            Objective,
        }

        internal readonly struct RendererTarget
        {
            internal readonly Renderer Renderer;
            internal readonly TargetKind Kind;

            internal RendererTarget(Renderer renderer, TargetKind kind)
            {
                Renderer = renderer;
                Kind = kind;
            }
        }

        internal readonly struct ScreenTarget
        {
            internal readonly Bounds Bounds;
            internal readonly Transform Root;
            internal readonly TargetKind Kind;
            internal readonly int SourceId;
            internal readonly string Label;
            internal readonly bool TracksRoot;
            private readonly Vector3 _rootPositionAtCapture;

            internal ScreenTarget(Bounds bounds, Transform root, TargetKind kind, int sourceId, string label)
            {
                Bounds = bounds;
                Root = root;
                Kind = kind;
                SourceId = sourceId;
                Label = label;
                TracksRoot = root != null;
                _rootPositionAtCapture = root != null ? root.position : Vector3.zero;
            }

            internal Bounds ResolveCurrentBounds()
            {
                if (!TracksRoot || Root == null)
                    return Bounds;

                Bounds current = Bounds;
                current.center += Root.position - _rootPositionAtCapture;
                return current;
            }
        }

        internal readonly struct GroundTarget
        {
            internal readonly Vector3 Position;
            internal readonly float Radius;
            internal readonly TargetKind Kind;

            internal GroundTarget(Vector3 position, float radius, TargetKind kind)
            {
                Position = position;
                Radius = Mathf.Clamp(radius, 0.35f, 5f);
                Kind = kind;
            }
        }

        private readonly struct PendingRootWork
        {
            internal readonly GameObject Root;
            internal readonly TargetKind Kind;
            internal readonly Vector3 FallbackPosition;
            internal readonly float FallbackRadius;
            internal readonly string Label;
            internal readonly bool AddGroundWhenNoRenderer;
            internal readonly bool TryParentForRenderers;

            internal PendingRootWork(
                GameObject root,
                TargetKind kind,
                Vector3 fallbackPosition,
                float fallbackRadius,
                string label,
                bool addGroundWhenNoRenderer,
                bool tryParentForRenderers)
            {
                Root = root;
                Kind = kind;
                FallbackPosition = fallbackPosition;
                FallbackRadius = fallbackRadius;
                Label = label;
                AddGroundWhenNoRenderer = addGroundWhenNoRenderer;
                TryParentForRenderers = tryParentForRenderers;
            }
        }

        private readonly struct ObjectiveMarkerInfo
        {
            internal readonly string Label;
            internal readonly Vector3 Position;
            internal readonly float Radius;
            internal readonly bool IsComplete;
            internal readonly Component Target;

            internal ObjectiveMarkerInfo(string label, Vector3 position, float radius, bool isComplete, Component target)
            {
                Label = string.IsNullOrWhiteSpace(label) ? "OBJECTIVE" : label;
                Position = position;
                Radius = Mathf.Clamp(radius, 0.35f, 5f);
                IsComplete = isComplete;
                Target = target;
            }
        }

        internal static IReadOnlyList<RendererTarget> RendererTargets => _rendererTargets;
        internal static IReadOnlyList<ScreenTarget> ScreenTargets => _screenTargets;
        internal static IReadOnlyList<GroundTarget> GroundTargets => _groundTargets;

        internal static void RefreshIfDue(bool force = false)
        {
            float now = Time.unscaledTime;
            if (_slicedRefreshActive)
            {
                AdvanceSlicedRefresh();
                return;
            }

            if (!force && now < _nextRefreshAt)
            {
                PruneDestroyedTargetsIfDue(now);
                return;
            }

            _nextRefreshAt = now + FallbackRefreshIntervalSeconds;
            _nextStalePruneAt = now + StalePruneIntervalSeconds;

            // The first fill used to run synchronously so the first focused frame
            // had targets, but that single pass cost 136-240ms at focus entry
            // (#279). Boxes appearing a moment after focus entry is cheaper than
            // the hitch, so every rebuild - including the first - is sliced and
            // consumers serve the existing (possibly empty) cache meanwhile.
            BeginSlicedRefresh();
            AdvanceSlicedRefresh();
        }

        internal static void Invalidate() => _nextRefreshAt = 0f;

        internal static void Clear()
        {
            CancelSlicedRefresh();
            _rendererTargets.Clear();
            _screenTargets.Clear();
            _groundTargets.Clear();
            _screenIndexByRoot.Clear();
            _objectiveMarkers.Clear();
            _rendererIndexById.Clear();
            _nextRefreshAt = 0f;
            _nextStalePruneAt = 0f;
            _nextSyntheticSourceId = -1;
        }

        // Collector order matters: CollectObjectiveMarkers fills _objectiveMarkers,
        // which the later collectors read for objective classification.
        // A collector returns true when it is finished; returning false keeps it at
        // the head of the queue so it resumes on the next pass (see CollectItemTargets).
        private static readonly Func<bool>[] SlicedCollectors =
        {
            CollectObjectiveMarkers,
            CollectSystemTargets,
            CollectEntranceTargets,
            CollectContractObjectiveTargets,
            CollectPlayerTargets,
            CollectHostileTargets,
            CollectItemTargets,
        };

        private static void BeginSlicedRefresh()
        {
            _stagingRendererTargets.Clear();
            _stagingScreenTargets.Clear();
            _stagingGroundTargets.Clear();
            _stagingRendererIndexById.Clear();
            _stagingScreenIndexByRoot.Clear();
            _pendingRoots.Clear();
            _pendingRootIndex = 0;
            _pendingCollectorIndex = 0;
            ClearPendingItemScan();
            _objectiveMarkers.Clear();
            _nextSyntheticSourceId = -1;
            _slicedRefreshActive = true;
        }

        private static void AdvanceSlicedRefresh()
        {
            if (!_slicedRefreshActive)
                return;

            // Running every collector in one pass cost 73-78ms (the scene-wide
            // FindObjectsOfType scans dominate), so each pass runs a single
            // collector; root renderer walks start only after the last one.
            if (_pendingCollectorIndex < SlicedCollectors.Length)
            {
                long collectStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                // A throwing collector leaves this true so it cannot wedge the rebuild.
                bool collectorComplete = true;
                _writeToStaging = true;
                _enqueueRootTargets = true;
                try
                {
                    collectorComplete = SlicedCollectors[_pendingCollectorIndex]();
                }
                finally
                {
                    _enqueueRootTargets = false;
                    _writeToStaging = false;
                    if (collectorComplete)
                        _pendingCollectorIndex++;
                }

                if (MonitorFocus.IsFocused)
                {
                    FocusPerfProbe.RecordStep(
                        "CctvTargetCache.RefreshCollect",
                        System.Diagnostics.Stopwatch.GetTimestamp() - collectStartedAt);
                }

                if (_pendingCollectorIndex >= SlicedCollectors.Length && !_loggedSlicedRefresh)
                {
                    _loggedSlicedRefresh = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV.Timing] target cache rebuild is sliced: one collector per pass " +
                        $"(items at {MaxItemsPerRefreshSlice} per pass), then " +
                        $"{_pendingRoots.Count} root(s) at {MaxRootsPerRefreshSlice} per pass (#279).");
                }
                return;
            }

            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _writeToStaging = true;
            try
            {
                int end = Mathf.Min(_pendingRootIndex + MaxRootsPerRefreshSlice, _pendingRoots.Count);
                for (; _pendingRootIndex < end; _pendingRootIndex++)
                {
                    PendingRootWork work = _pendingRoots[_pendingRootIndex];
                    AddRootTarget(
                        work.Root,
                        work.Kind,
                        work.FallbackPosition,
                        work.FallbackRadius,
                        work.Label,
                        work.AddGroundWhenNoRenderer,
                        work.TryParentForRenderers);
                }
            }
            finally
            {
                _writeToStaging = false;
            }

            if (_pendingRootIndex >= _pendingRoots.Count)
                CommitSlicedRefresh();

            if (MonitorFocus.IsFocused)
            {
                FocusPerfProbe.RecordStep(
                    "CctvTargetCache.RefreshSlice",
                    System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
            }
        }

        private static void CommitSlicedRefresh()
        {
            (_rendererTargets, _stagingRendererTargets) = (_stagingRendererTargets, _rendererTargets);
            (_screenTargets, _stagingScreenTargets) = (_stagingScreenTargets, _screenTargets);
            (_groundTargets, _stagingGroundTargets) = (_stagingGroundTargets, _groundTargets);
            (_rendererIndexById, _stagingRendererIndexById) = (_stagingRendererIndexById, _rendererIndexById);
            (_screenIndexByRoot, _stagingScreenIndexByRoot) = (_stagingScreenIndexByRoot, _screenIndexByRoot);

            _stagingRendererTargets.Clear();
            _stagingScreenTargets.Clear();
            _stagingGroundTargets.Clear();
            _stagingRendererIndexById.Clear();
            _stagingScreenIndexByRoot.Clear();
            _pendingRoots.Clear();
            _pendingRootIndex = 0;
            _pendingCollectorIndex = 0;
            ClearPendingItemScan();
            _slicedRefreshActive = false;
        }

        private static void CancelSlicedRefresh()
        {
            _stagingRendererTargets.Clear();
            _stagingScreenTargets.Clear();
            _stagingGroundTargets.Clear();
            _stagingRendererIndexById.Clear();
            _stagingScreenIndexByRoot.Clear();
            _pendingRoots.Clear();
            _pendingRootIndex = 0;
            _pendingCollectorIndex = 0;
            ClearPendingItemScan();
            _slicedRefreshActive = false;
            _writeToStaging = false;
            _enqueueRootTargets = false;
        }

        // The scan snapshot keeps managed references to every grabbable in the
        // level, so it is released as soon as the rebuild that took it is done.
        private static void ClearPendingItemScan()
        {
            _pendingItems = null;
            _pendingItemIndex = 0;
        }

        private static bool CollectSystemTargets()
        {
            List<CctvCommandTargetBridge.TargetInfo> commandTargets = CctvCommandTargetBridge.GetActiveTargets();
            for (int i = 0; i < commandTargets.Count; i++)
            {
                CctvCommandTargetBridge.TargetInfo target = commandTargets[i];
                if (target == null || target.Component == null) continue;
                AddRootTarget(
                    target.Component.gameObject,
                    TargetKind.Objective,
                    target.Position,
                    target.Radius,
                    target.DisplayName,
                    addGroundWhenNoRenderer: true,
                    tryParentForRenderers: true);
            }

            TryAddTypeTargets("MainframeSupport", TargetKind.Objective);
            TryAddTypeTargets("CompanyStashController", TargetKind.Objective, "COMPANY STASH");
            TryAddApparatusTargets();
            TryAddRadarBoosterTargets();
            return true;
        }

        private static bool CollectEntranceTargets()
        {
            EntranceTeleport[] entrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>(includeInactive: false);
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null) continue;
                if (entrance.entranceId == 0 && !ShowMainEntranceTrackingBox())
                    continue;
                if (entrance.entranceId != 0 && !ShowFireExitTrackingBoxes())
                    continue;
                Vector3 position = entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
                AddRootTarget(
                    entrance.gameObject,
                    TargetKind.Objective,
                    position,
                    entrance.entranceId == 0 ? 1.25f : 0.95f,
                    entrance.entranceId == 0 ? "MAIN ENTRANCE" : "FIRE EXIT",
                    addGroundWhenNoRenderer: true);
            }

            return true;
        }

        private static bool CollectContractObjectiveTargets()
        {
            for (int i = 0; i < _objectiveMarkers.Count; i++)
            {
                ObjectiveMarkerInfo marker = _objectiveMarkers[i];
                if (marker.IsComplete) continue;
                if (!ShowApparatusTrackingBox() && IsApparatusMarker(marker))
                    continue;

                if (marker.Target != null)
                {
                    AddRootTarget(
                        marker.Target.gameObject,
                        TargetKind.Objective,
                        marker.Position,
                        marker.Radius,
                        marker.Label,
                        addGroundWhenNoRenderer: true,
                        tryParentForRenderers: true);
                }
                else
                {
                    AddBoundsTarget(
                        new Bounds(marker.Position, Vector3.one * Mathf.Max(0.7f, marker.Radius * 2f)),
                        TargetKind.Objective,
                        marker.Label);
                    AddGroundTarget(marker.Position, marker.Radius, TargetKind.Objective);
                }
            }

            return true;
        }

        private static bool CollectPlayerTargets()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || players.Length == 0)
                players = UnityEngine.Object.FindObjectsOfType<PlayerControllerB>(includeInactive: false);

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || player.isPlayerDead) continue;
                if (!player.isPlayerControlled && !player.isHostPlayerObject) continue;
                if (player.gameObject == null || !player.gameObject.activeInHierarchy) continue;
                string label = !string.IsNullOrWhiteSpace(player.playerUsername) ? player.playerUsername : "PLAYER";
                AddRootTarget(player.gameObject, TargetKind.Player, player.transform.position + Vector3.up, 0.85f, label);
            }

            return true;
        }

        private static bool CollectHostileTargets()
        {
            List<EnemyAI> spawnedEnemies = RoundManager.Instance?.SpawnedEnemies;
            if (spawnedEnemies != null && spawnedEnemies.Count > 0)
            {
                for (int i = 0; i < spawnedEnemies.Count; i++)
                    AddHostileTarget(spawnedEnemies[i]);
                return true;
            }

            EnemyAI[] enemies = UnityEngine.Object.FindObjectsOfType<EnemyAI>(includeInactive: false);
            for (int i = 0; i < enemies.Length; i++)
                AddHostileTarget(enemies[i]);
            return true;
        }

        private static void AddHostileTarget(EnemyAI enemy)
        {
            if (enemy == null || enemy.isEnemyDead) return;
            if (enemy.isInsidePlayerShip || enemy.isOutside) return;
            if (enemy.gameObject == null || !enemy.gameObject.activeInHierarchy) return;
            TargetKind kind = IsObjectiveTarget(enemy, enemy.transform.position) ? TargetKind.Objective : TargetKind.Hostile;
            string label = enemy.enemyType != null && !string.IsNullOrWhiteSpace(enemy.enemyType.enemyName)
                ? enemy.enemyType.enemyName
                : enemy.GetType().Name;
            AddRootTarget(enemy.gameObject, kind, enemy.transform.position + Vector3.up, 1.0f, label);
        }

        // Sliced in two stages: one pass takes the scene-wide snapshot and stops,
        // then later passes classify MaxItemsPerRefreshSlice entries each. The
        // snapshot ages by at most (count / MaxItemsPerRefreshSlice) passes, which
        // is the same order of staleness the one-collector-per-pass rebuild and the
        // MaxRootsPerRefreshSlice renderer walks already accept; results converge to
        // the same set at commit, and the 1s stale prune still drops dead targets.
        private static bool CollectItemTargets()
        {
            if (_pendingItems == null)
            {
                _pendingItems = UnityEngine.Object.FindObjectsOfType<GrabbableObject>(includeInactive: false);
                _pendingItemIndex = 0;
                if (_pendingItems.Length > 0)
                    return false;
            }

            int end = Mathf.Min(_pendingItemIndex + MaxItemsPerRefreshSlice, _pendingItems.Length);
            for (; _pendingItemIndex < end; _pendingItemIndex++)
            {
                GrabbableObject item = _pendingItems[_pendingItemIndex];
                // Items can be picked up, sold or destroyed between the snapshot and
                // the pass that classifies them, so re-check the Unity object here.
                if (item == null || item.gameObject == null) continue;
                if (item.isHeldByEnemy) continue;
                if (item.itemProperties == null) continue;
                if (item.isInShipRoom || item.isInElevator) continue;
                if (!item.isInFactory && !item.isHeld && !item.gameObject.activeInHierarchy) continue;

                bool isApparatus = item is LungProp;
                if (isApparatus && !ShowApparatusTrackingBox())
                    continue;
                bool objective = isApparatus
                    || IsObjectiveTarget(item, item.transform.position)
                    || IsRadarSystem(item.gameObject, item.GetType());
                TargetKind kind = objective ? TargetKind.Objective : TargetKind.Item;
                string label = item.itemProperties != null && !string.IsNullOrWhiteSpace(item.itemProperties.itemName)
                    ? item.itemProperties.itemName
                    : item.gameObject.name;
                AddRootTarget(item.gameObject, kind, item.transform.position, 0.45f, objective ? "RADAR" : label);
            }

            if (_pendingItemIndex < _pendingItems.Length)
                return false;

            ClearPendingItemScan();
            return true;
        }

        private static void TryAddTypeTargets(string typeName, TargetKind kind, string displayLabel = null)
        {
            Type type = ResolveLguContractHudType(typeName);
            if (type == null) return;

            UnityEngine.Object[] instances = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < instances.Length; i++)
            {
                Component component = instances[i] as Component;
                if (component == null) continue;
                AddRootTarget(
                    component.gameObject,
                    kind,
                    component.transform.position,
                    1.0f,
                    displayLabel ?? CleanTypeLabel(typeName),
                    addGroundWhenNoRenderer: true);
            }
        }

        private static Type ResolveLguContractHudType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            return Type.GetType($"Y4NGZCompany.Facility.Mainframe.{typeName}, LethalCCTV", throwOnError: false)
                   ?? Type.GetType($"Y4NGZCompany.Facility.Stash.{typeName}, LethalCCTV", throwOnError: false)
                   ?? Type.GetType($"LGUContractHUD.{typeName}, LGUContractHUD", throwOnError: false);
        }

        private static void TryAddApparatusTargets()
        {
            if (!ShowApparatusTrackingBox())
                return;

            LungProp[] apparatuses = UnityEngine.Object.FindObjectsByType<LungProp>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < apparatuses.Length; i++)
            {
                LungProp apparatus = apparatuses[i];
                if (apparatus == null || apparatus.gameObject == null)
                    continue;
                if (!apparatus.gameObject.activeInHierarchy)
                    continue;
                if (apparatus.isInShipRoom || apparatus.isInElevator)
                    continue;

                AddRootTarget(
                    apparatus.gameObject,
                    TargetKind.Objective,
                    apparatus.transform.position,
                    0.85f,
                    "APPARATUS",
                    addGroundWhenNoRenderer: true,
                    tryParentForRenderers: false);
            }
        }

        private static bool ShowMainEntranceTrackingBox()
        {
            return SurveillanceBootstrap.Config?.ShowMainEntranceTrackingBox?.Value ?? true;
        }

        private static bool ShowFireExitTrackingBoxes()
        {
            return SurveillanceBootstrap.Config?.ShowFireExitTrackingBoxes?.Value ?? true;
        }

        private static bool ShowApparatusTrackingBox()
        {
            return SurveillanceBootstrap.Config?.ShowApparatusTrackingBox?.Value ?? true;
        }

        private static bool IsApparatus(Component component)
        {
            if (component == null)
                return false;
            return component is LungProp
                || component.GetComponentInParent<LungProp>() != null
                || component.GetComponentInChildren<LungProp>(includeInactive: true) != null;
        }

        private static bool IsApparatusMarker(ObjectiveMarkerInfo marker)
        {
            return IsApparatus(marker.Target)
                || (!string.IsNullOrWhiteSpace(marker.Label)
                    && marker.Label.IndexOf("apparatus", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void TryAddRadarBoosterTargets()
        {
            Type radarType = Type.GetType("RadarBoosterItem, Assembly-CSharp", throwOnError: false);
            if (radarType == null) return;

            UnityEngine.Object[] boosters = UnityEngine.Object.FindObjectsOfType(radarType);
            for (int i = 0; i < boosters.Length; i++)
            {
                Component booster = boosters[i] as Component;
                if (booster == null) continue;
                AddRootTarget(booster.gameObject, TargetKind.Objective, booster.transform.position, 0.9f, "RADAR", addGroundWhenNoRenderer: true);
            }
        }

        private static List<RendererTarget> BuildRendererTargets => _writeToStaging ? _stagingRendererTargets : _rendererTargets;
        private static List<ScreenTarget> BuildScreenTargets => _writeToStaging ? _stagingScreenTargets : _screenTargets;
        private static List<GroundTarget> BuildGroundTargets => _writeToStaging ? _stagingGroundTargets : _groundTargets;
        private static Dictionary<int, int> BuildRendererIndexById => _writeToStaging ? _stagingRendererIndexById : _rendererIndexById;
        private static Dictionary<int, int> BuildScreenIndexByRoot => _writeToStaging ? _stagingScreenIndexByRoot : _screenIndexByRoot;

        private static void AddRootTarget(
            GameObject root,
            TargetKind kind,
            Vector3 fallbackPosition,
            float fallbackRadius,
            string label = null,
            bool addGroundWhenNoRenderer = false,
            bool tryParentForRenderers = false)
        {
            if (_enqueueRootTargets && root != null)
            {
                _pendingRoots.Add(new PendingRootWork(
                    root, kind, fallbackPosition, fallbackRadius, label,
                    addGroundWhenNoRenderer, tryParentForRenderers));
                return;
            }

            if (root == null)
            {
                AddBoundsTarget(new Bounds(fallbackPosition, Vector3.one * Mathf.Max(0.4f, fallbackRadius * 2f)), kind, label);
                if (addGroundWhenNoRenderer)
                    AddGroundTarget(fallbackPosition, fallbackRadius, kind);
                return;
            }

            int rendererCount = AddRenderersAndResolveBounds(root, kind, out Bounds bounds, out Transform boundsRoot);
            if (rendererCount == 0 && tryParentForRenderers && root.transform != null && root.transform.parent != null)
                rendererCount = AddRenderersAndResolveBounds(root.transform.parent.gameObject, kind, out bounds, out boundsRoot);

            Transform screenRoot = rendererCount > 0 && boundsRoot != null ? boundsRoot : root.transform;
            Bounds screenBounds = rendererCount > 0
                ? bounds
                : new Bounds(fallbackPosition, Vector3.one * Mathf.Max(0.4f, fallbackRadius * 2f));
            AddScreenRootTarget(screenRoot, screenBounds, kind, label, root.name);

            if (rendererCount == 0 && addGroundWhenNoRenderer)
                AddGroundTarget(fallbackPosition, fallbackRadius, kind);
        }

        private static int AddRenderersAndResolveBounds(GameObject root, TargetKind kind, out Bounds bounds, out Transform boundsRoot)
        {
            bounds = default;
            boundsRoot = root != null ? root.transform : null;
            bool hasBounds = false;
            int added = 0;
            if (root == null) return 0;

            _scratchRenderers.Clear();
            root.GetComponentsInChildren(includeInactive: true, _scratchRenderers);
            for (int i = 0; i < _scratchRenderers.Count; i++)
            {
                Renderer renderer = _scratchRenderers[i];
                if (!IsEligibleRenderer(renderer)) continue;
                AddRendererTarget(renderer, kind);
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
                added++;
            }
            return added;
        }

        private static void AddRendererTarget(Renderer renderer, TargetKind kind)
        {
            if (renderer == null) return;
            List<RendererTarget> targets = BuildRendererTargets;
            Dictionary<int, int> indexById = BuildRendererIndexById;
            int id = renderer.GetInstanceID();
            if (indexById.TryGetValue(id, out int existingIndex))
            {
                RendererTarget target = targets[existingIndex];
                if (Priority(kind) > Priority(target.Kind))
                    targets[existingIndex] = new RendererTarget(renderer, kind);
                return;
            }

            indexById[id] = targets.Count;
            targets.Add(new RendererTarget(renderer, kind));
        }

        private static void AddScreenRootTarget(Transform root, Bounds bounds, TargetKind kind, string label, string fallbackName)
        {
            List<ScreenTarget> targets = BuildScreenTargets;
            Dictionary<int, int> indexByRoot = BuildScreenIndexByRoot;
            int id = root != null ? root.gameObject.GetInstanceID() : _nextSyntheticSourceId--;
            string normalized = NormalizeLabel(label, fallbackName, kind);
            if (indexByRoot.TryGetValue(id, out int existingIndex))
            {
                ScreenTarget existing = targets[existingIndex];
                Bounds mergedBounds = existing.Bounds;
                mergedBounds.Encapsulate(bounds);
                existing = new ScreenTarget(mergedBounds, existing.Root, existing.Kind, id, existing.Label);
                if (Priority(kind) > Priority(existing.Kind))
                    existing = new ScreenTarget(mergedBounds, existing.Root, kind, id, existing.Label);
                if (string.IsNullOrEmpty(existing.Label))
                    existing = new ScreenTarget(mergedBounds, existing.Root, existing.Kind, id, normalized);
                targets[existingIndex] = existing;
                return;
            }

            indexByRoot[id] = targets.Count;
            targets.Add(new ScreenTarget(bounds, root, kind, id, normalized));
        }

        private static void AddBoundsTarget(Bounds bounds, TargetKind kind, string label = null)
        {
            int id = _nextSyntheticSourceId--;
            BuildScreenTargets.Add(new ScreenTarget(bounds, null, kind, id, NormalizeLabel(label, null, kind)));
        }

        private static void AddGroundTarget(Vector3 position, float radius, TargetKind kind)
        {
            BuildGroundTargets.Add(new GroundTarget(position, radius, kind));
        }

        private static bool CollectObjectiveMarkers()
        {
            if (!TryResolveObjectiveApi()) return true;

            try
            {
                object result = _getActiveMarkersMethod.Invoke(null, null);
                if (!(result is System.Collections.IEnumerable enumerable)) return true;

                foreach (object marker in enumerable)
                {
                    if (marker == null) continue;
                    bool complete = ReadMemberBool(marker, "IsComplete", false);
                    Vector3 position = ReadMemberVector3(marker, "Position", Vector3.zero);
                    if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z)) continue;

                    _objectiveMarkers.Add(new ObjectiveMarkerInfo(
                        ReadMemberString(marker, "Label", "OBJECTIVE"),
                        position,
                        ReadMemberFloat(marker, "Radius", 1.0f),
                        complete,
                        ReadMemberComponent(marker, "Target")));
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV target cache marker read failed: {ex.Message}");
            }

            return true;
        }

        private static bool TryResolveObjectiveApi()
        {
            if (_getActiveMarkersMethod != null)
                return true;

            // One attempt, memoized: Contracted is a soft BepInEx dependency of this
            // plugin, so its absence is already final here. Re-probing on a timer cost a
            // Mono assembly-load probe every retry on a standalone install (#592).
            if (_objectiveApiResolveAttempted)
                return false;
            _objectiveApiResolveAttempted = true;

            _objectiveApiType = CompanyAssemblyBridge.ResolveContractedType("Y4NGZCompany.Contracts._Shared.MoonContractObjectiveMarkerApi");
            _getActiveMarkersMethod = _objectiveApiType?.GetMethod("GetActiveMarkers", BindingFlags.Public | BindingFlags.Static);
            return _getActiveMarkersMethod != null;
        }

        private static bool IsObjectiveTarget(Component component, Vector3 position)
        {
            if (component == null) return IsNearObjectiveMarker(position);
            for (int i = 0; i < _objectiveMarkers.Count; i++)
            {
                ObjectiveMarkerInfo marker = _objectiveMarkers[i];
                if (marker.IsComplete) continue;
                if (marker.Target != null)
                {
                    if (ReferenceEquals(marker.Target, component)) return true;
                    if (component.transform != null && marker.Target.transform != null)
                    {
                        if (component.transform.IsChildOf(marker.Target.transform) || marker.Target.transform.IsChildOf(component.transform))
                            return true;
                    }
                }

                float radius = Mathf.Max(marker.Radius, 0.8f) + 1.0f;
                if ((position - marker.Position).sqrMagnitude <= radius * radius)
                    return true;
            }
            return false;
        }

        private static bool IsNearObjectiveMarker(Vector3 position)
        {
            for (int i = 0; i < _objectiveMarkers.Count; i++)
            {
                ObjectiveMarkerInfo marker = _objectiveMarkers[i];
                if (marker.IsComplete) continue;
                float radius = Mathf.Max(marker.Radius, 0.8f) + 1.0f;
                if ((position - marker.Position).sqrMagnitude <= radius * radius)
                    return true;
            }
            return false;
        }

        private static void PruneDestroyedTargetsIfDue(float now)
        {
            if (now < _nextStalePruneAt)
                return;

            _nextStalePruneAt = now + StalePruneIntervalSeconds;
            bool rendererIndexChanged = false;
            for (int i = _rendererTargets.Count - 1; i >= 0; i--)
            {
                if (_rendererTargets[i].Renderer != null)
                    continue;
                _rendererTargets.RemoveAt(i);
                rendererIndexChanged = true;
            }

            if (rendererIndexChanged)
            {
                _rendererIndexById.Clear();
                for (int i = 0; i < _rendererTargets.Count; i++)
                {
                    Renderer renderer = _rendererTargets[i].Renderer;
                    if (renderer != null)
                        _rendererIndexById[renderer.GetInstanceID()] = i;
                }
            }

            bool screenIndexChanged = false;
            for (int i = _screenTargets.Count - 1; i >= 0; i--)
            {
                ScreenTarget target = _screenTargets[i];
                if (!target.TracksRoot || target.Root != null)
                    continue;
                _screenTargets.RemoveAt(i);
                screenIndexChanged = true;
            }

            if (screenIndexChanged)
            {
                _screenIndexByRoot.Clear();
                for (int i = 0; i < _screenTargets.Count; i++)
                {
                    ScreenTarget target = _screenTargets[i];
                    if (target.TracksRoot && target.Root != null)
                        _screenIndexByRoot[target.SourceId] = i;
                }
            }
        }

        private static bool IsEligibleRenderer(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled) return false;
            if (renderer is ParticleSystemRenderer) return false;
            if (renderer is LineRenderer) return false;
            if (renderer.gameObject == null) return false;
            string name = renderer.gameObject.name ?? string.Empty;
            if (name.StartsWith("LethalCCTVCamera_", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.StartsWith("LethalCCTV_", StringComparison.OrdinalIgnoreCase)) return false;
            if (renderer.GetComponentInParent<Canvas>() != null) return false;

            if (renderer is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh != null;

            if (renderer is MeshRenderer)
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                return filter != null && filter.sharedMesh != null;
            }

            return false;
        }

        private static bool IsRadarSystem(GameObject root, Type type)
        {
            string typeName = type != null ? type.Name : string.Empty;
            string objectName = root != null ? root.name : string.Empty;
            return Contains(typeName, "Radar") || Contains(objectName, "Radar");
        }

        private static string CleanTypeLabel(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            return typeName.Replace("Support", string.Empty).Replace("System", string.Empty);
        }

        private static string NormalizeLabel(string label, string fallbackName, TargetKind kind)
        {
            string value = !string.IsNullOrWhiteSpace(label) ? label : fallbackName;
            if (string.IsNullOrWhiteSpace(value))
            {
                switch (kind)
                {
                    case TargetKind.Objective: value = "OBJECTIVE"; break;
                    case TargetKind.Player: value = "PLAYER"; break;
                    case TargetKind.Hostile: value = "HOSTILE"; break;
                    default: value = "ITEM"; break;
                }
            }

            value = value.Replace("(Clone)", string.Empty)
                .Replace("_", " ")
                .Replace("-", " ")
                .Trim();
            value = CollapseSpaces(value).ToUpperInvariant();
            if (value.Length > MaxLabelCharacters)
                value = value.Substring(0, MaxLabelCharacters);
            return value;
        }

        private static string CollapseSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            System.Text.StringBuilder builder = null;
            bool wasSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool isSpace = char.IsWhiteSpace(c);
                if (isSpace && wasSpace) continue;
                wasSpace = isSpace;
                if (builder != null)
                    builder.Append(isSpace ? ' ' : c);
                else if (isSpace && c != ' ')
                {
                    builder = new System.Text.StringBuilder(text.Length);
                    builder.Append(text, 0, i);
                    builder.Append(' ');
                }
            }
            return builder != null ? builder.ToString() : text;
        }

        private static int Priority(TargetKind kind)
        {
            switch (kind)
            {
                case TargetKind.Objective: return 4;
                case TargetKind.Player: return 3;
                case TargetKind.Hostile: return 2;
                default: return 1;
            }
        }

        private static bool Contains(string text, string needle)
        {
            return !string.IsNullOrEmpty(text)
                   && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadMemberString(object target, string name, string fallback)
        {
            return ReadMember(target, name, fallback);
        }

        private static float ReadMemberFloat(object target, string name, float fallback)
        {
            return ReadMember(target, name, fallback);
        }

        private static bool ReadMemberBool(object target, string name, bool fallback)
        {
            return ReadMember(target, name, fallback);
        }

        private static Vector3 ReadMemberVector3(object target, string name, Vector3 fallback)
        {
            return ReadMember(target, name, fallback);
        }

        private static Component ReadMemberComponent(object target, string name)
        {
            return ReadMember<Component>(target, name, null);
        }

        private static T ReadMember<T>(object target, string name, T fallback)
        {
            if (target == null)
                return fallback;

            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object value = property != null
                    ? property.GetValue(target)
                    : type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

                if (value == null)
                    return fallback;
                if (value is T typed)
                    return typed;

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return fallback;
            }
        }
    }
}
