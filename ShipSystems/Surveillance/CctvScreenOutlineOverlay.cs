using System.Collections.Generic;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CctvScreenOutlineOverlay
    {
        private const float RefreshIntervalSeconds = 0.55f;
        private const float LayoutIntervalSeconds = 1f / 12f;
        private const float VisibilityCacheSeconds = 0.18f;
        private const float MaxTargetDistanceMeters = 60f;
        private const int MaxBoxes = 28;
        private const float MinBoxPixels = 18f;
        private const float BoxPaddingPixels = 8f;
        private const float LineThicknessPixels = 4.5f;
        private const float LineAlpha = 1.0f;
        private const float LineOfSightOriginOffset = 0.08f;
        private const float MinSampleExtentMeters = 0.12f;
        private const float LabelWidthPixels = 148f;
        private const float LabelHeightPixels = 18f;
        private const float LabelInsetPixels = 5f;
        private const int MaxLabelCharacters = 18;

        private static readonly Color ObjectiveColor = new Color(1.0f, 0.86f, 0.10f, 1f);
        private static readonly Color HostileColor = new Color(1.0f, 0.06f, 0.04f, 1f);
        private static readonly Color ItemColor = new Color(0.10f, 1.0f, 0.28f, 1f);
        private static readonly Color PlayerColor = Color.white;

        private static readonly List<TargetSnapshot> _targets = new List<TargetSnapshot>(160);
        private static readonly Dictionary<int, int> _targetIndexByRoot = new Dictionary<int, int>(160);
        private static readonly Dictionary<int, VisibilityCacheEntry> _visibilityBySourceId = new Dictionary<int, VisibilityCacheEntry>(160);
        private static readonly List<OutlineBox> _boxes = new List<OutlineBox>(MaxBoxes);
        private static readonly List<Renderer> _scratchRenderers = new List<Renderer>(16);

        private static RectTransform _root;
        private static Texture2D _scanlineTexture;
        private static Sprite _scanlineSprite;
        private static float _nextRefreshAt;
        private static float _nextLayoutAt;
        private static int _lastCameraId;
        private static int _cachedLineOfSightCameraId = int.MinValue;
        private static int _cachedLineOfSightMask;
        private static string _lastTargetSummary;
        private static string _lastVisibilitySummary;

        private enum TargetKind
        {
            Item,
            Hostile,
            Player,
            Objective,
        }

        private struct TargetSnapshot
        {
            public Bounds Bounds;
            public Transform Root;
            public TargetKind Kind;
            public int SourceId;
            public string Label;

            public TargetSnapshot(Bounds bounds, Transform root, TargetKind kind, int sourceId, string label)
            {
                Bounds = bounds;
                Root = root;
                Kind = kind;
                SourceId = sourceId;
                Label = label;
            }
        }

        private struct VisibilityCacheEntry
        {
            public bool Visible;
            public float ExpiresAt;

            public VisibilityCacheEntry(bool visible, float expiresAt)
            {
                Visible = visible;
                ExpiresAt = expiresAt;
            }
        }

        internal static void Initialize(Transform parent)
        {
            if (parent == null) return;
            if (_root != null)
            {
                AttachRoot(parent);
                return;
            }

            GameObject go = new GameObject("CCTV_TargetScreenOutlines");
            _root = go.AddComponent<RectTransform>();
            AttachRoot(parent);
            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] CCTV screen-space outline overlay initialized.");
        }

        internal static void Shutdown()
        {
            HideAll();
            _targets.Clear();
            _targetIndexByRoot.Clear();
            _visibilityBySourceId.Clear();
            CctvTargetCache.Clear();
            _lastTargetSummary = null;
            _lastVisibilitySummary = null;
            _lastCameraId = 0;
            _cachedLineOfSightCameraId = int.MinValue;
            _cachedLineOfSightMask = 0;

            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
                _root = null;
            }
            if (_scanlineTexture != null)
            {
                Object.Destroy(_scanlineTexture);
                _scanlineTexture = null;
                _scanlineSprite = null;
            }
        }

        internal static void Tick(bool focused)
        {
            if (_root == null || !focused || MonitorFocus.IsTurretPageActive)
            {
                HideAll();
                _nextLayoutAt = 0f;
                return;
            }

            if (Time.unscaledTime >= _nextRefreshAt)
            {
                _nextRefreshAt = Time.unscaledTime + RefreshIntervalSeconds;
                RefreshTargets();
            }

            int used = 0;
            int projected = 0;
            int occluded = 0;
            const int slot = 0;
            {
                CCTVCamera holder = QuadCameraAssignment.GetBoundCamera(slot);
                Camera camera = holder != null ? holder.Cam : null;
                if (camera == null)
                {
                    HideUnused(used);
                    MaybeLogVisibilitySummary(projected, used, occluded);
                    return;
                }
                int cameraId = camera.GetInstanceID();
                if (cameraId != _lastCameraId)
                {
                    _lastCameraId = cameraId;
                    _visibilityBySourceId.Clear();
                    _cachedLineOfSightCameraId = int.MinValue;
                    _cachedLineOfSightMask = 0;
                    _nextLayoutAt = 0f;
                }
                if (!TryGetTargetPaneGeometry(slot, out Vector2 paneCenter, out Vector2 paneSize))
                {
                    HideUnused(used);
                    MaybeLogVisibilitySummary(projected, used, occluded);
                    return;
                }
                if (Time.unscaledTime < _nextLayoutAt)
                    return;
                _nextLayoutAt = Time.unscaledTime + LayoutIntervalSeconds;

                Vector3 cameraPosition = camera.transform.position;
                for (int i = 0; i < _targets.Count && used < MaxBoxes; i++)
                {
                    TargetSnapshot target = _targets[i];
                    if ((target.Bounds.center - cameraPosition).sqrMagnitude > MaxTargetDistanceMeters * MaxTargetDistanceMeters)
                        continue;
                    if (!TryProjectBounds(camera, target.Bounds, out Rect viewportRect))
                        continue;
                    projected++;
                    if (!IsTargetVisibleToCameraCached(camera, target))
                    {
                        occluded++;
                        continue;
                    }

                    OutlineBox box = EnsureBox(used);
                    if (ApplyBox(box, paneCenter, paneSize, viewportRect, ColorForKind(target.Kind), target.Label))
                        used++;
                }
            }

            HideUnused(used);

            MaybeLogVisibilitySummary(projected, used, occluded);
        }

        private static void RefreshTargets()
        {
            _targets.Clear();
            _targetIndexByRoot.Clear();
            CctvTargetCache.RefreshIfDue();

            IReadOnlyList<CctvTargetCache.ScreenTarget> cachedTargets = CctvTargetCache.ScreenTargets;
            for (int i = 0; i < cachedTargets.Count; i++)
            {
                CctvTargetCache.ScreenTarget target = cachedTargets[i];
                if (!ShouldDrawScreenBox(target.Kind))
                    continue;
                if (target.TracksRoot && target.Root == null)
                    continue;

                _targets.Add(new TargetSnapshot(
                    target.ResolveCurrentBounds(),
                    target.Root,
                    ToTargetKind(target.Kind),
                    target.SourceId,
                    target.Label));
            }

            string summary = $"targets={_targets.Count}";
            if (ShouldLogDiagnostics() && summary != _lastTargetSummary)
            {
                _lastTargetSummary = summary;
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV screen-space outline summary {summary}.");
            }
        }

        private static void AttachRoot(Transform parent)
        {
            if (_root == null || parent == null)
                return;

            _root.SetParent(parent, worldPositionStays: false);
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;
            _root.localRotation = Quaternion.identity;
            _root.localScale = Vector3.one;
            SetLayerRecursive(_root.gameObject, parent.gameObject.layer);
            _root.SetAsLastSibling();
        }

        private static bool ShouldDrawScreenBox(CctvTargetCache.TargetKind kind)
        {
            return kind == CctvTargetCache.TargetKind.Objective;
        }

        private static bool TryGetTargetPaneGeometry(int slot, out Vector2 center, out Vector2 size)
        {
            if (CCTVVanillaMonitorDisplay.TryGetCctvPaneGeometry(out center, out size))
            {
                return true;
            }

            return MonitorFocus.TryGetOverlayPaneGeometry(slot, out center, out size);
        }

        private static TargetKind ToTargetKind(CctvTargetCache.TargetKind kind)
        {
            switch (kind)
            {
                case CctvTargetCache.TargetKind.Objective: return TargetKind.Objective;
                case CctvTargetCache.TargetKind.Player: return TargetKind.Player;
                case CctvTargetCache.TargetKind.Hostile: return TargetKind.Hostile;
                default: return TargetKind.Item;
            }
        }

        private static void CollectSystemTargets()
        {
            List<CctvCommandTargetBridge.TargetInfo> commandTargets = CctvCommandTargetBridge.GetActiveTargets();
            for (int i = 0; i < commandTargets.Count; i++)
            {
                CctvCommandTargetBridge.TargetInfo target = commandTargets[i];
                if (target == null || target.Component == null) continue;
                AddRootTarget(target.Component.gameObject, TargetKind.Objective, target.Position, target.Radius, target.DisplayName);
            }

            TryAddTypeTargets("MainframeSupport", TargetKind.Objective);
            TryAddRadarBoosterTargets();
        }

        private static void CollectEntranceTargets()
        {
            EntranceTeleport[] entrances = Object.FindObjectsOfType<EntranceTeleport>(includeInactive: false);
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null) continue;
                Vector3 position = entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
                AddRootTarget(entrance.gameObject, TargetKind.Objective, position, entrance.entranceId == 0 ? 1.25f : 0.95f,
                    entrance.entranceId == 0 ? "MAIN ENTRANCE" : "FIRE EXIT");
            }
        }

        private static void CollectContractObjectiveTargets()
        {
            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> markers = ContractObjectiveProvider.GetMarkers();
            for (int i = 0; i < markers.Count; i++)
            {
                ContractObjectiveProvider.MarkerSnapshot marker = markers[i];
                if (marker.IsComplete) continue;
                AddBoundsTarget(new Bounds(marker.Position, Vector3.one * Mathf.Max(0.7f, marker.Radius * 2f)), TargetKind.Objective, marker.Label);
            }
        }

        private static void CollectPlayerTargets()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || players.Length == 0)
                players = Object.FindObjectsOfType<PlayerControllerB>(includeInactive: false);

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || player.isPlayerDead) continue;
                if (!player.isPlayerControlled && !player.isHostPlayerObject) continue;
                string label = !string.IsNullOrWhiteSpace(player.playerUsername) ? player.playerUsername : "PLAYER";
                AddRootTarget(player.gameObject, TargetKind.Player, player.transform.position + Vector3.up, 0.85f, label);
            }
        }

        private static void CollectHostileTargets()
        {
            EnemyAI[] enemies = Object.FindObjectsOfType<EnemyAI>(includeInactive: false);
            for (int i = 0; i < enemies.Length; i++)
            {
                EnemyAI enemy = enemies[i];
                if (enemy == null || enemy.isEnemyDead) continue;
                if (enemy.isInsidePlayerShip || enemy.isOutside) continue;
                string label = enemy.enemyType != null && !string.IsNullOrWhiteSpace(enemy.enemyType.enemyName)
                    ? enemy.enemyType.enemyName
                    : enemy.GetType().Name;
                AddRootTarget(enemy.gameObject, TargetKind.Hostile, enemy.transform.position + Vector3.up, 1.0f, label);
            }
        }

        private static void CollectItemTargets()
        {
            GrabbableObject[] items = Object.FindObjectsOfType<GrabbableObject>(includeInactive: false);
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item == null || item.isHeldByEnemy) continue;
                if (item.itemProperties == null) continue;
                if (item.isInShipRoom || item.isInElevator) continue;

                TargetKind kind = IsRadarSystem(item.gameObject, item.GetType()) ? TargetKind.Objective : TargetKind.Item;
                string label = item.itemProperties != null && !string.IsNullOrWhiteSpace(item.itemProperties.itemName)
                    ? item.itemProperties.itemName
                    : item.gameObject.name;
                AddRootTarget(item.gameObject, kind, item.transform.position, 0.45f, kind == TargetKind.Objective ? "RADAR" : label);
            }
        }

        private static void TryAddTypeTargets(string typeName, TargetKind kind)
        {
            System.Type type = System.Type.GetType($"Y4NGZCompany.Facility.Mainframe.{typeName}, LethalCCTV", throwOnError: false);
            if (type == null) return;

            Object[] instances = Object.FindObjectsOfType(type);
            for (int i = 0; i < instances.Length; i++)
            {
                Component component = instances[i] as Component;
                if (component == null) continue;
                AddRootTarget(component.gameObject, kind, component.transform.position, 1.0f, CleanTypeLabel(typeName));
            }
        }

        private static void TryAddRadarBoosterTargets()
        {
            System.Type radarType = System.Type.GetType("RadarBoosterItem, Assembly-CSharp", throwOnError: false);
            if (radarType == null) return;

            Object[] boosters = Object.FindObjectsOfType(radarType);
            for (int i = 0; i < boosters.Length; i++)
            {
                Component booster = boosters[i] as Component;
                if (booster == null) continue;
                AddRootTarget(booster.gameObject, TargetKind.Objective, booster.transform.position, 0.9f, "RADAR");
            }
        }

        private static void AddRootTarget(GameObject root, TargetKind kind, Vector3 fallbackPosition, float fallbackRadius, string label = null)
        {
            if (root == null)
            {
                AddBoundsTarget(new Bounds(fallbackPosition, Vector3.one * Mathf.Max(0.4f, fallbackRadius * 2f)), kind, label);
                return;
            }

            Bounds bounds;
            if (!TryResolveRendererBounds(root, out bounds))
                bounds = new Bounds(fallbackPosition, Vector3.one * Mathf.Max(0.4f, fallbackRadius * 2f));

            int id = root.GetInstanceID();
            if (_targetIndexByRoot.TryGetValue(id, out int existingIndex))
            {
                TargetSnapshot existing = _targets[existingIndex];
                existing.Bounds.Encapsulate(bounds);
                if (Priority(kind) > Priority(existing.Kind))
                    existing.Kind = kind;
                if (string.IsNullOrEmpty(existing.Label))
                    existing.Label = NormalizeLabel(label, root.name, kind);
                _targets[existingIndex] = existing;
                return;
            }

            _targetIndexByRoot[id] = _targets.Count;
            _targets.Add(new TargetSnapshot(bounds, root.transform, kind, id, NormalizeLabel(label, root.name, kind)));
        }

        private static void AddBoundsTarget(Bounds bounds, TargetKind kind, string label = null)
        {
            _targets.Add(new TargetSnapshot(bounds, null, kind, -_targets.Count - 1, NormalizeLabel(label, null, kind)));
        }

        private static bool TryResolveRendererBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            _scratchRenderers.Clear();
            root.GetComponentsInChildren(includeInactive: true, _scratchRenderers);
            for (int i = 0; i < _scratchRenderers.Count; i++)
            {
                Renderer renderer = _scratchRenderers[i];
                if (!IsEligibleRenderer(renderer)) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return hasBounds;
        }

        private static bool TryProjectBounds(Camera camera, Bounds bounds, out Rect viewportRect)
        {
            viewportRect = default;
            if (camera == null) return false;

            Vector3 centerVp = camera.WorldToViewportPoint(bounds.center);
            if (centerVp.z <= camera.nearClipPlane) return false;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            int visibleCorners = 0;

            for (int xi = 0; xi < 2; xi++)
            {
                for (int yi = 0; yi < 2; yi++)
                {
                    for (int zi = 0; zi < 2; zi++)
                    {
                        Vector3 corner = new Vector3(xi == 0 ? min.x : max.x, yi == 0 ? min.y : max.y, zi == 0 ? min.z : max.z);
                        Vector3 vp = camera.WorldToViewportPoint(corner);
                        if (vp.z <= camera.nearClipPlane) continue;
                        minX = Mathf.Min(minX, vp.x);
                        maxX = Mathf.Max(maxX, vp.x);
                        minY = Mathf.Min(minY, vp.y);
                        maxY = Mathf.Max(maxY, vp.y);
                        visibleCorners++;
                    }
                }
            }

            if (visibleCorners == 0) return false;
            if (maxX < 0f || minX > 1f || maxY < 0f || minY > 1f) return false;

            minX = Mathf.Clamp01(minX);
            maxX = Mathf.Clamp01(maxX);
            minY = Mathf.Clamp01(minY);
            maxY = Mathf.Clamp01(maxY);
            if (maxX <= minX || maxY <= minY) return false;

            viewportRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private static bool IsTargetVisibleToCamera(Camera camera, TargetSnapshot target)
        {
            if (camera == null) return false;
            if (target.Bounds.size.sqrMagnitude <= 1e-6f) return false;

            Transform cameraTransform = camera.transform;
            Vector3 origin = cameraTransform.position + cameraTransform.forward * LineOfSightOriginOffset;
            Bounds bounds = target.Bounds;
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            extents.x = Mathf.Max(extents.x, MinSampleExtentMeters);
            extents.y = Mathf.Max(extents.y, MinSampleExtentMeters);
            extents.z = Mathf.Max(extents.z, MinSampleExtentMeters);

            if (HasClearCameraLine(camera, target, origin, center)) return true;
            if (HasClearCameraLine(camera, target, origin, center + new Vector3(extents.x, 0f, 0f))) return true;
            if (HasClearCameraLine(camera, target, origin, center - new Vector3(extents.x, 0f, 0f))) return true;
            if (HasClearCameraLine(camera, target, origin, center + new Vector3(0f, extents.y, 0f))) return true;
            if (HasClearCameraLine(camera, target, origin, center - new Vector3(0f, extents.y, 0f))) return true;
            if (HasClearCameraLine(camera, target, origin, center + new Vector3(0f, 0f, extents.z))) return true;
            if (HasClearCameraLine(camera, target, origin, center - new Vector3(0f, 0f, extents.z))) return true;
            return false;
        }

        private static bool IsTargetVisibleToCameraCached(Camera camera, TargetSnapshot target)
        {
            float now = Time.unscaledTime;
            if (_visibilityBySourceId.TryGetValue(target.SourceId, out VisibilityCacheEntry cached) && now < cached.ExpiresAt)
                return cached.Visible;

            bool visible = IsTargetVisibleToCamera(camera, target);
            _visibilityBySourceId[target.SourceId] = new VisibilityCacheEntry(visible, now + VisibilityCacheSeconds);
            return visible;
        }

        private static bool HasClearCameraLine(Camera camera, TargetSnapshot target, Vector3 origin, Vector3 point)
        {
            Vector3 viewport = camera.WorldToViewportPoint(point);
            if (viewport.z <= camera.nearClipPlane || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                return false;

            Vector3 delta = point - origin;
            float distance = delta.magnitude;
            if (distance <= 0.05f) return true;

            int mask = ResolveLineOfSightMask(camera);
            if (!Physics.Raycast(origin, delta / distance, out RaycastHit hit, distance, mask, QueryTriggerInteraction.Ignore))
                return true;

            return IsHitPartOfTarget(hit, target);
        }

        private static int ResolveLineOfSightMask(Camera camera)
        {
            int cameraId = camera != null ? camera.GetInstanceID() : 0;
            if (_cachedLineOfSightCameraId == cameraId && _cachedLineOfSightMask != 0)
                return _cachedLineOfSightMask;

            int mask = camera != null && camera.cullingMask != 0 ? camera.cullingMask : Physics.DefaultRaycastLayers;
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) mask &= ~(1 << uiLayer);
            int helmetLayer = LayerMask.NameToLayer("HelmetVisor");
            if (helmetLayer >= 0) mask &= ~(1 << helmetLayer);
            _cachedLineOfSightCameraId = cameraId;
            _cachedLineOfSightMask = mask;
            return mask;
        }

        private static bool IsHitPartOfTarget(RaycastHit hit, TargetSnapshot target)
        {
            Collider collider = hit.collider;
            Transform root = target.Root;
            if (collider == null || root == null) return false;

            Transform hitTransform = collider.transform;
            if (hitTransform == null) return false;
            return hitTransform == root || hitTransform.IsChildOf(root) || root.IsChildOf(hitTransform);
        }

        private static void MaybeLogVisibilitySummary(int projected, int visible, int occluded)
        {
            if (!ShouldLogDiagnostics()) return;

            string summary = $"projected={projected} visible={visible} occluded={occluded}";
            if (summary == _lastVisibilitySummary) return;
            _lastVisibilitySummary = summary;
            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV screen-space outline visibility {summary}.");
        }

        private static bool ShouldLogDiagnostics()
        {
            return SurveillanceBootstrap.Config?.ReconLoggingEnabled?.Value == true;
        }

        private static OutlineBox EnsureBox(int index)
        {
            while (_boxes.Count <= index && _boxes.Count < MaxBoxes)
                _boxes.Add(new OutlineBox(_root, EnsureScanlineSprite(), _boxes.Count));
            return _boxes[index];
        }

        private static bool ApplyBox(OutlineBox box, Vector2 paneCenter, Vector2 paneSize, Rect viewportRect, Color color, string label)
        {
            float width = Mathf.Max(MinBoxPixels, viewportRect.width * paneSize.x + BoxPaddingPixels * 2f);
            float height = Mathf.Max(MinBoxPixels, viewportRect.height * paneSize.y + BoxPaddingPixels * 2f);
            if (width > paneSize.x * 0.78f || height > paneSize.y * 0.78f)
            {
                box.SetActive(false);
                return false;
            }

            Vector2 center = new Vector2(
                paneCenter.x + (viewportRect.center.x - 0.5f) * paneSize.x,
                paneCenter.y + (viewportRect.center.y - 0.5f) * paneSize.y);

            box.Set(center, new Vector2(width, height), color, label);
            return true;
        }

        private static void HideUnused(int used)
        {
            for (int i = used; i < _boxes.Count; i++)
                _boxes[i].SetActive(false);
        }

        private static void HideAll()
        {
            for (int i = 0; i < _boxes.Count; i++)
                _boxes[i].SetActive(false);
        }

        private static bool IsEligibleRenderer(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled) return false;
            if (renderer is ParticleSystemRenderer) return false;
            if (renderer is LineRenderer) return false;
            if (renderer.gameObject == null) return false;
            if (renderer.GetComponentInParent<Canvas>() != null) return false;
            string name = renderer.gameObject.name ?? string.Empty;
            if (name.StartsWith("LethalCCTV_", System.StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static bool IsRadarSystem(GameObject root, System.Type type)
        {
            string typeName = type != null ? type.Name : string.Empty;
            string objectName = root != null ? root.name : string.Empty;
            return Contains(typeName, "Radar") || Contains(objectName, "Radar");
        }

        private static bool Contains(string text, string needle)
        {
            return !string.IsNullOrEmpty(text)
                   && text.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0;
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

        private static string CleanTypeLabel(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            string label = typeName.Replace("Support", string.Empty).Replace("System", string.Empty);
            return label;
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

        private static Color ColorForKind(TargetKind kind)
        {
            switch (kind)
            {
                case TargetKind.Objective: return ObjectiveColor;
                case TargetKind.Hostile: return HostileColor;
                case TargetKind.Player: return PlayerColor;
                default: return ItemColor;
            }
        }

        private static Sprite EnsureScanlineSprite()
        {
            if (_scanlineSprite != null) return _scanlineSprite;

            _scanlineTexture = new Texture2D(1, 8, TextureFormat.RGBA32, false)
            {
                name = "LethalCCTV_ScreenOutlineScanlines",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };
            for (int y = 0; y < 8; y++)
            {
                float alpha = (y == 1 || y == 5) ? 1.0f : (y == 2 || y == 6 ? 0.58f : 0.26f);
                _scanlineTexture.SetPixel(0, y, new Color(1f, 1f, 1f, alpha));
            }
            _scanlineTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            _scanlineSprite = Sprite.Create(_scanlineTexture, new Rect(0f, 0f, 1f, 8f), new Vector2(0.5f, 0.5f), 1f);
            return _scanlineSprite;
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            if (root == null) return;
            root.layer = layer;
            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; i++)
                SetLayerRecursive(transform.GetChild(i).gameObject, layer);
        }

        private sealed class OutlineBox
        {
            private readonly RectTransform _root;
            private readonly Image _fill;
            private readonly Image[] _lines;
            private readonly RectTransform _labelRoot;
            private readonly Image _labelBackground;
            private readonly TextMeshProUGUI _labelText;

            public OutlineBox(RectTransform parent, Sprite lineSprite, int index)
            {
                GameObject go = new GameObject($"CCTV_TargetOutline_{index:00}");
                go.transform.SetParent(parent, worldPositionStays: false);
                InheritLayer(go, parent);
                _root = go.AddComponent<RectTransform>();
                _root.anchorMin = new Vector2(0.5f, 0.5f);
                _root.anchorMax = new Vector2(0.5f, 0.5f);
                _root.pivot = new Vector2(0.5f, 0.5f);

                GameObject fillGo = new GameObject("ScanlineFill");
                fillGo.transform.SetParent(go.transform, worldPositionStays: false);
                InheritLayer(fillGo, go.transform);
                RectTransform fillRect = fillGo.AddComponent<RectTransform>();
                fillRect.anchorMin = Vector2.zero;
                fillRect.anchorMax = Vector2.one;
                fillRect.offsetMin = Vector2.zero;
                fillRect.offsetMax = Vector2.zero;
                _fill = fillGo.AddComponent<Image>();
                _fill.sprite = lineSprite;
                _fill.type = Image.Type.Tiled;
                _fill.raycastTarget = false;
                _fill.gameObject.SetActive(false);

                _lines = new Image[4];
                _lines[0] = CreateLine(go.transform, "Top", lineSprite, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 0f), new Vector2(0f, LineThicknessPixels));
                _lines[1] = CreateLine(go.transform, "Bottom", lineSprite, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(0f, LineThicknessPixels));
                _lines[2] = CreateLine(go.transform, "Left", lineSprite, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(LineThicknessPixels, 0f));
                _lines[3] = CreateLine(go.transform, "Right", lineSprite, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(0f, 0f), new Vector2(LineThicknessPixels, 0f));

                GameObject labelGo = new GameObject("TargetLabel");
                labelGo.transform.SetParent(go.transform, worldPositionStays: false);
                InheritLayer(labelGo, go.transform);
                _labelRoot = labelGo.AddComponent<RectTransform>();
                _labelRoot.anchorMin = new Vector2(0f, 1f);
                _labelRoot.anchorMax = new Vector2(0f, 1f);
                _labelRoot.pivot = new Vector2(0f, 1f);
                _labelRoot.anchoredPosition = new Vector2(LabelInsetPixels, -LabelInsetPixels);
                _labelRoot.sizeDelta = new Vector2(LabelWidthPixels, LabelHeightPixels);
                _labelBackground = labelGo.AddComponent<Image>();
                _labelBackground.color = new Color(0f, 0f, 0f, 0.58f);
                _labelBackground.raycastTarget = false;

                GameObject labelTextGo = new GameObject("Text");
                labelTextGo.transform.SetParent(labelGo.transform, worldPositionStays: false);
                InheritLayer(labelTextGo, labelGo.transform);
                RectTransform labelTextRect = labelTextGo.AddComponent<RectTransform>();
                labelTextRect.anchorMin = Vector2.zero;
                labelTextRect.anchorMax = Vector2.one;
                labelTextRect.offsetMin = new Vector2(4f, 0f);
                labelTextRect.offsetMax = new Vector2(-4f, 0f);
                _labelText = labelTextGo.AddComponent<TextMeshProUGUI>();
                _labelText.fontSize = 10.5f;
                _labelText.fontStyle = FontStyles.Bold;
                _labelText.alignment = TextAlignmentOptions.MidlineLeft;
                _labelText.enableWordWrapping = false;
                _labelText.overflowMode = TextOverflowModes.Overflow;
                _labelText.raycastTarget = false;

                SetActive(false);
            }

            public void Set(Vector2 position, Vector2 size, Color color, string label)
            {
                SetActive(true);
                _root.anchoredPosition = position;
                _root.sizeDelta = size;

                if (_fill != null)
                    _fill.color = Color.clear;

                Color line = color;
                line.a = LineAlpha;
                for (int i = 0; i < _lines.Length; i++)
                    _lines[i].color = line;

                if (_labelText != null)
                {
                    _labelText.text = label ?? string.Empty;
                    _labelText.color = line;
                }
                if (_labelBackground != null)
                    _labelBackground.enabled = !string.IsNullOrEmpty(label);
                if (_labelRoot != null)
                    _labelRoot.gameObject.SetActive(!string.IsNullOrEmpty(label));
            }

            public void SetActive(bool active)
            {
                if (_root != null && _root.gameObject.activeSelf != active)
                    _root.gameObject.SetActive(active);
            }

            private static Image CreateLine(Transform parent, string name, Sprite sprite, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 position, Vector2 size)
            {
                GameObject go = new GameObject(name);
                go.transform.SetParent(parent, worldPositionStays: false);
                InheritLayer(go, parent);
                RectTransform rect = go.AddComponent<RectTransform>();
                rect.anchorMin = anchorMin;
                rect.anchorMax = anchorMax;
                rect.pivot = pivot;
                rect.anchoredPosition = position;
                rect.sizeDelta = size;
                Image image = go.AddComponent<Image>();
                image.sprite = sprite;
                image.type = Image.Type.Tiled;
                image.preserveAspect = false;
                image.raycastTarget = false;
                return image;
            }

            private static void InheritLayer(GameObject go, Transform parent)
            {
                if (go != null && parent != null)
                    go.layer = parent.gameObject.layer;
            }
        }
    }
}
