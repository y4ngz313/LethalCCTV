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
            CreateMachineVisionFurniture(canvas, 72f);
            // The physical instruction note overlaps the first 7% of the screen.
            _leftCameraLabel = CreateMachineVisionLabel(canvas, "CCTVCameraLabel", Vector2.up,
                new Vector2(72f, -14f), new Vector2(510f, 30f), 23f, TextAlignmentOptions.TopLeft);
            _leftCameraLabel.text = "CAM -- / INTERIOR";
            _leftClockLabel = CreateMachineVisionLabel(canvas, "CCTVClockLabel", Vector2.one,
                new Vector2(-24f, -44f), new Vector2(160f, 22f), 18f, TextAlignmentOptions.TopRight);
            _leftClockLabel.text = CctvScreenClock.UnavailableText;
            _lastLeftClockText = CctvScreenClock.UnavailableText;
        }

        private static void CreateLeftRecIndicator(RectTransform canvas)
        {
            _leftRecLabel = CreateMachineVisionLabel(canvas, "CCTVLive", Vector2.one,
                new Vector2(-24f, -14f), new Vector2(125f, 28f), 22f, TextAlignmentOptions.TopRight);
            _leftRecLabel.text = "+ LIVE";
            _lastLeftRecOn = true;
        }

        private static void CreateLeftStatusLine(RectTransform canvas)
        {
            _leftStatusLabel = CreateMachineVisionLabel(canvas, "CCTVStatusLabel", Vector2.zero,
                new Vector2(24f, 6f), new Vector2(380f, 25f), 20f, TextAlignmentOptions.MidlineLeft);
            _leftStatusLabel.text = BuildStatusLineText();
            _lastLeftStatusText = _leftStatusLabel.text;
            _leftZoomLabel = CreateMachineVisionLabel(canvas, "CCTVZoomLabel", Vector2.right,
                new Vector2(-24f, 6f), new Vector2(220f, 25f), 20f, TextAlignmentOptions.MidlineRight);
        }

        private static string BuildStatusLineText()
        {
            if (MonitorFocus.IsTurretPageActive) return "LINK STABLE / TURRET";
            if (MonitorFocus.IsBodycamFeedActive) return "LINK STABLE / CREW";
            CCTVCamera active = QuadCameraAssignment.GetBoundCamera(0);
            if (active != null && active.IsSecurityBroken) return "SIGNAL LOST";
            return active == null ? "NO FEED" : "LINK STABLE";
        }

        private static string BuildZoomText()
        {
            CCTVCamera active = MonitorFocus.IsFacilityFeedActive ? QuadCameraAssignment.GetBoundCamera(0) : null;
            if (active == null || active.Cam == null) return string.Empty;
            float baseFov = SurveillanceBootstrap.Config?.FieldOfView?.Value ?? 75f;
            float zoom = Mathf.Tan(baseFov * Mathf.Deg2Rad * 0.5f) / Mathf.Tan(active.Cam.fieldOfView * Mathf.Deg2Rad * 0.5f);
            return $"ZOOM {zoom:0.0}x";
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
            ApplySingleCameraRect(rootRt, 0f);

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
            ApplySingleCameraRect(rt, 0f);

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

            Color color = MachineVisionInk;
            CreateReticleSegment(rt, "CenterH", Vector2.zero, new Vector2(10f, 1.5f), new Color(color.r, color.g, color.b, 0.48f));
            CreateReticleSegment(rt, "CenterV", Vector2.zero, new Vector2(1.5f, 10f), new Color(color.r, color.g, color.b, 0.48f));
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

            string feed = MonitorFocus.GetActiveFeedDisplayLabel().Replace("CAM_", "CAM ");
            string label = MonitorFocus.IsFacilityFeedActive ? feed + " / INTERIOR"
                : MonitorFocus.IsBodycamFeedActive ? "CREW / " + feed : feed;
            string page = MonitorFocus.GetActiveFeedPageLabel();
            bool layoutChanged = false;
            if (_leftCameraLabel != null)
            {
                RectTransform labelRect = _leftCameraLabel.rectTransform;
                float labelWidth = 510f;
                float fontSize = 23f;
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
        /// Clock, live state, status readout and signal-lost plate.
        ///
        /// State that reflects a real event (a camera breaking, the bound camera
        /// changing) is tracked unconditionally, so the plate is already correct on the
        /// first frame the monitor comes back into view. The purely animated parts —
        /// clock text — only advance while the monitor is observable.
        /// ShouldRequestCompositor deliberately checks observability BEFORE the dirty
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
            string zoom = BuildZoomText();
            if (_leftZoomLabel != null && _leftZoomLabel.text != zoom)
            {
                _leftZoomLabel.text = zoom;
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

            bool recOn = hasFeed && !signalLost;
            if (recOn != _lastLeftRecOn)
            {
                _lastLeftRecOn = recOn;
                ApplyLeftLiveState(recOn);
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

        private static void ApplyLeftLiveState(bool on)
        {
            if (_leftRecLabel == null) return;
            _leftRecLabel.text = on ? "+ LIVE" : "OFFLINE";
            _leftRecLabel.color = on ? MachineVisionInk : new Color(1f, 0.55f, 0.35f, 0.85f);
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
