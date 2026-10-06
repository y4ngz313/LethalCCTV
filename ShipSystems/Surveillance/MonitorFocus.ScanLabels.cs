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
        private static void UpdateCameraScanOverlay()
        {
            // Target identity and reveal now have one owner, including occlusion/fade.
            HideScanLabels();
        }

        private static void UpdateContextLabel(ScanLabel label, ContextTarget target, Camera cam, Vector2 paneCenter)
        {
            if (label == null || target == null || cam == null) return;

            Vector3 viewport = cam.WorldToViewportPoint(target.Position);
            if (viewport.z <= 0f)
            {
                SetScanLabelActive(label, false);
                return;
            }

            Vector2 paneSize = GetOverlayPaneSize(ActiveSlot);
            float x = paneCenter.x - paneSize.x * 0.5f + viewport.x * paneSize.x;
            float y = paneCenter.y - paneSize.y * 0.5f + viewport.y * paneSize.y;
            float halfW = OVERLAY_SCAN_LABEL_WIDTH * 0.5f;
            float halfH = OVERLAY_SCAN_LABEL_HEIGHT * 0.5f;
            x = Mathf.Clamp(x, paneCenter.x - paneSize.x * 0.5f + halfW, paneCenter.x + paneSize.x * 0.5f - halfW);
            y = Mathf.Clamp(y, paneCenter.y - paneSize.y * 0.5f + halfH, paneCenter.y + paneSize.y * 0.5f - halfH);

            label.Rect.anchoredPosition = new Vector2(x, y);
            float distance = Mathf.Max(1f, Vector3.Distance(cam.transform.position, target.Position));
            float size = Mathf.Clamp((target.Radius / distance) * GetOverlayPaneSize(ActiveSlot).x * 1.9f, 34f, 96f);
            label.Rect.sizeDelta = new Vector2(size, size);
            label.Header.text = target.DisplayName;
            label.Header.color = target.Color;
            label.Background.color = new Color(target.Color.r, target.Color.g, target.Color.b, target.Commandable ? 0.12f : 0.08f);
            ApplyScanMarkerShape(label, target.Color, size);
            UpdateScanTracer(label.Tracer, paneCenter, label.Rect.anchoredPosition);
            SetScanLabelActive(label, true);
        }

        private static void UpdateScanTracer(Image tracer, Vector2 paneCenter, Vector2 markerPosition)
        {
            if (tracer == null) return;
            Vector2 delta = markerPosition - paneCenter;
            float length = delta.magnitude;
            if (length < 18f)
            {
                tracer.gameObject.SetActive(false);
                return;
            }

            Vector2 dir = delta / length;
            float dashLength = Mathf.Clamp(length * 0.22f, 18f, 76f);
            RectTransform rect = tracer.rectTransform;
            rect.anchoredPosition = markerPosition - dir * (dashLength * 0.5f + 12f);
            rect.sizeDelta = new Vector2(dashLength, OVERLAY_SCAN_TRACER_THICKNESS);
            rect.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            tracer.color = new Color(OVERLAY_SCAN_BLUE.r, OVERLAY_SCAN_BLUE.g, OVERLAY_SCAN_BLUE.b, 0.26f);
            tracer.gameObject.SetActive(true);
        }

        private static void ApplyScanMarkerShape(ScanLabel label, Color color, float size)
        {
            if (label == null) return;
            float half = size * 0.5f;
            float thickness = 4f;
            float segment = Mathf.Clamp(size * 0.34f, 12f, 30f);
            SetScanMarkerLine(label.Top, new Vector2(-half + segment * 0.5f, half), new Vector2(segment, thickness), color);
            SetScanMarkerLine(label.Left, new Vector2(-half, half - segment * 0.5f), new Vector2(thickness, segment), color);
            SetScanMarkerLine(label.Bottom, new Vector2(half - segment * 0.5f, -half), new Vector2(segment, thickness), color);
            SetScanMarkerLine(label.Right, new Vector2(half, -half + segment * 0.5f), new Vector2(thickness, segment), color);
            SetScanMarkerLine(label.Center, Vector2.zero, new Vector2(4f, 4f), new Color(color.r, color.g, color.b, 0.70f));
            if (label.Header != null)
            {
                label.Header.rectTransform.anchoredPosition = new Vector2(0f, -half - 5f);
            }
        }

        private static void SetScanMarkerLine(Image image, Vector2 position, Vector2 size, Color color)
        {
            if (image == null) return;
            RectTransform rect = image.rectTransform;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            image.color = color;
        }

        private static Color ColorForScanNode(ScanNodeProperties node)
        {
            if (node == null) return OVERLAY_SCAN_BLUE;
            if (node.nodeType == 2) return OVERLAY_AMBER;
            if (node.creatureScanID >= 0) return OVERLAY_SCAN_RED;
            return OVERLAY_SCAN_BLUE;
        }

        private static void ShowScanStatus(string text, Color color)
        {
            if (_scanStatusText == null) return;
            _scanStatusText.text = text;
            _scanStatusText.color = color;
            _scanStatusText.gameObject.SetActive(true);
        }

        private static void ShowTimedScanStatus(string text, Color color, float seconds)
        {
            _scanVisibleUntil = Time.unscaledTime + Mathf.Max(0.1f, seconds);
            ShowScanStatus(text, color);
            UpdateCameraScanOverlay();
        }

        private static bool FocusCamera(CCTVCamera camera, string reason)
        {
            if (camera == null)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] FocusCamera failed reason={reason} camera=null.");
                return false;
            }

            if (!QuadCameraAssignment.TrySelectCamera(camera, out int slot))
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] FocusCamera failed reason={reason} camera='{DescribeCameraForLog(camera)}' activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'.");
                return false;
            }

            SetActiveSlot(slot);
            BoostStationCameraRenderCadence(0.25f);
            RefreshOperatorStats(force: true);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] FocusCamera reason={reason} camera='{DescribeCameraForLog(camera)}' page={QuadCameraAssignment.CurrentPage + 1}/{QuadCameraAssignment.TotalPages} slot={slot} active='{DescribeCameraForLog(GetActiveCamera())}'.");
            return ReferenceEquals(GetActiveCamera(), camera);
        }

        private static string DescribeCameraForLog(CCTVCamera camera)
        {
            if (camera == null) return "<null>";
            Vector3 pos = camera.transform != null ? camera.transform.position : Vector3.zero;
            return $"{camera.ResolvedLabel}#{camera.CameraIndex}@{pos.x:F1},{pos.y:F1},{pos.z:F1}";
        }

        private static void HideCameraScanOverlay()
        {
            _scanVisibleUntil = 0f;
            _contextTarget = null;
            HideScanLabels();
            if (_scanStatusText != null) _scanStatusText.gameObject.SetActive(false);
        }

        private static void HideScanLabels()
        {
            if (_scanLabels == null) return;
            for (int i = 0; i < _scanLabels.Length; i++)
            {
                SetScanLabelActive(_scanLabels[i], false);
            }
        }

        private static void SetScanLabelActive(ScanLabel label, bool active)
        {
            if (label?.Rect == null) return;
            label.Rect.gameObject.SetActive(active);
            if (label.Tracer != null) label.Tracer.gameObject.SetActive(active);
        }

        private static void UpdateActivePaneReticle()
        {
            if (_centerReticle == null) return;
            _centerReticle.gameObject.SetActive(IsFocused);
            if (!IsFocused) return;

            RectTransform rect = _centerReticle.rectTransform;
            rect.anchoredPosition = GetOverlayPaneCenter(ActiveSlot);
            bool hitmarker = IsTurretPageActive && Time.unscaledTime <= _turretHitmarkerUntil;
            rect.sizeDelta = hitmarker ? new Vector2(62f, 62f) : new Vector2(46f, 46f);
            Color color = hitmarker
                ? (_turretHitmarkerEnemy ? OVERLAY_SCAN_RED : OVERLAY_AMBER)
                : OVERLAY_ACTIVE_GREEN;
            SetReticleColor(color, hitmarker ? 0.95f : 0.72f);
        }

        internal static void ShowTurretHitmarker(bool enemyHit)
        {
            _turretHitmarkerEnemy = enemyHit;
            _turretHitmarkerUntil = Time.unscaledTime + 0.22f;
            UpdateActivePaneReticle();
        }

        private static void SetReticleColor(Color color, float alpha)
        {
            if (_centerReticle == null) return;
            Image[] images = _centerReticle.GetComponentsInChildren<Image>(includeInactive: true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] == null || ReferenceEquals(images[i], _centerReticle)) continue;
                float segmentAlpha = images[i].name.StartsWith("Center", StringComparison.OrdinalIgnoreCase)
                    ? alpha * 0.66f
                    : alpha;
                images[i].color = new Color(color.r, color.g, color.b, segmentAlpha);
            }
        }

    }
}
