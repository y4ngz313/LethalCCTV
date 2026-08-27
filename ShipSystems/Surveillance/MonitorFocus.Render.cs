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
        // FPS diagnosis: dump everything that can render while the CCTV focus view is
        // active. Runs on every focus enter (one block per enter, never per frame) so
        // regressions on different moons/camera sets stay diagnosable from logs.
        private static void LogFocusRenderDiagnostics()
        {
            try
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] --- focus render diagnostics ---");
                Camera[] cameras = Camera.allCameras;
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   enabled cameras: {cameras.Length}");
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera cam = cameras[i];
                    if (cam == null) continue;
                    string rt = cam.targetTexture != null
                        ? $"{cam.targetTexture.name} {cam.targetTexture.width}x{cam.targetTexture.height}"
                        : "<screen>";
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV]   cam[{i}] '{GetTransformPath(cam.transform)}' target={rt} " +
                        $"cullingMask=0x{cam.cullingMask:X8} fov={cam.fieldOfView:F0} ortho={cam.orthographic}");
                }

                for (int slot = 0; slot < 4; slot++)
                {
                    CCTVCamera bound = QuadCameraAssignment.GetBoundCamera(slot);
                    if (bound == null || bound.Cam == null) continue;
                    RenderTexture target = bound.Cam.targetTexture;
                    string rt = target != null ? $"{target.name} {target.width}x{target.height}" : "<none>";
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV]   cctv slot[{slot}] '{bound.ResolvedLabel}' camEnabled={bound.Cam.enabled} target={rt}");
                }

                float renderHz = SurveillanceBootstrap.Config?.RenderHzPerMonitor != null ? SurveillanceBootstrap.Config.RenderHzPerMonitor.Value : -1f;
                float inactiveHz = SurveillanceBootstrap.Config?.InactivePaneRenderHz != null ? SurveillanceBootstrap.Config.InactivePaneRenderHz.Value : -1f;
                bool dynamicActive = SurveillanceBootstrap.Config?.DynamicActiveRendererEnabled != null && SurveillanceBootstrap.Config.DynamicActiveRendererEnabled.Value;
                int rtWidth = SurveillanceBootstrap.Config?.RenderRTWidth != null ? SurveillanceBootstrap.Config.RenderRTWidth.Value : -1;
                float stationBase = renderHz > 0f ? Mathf.Min(renderHz, 24f) : -1f;
                float stationBurst = stationBase > 0f ? Mathf.Min(24f, Mathf.Max(stationBase, stationBase * 1.8f)) : -1f;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV]   cctv throttle: stationBase={stationBase:F1}fps stationBurst={stationBurst:F1}fps stationInactiveSlots=disabled " +
                    $"renderHzPerMonitor={renderHz:F1} inactivePaneHz={inactiveHz:F1} dynamicActive={dynamicActive} quadRtWidth={rtWidth}");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   {CCTVVanillaMonitorDisplay.GetRenderDiagnostics()}");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   {VanillaRadarFeed.GetRenderDiagnostics()}");
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV]   focusUiCamEnabled={(_physicalFocusUiCamera != null && _physicalFocusUiCamera.enabled)} " +
                    $"legacyOverlayActive={(_overlayRoot != null && _overlayRoot.activeSelf)} " +
                    $"focusRT={PHYSICAL_FOCUS_RT_WIDTH}x{PHYSICAL_FOCUS_RT_HEIGHT} (legacy path only)");
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] --- end focus render diagnostics ---");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Focus render diagnostics failed: {ex.Message}");
            }
        }

    }
}
