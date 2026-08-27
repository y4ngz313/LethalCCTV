using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DunGen;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using LethalCompanyInputUtils.Api;
using LethalCompanyInputUtils.BindingPathEnums;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class MonitorFocus
    {
        private static void CreateOverlayBackground(Transform parent)
        {
            var bg = new GameObject("Background");
            bg.transform.SetParent(parent, worldPositionStays: false);
            var rt = bg.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var image = bg.AddComponent<Image>();
            image.color = new Color(0.018f, 0.052f, 0.038f, 1f);
            image.raycastTarget = false;
        }

        // Shared dark console plate behind the feed panes and radar.
        private static void CreateOverlayWorkstationBackplate(Transform parent)
        {
            float width = OVERLAY_WIDTH + OVERLAY_BACKPLATE_MARGIN * 2f;
            float height = OVERLAY_HEIGHT + OVERLAY_BACKPLATE_MARGIN * 2f;

            var panel = new GameObject("WorkstationBackplate");
            panel.transform.SetParent(parent, worldPositionStays: false);
            var rect = panel.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);

            var image = panel.AddComponent<Image>();
            image.sprite = EnsureTerminalBackdropSprite();
            image.color = Color.white;
            image.raycastTarget = false;

            CreateCrtScanlineOverlay(parent);

            // Outer rim.
            CreateBackplateRule(parent, "BackplateTopRule", new Vector2(0f, height / 2f), new Vector2(width, OVERLAY_BACKPLATE_RULE_PX));
            CreateBackplateRule(parent, "BackplateBottomRule", new Vector2(0f, -height / 2f), new Vector2(width, OVERLAY_BACKPLATE_RULE_PX));
            CreateBackplateRule(parent, "BackplateLeftRule", new Vector2(-width / 2f, 0f), new Vector2(OVERLAY_BACKPLATE_RULE_PX, height));
            CreateBackplateRule(parent, "BackplateRightRule", new Vector2(width / 2f, 0f), new Vector2(OVERLAY_BACKPLATE_RULE_PX, height));

            // Divider between the top CCTV grid and the compact lower
            // terminal / radar / controls section.
            float dividerY = (OVERLAY_HEIGHT * 0.5f) - OVERLAY_TOP_SECTION_HEIGHT;
            CreateBackplateRule(parent, "BackplateSectionDivider", new Vector2(0f, dividerY), new Vector2(width, OVERLAY_BACKPLATE_RULE_PX));

            float bottomDividerX = GetBottomLeftDockCenterX() + OVERLAY_LEFT_COLUMN_WIDTH * 0.5f + OVERLAY_GAP * 0.5f;
            CreateBackplateRule(parent, "BackplateBottomColumnDivider", new Vector2(bottomDividerX, GetBottomDockCenterY()), new Vector2(OVERLAY_BACKPLATE_RULE_PX, OVERLAY_BOTTOM_DOCK_HEIGHT));

            float rightSectionDividerY = GetBottomSectionBottomY() + OVERLAY_BOTTOM_MARGIN + OVERLAY_RIGHT_BOTTOM_RADAR_HEIGHT + OVERLAY_RIGHT_DOCK_GAP * 0.5f;
            CreateBackplateRule(parent, "BackplateRightSectionDivider", new Vector2(GetRightColumnCenterX(), rightSectionDividerY), new Vector2(OVERLAY_RIGHT_COLUMN_WIDTH, OVERLAY_BACKPLATE_RULE_PX));

            CreateBackplateRule(parent, "BackplateControlsDivider", new Vector2(GetRightColumnCenterX(), GetRightTopHalfCenter(isLeftHalf: true).y), new Vector2(OVERLAY_BACKPLATE_RULE_PX, OVERLAY_RIGHT_TOP_PANEL_HEIGHT));
        }

        private static void CreateCrtScanlineOverlay(Transform parent)
        {
            var go = new GameObject("CRTScanlines");
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = go.AddComponent<Image>();
            image.sprite = EnsureCrtScanlineSprite();
            image.type = Image.Type.Tiled;
            image.color = new Color(0.05f, 0.78f, 0.23f, 0.055f);
            image.raycastTarget = false;
        }

        private static Sprite EnsureTerminalBackdropSprite()
        {
            if (_terminalBackdropSprite != null)
                return _terminalBackdropSprite;

            const int width = 256;
            const int height = 192;
            _terminalBackdropTexture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = "LethalCCTV_FocusTerminalBackplate",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color32[] pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float nx = (x / (float)(width - 1)) * 2f - 1f;
                    float ny = (y / (float)(height - 1)) * 2f - 1f;
                    float edge = Mathf.Clamp01((Mathf.Sqrt(nx * nx * 0.78f + ny * ny * 1.25f) - 0.34f) / 0.74f);
                    int noise = ((x * 17 + y * 31 + ((x ^ y) * 7)) & 7) - 3;
                    int scan = (y % 4 == 0) ? 18 : ((y % 4 == 2) ? -2 : 0);
                    int grid = (x % 64 == 0 || y % 48 == 0) ? 18 : 0;
                    float darken = 1f - edge * 0.24f;
                    int r = Mathf.RoundToInt((18 + noise) * darken);
                    int g = Mathf.RoundToInt((70 + scan + grid + noise) * darken);
                    int b = Mathf.RoundToInt((50 + noise) * darken);
                    pixels[y * width + x] = new Color32(ToByte(r), ToByte(g), ToByte(b), 255);
                }
            }

            _terminalBackdropTexture.SetPixels32(pixels);
            _terminalBackdropTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            _terminalBackdropSprite = Sprite.Create(_terminalBackdropTexture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), pixelsPerUnit: 1f);
            return _terminalBackdropSprite;
        }

        private static byte ToByte(int value)
        {
            return (byte)Mathf.Clamp(value, 0, 255);
        }

        private static Sprite EnsureCrtScanlineSprite()
        {
            if (_crtScanlineSprite != null)
                return _crtScanlineSprite;

            _crtScanlineTexture = new Texture2D(1, 8, TextureFormat.RGBA32, mipChain: false)
            {
                name = "LethalCCTV_FocusCrtScanlines",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };

            Color32[] pixels = new Color32[8];
            pixels[0] = new Color32(80, 255, 128, 115);
            for (int i = 1; i < pixels.Length; i++)
                pixels[i] = new Color32(0, 0, 0, 0);
            _crtScanlineTexture.SetPixels32(pixels);
            _crtScanlineTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            _crtScanlineSprite = Sprite.Create(_crtScanlineTexture, new Rect(0f, 0f, 1f, 8f), new Vector2(0.5f, 0.5f), pixelsPerUnit: 1f);
            return _crtScanlineSprite;
        }

        private static void CreateBackplateRule(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var image = go.AddComponent<Image>();
            image.color = new Color(0.045f, 0.64f, 0.27f, 0.98f);
            image.raycastTarget = false;
        }

        private static Vector2 GetTurretPaneCenter()
        {
            float panelTop = OVERLAY_HEIGHT * 0.5f;
            float dividerY = panelTop - OVERLAY_TOP_SECTION_HEIGHT;
            float topLimit = panelTop - OVERLAY_STATUS_HEIGHT - 18f;
            float bottomLimit = dividerY + 20f;
            return new Vector2(0f, (topLimit + bottomLimit) * 0.5f);
        }

        private static Vector2 GetTurretPaneSize()
        {
            return new Vector2(
                OVERLAY_QUAD_WIDTH * 2f + OVERLAY_GAP,
                OVERLAY_QUAD_HEIGHT * 2f + OVERLAY_GAP);
        }

        private static Vector2 GetOverlayPaneCenter(int slotIndex)
        {
            if (IsTurretPageActive && slotIndex == 0)
                return GetTurretPaneCenter();

            // 2x2 grid centered inside the bounded top CCTV section.
            float leftX = -(OVERLAY_QUAD_WIDTH * 0.5f + OVERLAY_GAP * 0.5f);
            float rightX = +(OVERLAY_QUAD_WIDTH * 0.5f + OVERLAY_GAP * 0.5f);

            float panelTop = OVERLAY_HEIGHT * 0.5f;
            float dividerY = panelTop - OVERLAY_TOP_SECTION_HEIGHT;
            float topLimit = panelTop - OVERLAY_STATUS_HEIGHT - 18f;
            float bottomLimit = dividerY + 20f;
            float gridCenterY = (topLimit + bottomLimit) * 0.5f;
            float rowOffset = (OVERLAY_QUAD_HEIGHT + OVERLAY_GAP) * 0.5f;
            float rowTopY = gridCenterY + rowOffset;
            float rowBottomY = gridCenterY - rowOffset;

            switch (slotIndex)
            {
                case 0: return new Vector2(leftX, rowTopY);
                case 1: return new Vector2(rightX, rowTopY);
                case 2: return new Vector2(leftX, rowBottomY);
                default: return new Vector2(rightX, rowBottomY);
            }
        }

        private static Vector2 GetOverlayPaneSize(int slotIndex)
        {
            if (IsTurretPageActive && slotIndex == 0)
                return GetTurretPaneSize();

            return new Vector2(OVERLAY_QUAD_WIDTH, OVERLAY_QUAD_HEIGHT);
        }

        internal static bool TryGetOverlayPaneGeometry(int slotIndex, out Vector2 center, out Vector2 size)
        {
            center = Vector2.zero;
            size = Vector2.zero;
            if (_overlayPaneRects == null || slotIndex < 0 || slotIndex >= _overlayPaneRects.Length)
                return false;

            RectTransform pane = _overlayPaneRects[slotIndex];
            if (pane == null)
                return false;

            center = pane.anchoredPosition;
            size = pane.sizeDelta;
            if (size.x <= 1f || size.y <= 1f)
                size = GetOverlayPaneSize(slotIndex);
            return size.x > 1f && size.y > 1f;
        }

        private static Image CreateOverlayOutline(Transform parent, int slotIndex)
        {
            Vector2 center = GetOverlayPaneCenter(slotIndex);
            Vector2 size = GetOverlayPaneSize(slotIndex);
            var go = new GameObject($"OverlayQuadrantOutline_{slotIndex}");
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = center;
            rect.sizeDelta = size + new Vector2(OVERLAY_OUTLINE_BORDER_PX * 2f, OVERLAY_OUTLINE_BORDER_PX * 2f);
            if (_overlayOutlineRects != null && slotIndex >= 0 && slotIndex < _overlayOutlineRects.Length)
            {
                _overlayOutlineRects[slotIndex] = rect;
            }
            var img = go.AddComponent<Image>();
            img.color = Color.clear;
            img.raycastTarget = false;
            if (_overlayPaneFrames != null && slotIndex >= 0 && slotIndex < _overlayPaneFrames.Length)
            {
                _overlayPaneFrames[slotIndex] = CreatePaneFrame(go.transform, slotIndex);
                ApplyPaneFrameLayout(_overlayPaneFrames[slotIndex], rect.sizeDelta);
            }
            return img;
        }

        private static PaneFrame CreatePaneFrame(Transform parent, int slotIndex)
        {
            var frame = new PaneFrame { Segments = new Image[8] };
            for (int i = 0; i < frame.Segments.Length; i++)
            {
                var go = new GameObject($"OverlayQuadrantFrame_{slotIndex}_{i}");
                go.transform.SetParent(parent, worldPositionStays: false);
                var rect = go.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                var image = go.AddComponent<Image>();
                image.color = Color.clear;
                image.raycastTarget = false;
                frame.Segments[i] = image;
            }
            return frame;
        }

        private static void ApplyPaneFrameLayout(PaneFrame frame, Vector2 outlineSize)
        {
            if (frame?.Segments == null || frame.Segments.Length < 8) return;
            float halfW = outlineSize.x * 0.5f;
            float halfH = outlineSize.y * 0.5f;
            float thickness = OVERLAY_FRAME_THICKNESS_PX;
            float length = Mathf.Min(OVERLAY_FRAME_CORNER_PX, Mathf.Min(outlineSize.x, outlineSize.y) * 0.27f);

            SetPaneFrameSegment(frame.Segments[0], new Vector2(-halfW + length * 0.5f, halfH - thickness * 0.5f), new Vector2(length, thickness));
            SetPaneFrameSegment(frame.Segments[1], new Vector2(-halfW + thickness * 0.5f, halfH - length * 0.5f), new Vector2(thickness, length));
            SetPaneFrameSegment(frame.Segments[2], new Vector2(halfW - length * 0.5f, halfH - thickness * 0.5f), new Vector2(length, thickness));
            SetPaneFrameSegment(frame.Segments[3], new Vector2(halfW - thickness * 0.5f, halfH - length * 0.5f), new Vector2(thickness, length));
            SetPaneFrameSegment(frame.Segments[4], new Vector2(-halfW + length * 0.5f, -halfH + thickness * 0.5f), new Vector2(length, thickness));
            SetPaneFrameSegment(frame.Segments[5], new Vector2(-halfW + thickness * 0.5f, -halfH + length * 0.5f), new Vector2(thickness, length));
            SetPaneFrameSegment(frame.Segments[6], new Vector2(halfW - length * 0.5f, -halfH + thickness * 0.5f), new Vector2(length, thickness));
            SetPaneFrameSegment(frame.Segments[7], new Vector2(halfW - thickness * 0.5f, -halfH + length * 0.5f), new Vector2(thickness, length));
        }

        private static void SetPaneFrameSegment(Image image, Vector2 position, Vector2 size)
        {
            if (image == null) return;
            RectTransform rect = image.rectTransform;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        internal static void SetOverlayFrameTint(int slotIndex, Color color)
        {
            if (_overlayPaneFrames == null || slotIndex < 0 || slotIndex >= _overlayPaneFrames.Length)
                return;

            PaneFrame frame = _overlayPaneFrames[slotIndex];
            if (frame?.Segments == null) return;
            for (int i = 0; i < frame.Segments.Length; i++)
            {
                if (frame.Segments[i] != null)
                    frame.Segments[i].color = color;
            }
        }

        private static RawImage CreateOverlayQuadrant(Transform parent, int slotIndex)
        {
            // Legacy 2x2 path retained for the physical focus canvas.
            Vector2 center = GetOverlayPaneCenter(slotIndex);
            Vector2 size = GetOverlayPaneSize(slotIndex);
            var go = new GameObject($"OverlayQuadrant_{slotIndex}");
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = center;
            rect.sizeDelta = size;
            if (_overlayPaneRects != null && slotIndex >= 0 && slotIndex < _overlayPaneRects.Length)
            {
                _overlayPaneRects[slotIndex] = rect;
            }
            var rawImage = go.AddComponent<RawImage>();
            rawImage.texture = QuadMonitor.GetCurrentDisplayTexture(slotIndex);
            rawImage.raycastTarget = false;
            return rawImage;
        }

        private static Image CreateOverlayCameraLabel(Transform parent, int slotIndex, out TextMeshProUGUI label)
        {
            // Legacy label path; new layout uses per-slot label helpers.
            Vector2 center = GetOverlayPaneCenter(slotIndex);
            Vector2 size = GetOverlayPaneSize(slotIndex);
            var bg = new GameObject($"OverlayCameraLabelBg_{slotIndex}");
            bg.transform.SetParent(parent, worldPositionStays: false);
            var rect = bg.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(
                center.x - size.x / 2f + OVERLAY_LABEL_INSET,
                center.y + size.y / 2f - OVERLAY_LABEL_INSET);
            rect.sizeDelta = new Vector2(OVERLAY_LABEL_WIDTH, OVERLAY_LABEL_HEIGHT);
            if (_overlayCameraLabelRects != null && slotIndex >= 0 && slotIndex < _overlayCameraLabelRects.Length)
            {
                _overlayCameraLabelRects[slotIndex] = rect;
            }
            var image = bg.AddComponent<Image>();
            image.color = new Color(0.002f, 0.022f, 0.014f, 0.78f);
            image.raycastTarget = false;

            var textGo = new GameObject($"OverlayCameraLabel_{slotIndex}");
            textGo.transform.SetParent(bg.transform, worldPositionStays: false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);
            label = textGo.AddComponent<TextMeshProUGUI>();
            label.text = "CAM_--";
            label.fontSize = 20f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = Color.white;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = false;
            label.raycastTarget = false;
            ApplyOverlayFont(label);
            image.enabled = false;
            label.enabled = false;
            return image;
        }

        private static bool ShouldRightAlignCameraLabel(int slotIndex)
        {
            return slotIndex == 0
                && IsBodycamFeedActive
                && OpenBodyCamsCompat.TryGetPlayerName(ActiveBodycamFeedIndex, out _);
        }

        private static void ApplyCameraLabelLayout(int slotIndex, bool alignRight)
        {
            if (_overlayCameraLabelRects == null
                || slotIndex < 0
                || slotIndex >= _overlayCameraLabelRects.Length)
                return;

            RectTransform rect = _overlayCameraLabelRects[slotIndex];
            if (rect == null)
                return;

            Vector2 center = GetOverlayPaneCenter(slotIndex);
            Vector2 size = GetOverlayPaneSize(slotIndex);
            float topInset = alignRight ? OVERLAY_BODYCAM_LABEL_TOP_INSET : OVERLAY_LABEL_INSET;
            rect.pivot = new Vector2(alignRight ? 1f : 0f, 1f);
            rect.anchoredPosition = new Vector2(
                alignRight
                    ? center.x + size.x * 0.5f - OVERLAY_LABEL_INSET
                    : center.x - size.x * 0.5f + OVERLAY_LABEL_INSET,
                center.y + size.y * 0.5f - topInset);
            rect.sizeDelta = new Vector2(
                alignRight ? OVERLAY_BODYCAM_LABEL_WIDTH : OVERLAY_LABEL_WIDTH,
                OVERLAY_LABEL_HEIGHT);

            if (_overlayCameraLabels != null
                && slotIndex < _overlayCameraLabels.Length
                && _overlayCameraLabels[slotIndex] != null)
            {
                _overlayCameraLabels[slotIndex].alignment = alignRight
                    ? TextAlignmentOptions.MidlineRight
                    : TextAlignmentOptions.MidlineLeft;
            }
        }

        private static void CreateOverlayChrome(Transform parent)
        {
            // Header bar sits at the very top of the CCTV grid region.
            float headerY = (OVERLAY_HEIGHT * 0.5f) - (OVERLAY_STATUS_HEIGHT * 0.5f) - 4f;
            Image header = CreateOverlayStatusBar(parent, "OverlayHeader", new Vector2(0f, headerY), new Vector2(OVERLAY_WIDTH, OVERLAY_STATUS_HEIGHT));
            _overlayHeaderText = CreateOverlayText(header.transform, "HeaderText", "LETHALCCTV SECURITY FEED", 14f, OVERLAY_ACTIVE_GREEN, TextAlignmentOptions.MidlineLeft);
            SetRectFill(_overlayHeaderText.rectTransform, new Vector2(28f, 0f), new Vector2(-330f, 0f));
            _overlayPageText = CreateOverlayText(header.transform, "PageText", "PAGE 1/1", 12f, Color.white, TextAlignmentOptions.MidlineRight);
            SetRectFill(_overlayPageText.rectTransform, new Vector2(620f, 0f), new Vector2(-28f, 0f));

            // Clock and REC light live in the dead space this bar already had between
            // the title on the left and PAGE on the right — no layout change, and the
            // existing two entries keep their exact rects.
            _overlayClockText = CreateOverlayText(header.transform, "HeaderClockText", CctvScreenClock.UnavailableText, 13f, Color.white, TextAlignmentOptions.Center);
            SetRectFill(_overlayClockText.rectTransform, new Vector2(330f, 0f), new Vector2(-330f, 0f));

            var recDotGo = new GameObject("HeaderRecDot");
            recDotGo.transform.SetParent(header.transform, worldPositionStays: false);
            var recDotRect = recDotGo.AddComponent<RectTransform>();
            recDotRect.anchorMin = new Vector2(0f, 0.5f);
            recDotRect.anchorMax = new Vector2(0f, 0.5f);
            recDotRect.pivot = new Vector2(0f, 0.5f);
            recDotRect.anchoredPosition = new Vector2(640f, 0f);
            recDotRect.sizeDelta = new Vector2(9f, 9f);
            _overlayRecDot = recDotGo.AddComponent<Image>();
            _overlayRecDot.color = OVERLAY_REC_RED;
            _overlayRecDot.raycastTarget = false;

            _overlayRecText = CreateOverlayText(header.transform, "HeaderRecText", "REC", 12f, OVERLAY_REC_RED, TextAlignmentOptions.MidlineLeft);
            SetRectFill(_overlayRecText.rectTransform, new Vector2(656f, 0f), new Vector2(-230f, 0f));

            _overlayRecOn = true;
            RefreshOverlayHeaderStatus(force: true);

            // Active-camera label sits inside the main camera view's top-left
            // alongside the header. The footer concept is gone — the bottom
            // 1/3 is its own controls section built by CreateOperatorStatsPanel.
        }

        /// <summary>
        /// Drives the header clock text and the REC blink. Sampled at 4 Hz, which is
        /// ample for a 1 Hz blink; unlike the live monitor's throttled compositor this
        /// canvas redraws every frame, so there is no dirty flag to manage here.
        /// </summary>
        private static void RefreshOverlayHeaderStatus(bool force)
        {
            if (_overlayClockText == null && _overlayRecText == null && _overlayRecDot == null)
                return;

            float now = Time.unscaledTime;
            if (!force && now < _nextOverlayHeaderRefreshTime)
                return;
            _nextOverlayHeaderRefreshTime = now + 0.25f;

            if (_overlayClockText != null)
            {
                string clock = CctvScreenClock.GetShipTime24h();
                if (!string.Equals(_overlayClockText.text, clock, StringComparison.Ordinal))
                {
                    _overlayClockText.text = clock;
                    CctvScreenClock.LogComparisonOnce(clock);
                }
            }

            bool recOn = Mathf.Repeat(now, OVERLAY_REC_BLINK_PERIOD) < OVERLAY_REC_BLINK_PERIOD * OVERLAY_REC_BLINK_ON_FRACTION;
            if (!force && recOn == _overlayRecOn)
                return;

            _overlayRecOn = recOn;
            if (_overlayRecDot != null)
                _overlayRecDot.color = new Color(OVERLAY_REC_RED.r, OVERLAY_REC_RED.g, OVERLAY_REC_RED.b, recOn ? 1f : 0.14f);
            if (_overlayRecText != null)
                _overlayRecText.color = new Color(OVERLAY_REC_RED.r, OVERLAY_REC_RED.g, OVERLAY_REC_RED.b, recOn ? 1f : 0.25f);
        }

        private static Image CreateOverlayStatusBar(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var image = go.AddComponent<Image>();
            image.color = new Color(0.002f, 0.022f, 0.014f, 0.78f);
            image.raycastTarget = false;
            return image;
        }

        private static Vector2 GetBottomLeftDockPosition()
        {
            return new Vector2(GetBottomLeftDockCenterX(), GetBottomDockCenterY());
        }

        private static Vector2 GetBottomLeftDockSize()
        {
            return new Vector2(OVERLAY_LEFT_COLUMN_WIDTH, OVERLAY_BOTTOM_DOCK_HEIGHT);
        }

        private static Vector2 GetRightBottomRadarPosition()
        {
            float y = GetBottomSectionBottomY() + OVERLAY_BOTTOM_MARGIN + OVERLAY_RIGHT_BOTTOM_RADAR_HEIGHT * 0.5f;
            return new Vector2(GetRightColumnCenterX(), y);
        }

        private static Vector2 GetRightBottomRadarSize()
        {
            return new Vector2(OVERLAY_RIGHT_COLUMN_WIDTH, OVERLAY_RIGHT_BOTTOM_RADAR_HEIGHT);
        }

        private static void CreateSignalLogPanel(Transform parent)
        {
            var frame = new GameObject("SignalLogFrame");
            frame.transform.SetParent(parent, worldPositionStays: false);
            _signalLogRect = frame.AddComponent<RectTransform>();
            _signalLogRect.anchorMin = new Vector2(0.5f, 0.5f);
            _signalLogRect.anchorMax = new Vector2(0.5f, 0.5f);
            _signalLogRect.pivot = new Vector2(0.5f, 0.5f);
            _signalLogRect.anchoredPosition = GetRightBottomRadarPosition();
            _signalLogRect.sizeDelta = GetRightBottomRadarSize();

            var frameImage = frame.AddComponent<Image>();
            frameImage.color = new Color(0.010f, 0.058f, 0.034f, 0.82f);
            frameImage.raycastTarget = false;

            _signalLogTitleText = CreateOverlayText(frame.transform, "SignalLogTitle", "SIGNAL LOG", 10f, OVERLAY_AMBER, TextAlignmentOptions.TopLeft);
            SetRectFill(_signalLogTitleText.rectTransform, new Vector2(14f, 0f), new Vector2(-14f, -6f));

            _signalLogText = CreateOverlayText(frame.transform, "SignalLogText", "", 8.8f, new Color(0.54f, 0.76f, 0.60f, 1f), TextAlignmentOptions.MidlineLeft);
            _signalLogText.lineSpacing = -7f;
            SetRectFill(_signalLogText.rectTransform, new Vector2(14f, 8f), new Vector2(-14f, -24f));
            RefreshSignalLogPanel(force: true);
        }

        private static Vector2 GetRightTopHalfSize()
        {
            float width = (OVERLAY_RIGHT_COLUMN_WIDTH - OVERLAY_RIGHT_DOCK_GAP) * 0.5f;
            return new Vector2(width, OVERLAY_RIGHT_TOP_PANEL_HEIGHT);
        }

        private static Vector2 GetRightTopHalfCenter(bool isLeftHalf)
        {
            Vector2 size = GetRightTopHalfSize();
            float xOffset = size.x * 0.5f + OVERLAY_RIGHT_DOCK_GAP * 0.5f;
            float x = GetRightColumnCenterX() + (isLeftHalf ? -xOffset : xOffset);
            float y = GetBottomSectionTopY() - OVERLAY_BOTTOM_MARGIN - OVERLAY_RIGHT_TOP_PANEL_HEIGHT * 0.5f;
            return new Vector2(x, y);
        }

        private static float GetBottomLeftDockCenterX()
        {
            return -(OVERLAY_RIGHT_COLUMN_WIDTH + OVERLAY_GAP) * 0.5f;
        }

        private static float GetRightColumnCenterX()
        {
            return (OVERLAY_LEFT_COLUMN_WIDTH + OVERLAY_GAP) * 0.5f;
        }

        private static float GetBottomSectionTopY()
        {
            return OVERLAY_HEIGHT * 0.5f - OVERLAY_TOP_SECTION_HEIGHT;
        }

        private static float GetBottomSectionBottomY()
        {
            return -OVERLAY_HEIGHT * 0.5f;
        }

        private static float GetBottomDockCenterY()
        {
            return GetBottomSectionBottomY() + OVERLAY_BOTTOM_MARGIN + OVERLAY_BOTTOM_DOCK_HEIGHT * 0.5f;
        }

        private static void RefreshFocusDockLayout()
        {
            // The mainframe control menu occupies the same bottom-left dock as the hacking overlay,
            // so either being open puts the layout into "dock" mode (radar moves to the right column).
            bool dockOpen = IsHackingOverlayOpen || IsMainframeOverlayOpen;
            bool turretOpen = IsTurretPageActive;
            if (_focusDockLayoutInitialized && _lastHackDockState == dockOpen && _lastDockTurretState == turretOpen)
                return;

            if (_lastHackDockState != dockOpen)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Focus dock layout mode={(dockOpen ? "terminal-dock" : "normal")}.");
            }
            ApplyRadarDockLayout(dockOpen);

            if (dockOpen)
                PrepareInteractiveOverlayDock();

            _lastHackDockState = dockOpen;
            _lastDockTurretState = turretOpen;
            _focusDockLayoutInitialized = true;
        }

        private static void ApplyRadarDockLayout(bool hackingOpen)
        {
            if (_radarInsetRect == null) return;

            if (IsTurretPageActive)
            {
                if (_radarInsetRect.gameObject.activeSelf)
                    _radarInsetRect.gameObject.SetActive(false);
                if (_signalLogRect != null && _signalLogRect.gameObject.activeSelf)
                    _signalLogRect.gameObject.SetActive(false);
                return;
            }

            if (!_radarInsetRect.gameObject.activeSelf)
                _radarInsetRect.gameObject.SetActive(true);
            if (_signalLogRect != null)
                _signalLogRect.gameObject.SetActive(!hackingOpen);

            if (hackingOpen)
            {
                _radarInsetRect.anchoredPosition = GetRightBottomRadarPosition();
                _radarInsetRect.sizeDelta = GetRightBottomRadarSize();
            }
            else
            {
                _radarInsetRect.anchoredPosition = GetBottomLeftDockPosition();
                _radarInsetRect.sizeDelta = GetBottomLeftDockSize();
            }

            if (_radarImageRect != null)
            {
                _radarImageRect.offsetMin = new Vector2(2f, 2f);
                _radarImageRect.offsetMax = new Vector2(-2f, -18f);
            }
        }

        private static void CreateRadarInset(Transform parent)
        {
            var frame = new GameObject("RadarInsetFrame");
            frame.transform.SetParent(parent, worldPositionStays: false);
            _radarInsetRect = frame.AddComponent<RectTransform>();
            _radarInsetRect.anchorMin = new Vector2(0.5f, 0.5f);
            _radarInsetRect.anchorMax = new Vector2(0.5f, 0.5f);
            _radarInsetRect.pivot = new Vector2(0.5f, 0.5f);
            var frameImage = frame.AddComponent<Image>();
            frameImage.color = new Color(0.010f, 0.062f, 0.036f, 0.82f);
            frameImage.raycastTarget = false;

            var radarGo = new GameObject("RadarInsetImage");
            radarGo.transform.SetParent(frame.transform, worldPositionStays: false);
            _radarImageRect = radarGo.AddComponent<RectTransform>();
            _radarImageRect.anchorMin = Vector2.zero;
            _radarImageRect.anchorMax = Vector2.one;
            _radarImageRect.offsetMin = new Vector2(2f, 2f);
            _radarImageRect.offsetMax = new Vector2(-2f, -18f);
            _radarInsetImage = radarGo.AddComponent<RawImage>();
            _radarInsetImage.color = Color.white;
            _radarInsetImage.raycastTarget = false;

            _radarInsetStatus = CreateOverlayText(frame.transform, "RadarInsetStatus", "RADAR MAP", 11f, OVERLAY_AMBER, TextAlignmentOptions.MidlineLeft);
            RectTransform statusRect = _radarInsetStatus.rectTransform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.anchoredPosition = new Vector2(0f, -2f);
            statusRect.sizeDelta = new Vector2(-14f, 20f);
            ApplyRadarDockLayout(hackingOpen: false);
            RefreshRadarInsetTexture();
        }

        private static void CreateOperatorStatsPanel(Transform parent)
        {
            var frame = new GameObject("OperatorStatsFrame");
            frame.transform.SetParent(parent, worldPositionStays: false);
            _operatorStatsRect = frame.AddComponent<RectTransform>();
            _operatorStatsRect.anchorMin = new Vector2(0.5f, 0.5f);
            _operatorStatsRect.anchorMax = new Vector2(0.5f, 0.5f);
            _operatorStatsRect.pivot = new Vector2(0.5f, 0.5f);
            _operatorStatsRect.anchoredPosition = GetRightTopHalfCenter(isLeftHalf: true);
            _operatorStatsRect.sizeDelta = GetRightTopHalfSize();

            var frameImage = frame.AddComponent<Image>();
            frameImage.color = new Color(0.010f, 0.055f, 0.032f, 0.82f);
            frameImage.raycastTarget = false;

            _operatorStatsText = CreateOverlayText(frame.transform, "OperatorStatsText", "MAINFRAME // READY", 10.4f, Color.white, TextAlignmentOptions.MidlineLeft);
            _operatorStatsText.lineSpacing = -8f;
            SetRectFill(_operatorStatsText.rectTransform, new Vector2(16f, 6f), new Vector2(-14f, -6f));
            RefreshOperatorStats(force: true);
        }

        private static void CreateCameraScanOverlay(Transform parent)
        {
            var statusGo = new GameObject("CameraScanStatus");
            statusGo.transform.SetParent(parent, worldPositionStays: false);
            var statusRect = statusGo.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0.5f, 0.5f);
            statusRect.anchorMax = new Vector2(0.5f, 0.5f);
            statusRect.pivot = new Vector2(0.5f, 0.5f);
            statusRect.sizeDelta = new Vector2(220f, 28f);
            _scanStatusText = statusGo.AddComponent<TextMeshProUGUI>();
            _scanStatusText.text = "";
            _scanStatusText.fontSize = 18f;
            _scanStatusText.fontStyle = FontStyles.Bold;
            _scanStatusText.alignment = TextAlignmentOptions.Center;
            _scanStatusText.color = OVERLAY_SCAN_BLUE;
            _scanStatusText.raycastTarget = false;
            ApplyOverlayFont(_scanStatusText);
            statusGo.SetActive(false);

            _scanLabels = new ScanLabel[OVERLAY_SCAN_LABEL_COUNT];
            for (int i = 0; i < _scanLabels.Length; i++)
            {
                _scanLabels[i] = CreateCameraScanLabel(parent, i, "CameraScan");
                _scanLabels[i].Tracer = CreateScanTracer(parent, i, "CameraScan");
            }
        }

        private static void CreateCameraReviewMenu(Transform parent)
        {
            _reviewMenuRoot = new GameObject("CameraReviewMenu");
            _reviewMenuRoot.transform.SetParent(parent, worldPositionStays: false);
            RectTransform rect = _reviewMenuRoot.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, 40f);
            rect.sizeDelta = new Vector2(520f, 360f);

            Image bg = _reviewMenuRoot.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.88f);
            bg.raycastTarget = false;

            _reviewMenuText = CreateOverlayText(
                _reviewMenuRoot.transform,
                "CameraReviewMenuText",
                string.Empty,
                16f,
                Color.white,
                TextAlignmentOptions.TopLeft);
            _reviewMenuText.enableWordWrapping = false;
            _reviewMenuText.richText = true;
            _reviewMenuText.lineSpacing = -6f;
            SetRectFill(_reviewMenuText.rectTransform, new Vector2(22f, 18f), new Vector2(-22f, -18f));

            _reviewMenuRoot.SetActive(false);
        }

        private static void CreatePlacementEditorMenu(Transform parent)
        {
            _placementEditorRoot = new GameObject("PlacementEditorMenu");
            _placementEditorRoot.transform.SetParent(parent, worldPositionStays: false);
            RectTransform rect = _placementEditorRoot.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, -18f);
            rect.sizeDelta = new Vector2(610f, 260f);

            Image bg = _placementEditorRoot.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.88f);
            bg.raycastTarget = false;

            _placementEditorText = CreateOverlayText(
                _placementEditorRoot.transform,
                "PlacementEditorMenuText",
                string.Empty,
                16f,
                Color.white,
                TextAlignmentOptions.TopLeft);
            _placementEditorText.enableWordWrapping = false;
            _placementEditorText.richText = true;
            _placementEditorText.lineSpacing = -4f;
            SetRectFill(_placementEditorText.rectTransform, new Vector2(22f, 18f), new Vector2(-22f, -18f));

            _placementEditorRoot.SetActive(false);
        }

        private static ScanLabel CreateCameraScanLabel(Transform parent, int index, string prefix)
        {
            var go = new GameObject($"{prefix}Label_{index}");
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(58f, 58f);

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0f);
            bg.raycastTarget = false;

            Image top = CreateScanMarkerLine(go.transform, "Top");
            Image bottom = CreateScanMarkerLine(go.transform, "Bottom");
            Image left = CreateScanMarkerLine(go.transform, "Left");
            Image right = CreateScanMarkerLine(go.transform, "Right");
            Image center = CreateScanMarkerLine(go.transform, "Center");

            TextMeshProUGUI header = CreateOverlayText(go.transform, "Header", "", 11f, OVERLAY_SCAN_BLUE, TextAlignmentOptions.Center);
            RectTransform headerRect = header.rectTransform;
            headerRect.anchorMin = new Vector2(0.5f, 0f);
            headerRect.anchorMax = new Vector2(0.5f, 0f);
            headerRect.pivot = new Vector2(0.5f, 1f);
            headerRect.anchoredPosition = new Vector2(0f, -5f);
            headerRect.sizeDelta = new Vector2(150f, 20f);
            TextMeshProUGUI sub = CreateOverlayText(go.transform, "SubText", "", 10f, Color.white, TextAlignmentOptions.Center);
            sub.gameObject.SetActive(false);

            go.SetActive(false);
            return new ScanLabel
            {
                Rect = rect,
                Background = bg,
                Top = top,
                Bottom = bottom,
                Left = left,
                Right = right,
                Center = center,
                Header = header,
                SubText = sub,
            };
        }

        private static Image CreateScanTracer(Transform parent, int index, string prefix)
        {
            var go = new GameObject($"{prefix}Tracer_{index}");
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            var image = go.AddComponent<Image>();
            image.color = new Color(OVERLAY_SCAN_BLUE.r, OVERLAY_SCAN_BLUE.g, OVERLAY_SCAN_BLUE.b, 0.68f);
            image.raycastTarget = false;
            go.SetActive(false);
            return image;
        }

        private static Image CreateScanMarkerLine(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            var image = go.AddComponent<Image>();
            image.color = OVERLAY_SCAN_BLUE;
            image.raycastTarget = false;
            return image;
        }

        private static void CreateActivePaneReticle(Transform parent)
        {
            var go = new GameObject("CameraCenterReticle");
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(46f, 46f);
            _centerReticle = go.AddComponent<Image>();
            _centerReticle.color = new Color(0f, 0f, 0f, 0f);
            _centerReticle.raycastTarget = false;
            CreateReticleSegment(go.transform, "Top", new Vector2(0f, 15f), new Vector2(2f, 12f));
            CreateReticleSegment(go.transform, "Bottom", new Vector2(0f, -15f), new Vector2(2f, 12f));
            CreateReticleSegment(go.transform, "Left", new Vector2(-15f, 0f), new Vector2(12f, 2f));
            CreateReticleSegment(go.transform, "Right", new Vector2(15f, 0f), new Vector2(12f, 2f));
            CreateReticleSegment(go.transform, "CenterH", Vector2.zero, new Vector2(8f, 1.5f), 0.48f);
            CreateReticleSegment(go.transform, "CenterV", Vector2.zero, new Vector2(1.5f, 8f), 0.48f);
            go.SetActive(false);
        }

        private static Image CreateReticleSegment(Transform parent, string name, Vector2 position, Vector2 size, float alpha = 0.72f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var image = go.AddComponent<Image>();
            image.color = new Color(0.10f, 1f, 0.35f, alpha);
            image.raycastTarget = false;
            return image;
        }

        private static TextMeshProUGUI CreateOverlayText(Transform parent, string name, string text, float fontSize, Color color, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            ApplyOverlayFont(tmp);
            return tmp;
        }

        private static void SetRectFill(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
        {
            if (rect == null) return;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void ApplyActivePaneLayout()
        {
            if (_overlayPaneRects == null) return;

            for (int slot = 0; slot < 4; slot++)
            {
                Vector2 center = GetOverlayPaneCenter(slot);
                Vector2 size = GetOverlayPaneSize(slot);

                RectTransform pane = _overlayPaneRects[slot];
                if (pane != null)
                {
                    pane.anchoredPosition = center;
                    pane.sizeDelta = size;
                }

                RectTransform outline = _overlayOutlineRects != null && slot < _overlayOutlineRects.Length
                    ? _overlayOutlineRects[slot]
                    : null;
                if (outline != null)
                {
                    outline.anchoredPosition = center;
                    outline.sizeDelta = new Vector2(
                        size.x + OVERLAY_OUTLINE_BORDER_PX * 2f,
                        size.y + OVERLAY_OUTLINE_BORDER_PX * 2f);
                    if (_overlayPaneFrames != null && slot < _overlayPaneFrames.Length)
                        ApplyPaneFrameLayout(_overlayPaneFrames[slot], outline.sizeDelta);
                }

                RectTransform label = _overlayCameraLabelRects != null && slot < _overlayCameraLabelRects.Length
                    ? _overlayCameraLabelRects[slot]
                    : null;
                if (label != null)
                    ApplyCameraLabelLayout(slot, ShouldRightAlignCameraLabel(slot));
            }
        }

        // Private "GetOverlayPaneSize" overload (no IsFocused scaling) used
        // by the layout-creation helpers above — the runtime pane sizing
        // for reticle / scale logic is still applied by the legacy call
        // sites via GetOverlayPaneSize that consults IsFocused below.

        private static void ApplyOverlayFont(TextMeshProUGUI tmp)
        {
            if (tmp == null) return;
            TMP_FontAsset font = ResolveOverlayFontAsset();
            if (font != null) tmp.font = font;
        }

        private static void RefreshOverlayFonts()
        {
            ApplyOverlayFont(_overlayHeaderText);
            ApplyOverlayFont(_overlayPageText);
            ApplyOverlayFont(_overlayClockText);
            ApplyOverlayFont(_overlayRecText);
            ApplyOverlayFont(_overlayActiveText);
            ApplyOverlayFont(_radarInsetStatus);
            ApplyOverlayFont(_operatorStatsText);
            ApplyOverlayFont(_signalLogTitleText);
            ApplyOverlayFont(_signalLogText);
            ApplyOverlayFont(_scanStatusText);

            if (_overlayCameraLabels != null)
            {
                for (int i = 0; i < _overlayCameraLabels.Length; i++)
                {
                    ApplyOverlayFont(_overlayCameraLabels[i]);
                }
            }

            if (_scanLabels != null)
            {
                for (int i = 0; i < _scanLabels.Length; i++)
                {
                    ApplyOverlayFont(_scanLabels[i]?.Header);
                    ApplyOverlayFont(_scanLabels[i]?.SubText);
                }
            }
        }

        private static TMP_FontAsset ResolveOverlayFontAsset()
        {
            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                TMP_Text source =
                    ReadTerminalTmp(terminal, "topRightText") ??
                    ReadTerminalTmp(terminal, "inputFieldText") ??
                    ReadTerminalTmp(terminal, "screenText");
                if (source != null && source.font != null)
                    return source.font;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] terminal font read failed: {ex.Message}");
            }

            try
            {
                HUDManager hud = HUDManager.Instance;
                if (hud != null && hud.controlTipLines != null && hud.controlTipLines.Length > 0)
                {
                    TextMeshProUGUI line = hud.controlTipLines[0];
                    if (line != null && line.font != null) return line.font;
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] HUD control-tip font read failed: {ex.Message}");
            }

            try
            {
                if (TMP_Settings.defaultFontAsset != null)
                {
                    return TMP_Settings.defaultFontAsset;
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] TMP default font read failed: {ex.Message}");
            }

            return null;
        }

        private static TMP_Text ReadTerminalTmp(object target, string memberName)
        {
            if (target == null || string.IsNullOrEmpty(memberName)) return null;
            Type type = target.GetType();
            object value = null;

            FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                try { value = field.GetValue(target); }
                catch { value = null; }
            }

            if (value == null)
            {
                PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null)
                {
                    try { value = property.GetValue(target); }
                    catch { value = null; }
                }
            }

            if (value is TMP_Text text) return text;
            if (value is TMP_InputField input) return input.textComponent;
            return null;
        }

        private static void RefreshRadarInsetTexture()
        {
            if (_radarInsetImage == null) return;
            if (Time.unscaledTime < _nextRadarInsetRefreshTime) return;
            _nextRadarInsetRefreshTime = Time.unscaledTime + RADAR_INSET_REFRESH_INTERVAL;

            Texture mapTexture = RadarOverlay.ResolveMapTexture();
            if (_radarInsetImage.texture != mapTexture)
                _radarInsetImage.texture = mapTexture;
            if (_radarInsetStatus != null)
            {
                string status = mapTexture != null
                    ? RadarOverlay.GetActiveInteriorFloorLabel()
                    : "RADAR WAITING";
                if (_radarInsetStatus.text != status)
                    _radarInsetStatus.text = status;
            }
        }

        private static void RefreshSignalLogPanel(bool force)
        {
            if (_signalLogText == null) return;
            if (!force && Time.unscaledTime < _nextSignalLogRefreshTime) return;
            _nextSignalLogRefreshTime = Time.unscaledTime + 0.85f;

            CCTVCamera active = GetActiveCamera();
            string cameraLabel = GetActiveFeedLabel();
            bool remoteAudio = _cameraAudioProxyListener != null
                && _cameraAudioProxyListener.enabled
                && (active != null || IsBodycamFeedActive);
            string audio = remoteAudio
                ? cameraLabel
                : "LOCAL";
            int normalCount = GetNormalFeedCount(out int bodycamCount, out int facilityCount);
            int total = normalCount + (ShipTurretController.IsUnlocked ? 1 : 0);
            int current = IsTurretPageActive
                ? total - 1
                : GetCurrentNormalFeedIndex(bodycamCount, facilityCount);
            string page = total > 0 && current >= 0
                ? (current + 1).ToString() + "/" + total.ToString()
                : "0/0";

            _signalLogText.text =
                "LINK     " + cameraLabel + "\n" +
                "PAGE     " + page + "\n" +
                "RADAR MAP READY\n" +
                "AUDIO    " + audio + "\n" +
                "CMD PORT DIRECT\n" +
                "HACK BUS IDLE";
        }

        private static void RefreshOperatorStats(bool force)
        {
            if (_operatorStatsText == null) return;
            if (!force && Time.unscaledTime < _nextStatsRefreshTime) return;
            _nextStatsRefreshTime = Time.unscaledTime + 0.5f;

            RadarOverlay.StatsSnapshot stats = RadarOverlay.CollectStats();
            CCTVCamera audioCamera = GetActiveCamera();
            bool remoteAudio = _cameraAudioProxyListener != null
                && _cameraAudioProxyListener.enabled
                && (audioCamera != null || IsBodycamFeedActive);
            string audioSource = remoteAudio
                ? GetActiveFeedLabel()
                : "LOCAL";
            string walkie = CCTVWalkieTalkieBridge.IsTransmitting ? "TX" : (CCTVWalkieTalkieBridge.IsListening ? "READY" : "OFF");
            if (IsTurretPageActive)
            {
                _operatorStatsText.text = ShipTurretController.BuildStatsBlock(walkie);
                return;
            }
            string zoom = audioCamera != null ? FormatZoom(audioCamera) : "1.0X";
            _operatorStatsText.text =
                "INTERIOR\n" +
                "SCRAP    $" + stats.FacilityScrapValue + "\n" +
                "PLAYERS  " + stats.LiveInteriorPlayers + "\n" +
                "HOSTILES " + stats.LiveInteriorEnemies + "\n" +
                "AUDIO    " + audioSource + "\n" +
                "ZOOM     " + zoom + "\n" +
                "WALKIE   " + walkie;
        }

    }
}
