using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CCTVFocusControlsOverlay
    {
        private static GameObject _root;
        private static Canvas _canvas;
        private static Image _background;
        private static RectTransform _panelRect;
        private static RectTransform _textRect;
        private static TextMeshProUGUI _text;
        private static string _lastText;

        private const float PanelPaddingX = 18f;
        private const float PanelPaddingY = 12f;
        private const float PanelMinWidth = 260f;
        private const float PanelMaxWidth = 620f;
        private const float PanelMinHeight = 56f;

        // #541 — what is actually bound in a focus session, verified end to
        // end. RMB is the vanilla PingScan binding that CameraOperatorPingScanPatch
        // redirects into MonitorFocus.TriggerCameraContextOrScan; the wheel only
        // drives radar zoom, and only while SPACE is held. There is no camera zoom
        // — MonitorFocus._zoomByCamera is never written, so do not advertise one.
        private const string PlayerControlsText =
            "CCTV CONTROLS\n" +
            "MOUSE   AIM CAMERA\n" +
            "ARROWS  CAMERA LIST\n" +
            "SPACE   RADAR VIEW\n" +
            "SPC+WHL RADAR ZOOM\n" +
            "LMB     PING\n" +
            "RMB     TARGET\n" +
            "V HOLD  WALKIE\n" +
            "E/ESC   EXIT";

        // H moved here with the panel itself (#579): outside debug tools the
        // keybind is inert, so advertising it to a player would be a lie.
        private const string DebugControlsText =
            "\n" +
            "H       HIDE PANEL\n" +
            "F1      EDIT MENU\n" +
            "F2      CAMERA EDIT";

        internal static Transform RootTransform => _root != null ? _root.transform : null;

        // #579 — the sticky-note prop is the shipped controls reference now, so
        // the panel is off by default and a missing config entry reads as off
        // rather than on.
        private static bool ConfiguredOn =>
            SurveillanceBootstrap.Config?.ShowOperatorControlsOverlay != null &&
            SurveillanceBootstrap.Config.ShowOperatorControlsOverlay.Value;

        /// <summary>
        /// #579 — the panel's visibility rule.
        ///
        /// The config value is authoritative in both modes, and that is what makes
        /// the H keybind mean something: H is live only while debug tools are on,
        /// and it flips exactly this entry. Debug tools must therefore not force
        /// the panel on independently, or H would toggle a value nothing reads.
        /// The F1/F2/F8 edit-mode text draws into this same panel, so with debug
        /// tools on and the panel hidden, H is how it is brought back.
        ///
        /// The one override is a failed note registration: the note is the only
        /// shipped controls reference, so if it never registered the panel is
        /// forced on rather than leaving the player with no reference at all.
        /// </summary>
        private static bool OverlayEnabled =>
            ConfiguredOn || CCTVStickyNoteItem.RegistrationFailed;

        internal static void Tick()
        {
            if (!OverlayEnabled)
            {
                // Tear down rather than hide. MonitorFocus.HudDimming reads
                // RootTransform to exempt this panel from focus HUD dimming and
                // already null-guards, so a destroyed canvas is the cheaper and
                // more honest "off" — nothing stays parented and invisible.
                Shutdown();
                return;
            }

            if (!MonitorFocus.IsFocused)
            {
                SetVisible(false);
                return;
            }

            Ensure();
            SetVisible(true);
            RefreshText();
        }

        internal static void Shutdown()
        {
            if (_root != null)
                Object.Destroy(_root);

            _root = null;
            _canvas = null;
            _background = null;
            _panelRect = null;
            _textRect = null;
            _text = null;
            _lastText = null;
        }

        private static void Ensure()
        {
            if (_root != null)
                return;

            _root = new GameObject("LethalCCTV_FocusControlsOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_root);

            _canvas = _root.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32760;

            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            GraphicRaycaster raycaster = _root.GetComponent<GraphicRaycaster>();
            raycaster.enabled = false;

            GameObject panel = new GameObject("ControlsPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            panel.transform.SetParent(_root.transform, worldPositionStays: false);
            _panelRect = panel.GetComponent<RectTransform>();
            _panelRect.anchorMin = new Vector2(0f, 1f);
            _panelRect.anchorMax = new Vector2(0f, 1f);
            _panelRect.pivot = new Vector2(0f, 1f);
            _panelRect.anchoredPosition = new Vector2(26f, -86f);
            _panelRect.sizeDelta = new Vector2(PanelMinWidth, PanelMinHeight);

            _background = panel.GetComponent<Image>();
            _background.color = new Color(0f, 0f, 0f, 0.56f);
            _background.raycastTarget = false;

            GameObject textGo = new GameObject("ControlsText", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(panel.transform, worldPositionStays: false);
            _textRect = textGo.GetComponent<RectTransform>();
            _textRect.anchorMin = Vector2.zero;
            _textRect.anchorMax = Vector2.one;
            _textRect.offsetMin = new Vector2(PanelPaddingX, PanelPaddingY);
            _textRect.offsetMax = new Vector2(-PanelPaddingX, -PanelPaddingY);

            _text = textGo.GetComponent<TextMeshProUGUI>();
            _text.raycastTarget = false;
            _text.enableWordWrapping = false;
            _text.richText = true;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.fontSize = 24f;
            _text.lineSpacing = -7f;
            _text.color = new Color(1f, 0.58f, 0.14f, 0.96f);
            TryAssignHudFont(_text);
        }

        private static void RefreshText()
        {
            if (_text == null)
                return;

            // The three edit-mode providers hijack the whole panel. They are gated
            // by the same switch as the keys that open them, or closing an editor
            // by config could strand its panel on screen with nothing left to
            // dismiss it.
            if (MonitorFocus.IsOperatorDebugToolsEnabled)
            {
                string cameraEditText = MonitorFocus.GetCameraPlacementEditOverlayText();
                if (!string.IsNullOrWhiteSpace(cameraEditText))
                {
                    SetText(cameraEditText);
                    return;
                }

                string stationEditText = MonitorFocus.GetStationEditMenuOverlayText();
                if (!string.IsNullOrWhiteSpace(stationEditText))
                {
                    SetText(stationEditText);
                    return;
                }

                string editText = Y4NGZPlayerAnimationBridge.GetFirstPersonHandEditOverlayText();
                if (!string.IsNullOrWhiteSpace(editText))
                {
                    SetText(editText);
                    return;
                }

                SetText(PlayerControlsText + DebugControlsText);
                return;
            }

            SetText(PlayerControlsText);
        }

        private static void SetText(string value)
        {
            if (value == _lastText)
                return;

            _lastText = value;
            _text.text = value;
            ResizePanelToText();
        }

        private static void ResizePanelToText()
        {
            if (_panelRect == null || _text == null)
                return;

            _text.ForceMeshUpdate(ignoreActiveState: true);
            Vector2 preferred = _text.GetPreferredValues(_text.text, PanelMaxWidth - PanelPaddingX * 2f, 0f);
            float width = Mathf.Clamp(Mathf.Ceil(preferred.x + PanelPaddingX * 2f), PanelMinWidth, PanelMaxWidth);
            float height = Mathf.Max(PanelMinHeight, Mathf.Ceil(preferred.y + PanelPaddingY * 2f));
            _panelRect.sizeDelta = new Vector2(width, height);
            if (_textRect != null)
            {
                _textRect.offsetMin = new Vector2(PanelPaddingX, PanelPaddingY);
                _textRect.offsetMax = new Vector2(-PanelPaddingX, -PanelPaddingY);
            }
        }

        private static void SetVisible(bool visible)
        {
            if (_root != null && _root.activeSelf != visible)
                _root.SetActive(visible);
        }

        private static void TryAssignHudFont(TextMeshProUGUI target)
        {
            if (target == null)
                return;

            try
            {
                HUDManager hud = HUDManager.Instance;
                TextMeshProUGUI[] tips = hud != null ? hud.controlTipLines : null;
                if (tips != null && tips.Length > 0 && tips[0] != null && tips[0].font != null)
                    target.font = tips[0].font;
            }
            catch { }
        }
    }
}
