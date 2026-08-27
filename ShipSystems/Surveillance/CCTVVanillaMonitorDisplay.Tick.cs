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
        internal static void Tick()
        {
            // #303/#304 — must run before every early return below so the guard
            // survives round transitions and CCTV teardown.
            TickVanillaCameraGuard(Time.unscaledTime);

            if (!CCTVTerminalUnlockable.IsPurchased())
            {
                SetCctvModeActive(false, announce: false);
                SetActive(false);
                TickCctvTargetOutlines(active: false);
                return;
            }

            if (!_cctvModeActive || QuadMonitor.QuadRTs == null)
            {
                SetActive(false);
                TickCctvTargetOutlines(active: false);
                return;
            }

            EnsureRenderRig();
            if (_leftRenderTexture == null || _rightRenderTexture == null)
            {
                TickCctvTargetOutlines(active: false);
                return;
            }

            // Ship power gate. The vanilla blackout in ShipSystemsController
            // only collects MonitorWall renderers named 'Cube' and
            // 'SingleScreen', so the two large lower monitors CCTV owns on
            // 'Cube.001' stayed fully lit through a battery break while the
            // rest of the ship went dark. CCTV owns those slots, so CCTV blanks
            // them: hold the bindings (releasing them every frame would thrash
            // the vanilla material restore) and clear both feeds to black until
            // power comes back.
            if (!CCTVShipSystemsBridge.IsShipPowerOnline())
            {
                ApplyMonitorPowerBlank();
                TickCctvTargetOutlines(active: false);
                return;
            }

            ClearMonitorPowerBlank();

            float now = Time.unscaledTime;
            bool monitorVisible = IsMonitorWorkVisible();
            bool allowRadarWork = MonitorFocus.IsFocused || monitorVisible;
            VanillaRadarFeed.SetActive(allowRadarWork);

            ReassertCctvScreenOwnershipFast(now);
            UpdateLeftSwitchFlash(now);
            TickCctvTargetOutlines(active: ShouldRunTargetOutlines());

            if (ShouldRunBindMaintenance(now, monitorVisible))
            {
                bool maintenanceChanged = false;
                long maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                DumpMonitorDiagnosticsOnce();
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.Diagnostics",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                maintenanceChanged |= EnsureWorldUiOverlays();
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.EnsureWorldUiOverlays",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                SuppressVanillaLowerMonitorFeeds();
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.SuppressVanillaLowerMonitorFeeds",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                maintenanceChanged |= BindLeftMonitorTexture();
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.BindLeftMonitorTexture",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                maintenanceChanged |= BindRightMonitorTexture();
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.BindRightMonitorTexture",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                maintenanceChanged |= _lowerLeftBinding.RediscoverDriversIfDue(now);
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.RediscoverDrivers.lowerLeft",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                maintenanceChanged |= _lowerRightBinding.RediscoverDriversIfDue(now);
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.RediscoverDrivers.lowerRight",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                maintenanceChanged |= _lowerRightAuxBinding.RediscoverDriversIfDue(now);
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.RediscoverDrivers.lowerRightAux",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                maintenanceChanged |= SyncSlotTextures();
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.SyncSlotTextures",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                maintenanceChanged |= RefreshLeftCameraLabel(now, monitorVisible);
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.RefreshLeftCameraLabel",
                    maintenanceStepStartedAt);

                maintenanceStepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_lowerLeftRoot != null && !_lowerLeftRoot.activeSelf)
                {
                    _lowerLeftRoot.SetActive(true);
                    _lowerLeftRoot.transform.SetAsLastSibling();
                    maintenanceChanged = true;
                }
                RecordBindMaintenanceStep(
                    "CCTVVanillaMonitorDisplay.BindMaintenance.ReactivateLowerLeftOverlay",
                    maintenanceStepStartedAt);

                if (maintenanceChanged)
                {
                    _compositorDirty = true;
                    _forceFastMaterialVerify = true;
                }
            }

            if (now >= _nextReassertAt)
            {
                _nextReassertAt = now + ReassertIntervalSeconds;
                ReassertCctvScreenOwnership();
            }

            // First frame after the monitor wall comes back into view must repaint:
            // hidden-ambient mode below skips renders entirely unless dirty.
            if (monitorVisible && !_wasMonitorVisible)
            {
                _compositorDirty = true;
                // #562 — the facility cameras were disabled while the wall was
                // out of view, so the compositor would repaint a stale frame.
                // RequestWakeAllSlots defers the wake to the throttle's Update;
                // WakeRender must NOT be used here (we are inside the render
                // loop and Camera.Render would re-enter HDRP's SRP pass).
                QuadCameraAssignment.RequestWakeAllSlots();
            }
            _wasMonitorVisible = monitorVisible;

            if (ShouldRenderCompositor(now, monitorVisible))
            {
                _nextCompositorRenderAt = now + ResolveCompositorInterval(monitorVisible);
                SyncRightMonitor(allowRadarWork);
                RefreshLeftCameraLabel(now, monitorVisible);
                RenderMonitorTextures(now, monitorVisible);
                PinLowerLeftVideoTexture();
                _compositorDirty = false;
                _lastCompositorRenderAt = now;
            }

            ReportSuppressedUnobservedBindMaintenance(now);
            ReportSuppressedUnobservedRepaints(now);
        }

        private static void RecordBindMaintenanceStep(string stepName, long startedAt)
        {
            SurveillanceBootstrap.RecordLateUpdateNestedStep(
                stepName,
                System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
        }

        private static bool ShouldRunBindMaintenance(float now, bool monitorVisible)
        {
            bool observable = monitorVisible || MonitorFocus.IsFocused;
            bool due = now >= _nextBindMaintenanceAt;
            if (!due && !(_bindMaintenancePending && observable))
                return false;

            _nextBindMaintenanceAt = now + BindMaintenanceIntervalSeconds;
            if (!observable)
            {
                _bindMaintenancePending = true;
                _suppressedUnobservedBindMaintenancePasses++;
                if (!_bindMaintenanceGateLogged)
                {
                    _bindMaintenanceGateLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV.Timing] bind-maintenance gate deferred an unobserved pass; " +
                        "one maintenance pass is pending for the first observable frame.");
                }
                return false;
            }

            bool releasedPendingPass = _bindMaintenancePending;
            _bindMaintenancePending = false;
            if (releasedPendingPass && !_bindMaintenanceResumeLogged)
            {
                _bindMaintenanceResumeLogged = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV.Timing] bind-maintenance gate released its pending pass " +
                    "on the first observable frame.");
            }
            return true;
        }

        private static void ReportSuppressedUnobservedBindMaintenance(float now)
        {
            if (_nextBindMaintenanceSuppressionReportAt <= 0f)
            {
                _nextBindMaintenanceSuppressionReportAt = now + SuppressionReportIntervalSeconds;
                return;
            }

            if (now < _nextBindMaintenanceSuppressionReportAt)
                return;

            _nextBindMaintenanceSuppressionReportAt = now + SuppressionReportIntervalSeconds;
            if (_suppressedUnobservedBindMaintenancePasses <= 0L)
                return;

            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV.Timing] bind-maintenance gate suppressed " +
                $"{_suppressedUnobservedBindMaintenancePasses} unobserved pass(es) in the last " +
                $"{SuppressionReportIntervalSeconds:0}s (pending={_bindMaintenancePending}, " +
                $"focused={MonitorFocus.IsFocused}).");
            _suppressedUnobservedBindMaintenancePasses = 0L;
        }

        /// <summary>Warning level on purpose: Info is filtered off disk in the diagnostic
        /// profiles that read these logs, and this line exists to prove the gate fires.</summary>
        private static void ReportSuppressedUnobservedRepaints(float now)
        {
            if (_nextSuppressionReportAt <= 0f)
            {
                _nextSuppressionReportAt = now + SuppressionReportIntervalSeconds;
                return;
            }

            if (now < _nextSuppressionReportAt)
                return;

            _nextSuppressionReportAt = now + SuppressionReportIntervalSeconds;
            if (_suppressedUnobservedRepaints <= 0L)
                return;

            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV.Timing] compositor gate suppressed {_suppressedUnobservedRepaints} " +
                $"unobserved repaint(s) in the last {SuppressionReportIntervalSeconds:0}s " +
                $"(dirty={_compositorDirty}, focused={MonitorFocus.IsFocused})");
            _suppressedUnobservedRepaints = 0L;
        }

        private static bool ShouldRenderCompositor(float now, bool monitorVisible)
        {
            if (_lastCompositorRenderAt <= 0f)
                return true;

            // Observability is checked BEFORE the dirty flag on purpose. The two rig
            // Camera.Render calls cost 7-10ms a pass, and _compositorDirty is set by the
            // 1s maintenance pass whenever anything changed - SyncSlotTextures flips as
            // the facility cameras cycle, RefreshLeftCameraLabel sets it directly - so
            // checking dirty first let an unobserved repaint run about once a second and
            // made this the largest remaining variance in LateUpdate frame time
            // (25.7ms/s in good windows, 123.9ms/s in bad ones; y4ngz313/Y4NGZCompany#196).
            //
            // Dirty therefore means "repaint pending", not "repaint now": the flag stays
            // set while hidden and is consumed on the first observable frame, forced by
            // the _wasMonitorVisible transition in Tick. IsMonitorWorkVisible already
            // returns true when focused, so monitorVisible is the whole predicate.
            if (!monitorVisible)
            {
                _suppressedUnobservedRepaints++;
                return false;
            }

            if (_compositorDirty || IsLeftSwitchAnimating(now))
                return true;
            return now >= _nextCompositorRenderAt;
        }

        private static float ResolveCompositorInterval(bool monitorVisible)
        {
            if (MonitorFocus.IsFocused)
                return MonitorFocus.IsStationCameraFeedInteractionActive
                    ? FocusedCompositorInteractiveIntervalSeconds
                    : FocusedCompositorPassiveIntervalSeconds;
            return monitorVisible
                ? VisibleAmbientCompositorIntervalSeconds
                : HiddenAmbientCompositorIntervalSeconds;
        }

        private static bool IsMonitorWorkVisible()
        {
            if (MonitorFocus.IsFocused)
                return true;
            if (IsRendererVisible(_lowerLeftBinding.Renderer) ||
                IsRendererVisible(_lowerRightBinding.Renderer) ||
                IsRendererVisible(_lowerRightAuxBinding.Renderer))
            {
                return true;
            }

            // Before first bind, do a conservative ship-room fallback so the first
            // visible frame is already painted when a player walks up to the wall.
            if (_lowerLeftBinding.IsBound || _lowerRightBinding.IsBound || _lowerRightAuxBinding.IsBound)
                return false;

            var player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null || player.transform == null)
                return false;
            if (!player.isInHangarShipRoom && !player.isInElevator)
                return false;

            Transform ship = StartOfRound.Instance != null
                ? StartOfRound.Instance.elevatorTransform
                : null;
            if (ship == null)
                return true;

            Transform monitorWall = ship.Find("ShipModels2b/MonitorWall");
            Vector3 target = monitorWall != null ? monitorWall.position : ship.position;
            return (player.transform.position - target).sqrMagnitude <= 144f;
        }

        private static bool IsRendererVisible(Renderer renderer)
        {
            return renderer != null &&
                   renderer.enabled &&
                   renderer.gameObject.activeInHierarchy &&
                   renderer.isVisible;
        }

    }
}
