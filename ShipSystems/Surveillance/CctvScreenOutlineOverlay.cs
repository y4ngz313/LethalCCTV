using System.Collections.Generic;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Bootstrap;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CctvScreenOutlineOverlay
    {
        private const int MaxBoxes = 28;
        private const float LineOfSightOriginOffset = 0.08f;
        private const float MinSampleExtentMeters = 0.03f;
        private static RectTransform _root;
        private static readonly Dictionary<long, Track> Tracks = new Dictionary<long, Track>();
        private static readonly List<long> Expired = new List<long>();
        private static readonly Stack<OutlineBox> Pool = new Stack<OutlineBox>();
        private static int _lastCameraId;
        private static int _cachedLineOfSightCameraId;
        private static int _cachedLineOfSightMask;
        private static float _lastTickAt;
        private static Quaternion _lastCameraRotation;
        private static Vector3 _lastCameraPosition;
        internal static bool IsFading { get; private set; }
        private struct TargetSnapshot { internal Bounds Bounds; internal Transform Root; }
        private sealed class Track
        {
            internal OutlineBox Box;
            internal float Alpha, VisibilityUntil;
            internal bool Acquired, Visible, Seen;
            internal Rect LastRect;
            internal Vector3 LastWorld;
            internal Vector3 VisibilityWorld;
            internal CctvTargetCache.ScreenTarget Target;
        }

        internal static void Initialize(Transform parent)
        {
            if (parent == null) return;
            if (_root == null)
            {
                // A monitor rebind can destroy the old canvas underneath us.
                Tracks.Clear();
                Pool.Clear();
                _lastCameraId = 0;
                IsFading = false;
                _root = new GameObject("CCTV_MachineVision", typeof(RectTransform)).GetComponent<RectTransform>();
            }
            _root.SetParent(parent, false);
            _root.gameObject.layer = parent.gameObject.layer;
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = _root.offsetMax = Vector2.zero;
            _root.localScale = Vector3.one;
            _root.localRotation = Quaternion.identity;
        }

        internal static void Shutdown()
        {
            Clear();
            Pool.Clear();
            if (_root != null) Object.Destroy(_root.gameObject);
            _root = null;
            CctvTargetCache.Clear();
        }

        private static void Clear()
        {
            if (Tracks.Count > 0) CCTVVanillaMonitorDisplay.InvalidateMachineVisionUi();
            foreach (Track track in Tracks.Values)
            {
                track.Box.Hide();
                Pool.Push(track.Box);
            }
            Tracks.Clear();
            IsFading = false;
            _lastCameraId = 0;
            _lastTickAt = Time.unscaledTime;
        }

        internal static void Tick(bool focused)
        {
            CCTVCamera holder = QuadCameraAssignment.GetBoundCamera(0);
            Camera camera = holder != null ? holder.Cam : null;
            if (_root == null || !focused || MonitorFocus.IsTurretPageActive || MonitorFocus.IsBodycamFeedActive ||
                camera == null || holder.IsSecurityBroken)
            { Clear(); return; }
            int id = camera.GetInstanceID();
            if (_lastCameraId != id) { Clear(); _lastCameraId = id; }
            if (!CCTVVanillaMonitorDisplay.TryGetCctvPaneGeometry(out Vector2 center, out Vector2 size) &&
                !MonitorFocus.TryGetOverlayPaneGeometry(0, out center, out size)) { Clear(); return; }
            float now = Time.unscaledTime;
            float delta = Mathf.Min(0.1f, Mathf.Max(0f, now - _lastTickAt));
            _lastTickAt = now;
            bool cameraMoved = Quaternion.Angle(_lastCameraRotation, camera.transform.rotation) > 0.25f ||
                (_lastCameraPosition - camera.transform.position).sqrMagnitude > 0.0025f;
            _lastCameraRotation = camera.transform.rotation;
            _lastCameraPosition = camera.transform.position;
            foreach (Track track in Tracks.Values) { track.Seen = false; if (cameraMoved) track.VisibilityUntil = 0f; }
            // Discovery is sliced; geometry/projection and fades run at display cadence.
            CctvTargetCache.RefreshIfDue();
            int visibilityBudget = 8;
            float baseFov = SurveillanceBootstrap.Config?.FieldOfView?.Value ?? 60f;
            float zoom = Mathf.Tan(baseFov * Mathf.Deg2Rad * 0.5f) / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            foreach (CctvTargetCache.ScreenTarget target in CctvTargetCache.ScreenTargets)
            {
                if (target.TracksRoot && (target.Root == null || !target.Root.gameObject.activeInHierarchy)) continue;
                Bounds bounds = target.ResolveCurrentBounds();
                if ((bounds.center - camera.transform.position).sqrMagnitude > 3600f) continue;
                if (!TryProjectBounds(camera, bounds, out Rect rect)) continue;
                CctvRevealCategory category = Category(target.Kind);
                Tracks.TryGetValue(target.SourceId, out Track track);
                float dx = Mathf.Max(rect.xMin - 0.5f, 0f, 0.5f - rect.xMax) * size.x / size.y;
                float dy = Mathf.Max(rect.yMin - 0.5f, 0f, 0.5f - rect.yMax);
                float aimDistance = Mathf.Sqrt(dx * dx + dy * dy);
                float pixels = Mathf.Min(rect.width * (camera.targetTexture != null ? camera.targetTexture.width : 768),
                    rect.height * (camera.targetTexture != null ? camera.targetTexture.height : 432));
                bool near = CctvRevealPolicy.Eligible(category, track != null && track.Acquired, aimDistance, true, zoom, pixels);
                if (track == null && (!near || Tracks.Count >= MaxBoxes)) continue;
                if (track == null)
                {
                    track = new Track { Box = Pool.Count > 0 ? Pool.Pop() : new OutlineBox(_root) };
                    Tracks.Add(target.SourceId, track);
                }
                track.Seen = true;
                track.Target = target;
                if ((bounds.center - track.VisibilityWorld).sqrMagnitude > 0.0001f) track.VisibilityUntil = 0f;
                if (near && now >= track.VisibilityUntil)
                {
                    if (visibilityBudget-- > 0)
                    {
                        track.Visible = IsTargetVisibleToCamera(camera, new TargetSnapshot { Bounds = bounds, Root = target.Root });
                        track.VisibilityWorld = bounds.center;
                        track.VisibilityUntil = now + 0.10f;
                    }
                    else track.Visible = false; // Never reveal on an unvalidated frame.
                }
                track.Acquired = near && track.Visible;
                // A hidden subject fades at its last visible location.
                if (track.Visible && near) { track.LastRect = rect; track.LastWorld = bounds.center; }
            }
            Expired.Clear();
            IsFading = false;
            foreach (var pair in Tracks)
            {
                Track track = pair.Value;
                if (!track.Seen) track.Acquired = false;
                track.Alpha = CctvRevealPolicy.Fade(track.Alpha, track.Acquired, delta);
                IsFading |= track.Alpha > 0f && track.Alpha < 1f;
                if (track.Alpha <= 0f && !track.Acquired) { Expired.Add(pair.Key); continue; }
                track.Box.Set(center, size, track.LastRect, ColorFor(track.Target.Kind), track.Target.Label,
                    track.Alpha, track.Target.IsArea, camera, track.LastWorld, track.Target.AreaRadius);
            }
            foreach (long key in Expired) { Tracks[key].Box.Hide(); Pool.Push(Tracks[key].Box); Tracks.Remove(key); }
            if (Expired.Count > 0) CCTVVanillaMonitorDisplay.InvalidateMachineVisionUi();
        }

        private static CctvRevealCategory Category(CctvTargetCache.TargetKind kind)
        {
            switch (kind)
            {
                case CctvTargetCache.TargetKind.Objective: return CctvRevealCategory.Objective;
                case CctvTargetCache.TargetKind.Player: return CctvRevealCategory.Teammate;
                case CctvTargetCache.TargetKind.Hostile: return CctvRevealCategory.Enemy;
                case CctvTargetCache.TargetKind.Device: return CctvRevealCategory.Device;
                default: return CctvRevealCategory.Item;
            }
        }

        private static Color ColorFor(CctvTargetCache.TargetKind kind)
        {
            switch (kind)
            {
                case CctvTargetCache.TargetKind.Objective: return new Color(1f, 0.79f, 0.39f);
                case CctvTargetCache.TargetKind.Hostile: return new Color(1f, 0.39f, 0.32f);
                case CctvTargetCache.TargetKind.Item: return new Color(0.87f, 0.94f, 0.94f);
                default: return new Color(0.42f, 0.85f, 0.95f);
            }
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
            return hitTransform == root || hitTransform.IsChildOf(root);
        }
        private sealed class OutlineBox
        {
            private readonly RectTransform root;
            private readonly CanvasGroup group;
            private readonly Image[] corners = new Image[8];
            private readonly Image[] area = new Image[24];
            private readonly Image pin;
            private readonly TextMeshProUGUI label;
            private readonly Image labelBacking;
            internal OutlineBox(RectTransform parent)
            {
                root = new GameObject("Target", typeof(RectTransform), typeof(CanvasGroup)).GetComponent<RectTransform>();
                root.SetParent(parent, false);
                root.gameObject.layer = parent.gameObject.layer;
                group = root.GetComponent<CanvasGroup>();
                group.blocksRaycasts = group.interactable = false;
                for (int i = 0; i < corners.Length; i++) corners[i] = Line("Corner", root);
                for (int i = 0; i < area.Length; i++) area[i] = Line("GroundRing", root);
                pin = Line("Location", root);
                pin.rectTransform.sizeDelta = new Vector2(7f, 7f);
                pin.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);
                labelBacking = Line("LabelBacking", root);
                labelBacking.rectTransform.pivot = Vector2.zero;
                labelBacking.color = new Color(0.012f, 0.025f, 0.03f, 0.88f);
                label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
                label.transform.SetParent(root, false);
                label.gameObject.layer = parent.gameObject.layer;
                CCTVVanillaMonitorDisplay.ApplyMachineVisionText(label);
                label.fontSize = 15f;
                label.richText = false;
                label.enableWordWrapping = false;
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.raycastTarget = false;
                label.rectTransform.pivot = new Vector2(0f, 0f);
                label.rectTransform.sizeDelta = new Vector2(210f, 24f);
                Hide();
            }
            internal void Hide() { if (root != null) root.gameObject.SetActive(false); }
            internal void Set(Vector2 paneCenter, Vector2 paneSize, Rect rect, Color color, string text,
                float alpha, bool isArea, Camera camera, Vector3 world, float radius)
            {
                root.gameObject.SetActive(true);
                group.alpha = alpha;
                Vector2 position = paneCenter + Vector2.Scale(rect.center - Vector2.one * 0.5f, paneSize);
                root.anchoredPosition = position;
                Vector2 half = new Vector2(Mathf.Max(6f, rect.width * paneSize.x * 0.5f + 3f), Mathf.Max(6f, rect.height * paneSize.y * 0.5f + 3f));
                float arm = Mathf.Clamp(Mathf.Min(half.x, half.y) * 0.45f, 5f, 14f);
                for (int i = 0; i < 4; i++)
                {
                    float x = (i % 2 == 0 ? -1f : 1f), y = (i < 2 ? 1f : -1f);
                    SetLine(corners[i * 2], new Vector2(x * (half.x - arm * 0.5f), y * half.y), new Vector2(arm, 1.2f), color, !isArea);
                    SetLine(corners[i * 2 + 1], new Vector2(x * half.x, y * (half.y - arm * 0.5f)), new Vector2(1.2f, arm), color, !isArea);
                }
                pin.gameObject.SetActive(isArea);
                pin.color = color;
                if (label.text != text)
                {
                    label.text = text;
                    float preferredWidth = Mathf.Clamp(label.GetPreferredValues(text).x + 12f, 60f, 220f);
                    label.rectTransform.sizeDelta = new Vector2(preferredWidth - 12f, 24f);
                    labelBacking.rectTransform.sizeDelta = new Vector2(preferredWidth, 24f);
                }
                label.color = new Color(0.87f, 0.94f, 0.96f, 1f);
                float labelWidth = labelBacking.rectTransform.sizeDelta.x;
                Vector2 labelPos = isArea ? new Vector2(9f, 3f) : new Vector2(-half.x, half.y + 3f);
                labelPos.x = Mathf.Clamp(labelPos.x, paneCenter.x - paneSize.x * 0.5f - position.x + 4f, paneCenter.x + paneSize.x * 0.5f - position.x - labelWidth - 4f);
                labelPos.y = Mathf.Clamp(labelPos.y, paneCenter.y - paneSize.y * 0.5f - position.y + 40f,
                    paneCenter.y + paneSize.y * 0.5f - position.y - 98f);
                labelBacking.rectTransform.anchoredPosition = labelPos;
                label.rectTransform.anchoredPosition = labelPos + new Vector2(6f, 0f);
                for (int i = 0; i < area.Length; i++)
                {
                    area[i].gameObject.SetActive(isArea);
                    if (!isArea) continue;
                    float a = i * Mathf.PI * 2f / area.Length, b = (i + 1) * Mathf.PI * 2f / area.Length;
                    Vector3 va = camera.WorldToViewportPoint(world + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius);
                    Vector3 vb = camera.WorldToViewportPoint(world + new Vector3(Mathf.Cos(b), 0f, Mathf.Sin(b)) * radius);
                    if (va.z <= camera.nearClipPlane || vb.z <= camera.nearClipPlane || va.x < 0f || va.x > 1f || va.y < 0f || va.y > 1f || vb.x < 0f || vb.x > 1f || vb.y < 0f || vb.y > 1f)
                    { area[i].gameObject.SetActive(false); continue; }
                    Vector2 pa = paneCenter + Vector2.Scale(new Vector2(va.x - 0.5f, va.y - 0.5f), paneSize) - position;
                    Vector2 pb = paneCenter + Vector2.Scale(new Vector2(vb.x - 0.5f, vb.y - 0.5f), paneSize) - position;
                    Color faint = color; faint.a = 0.3f;
                    SetLine(area[i], (pa + pb) * 0.5f, new Vector2((pb - pa).magnitude, 1f), faint, true);
                    area[i].rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(pb.y - pa.y, pb.x - pa.x) * Mathf.Rad2Deg);
                }
            }
            private static void SetLine(Image line, Vector2 position, Vector2 size, Color color, bool active)
            {
                line.gameObject.SetActive(active);
                line.rectTransform.anchoredPosition = position;
                line.rectTransform.sizeDelta = size;
                line.color = color;
            }
            private static Image Line(string name, RectTransform parent)
            {
                Image image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                image.transform.SetParent(parent, false);
                image.gameObject.layer = parent.gameObject.layer;
                image.raycastTarget = false;
                return image;
            }
        }
    }
}
