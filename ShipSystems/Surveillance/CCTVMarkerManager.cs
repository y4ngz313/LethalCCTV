using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using LethalNetworkAPI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [Serializable]
    public sealed class CCTVMarkerMessage
    {
        public int Id;
        public int Kind;
        public Vector3 Position;
        public float Radius;
        public int ColorKind;
        public string Label;
        public float Lifetime;
        public int CameraIndex;
    }

    internal static class CCTVMarkerManager
    {
        private const float DEFAULT_SCAN_LIFETIME = 8.0f;
        private const float DEFAULT_PING_LIFETIME = 10.0f;
        private const float MAX_SCREEN_SIZE = 128f;
        private const float MIN_SCREEN_SIZE = 26f;

        private static readonly Color MARKER_GREEN = new Color(0.10f, 1f, 0.35f, 1f);
        private static readonly Color MARKER_RED = new Color(1f, 0.12f, 0.12f, 1f);

        private static GameObject _root;
        private static Canvas _canvas;
        private static LNetworkMessage<CCTVMarkerMessage> _message;
        private static readonly List<MarkerVisual> _markers = new List<MarkerVisual>(32);
        private static readonly HashSet<int> _seenIds = new HashSet<int>();
        private static int _nextMarkerId;

        internal static bool HasActiveMarkers => _markers.Count > 0;

        private sealed class MarkerVisual
        {
            public int Id;
            public int Kind;
            public Vector3 Position;
            public float Radius;
            public float ExpiresAt;
            public Color Color;
            public string Label;
            public RectTransform Root;
            public Image Top;
            public Image Bottom;
            public Image Left;
            public Image Right;
            public Image Dot;
            public TextMeshProUGUI Text;
        }

        internal static void Initialize()
        {
            if (_root != null) return;

            _root = new GameObject("LethalCCTV_WorldMarkers");
            if (SurveillanceBootstrap.Instance != null)
            {
                _root.transform.SetParent(SurveillanceBootstrap.Instance.transform, worldPositionStays: false);
            }
            else
            {
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }

            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 135;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _message = LNetworkMessage<CCTVMarkerMessage>.Connect(
                "cctv_marker_v1",
                onServerReceived: OnServerReceived,
                onClientReceived: OnClientReceived);
        }

        internal static void Shutdown()
        {
            ClearAll();
            _message?.ClearSubscriptions();
            _message = null;

            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
                _canvas = null;
            }
        }

        internal static void ClearAll()
        {
            for (int i = 0; i < _markers.Count; i++)
            {
                if (_markers[i]?.Root != null)
                {
                    UnityEngine.Object.Destroy(_markers[i].Root.gameObject);
                }
            }
            _markers.Clear();
            _seenIds.Clear();
        }

        internal static void PublishPing(Vector3 position, int cameraIndex)
        {
            Publish(new CCTVMarkerMessage
            {
                Id = NextId(),
                Kind = 1,
                Position = position,
                Radius = 0.55f,
                ColorKind = 0,
                Label = "CCTV PING",
                Lifetime = DEFAULT_PING_LIFETIME,
                CameraIndex = cameraIndex,
            });
        }

        internal static void PublishContextMarker(Vector3 position, float radius, string label, int colorKind, int cameraIndex)
        {
            Publish(new CCTVMarkerMessage
            {
                Id = NextId(),
                Kind = 2,
                Position = position,
                Radius = Mathf.Clamp(radius, 0.35f, 4f),
                ColorKind = colorKind,
                Label = string.IsNullOrEmpty(label) ? "CCTV TARGET" : label,
                Lifetime = DEFAULT_SCAN_LIFETIME,
                CameraIndex = cameraIndex,
            });
        }

        internal static void Tick()
        {
            if (_root == null) return;

            Camera camera = ResolveGameplayCamera();
            bool hideForOperator = MonitorFocus.IsFocused;
            for (int i = _markers.Count - 1; i >= 0; i--)
            {
                MarkerVisual marker = _markers[i];
                if (marker == null || Time.unscaledTime >= marker.ExpiresAt)
                {
                    if (marker?.Root != null) UnityEngine.Object.Destroy(marker.Root.gameObject);
                    _markers.RemoveAt(i);
                    continue;
                }

                UpdateMarker(marker, camera, hideForOperator);
            }
        }

        private static int NextId()
        {
            unchecked
            {
                int client = GameNetworkManager.Instance?.localPlayerController != null
                    ? (int)GameNetworkManager.Instance.localPlayerController.playerClientId
                    : 0;
                _nextMarkerId++;
                return (client << 20) ^ _nextMarkerId ^ Mathf.RoundToInt(Time.realtimeSinceStartup * 1000f);
            }
        }

        private static void Publish(CCTVMarkerMessage data)
        {
            if (!IsMarkerKindAllowed(data)) return;
            AddLocalMarker(data);

            if (_message == null) return;
            try
            {
                _message.SendServer(data);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV marker stayed local: {ex.Message}");
            }
        }

        private static void OnServerReceived(CCTVMarkerMessage data, ulong _)
        {
            if (!IsMarkerKindAllowed(data)) return;
            try
            {
                _message?.SendClients(data);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV marker server broadcast failed: {ex.Message}");
            }
        }

        private static bool IsMarkerKindAllowed(CCTVMarkerMessage data)
        {
            if (data == null || SurveillanceBootstrap.Config == null)
                return data != null;

            if (data.Kind == 1)
                return SurveillanceBootstrap.Config.AllowCameraPings.Value;
            if (data.Kind == 2)
                return SurveillanceBootstrap.Config.AllowTargetScanning.Value;
            return true;
        }

        private static void OnClientReceived(CCTVMarkerMessage data)
        {
            AddLocalMarker(data);
        }

        private static void AddLocalMarker(CCTVMarkerMessage data)
        {
            if (data == null) return;
            Initialize();
            if (_seenIds.Contains(data.Id)) return;
            _seenIds.Add(data.Id);

            MarkerVisual marker = CreateMarkerVisual(data);
            _markers.Add(marker);

            if (data.Kind == 0)
            {
                CCTVScanGlowManager.MarkScanTarget(data.Position, data.Radius, data.ColorKind, data.Lifetime);
            }
        }

        private static MarkerVisual CreateMarkerVisual(CCTVMarkerMessage data)
        {
            var go = new GameObject($"CCTVMarker_{data.Id}");
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(64f, 64f);

            Color color = data.ColorKind == 1 ? MARKER_RED : MARKER_GREEN;
            var marker = new MarkerVisual
            {
                Id = data.Id,
                Kind = data.Kind,
                Position = data.Position,
                Radius = Mathf.Clamp(data.Radius, 0.25f, 4f),
                ExpiresAt = Time.unscaledTime + Mathf.Clamp(data.Lifetime, 1f, 15f),
                Color = color,
                Label = string.IsNullOrEmpty(data.Label) ? (data.Kind == 1 ? "PING" : "SCAN") : data.Label,
                Root = rect,
            };

            marker.Top = CreateLine(rect, "Top", color);
            marker.Bottom = CreateLine(rect, "Bottom", color);
            marker.Left = CreateLine(rect, "Left", color);
            marker.Right = CreateLine(rect, "Right", color);
            marker.Dot = CreateLine(rect, "Dot", color);
            marker.Text = CreateText(rect, "Label", marker.Label, color);
            return marker;
        }

        private static Image CreateLine(RectTransform parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static TextMeshProUGUI CreateText(RectTransform parent, string name, string text, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -8f);
            rect.sizeDelta = new Vector2(160f, 24f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 13f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static void UpdateMarker(MarkerVisual marker, Camera camera, bool hide)
        {
            if (marker?.Root == null) return;
            if (hide || camera == null)
            {
                marker.Root.gameObject.SetActive(false);
                return;
            }

            Vector3 viewport = camera.WorldToViewportPoint(marker.Position);
            if (viewport.z <= 0.05f || viewport.x < -0.08f || viewport.x > 1.08f || viewport.y < -0.08f || viewport.y > 1.08f)
            {
                marker.Root.gameObject.SetActive(false);
                return;
            }

            marker.Root.gameObject.SetActive(true);

            RectTransform canvasRect = _canvas.transform as RectTransform;
            float width = canvasRect != null ? canvasRect.rect.width : Screen.width;
            float height = canvasRect != null ? canvasRect.rect.height : Screen.height;
            marker.Root.anchoredPosition = new Vector2((viewport.x - 0.5f) * width, (viewport.y - 0.5f) * height);

            float distance = Mathf.Max(1f, Vector3.Distance(camera.transform.position, marker.Position));
            float size = marker.Kind == 1
                ? 34f
                : Mathf.Clamp((marker.Radius / distance) * 1150f, MIN_SCREEN_SIZE, MAX_SCREEN_SIZE);
            marker.Root.sizeDelta = new Vector2(size, size);
            ApplyMarkerShape(marker, size);
        }

        private static void ApplyMarkerShape(MarkerVisual marker, float size)
        {
            float thickness = marker.Kind == 1 ? 5f : 4f;
            float half = size * 0.5f;

            if (marker.Kind == 1)
            {
                float arm = Mathf.Clamp(size * 0.32f, 8f, 14f);
                SetLine(marker.Top, new Vector2(0f, half - arm * 0.5f), new Vector2(thickness, arm));
                SetLine(marker.Bottom, new Vector2(0f, -half + arm * 0.5f), new Vector2(thickness, arm));
                SetLine(marker.Left, new Vector2(-half + arm * 0.5f, 0f), new Vector2(arm, thickness));
                SetLine(marker.Right, new Vector2(half - arm * 0.5f, 0f), new Vector2(arm, thickness));
                SetLine(marker.Dot, Vector2.zero, new Vector2(5f, 5f));
            }
            else
            {
                float segment = Mathf.Clamp(size * 0.34f, 12f, 34f);
                SetLine(marker.Top, new Vector2(-half + segment * 0.5f, half), new Vector2(segment, thickness));
                SetLine(marker.Left, new Vector2(-half, half - segment * 0.5f), new Vector2(thickness, segment));
                SetLine(marker.Bottom, new Vector2(half - segment * 0.5f, -half), new Vector2(segment, thickness));
                SetLine(marker.Right, new Vector2(half, -half + segment * 0.5f), new Vector2(thickness, segment));
                SetLine(marker.Dot, Vector2.zero, new Vector2(4f, 4f));
            }

            if (marker.Text != null)
            {
                marker.Text.rectTransform.anchoredPosition = new Vector2(0f, -half - 10f);
                marker.Text.color = new Color(marker.Color.r, marker.Color.g, marker.Color.b, 0.95f);
            }
        }

        private static void SetLine(Image image, Vector2 position, Vector2 size)
        {
            if (image == null) return;
            RectTransform rect = image.rectTransform;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static Camera ResolveGameplayCamera()
        {
            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player != null && player.gameplayCamera != null) return player.gameplayCamera;
            return Camera.main;
        }

    }
}
