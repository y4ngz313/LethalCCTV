using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVVanillaMonitorDisplay
    {
        private static void CreateLeftCameraLabel(RectTransform canvas)
        {
            GameObject labelGo = new GameObject("CCTVCameraLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(labelGo, UiRenderLayer);

            RectTransform rt = labelGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-32f, -26f);
            rt.sizeDelta = new Vector2(180f, 42f);

            _leftCameraLabel = labelGo.GetComponent<TextMeshProUGUI>();
            _leftCameraLabel.text = "CAM_--";
            _leftCameraLabel.raycastTarget = false;
            _leftCameraLabel.enableWordWrapping = false;
            _leftCameraLabel.overflowMode = TextOverflowModes.Ellipsis;
            _leftCameraLabel.richText = false;
            _leftCameraLabel.alignment = TextAlignmentOptions.TopRight;
            _leftCameraLabel.fontSize = 34f;
            _leftCameraLabel.color = new Color(0.12f, 1f, 0.28f, 0.96f);
            TryAssignHudFont(_leftCameraLabel);

            GameObject pageGo = new GameObject("CCTVPageLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            pageGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(pageGo, UiRenderLayer);

            RectTransform pageRt = pageGo.GetComponent<RectTransform>();
            pageRt.anchorMin = new Vector2(1f, 1f);
            pageRt.anchorMax = new Vector2(1f, 1f);
            pageRt.pivot = new Vector2(1f, 1f);
            pageRt.anchoredPosition = new Vector2(-34f, -64f);
            pageRt.sizeDelta = new Vector2(220f, 30f);

            _leftPageLabel = pageGo.GetComponent<TextMeshProUGUI>();
            _leftPageLabel.text = "01/01";
            _leftPageLabel.raycastTarget = false;
            _leftPageLabel.enableWordWrapping = false;
            _leftPageLabel.richText = false;
            _leftPageLabel.alignment = TextAlignmentOptions.TopRight;
            _leftPageLabel.fontSize = 20f;
            _leftPageLabel.color = new Color(0.12f, 1f, 0.28f, 0.82f);
            TryAssignHudFont(_leftPageLabel);

            // Third entry in the same top-right stack: CAM at -26, PAGE at -64, clock
            // at -90. 24-hour HH:MM, from CctvScreenClock.
            GameObject clockGo = new GameObject("CCTVClockLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            clockGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(clockGo, UiRenderLayer);

            RectTransform clockRt = clockGo.GetComponent<RectTransform>();
            clockRt.anchorMin = new Vector2(1f, 1f);
            clockRt.anchorMax = new Vector2(1f, 1f);
            clockRt.pivot = new Vector2(1f, 1f);
            clockRt.anchoredPosition = new Vector2(-34f, -90f);
            clockRt.sizeDelta = new Vector2(220f, 32f);

            _leftClockLabel = clockGo.GetComponent<TextMeshProUGUI>();
            _leftClockLabel.text = CctvScreenClock.UnavailableText;
            _lastLeftClockText = CctvScreenClock.UnavailableText;
            _leftClockLabel.raycastTarget = false;
            _leftClockLabel.enableWordWrapping = false;
            _leftClockLabel.richText = false;
            _leftClockLabel.alignment = TextAlignmentOptions.TopRight;
            _leftClockLabel.fontSize = 22f;
            _leftClockLabel.color = new Color(0.12f, 1f, 0.28f, 0.88f);
            TryAssignHudFont(_leftClockLabel);
        }

        /// <summary>
        /// Top-left REC light: a small red dot plus the word REC, both alpha-blinked at
        /// 1 Hz by <see cref="RefreshLeftScreenFurniture"/>.
        /// </summary>
        private static void CreateLeftRecIndicator(RectTransform canvas)
        {
            GameObject dotGo = new GameObject("CCTVRecDot", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            dotGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(dotGo, UiRenderLayer);

            RectTransform dotRt = dotGo.GetComponent<RectTransform>();
            dotRt.anchorMin = new Vector2(0f, 1f);
            dotRt.anchorMax = new Vector2(0f, 1f);
            dotRt.pivot = new Vector2(0f, 1f);
            dotRt.anchoredPosition = new Vector2(30f, -24f);
            dotRt.sizeDelta = new Vector2(14f, 14f);

            _leftRecDot = dotGo.GetComponent<Image>();
            _leftRecDot.color = new Color(1f, 0.16f, 0.14f, 1f);
            _leftRecDot.raycastTarget = false;

            GameObject recGo = new GameObject("CCTVRecLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            recGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(recGo, UiRenderLayer);

            RectTransform recRt = recGo.GetComponent<RectTransform>();
            recRt.anchorMin = new Vector2(0f, 1f);
            recRt.anchorMax = new Vector2(0f, 1f);
            recRt.pivot = new Vector2(0f, 1f);
            recRt.anchoredPosition = new Vector2(52f, -18f);
            recRt.sizeDelta = new Vector2(110f, 26f);

            _leftRecLabel = recGo.GetComponent<TextMeshProUGUI>();
            _leftRecLabel.text = "REC";
            _leftRecLabel.raycastTarget = false;
            _leftRecLabel.enableWordWrapping = false;
            _leftRecLabel.richText = false;
            _leftRecLabel.alignment = TextAlignmentOptions.MidlineLeft;
            _leftRecLabel.fontSize = 22f;
            _leftRecLabel.color = new Color(1f, 0.30f, 0.26f, 0.95f);
            TryAssignHudFont(_leftRecLabel);

            // Both are built lit, so seed the cache to match — otherwise the first
            // refresh sees "already off" and the light never starts blinking.
            _lastLeftRecOn = true;
        }

        /// <summary>
        /// Eight thin rects forming an L at each corner. Inset 12 px so they clear the
        /// 5 px border frame and sit just outside the 14 px feed margin, framing the
        /// picture the way a DVR's viewfinder brackets do.
        /// </summary>
        private static void CreateLeftCornerBrackets(RectTransform canvas)
        {
            Color color = new Color(0.12f, 1f, 0.28f, 0.55f);
            const float inset = 12f;
            const float arm = 34f;
            const float thickness = 3f;

            CreateCornerBracket(canvas, "TL", new Vector2(0f, 1f), new Vector2(inset, -inset), arm, thickness, color);
            CreateCornerBracket(canvas, "TR", new Vector2(1f, 1f), new Vector2(-inset, -inset), arm, thickness, color);
            CreateCornerBracket(canvas, "BL", new Vector2(0f, 0f), new Vector2(inset, inset), arm, thickness, color);
            CreateCornerBracket(canvas, "BR", new Vector2(1f, 0f), new Vector2(-inset, inset), arm, thickness, color);
        }

        private static void CreateCornerBracket(RectTransform canvas, string corner, Vector2 anchor, Vector2 position, float arm, float thickness, Color color)
        {
            CreateBracketArm(canvas, $"CCTVBracket{corner}H", anchor, position, new Vector2(arm, thickness), color);
            CreateBracketArm(canvas, $"CCTVBracket{corner}V", anchor, position, new Vector2(thickness, arm), color);
        }

        private static void CreateBracketArm(RectTransform canvas, string name, Vector2 anchor, Vector2 position, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(go, UiRenderLayer);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            // Pivot matches the anchored corner, so the arm always grows inward and
            // both arms of a corner share one anchoredPosition.
            rt.pivot = anchor;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;

            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        /// <summary>
        /// Bottom-left status readout: the feed's real resolution and nominal capture
        /// rate, plus five signal bars driven by the bound camera's actual state.
        /// </summary>
        private static void CreateLeftStatusLine(RectTransform canvas)
        {
            GameObject statusGo = new GameObject("CCTVStatusLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            statusGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(statusGo, UiRenderLayer);

            RectTransform statusRt = statusGo.GetComponent<RectTransform>();
            statusRt.anchorMin = new Vector2(0f, 0f);
            statusRt.anchorMax = new Vector2(0f, 0f);
            statusRt.pivot = new Vector2(0f, 0f);
            statusRt.anchoredPosition = new Vector2(30f, 18f);
            statusRt.sizeDelta = new Vector2(300f, 26f);

            _leftStatusLabel = statusGo.GetComponent<TextMeshProUGUI>();
            _leftStatusLabel.text = BuildStatusLineText();
            _lastLeftStatusText = _leftStatusLabel.text;
            _leftStatusLabel.raycastTarget = false;
            _leftStatusLabel.enableWordWrapping = false;
            _leftStatusLabel.richText = false;
            _leftStatusLabel.alignment = TextAlignmentOptions.MidlineLeft;
            _leftStatusLabel.fontSize = 18f;
            _leftStatusLabel.color = new Color(0.12f, 1f, 0.28f, 0.72f);
            TryAssignHudFont(_leftStatusLabel);

            _leftSignalBars = new Image[SignalBarCount];
            for (int i = 0; i < SignalBarCount; i++)
            {
                GameObject barGo = new GameObject($"CCTVSignalBar{i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                barGo.transform.SetParent(canvas, worldPositionStays: false);
                SetLayerRecursive(barGo, UiRenderLayer);

                RectTransform barRt = barGo.GetComponent<RectTransform>();
                barRt.anchorMin = new Vector2(0f, 0f);
                barRt.anchorMax = new Vector2(0f, 0f);
                barRt.pivot = new Vector2(0f, 0f);
                barRt.anchoredPosition = new Vector2(250f + i * 9f, 18f);
                barRt.sizeDelta = new Vector2(5f, 6f + i * 3f);

                _leftSignalBars[i] = barGo.GetComponent<Image>();
                _leftSignalBars[i].color = new Color(0.12f, 1f, 0.28f, 0.18f);
                _leftSignalBars[i].raycastTarget = false;
            }
        }

        private static string BuildStatusLineText()
        {
            return $"{MonitorWidth}x{MonitorHeight} | 24FPS";
        }

        /// <summary>
        /// Full-frame plate shown when the bound camera reports IsSecurityBroken: heavy
        /// static over black with a centred SIGNAL LOST readout. Sized to the feed rect
        /// so it replaces the picture rather than the whole screen.
        /// </summary>
        private static void CreateLeftSignalLostPlate(RectTransform canvas)
        {
            GameObject rootGo = new GameObject("CCTVSignalLostPlate", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            rootGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(rootGo, UiRenderLayer);

            RectTransform rootRt = rootGo.GetComponent<RectTransform>();
            ApplySingleCameraRect(rootRt, 14f);

            Image backing = rootGo.GetComponent<Image>();
            backing.color = new Color(0f, 0f, 0f, 0.88f);
            backing.raycastTarget = false;

            GameObject staticGo = new GameObject("CCTVSignalLostStatic", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            staticGo.transform.SetParent(rootRt, worldPositionStays: false);
            SetLayerRecursive(staticGo, UiRenderLayer);
            Stretch(staticGo.GetComponent<RectTransform>());

            _leftSignalLostStatic = staticGo.GetComponent<RawImage>();
            _leftSignalLostStatic.texture = EnsureSignalLostNoiseTexture();
            _leftSignalLostStatic.color = new Color(1f, 1f, 1f, 0.42f);
            _leftSignalLostStatic.raycastTarget = false;
            _leftSignalLostStatic.uvRect = new Rect(0f, 0f, 12f, 6.75f);

            GameObject labelGo = new GameObject("CCTVSignalLostLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelGo.transform.SetParent(rootRt, worldPositionStays: false);
            SetLayerRecursive(labelGo, UiRenderLayer);

            RectTransform labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0.5f, 0.5f);
            labelRt.anchorMax = new Vector2(0.5f, 0.5f);
            labelRt.pivot = new Vector2(0.5f, 0.5f);
            labelRt.anchoredPosition = Vector2.zero;
            labelRt.sizeDelta = new Vector2(620f, 60f);

            _leftSignalLostLabel = labelGo.GetComponent<TextMeshProUGUI>();
            _leftSignalLostLabel.text = "SIGNAL LOST";
            _leftSignalLostLabel.raycastTarget = false;
            _leftSignalLostLabel.enableWordWrapping = false;
            _leftSignalLostLabel.richText = false;
            _leftSignalLostLabel.alignment = TextAlignmentOptions.Center;
            _leftSignalLostLabel.fontSize = 38f;
            _leftSignalLostLabel.color = new Color(1f, 0.32f, 0.26f, 0.96f);
            TryAssignHudFont(_leftSignalLostLabel);

            _leftSignalLostRoot = rootGo;
            _leftSignalLostRoot.SetActive(false);
        }

        /// <summary>
        /// One 64x64 grayscale noise texture, tiled and jittered per repaint. Built from
        /// a fixed seed so it is identical every session and never touches
        /// UnityEngine.Random's global state.
        /// </summary>
        private static Texture2D EnsureSignalLostNoiseTexture()
        {
            if (_signalLostNoiseTexture != null)
                return _signalLostNoiseTexture;

            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "LethalCCTV_SignalLostStatic",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };

            Color32[] pixels = new Color32[size * size];
            System.Random rng = new System.Random(20260803);
            for (int i = 0; i < pixels.Length; i++)
            {
                byte v = (byte)rng.Next(18, 236);
                pixels[i] = new Color32(v, v, v, 255);
            }
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            _signalLostNoiseTexture = texture;
            return texture;
        }

        private static void CreateLeftSwitchFlash(RectTransform canvas)
        {
            GameObject flashGo = new GameObject("CCTVSwitchFlash", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            flashGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(flashGo, UiRenderLayer);
            RectTransform rt = flashGo.GetComponent<RectTransform>();
            ApplySingleCameraRect(rt, 14f);

            _leftSwitchFlashImage = flashGo.GetComponent<Image>();
            _leftSwitchFlashImage.color = Color.clear;
            _leftSwitchFlashImage.raycastTarget = false;
            flashGo.SetActive(false);
            flashGo.transform.SetAsLastSibling();
            if (_leftCameraLabel != null)
                _leftCameraLabel.transform.SetAsLastSibling();
        }

        private static void CreateLeftReticle(RectTransform canvas)
        {
            _leftReticleRoot = new GameObject("CCTVCameraReticle", typeof(RectTransform));
            _leftReticleRoot.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(_leftReticleRoot, UiRenderLayer);

            RectTransform rt = _leftReticleRoot.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(96f, 96f);

            Color color = new Color(0.1f, 1f, 0.28f, 0.76f);
            CreateReticleSegment(rt, "Top", new Vector2(0f, 24f), new Vector2(3f, 18f), color);
            CreateReticleSegment(rt, "Bottom", new Vector2(0f, -24f), new Vector2(3f, 18f), color);
            CreateReticleSegment(rt, "Left", new Vector2(-24f, 0f), new Vector2(18f, 3f), color);
            CreateReticleSegment(rt, "Right", new Vector2(24f, 0f), new Vector2(18f, 3f), color);
            CreateReticleSegment(rt, "CenterH", Vector2.zero, new Vector2(12f, 2f), new Color(color.r, color.g, color.b, 0.48f));
            CreateReticleSegment(rt, "CenterV", Vector2.zero, new Vector2(2f, 12f), new Color(color.r, color.g, color.b, 0.48f));
        }

        private static void CreateLeftTerminalOverlayRoot(RectTransform canvas)
        {
            GameObject overlayGo = new GameObject("LethalCCTV_LowerLeftTerminalOverlay", typeof(RectTransform));
            overlayGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(overlayGo, UiRenderLayer);

            _leftTerminalOverlayRoot = overlayGo.GetComponent<RectTransform>();
            _leftTerminalOverlayRoot.anchorMin = Vector2.zero;
            _leftTerminalOverlayRoot.anchorMax = Vector2.one;
            _leftTerminalOverlayRoot.pivot = new Vector2(0.5f, 0.5f);
            _leftTerminalOverlayRoot.offsetMin = Vector2.zero;
            _leftTerminalOverlayRoot.offsetMax = Vector2.zero;
            _leftTerminalOverlayRoot.localRotation = Quaternion.identity;
            _leftTerminalOverlayRoot.localScale = Vector3.one;
            _leftTerminalOverlayRoot.SetAsLastSibling();
        }

        private static void CreateReticleSegment(RectTransform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            GameObject go = new GameObject("Reticle_" + name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);
            SetLayerRecursive(go, UiRenderLayer);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = position;
            rt.sizeDelta = size;

            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        /// <summary>
        /// Refreshes every readout composited over the left feed and reports whether
        /// anything actually changed. Callers OR the result into the maintenance-pass
        /// change flag, so returning true on a no-op change would dirty the compositor
        /// for nothing.
        /// </summary>
        private static bool RefreshLeftCameraLabel(float now, bool monitorVisible)
        {
            CCTVCamera active = MonitorFocus.IsFacilityFeedActive
                ? QuadCameraAssignment.GetBoundCamera(0)
                : null;
            bool nonFacilityFeedActive = MonitorFocus.IsBodycamFeedActive || MonitorFocus.IsTurretPageActive;
            bool changed = RefreshLeftCameraAndPageLabels(MonitorFocus.IsBodycamFeedActive);
            changed |= RefreshLeftScreenFurniture(now, monitorVisible, active, nonFacilityFeedActive);
            return changed;
        }

        private static bool RefreshLeftCameraAndPageLabels(bool bodycamActive)
        {
            if (_leftCameraLabel == null && _leftPageLabel == null)
                return false;

            string label = MonitorFocus.GetActiveFeedDisplayLabel();
            string page = MonitorFocus.GetActiveFeedPageLabel();
            bool layoutChanged = false;
            if (_leftCameraLabel != null)
            {
                RectTransform labelRect = _leftCameraLabel.rectTransform;
                float labelWidth = bodycamActive ? 300f : 180f;
                float fontSize = bodycamActive ? 28f : 34f;
                if (!Mathf.Approximately(labelRect.sizeDelta.x, labelWidth))
                {
                    labelRect.sizeDelta = new Vector2(labelWidth, labelRect.sizeDelta.y);
                    layoutChanged = true;
                }
                if (!Mathf.Approximately(_leftCameraLabel.fontSize, fontSize))
                {
                    _leftCameraLabel.fontSize = fontSize;
                    layoutChanged = true;
                }
            }

            bool labelChanged = !string.Equals(label, _lastLeftCameraLabel, StringComparison.Ordinal);
            bool pageChanged = !string.Equals(page, _lastLeftPageLabel, StringComparison.Ordinal);
            if (!labelChanged && !pageChanged && !layoutChanged)
                return false;

            bool hadPrevious = !string.IsNullOrEmpty(_lastLeftCameraLabel) || !string.IsNullOrEmpty(_lastLeftPageLabel);
            _lastLeftCameraLabel = label;
            _lastLeftPageLabel = page;
            if (_leftCameraLabel != null)
                _leftCameraLabel.text = label;
            if (_leftPageLabel != null)
                _leftPageLabel.text = page;
            _compositorDirty = true;
            // The switch flash stays keyed to the camera/page labels alone. Every other
            // readout below changes on its own schedule — a clock tick or a REC blink
            // firing this would strobe the feed and, worse, IsLeftSwitchAnimating would
            // then force full-rate compositor renders for 0.18s out of every second.
            if (hadPrevious && _cctvModeActive)
                TriggerLeftSwitchFlash();
            return true;
        }

        /// <summary>
        /// Clock, REC blink, status readout and signal-lost plate.
        ///
        /// State that reflects a real event (a camera breaking, the bound camera
        /// changing) is tracked unconditionally, so the plate is already correct on the
        /// first frame the monitor comes back into view. The purely animated parts —
        /// clock text and REC blink — only advance while the monitor is observable.
        /// ShouldRenderCompositor deliberately checks observability BEFORE the dirty
        /// flag, so an unobserved repaint is suppressed either way; gating here keeps
        /// the work itself, and the once-a-second dirty churn, off the hidden path.
        /// </summary>
        private static bool RefreshLeftScreenFurniture(
            float now,
            bool monitorVisible,
            CCTVCamera active,
            bool nonFacilityFeedActive)
        {
            bool changed = false;

            bool signalLost = active != null && active.IsSecurityBroken;
            if (signalLost != _lastLeftSignalLost)
            {
                _lastLeftSignalLost = signalLost;
                if (_leftSignalLostRoot != null)
                    _leftSignalLostRoot.SetActive(signalLost);
                changed = true;
            }

            if (signalLost && _leftSignalLostLabel != null)
            {
                string lostLabel = !string.IsNullOrWhiteSpace(active.ResolvedLabel) ? active.ResolvedLabel : "CAM_--";
                string lostText = "SIGNAL LOST - " + lostLabel;
                if (!string.Equals(lostText, _lastLeftSignalLostText, StringComparison.Ordinal))
                {
                    _lastLeftSignalLostText = lostText;
                    _leftSignalLostLabel.text = lostText;
                    changed = true;
                }
            }

            bool hasFeed = active != null || nonFacilityFeedActive;
            int litBars = !hasFeed ? 0 : (signalLost ? 1 : SignalBarCount);
            if (litBars != _lastLeftSignalBars)
            {
                _lastLeftSignalBars = litBars;
                ApplyLeftSignalBars(litBars);
                changed = true;
            }

            string status = BuildStatusLineText();
            if (_leftStatusLabel != null && !string.Equals(status, _lastLeftStatusText, StringComparison.Ordinal))
            {
                _lastLeftStatusText = status;
                _leftStatusLabel.text = status;
                changed = true;
            }

            if (!monitorVisible || !_cctvModeActive)
            {
                if (changed)
                    _compositorDirty = true;
                return changed;
            }

            string clock = CctvScreenClock.GetShipTime24h();
            if (_leftClockLabel != null && !string.Equals(clock, _lastLeftClockText, StringComparison.Ordinal))
            {
                _lastLeftClockText = clock;
                _leftClockLabel.text = clock;
                CctvScreenClock.LogComparisonOnce(clock);
                changed = true;
            }

            bool recOn = Mathf.Repeat(now, RecBlinkPeriodSeconds) < RecBlinkPeriodSeconds * RecBlinkOnFraction;
            if (recOn != _lastLeftRecOn)
            {
                _lastLeftRecOn = recOn;
                ApplyLeftRecBlink(recOn);
                changed = true;
            }

            // The static scroll deliberately does NOT report a change. It rides
            // whatever repaint cadence is already running (2 Hz ambient, 10-18 Hz
            // focused) instead of forcing one of its own, which is why it is applied
            // here rather than being folded into the change flag above.
            if (signalLost)
                ScrollLeftSignalLostStatic();

            if (changed)
                _compositorDirty = true;
            return changed;
        }

        private static void ApplyLeftRecBlink(bool on)
        {
            if (_leftRecDot != null)
                _leftRecDot.color = new Color(1f, 0.16f, 0.14f, on ? 1f : 0.12f);
            if (_leftRecLabel != null)
                _leftRecLabel.color = new Color(1f, 0.30f, 0.26f, on ? 0.95f : 0.22f);
        }

        private static void ApplyLeftSignalBars(int litCount)
        {
            if (_leftSignalBars == null)
                return;

            for (int i = 0; i < _leftSignalBars.Length; i++)
            {
                if (_leftSignalBars[i] == null)
                    continue;
                bool lit = i < litCount;
                _leftSignalBars[i].color = new Color(0.12f, 1f, 0.28f, lit ? 0.85f : 0.18f);
            }
        }

        private static void ScrollLeftSignalLostStatic()
        {
            if (_leftSignalLostStatic == null)
                return;

            // Golden-ratio and sqrt(2) strides: two irrationals that never line up, so
            // consecutive repaints land on uncorrelated parts of the tile and the
            // static reads as random without a per-frame RNG call.
            _signalLostStaticStep++;
            float offsetX = _signalLostStaticStep * 0.6180339887f;
            float offsetY = _signalLostStaticStep * 0.4142135624f;
            Rect uv = _leftSignalLostStatic.uvRect;
            _leftSignalLostStatic.uvRect = new Rect(
                offsetX - Mathf.Floor(offsetX),
                offsetY - Mathf.Floor(offsetY),
                uv.width,
                uv.height);
        }

        private static void TriggerLeftSwitchFlash()
        {
            _leftSwitchFlashStartedAt = Time.unscaledTime;
            _leftSwitchFlashUntil = _leftSwitchFlashStartedAt + LeftSwitchTotalDurationSeconds;
            if (_leftSwitchFlashImage != null)
            {
                _leftSwitchFlashImage.gameObject.SetActive(true);
                _leftSwitchFlashImage.color = new Color(0.82f, 0.86f, 0.82f, 0.58f);
                _leftSwitchFlashImage.transform.SetAsLastSibling();
                if (_leftPageLabel != null)
                    _leftPageLabel.transform.SetAsLastSibling();
                if (_leftCameraLabel != null)
                    _leftCameraLabel.transform.SetAsLastSibling();
            }
        }

        private static void UpdateLeftSwitchFlash(float now)
        {
            if (_leftSwitchFlashImage == null)
                return;

            if (now >= _leftSwitchFlashUntil)
            {
                if (_leftSwitchFlashImage.gameObject.activeSelf)
                    _leftSwitchFlashImage.gameObject.SetActive(false);
                ResetLeftSlotUv();
                return;
            }

            float elapsed = Mathf.Max(0f, now - _leftSwitchFlashStartedAt);
            float flashT = Mathf.Clamp01(elapsed / LeftSwitchFlashDurationSeconds);
            float alpha = Mathf.Lerp(0.58f, 0f, flashT);
            _leftSwitchFlashImage.color = new Color(0.82f, 0.86f, 0.82f, alpha);
            ApplyLeftSlotSwitchRoll(elapsed);
            if (!_leftSwitchFlashImage.gameObject.activeSelf)
                _leftSwitchFlashImage.gameObject.SetActive(true);
        }

        private static void ApplyLeftSlotSwitchRoll(float elapsed)
        {
            if (_slotImages == null || _slotImages.Length == 0 || _slotImages[0] == null)
                return;

            float t = Mathf.Clamp01(elapsed / LeftSwitchTotalDurationSeconds);
            float roll = Mathf.Sin(t * Mathf.PI) * 0.018f;
            _slotImages[0].uvRect = new Rect(roll, -roll * 0.35f, 1f, 1f);
        }

        private static void ResetLeftSlotUv()
        {
            if (_slotImages == null || _slotImages.Length == 0 || _slotImages[0] == null)
                return;

            _slotImages[0].uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        private static bool IsLeftSwitchAnimating(float now)
        {
            return _leftSwitchFlashImage != null && now < _leftSwitchFlashUntil;
        }

    }
}
