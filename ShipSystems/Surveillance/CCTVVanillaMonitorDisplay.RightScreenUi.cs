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
        private static void CreateRightRigUi(RectTransform canvas)
        {
            CreateBorder(canvas, new Color(0f, 0.8f, 0.2f, 0.65f), 5f);

            GameObject radarGo = new GameObject("RadarView", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            radarGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(radarGo, UiRenderLayer);
            RectTransform radarRt = radarGo.GetComponent<RectTransform>();
            radarRt.anchorMin = new Vector2(0.5f, 0.5f);
            radarRt.anchorMax = new Vector2(0.5f, 0.5f);
            radarRt.pivot = new Vector2(0.5f, 0.5f);
            radarRt.anchoredPosition = Vector2.zero;
            radarRt.sizeDelta = new Vector2((MonitorWidth - 36f) * RadarImageScale, (MonitorHeight - 36f) * RadarImageScale);
            radarRt.localEulerAngles = new Vector3(0f, 0f, RadarImageRotationDegrees);
            _rightRadarImage = radarGo.GetComponent<RawImage>();
            _rightRadarImage.texture = Texture2D.blackTexture;
            _rightRadarImage.color = Color.white;
            _rightRadarImage.raycastTarget = false;

            GameObject labelGo = new GameObject("RadarFloorLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(labelGo, UiRenderLayer);

            RectTransform labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 1f);
            labelRt.anchorMax = new Vector2(0f, 1f);
            labelRt.pivot = new Vector2(0f, 1f);
            labelRt.anchoredPosition = new Vector2(30f, -24f);
            labelRt.sizeDelta = new Vector2(420f, 34f);

            _rightRadarLabel = labelGo.GetComponent<TextMeshProUGUI>();
            _rightRadarLabel.text = "RADAR MAP";
            _rightRadarLabel.raycastTarget = false;
            _rightRadarLabel.enableWordWrapping = false;
            _rightRadarLabel.richText = false;
            _rightRadarLabel.alignment = TextAlignmentOptions.TopLeft;
            _rightRadarLabel.fontSize = 22f;
            _rightRadarLabel.color = new Color(1f, 0.58f, 0.08f, 0.94f);
            TryAssignHudFont(_rightRadarLabel);

            GameObject legendGo = new GameObject("RadarLegend", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            legendGo.transform.SetParent(canvas, worldPositionStays: false);
            SetLayerRecursive(legendGo, UiRenderLayer);

            RectTransform legendRt = legendGo.GetComponent<RectTransform>();
            legendRt.anchorMin = new Vector2(0f, 0f);
            legendRt.anchorMax = new Vector2(0f, 0f);
            legendRt.pivot = new Vector2(0f, 0f);
            legendRt.anchoredPosition = new Vector2(30f, 18f);
            legendRt.sizeDelta = new Vector2(940f, 42f);

            _rightRadarLegend = legendGo.GetComponent<TextMeshProUGUI>();
            _rightRadarLegend.text = string.Empty;
            _rightRadarLegend.raycastTarget = false;
            _rightRadarLegend.enableWordWrapping = false;
            // The one label on this screen that needs markup: each entry is
            // wrapped in the <color> of the marker class it names, and the hex
            // comes from the same constants the marker materials are built from.
            _rightRadarLegend.richText = true;
            _rightRadarLegend.alignment = TextAlignmentOptions.BottomLeft;
            _rightRadarLegend.fontSize = 26f;
            // White base only: every visible span carries its own <color> tag,
            // dimmed there, because a TMP colour tag replaces this value rather
            // than tinting it.
            _rightRadarLegend.color = Color.white;
            TryAssignHudFont(_rightRadarLegend);
        }

        private static RawImage CreateSlotImage(RectTransform parent, int slot)
        {
            GameObject backGo = new GameObject($"CCTVSlot{slot}Back", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            backGo.transform.SetParent(parent, worldPositionStays: false);
            SetLayerRecursive(backGo, UiRenderLayer);
            RectTransform backRt = backGo.GetComponent<RectTransform>();
            ApplySingleCameraRect(backRt, 14f);
            Image back = backGo.GetComponent<Image>();
            back.color = Color.black;
            back.raycastTarget = false;

            GameObject go = new GameObject($"CCTVSlot{slot}", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(backRt, worldPositionStays: false);
            SetLayerRecursive(go, UiRenderLayer);
            RectTransform rt = go.GetComponent<RectTransform>();
            Stretch(rt);

            RawImage image = go.GetComponent<RawImage>();
            image.texture = QuadMonitor.GetCurrentDisplayTexture(slot) ?? Texture2D.blackTexture;
            image.color = Color.white;
            image.raycastTarget = false;
            return image;
        }

        private static void ApplySlotRect(RectTransform rt, int slot, float margin)
        {
            bool right = (slot % 2) == 1;
            bool top = slot < 2;
            rt.anchorMin = new Vector2(right ? 0.5f : 0f, top ? 0.5f : 0f);
            rt.anchorMax = new Vector2(right ? 1f : 0.5f, top ? 1f : 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(right ? margin * 0.5f : margin, top ? margin * 0.5f : margin);
            rt.offsetMax = new Vector2(right ? -margin : -margin * 0.5f, top ? -margin : -margin * 0.5f);
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;
        }

        private static void ApplySingleCameraRect(RectTransform rt, float margin)
        {
            if (rt == null)
                return;

            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(margin, margin);
            rt.offsetMax = new Vector2(-margin, -margin);
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;
        }

        private static bool EnsureWorldUiOverlays()
        {
            if (_lowerLeftRoot != null)
                return false;

            if (Time.unscaledTime < _nextProbeAt)
                return false;
            _nextProbeAt = Time.unscaledTime + ProbeIntervalSeconds;

            RectTransform target = ResolveLowerLeftMonitorTarget();
            if (target == null)
            {
                if (!_loggedWaitingForTarget)
                {
                    _loggedWaitingForTarget = true;
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Vanilla monitor display waiting for screenLevelDescription RectTransform.");
                }
                return false;
            }

            CreateWorldOverlay(target, LowerLeftRootName, _leftRenderTexture, out _lowerLeftRoot, out _lowerLeftImage, Vector3.zero);

            _loggedWaitingForTarget = false;
            if (!_loggedCreated)
            {
                _loggedCreated = true;
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Vanilla monitor CCTV overlay bound from lower-left monitor target '{GetPath(target)}'.");
            }
            return true;
        }

        private static void CreateWorldOverlay(RectTransform target, string name, Texture texture, out GameObject root, out RawImage image, Vector3 localOffset)
        {
            Transform parent = target.parent != null ? target.parent : target;
            root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            root.transform.SetParent(parent, worldPositionStays: false);
            RectTransform rt = root.GetComponent<RectTransform>();
            if (target.parent != null)
            {
                CopyRectTransform(target, rt);
                rt.localPosition += localOffset;
            }
            else
            {
                Stretch(rt);
            }

            image = root.GetComponent<RawImage>();
            image.texture = texture != null ? texture : Texture2D.blackTexture;
            image.color = Color.white;
            image.raycastTarget = false;
            root.transform.SetAsLastSibling();
        }

        private static RectTransform ResolveLowerLeftMonitorTarget()
        {
            StartOfRound sor = StartOfRound.Instance;
            RectTransform levelText = sor != null && sor.screenLevelDescription != null
                ? sor.screenLevelDescription.rectTransform
                : null;
            if (levelText == null)
                return null;

            RectTransform parent = levelText.parent as RectTransform;
            return parent != null ? parent : levelText;
        }

        private static Texture ResolveLeftFeedTexture()
        {
            if (MonitorFocus.IsTurretPageActive)
                return ShipTurretController.ViewTexture ?? Texture2D.blackTexture;

            if (MonitorFocus.IsBodycamFeedActive)
            {
                return OpenBodyCamsCompat.TryGetFocusTexture(out Texture bodycamTexture)
                    ? bodycamTexture
                    : Texture2D.blackTexture;
            }

            return QuadMonitor.GetCurrentDisplayTexture(0) ?? Texture2D.blackTexture;
        }

        private static bool SyncSlotTextures()
        {
            if (_slotImages == null)
                return false;

            bool changed = false;
            for (int i = 0; i < _slotImages.Length; i++)
            {
                if (_slotImages[i] == null)
                    continue;

                Texture texture = i == 0
                    ? ResolveLeftFeedTexture()
                    : QuadMonitor.GetCurrentDisplayTexture(i) ?? Texture2D.blackTexture;
                if (_slotImages[i].texture != texture)
                {
                    _slotImages[i].texture = texture;
                    changed = true;
                }
            }

            if (_lowerLeftImage != null && _lowerLeftImage.texture != _leftRenderTexture)
            {
                _lowerLeftImage.texture = _leftRenderTexture;
                changed = true;
            }

            return changed;
        }

        private static void SyncRightMonitor(bool allowRadarWork)
        {
            if (_rightRadarImage != null)
            {
                Texture texture = allowRadarWork
                    ? VanillaRadarFeed.ResolveTexture()
                    : VanillaRadarFeed.PeekTexture();
                if (texture == null)
                    texture = RadarOverlay.ResolveMapTexture();
                texture = texture ?? Texture2D.blackTexture;
                if (_rightRadarImage.texture != texture)
                {
                    _rightRadarImage.texture = texture;
                    _rightCompositorDirty = true;
                }
            }

            bool radarFeedLive = allowRadarWork && VanillaRadarFeed.HasLiveTexture;

            if (_rightRadarLabel != null)
            {
                string label = radarFeedLive
                    ? VanillaRadarFeed.GetStatusLabel()
                    : RadarOverlay.GetActiveInteriorFloorLabel();
                if (!string.Equals(_rightRadarLabel.text, label, System.StringComparison.Ordinal))
                {
                    _rightRadarLabel.text = label;
                    _rightCompositorDirty = true;
                }
            }

            if (_rightRadarLegend != null)
            {
                // Blank rather than hidden: an empty string costs the same as a
                // disabled component here and keeps the compositor's dirty
                // tracking to the single string comparison it already does.
                string legend = radarFeedLive ? VanillaRadarFeed.GetLegendLabel() : string.Empty;
                if (!string.Equals(_rightRadarLegend.text, legend, System.StringComparison.Ordinal))
                {
                    _rightRadarLegend.text = legend;
                    _rightCompositorDirty = true;
                }
            }
        }

        private static bool ShouldRenderRightCompositor(float now, bool monitorVisible)
        {
            if (_rightCompositorDirty)
                return true;
            if (MonitorFocus.IsStationRadarLookActive &&
                _nextRightCompositorRenderAt - now > RightFocusedCompositorIntervalSeconds)
            {
                return true;
            }
            return now >= _nextRightCompositorRenderAt;
        }

        private static float ResolveRightCompositorInterval(bool monitorVisible)
        {
            if (MonitorFocus.IsFocused)
                return MonitorFocus.IsStationRadarLookActive
                    ? RightFocusedCompositorIntervalSeconds
                    : RightFocusedIdleCompositorIntervalSeconds;
            return monitorVisible
                ? VisibleAmbientCompositorIntervalSeconds
                : HiddenAmbientCompositorIntervalSeconds;
        }

        private static void RenderMonitorTextures(float now, bool monitorVisible)
        {
            try
            {
                RunDisplayPerfStep("CCTVVanillaMonitorDisplay.RenderMonitorTextures.left", () => _leftCamera?.Render());
            }
            catch (Exception ex) { SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Lower-left CCTV monitor render failed: {ex.Message}"); }

            if (ShouldRenderRightCompositor(now, monitorVisible))
            {
                try
                {
                    RunDisplayPerfStep("CCTVVanillaMonitorDisplay.RenderMonitorTextures.right", () => _rightCamera?.Render());
                    _nextRightCompositorRenderAt = now + ResolveRightCompositorInterval(monitorVisible);
                    _rightCompositorDirty = false;
                }
                catch (Exception ex) { SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Right CCTV monitor render failed: {ex.Message}"); }
            }
        }

        private static void RunDisplayPerfStep(string stepName, System.Action action)
        {
            if (action == null)
                return;

            if (!MonitorFocus.IsFocused)
            {
                action();
                return;
            }

            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                action();
            }
            finally
            {
                FocusPerfProbe.RecordStep(
                    stepName,
                    System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
            }
        }

    }
}
