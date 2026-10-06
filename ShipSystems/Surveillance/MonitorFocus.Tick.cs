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
        /// <summary>
        /// Per-frame tick called from SurveillanceBootstrap.LateUpdate. Clears the same-frame race-survival
        /// flag once we're past the frame ESC was pressed on. The strict-greater comparison
        /// (not greater-or-equal) keeps the flag alive through every Update / LateUpdate
        /// pass within the originating frame.
        /// </summary>
        internal static void TickFrame()
        {
            // #291: the focus-entry tick costs ~230ms. BeginEntryPerfFrame arms
            // step bucketing only on frames that can complete an entry (and the
            // frame after); every other tick pays one bool test.
            bool entryPerfOwner = BeginEntryPerfFrame();
            try
            {
                TickFrameCore();
            }
            finally
            {
                if (entryPerfOwner)
                    EndEntryPerfFrame();
            }
        }

        private static void TickFrameCore()
        {
            CctvDeviceCommandLine.Tick();
            TickEntryPoseSettle();
            EntryPerfMark("TickEntryPoseSettle.rest");
            if (_enterPerfLogPending && Time.frameCount > _enterPerfFrame)
            {
                _enterPerfLogPending = false;
                // unscaledDeltaTime on the frame after EnterFocus spans the enter
                // frame's full engine cost (mod work + animator rebind + render).
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][EnterPerf] engine frame time spanning enter: {Time.unscaledDeltaTime * 1000f:F0}ms");
            }
            TickExitSeamSampling();
            TickExitCameraLocalYawInvariantReassert();
            TickExitPostureDiagnostic();
            EntryPerfMark("exit-diagnostics");
            TickPendingStationFeedFlip();
            EntryPerfMark("TickPendingStationFeedFlip");
            TickPendingLocalObstructorRestore();
            EntryPerfMark("TickPendingLocalObstructorRestore");
            // #541 - read once per frame and gate at the tick sites, never inside
            // the handlers: every one of them already carries the
            // IsStationEditInputActive / _placementEditorOpen / _reviewMenuOpen
            // guards that the rest of MonitorFocus.Input depends on.
            bool operatorDebugTools = IsOperatorDebugToolsEnabled;
            TickOperatorDebugToolsGate(operatorDebugTools);
            if (operatorDebugTools)
            {
                TickStationThirdPersonDebugHotkey();
                TickLocalFirstPersonOperatorAnimationHotkey();
            }
            if (!IsFocused)
            {
                // Deliberately ungated: this is the restore path for the F4
                // camera as well as its per-frame drive. Gating it while the
                // debug view was active would leave the player's view hijacked
                // with nothing left to hand it back.
                TickStationGlobalThirdPersonDebugView();
                TickFocusHudScanRearm();
            }
            EntryPerfMark("debug-hotkeys");
            if (WasActiveThisFrame && Time.frameCount > _wasActiveSetFrame)
            {
                WasActiveThisFrame = false;
            }
            if (IsFocused)
            {
                EnforceFocusInputLock();
                EntryPerfMark("EnforceFocusInputLock");
                RunFocusPerfStep("ApplyStationPlayerPoseLock", ApplyStationPlayerPoseLock);
                if (operatorDebugTools && !_stationEditMenuOpen)
                    TickFirstPersonArmsOffsetHotkeys();
                EntryPerfMark("TickFirstPersonArmsOffsetHotkeys");
                RunFocusPerfStep("ReassertLocalPlayerObstructors", ReassertLocalPlayerObstructors);
                RunFocusPerfStep("ApplyPhysicalFocusView", ApplyPhysicalFocusView);
                // The vanilla arms-only mesh is a truncated first-person
                // viewmodel. Keep its complete root in the same camera-space
                // pose captured before focus, after the camera has reached its
                // final world pose for this frame and before hand IK evaluates.
                RunFocusPerfStep(
                    "ApplyFirstPersonArmsCameraFollow",
                    ApplyFirstPersonArmsOffsetToForcedRenderers);
                if (!IsFocused) return;
                TickStationCameraControlActivation();
                EntryPerfMark("TickStationCameraControlActivation");
                if (_focusViewExitPending)
                {
                    EntryPerfMark("exit-pending-render");
                    return;
                }
                bool stationHackingOpen = IsHackingOverlayOpen;
                bool stationMainframeOpen = IsMainframeOverlayOpen;
                if (operatorDebugTools)
                    TickStationFocusAnchorHotkeys();
                EntryPerfMark("TickStationFocusAnchorHotkeys");
                if (operatorDebugTools && !stationHackingOpen && !stationMainframeOpen)
                    TickPlacementEditorInput();
                EntryPerfMark("TickPlacementEditorInput");
                if (operatorDebugTools && !stationHackingOpen && !stationMainframeOpen && !_placementEditorOpen)
                    TickCameraReviewInput();
                EntryPerfMark("TickCameraReviewInput");
                if (IsStationEditInputActive())
                {
                    TickStationFocusMaintenance();
                    EntryPerfMark("TickStationFocusMaintenance.rest");
                    return;
                }
                if (_placementEditorOpen || _reviewMenuOpen)
                {
                    TickStationFocusMaintenance();
                    EntryPerfMark("TickStationFocusMaintenance.rest");
                    return;
                }
                if (stationHackingOpen || stationMainframeOpen)
                {
                    TickInteractiveOverlays();
                    EntryPerfMark("TickInteractiveOverlays");
                }
                else
                {
                    TickStationRadarZoomInput();
                    EntryPerfMark("TickStationRadarZoomInput");
                }
                TickStationFocusMaintenance();
                EntryPerfMark("TickStationFocusMaintenance.rest");
            }
            else
            {
                RestoreFocusHudDimming();
                CctvOutlineManager.Tick(null, false);
                CctvScreenOutlineOverlay.Tick(false);
                if (_cameraAudioProxyListener != null && _cameraAudioProxyListener.enabled)
                {
                    RestoreAudioListenerToPlayer();
                }
            }
        }

        /// <summary>
        /// Samples camera/body pose every ~0.15s for 1.6s after the exit camera
        /// lerp completes, spanning the vanilla-controller restore and deferred
        /// rig rebuild.
        /// A yaw step between samples pinpoints the exit-seam drift (#20).
        /// </summary>
        private static void TickExitSeamSampling()
        {
            if (Time.unscaledTime >= _exitSeamLogUntil || Time.unscaledTime < _exitSeamNextLogAt)
                return;

            _exitSeamNextLogAt = Time.unscaledTime + 0.15f;
            PlayerControllerB seamPlayer = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (seamPlayer == null || seamPlayer.gameplayCamera == null)
                return;

            string controllerName = seamPlayer.playerBodyAnimator != null && seamPlayer.playerBodyAnimator.runtimeAnimatorController != null
                ? seamPlayer.playerBodyAnimator.runtimeAnimatorController.name
                : "<null>";
            Transform cam = seamPlayer.gameplayCamera.transform;
            float rotationDelta = Quaternion.Angle(
                _exitSeamReferenceRotation,
                cam.rotation);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][ExitSeam] t+{Time.unscaledTime - _exitSeamStartedAt:F2}s " +
                $"camPos={FormatDebugVector(cam.position)} " +
                $"camYaw={GetPlanarYaw(cam.forward):F1} " +
                $"camPitch={GetSignedCameraPitchDegrees(cam.rotation):F1} " +
                $"rotationDelta={rotationDelta:F3}deg " +
                $"bodyYaw={seamPlayer.transform.eulerAngles.y:F1} controller='{controllerName}'.");
        }

        // One-shot posture sample after the controller and rig have returned
        // to vanilla ownership, so any remaining walk lean is measurable.
        private static void ScheduleExitPostureDiagnostic(PlayerControllerB player)
        {
            if (player == null)
                return;

            _exitPosturePlayer = player;
            _exitPostureRestoredAt = Time.unscaledTime;
            _exitPostureSampleIndex = 0;
            _exitPostureLogAt = _exitPostureRestoredAt + 0.25f;
            _exitPostureLogPending = true;
        }

        private static void TickExitPostureDiagnostic()
        {
            if (!_exitPostureLogPending || Time.unscaledTime < _exitPostureLogAt)
                return;

            PlayerControllerB player = _exitPosturePlayer;
            if (player == null)
            {
                _exitPostureLogPending = false;
                _exitPosturePlayer = null;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV][ExitPosture] player unavailable for the scheduled sample.");
                return;
            }

            try
            {
                Transform gameplayCamera = player.gameplayCamera != null
                    ? player.gameplayCamera.transform
                    : null;
                Transform cameraContainer = player.cameraContainerTransform;
                Transform armsMetarig = player.playerModelArmsMetarig;
                string gameplayCameraEuler = gameplayCamera != null
                    ? FormatDebugVector(gameplayCamera.localEulerAngles)
                    : "<missing>";
                string cameraContainerPosition = cameraContainer != null
                    ? FormatDebugVector(cameraContainer.localPosition)
                    : "<missing>";
                string cameraContainerEuler = cameraContainer != null
                    ? FormatDebugVector(cameraContainer.localEulerAngles)
                    : "<missing>";
                string armsMetarigEuler = armsMetarig != null
                    ? FormatDebugVector(armsMetarig.localEulerAngles)
                    : "<missing>";
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ExitPosture] t+{Time.unscaledTime - _exitPostureRestoredAt:F2}s " +
                    $"gameplayCamera.localEulerAngles={gameplayCameraEuler} " +
                    $"cameraContainer.localPosition={cameraContainerPosition} " +
                    $"cameraContainer.localEulerAngles={cameraContainerEuler} " +
                    $"playerModelArmsMetarig.localEulerAngles={armsMetarigEuler} " +
                    $"inSpecialInteractAnimation={player.inSpecialInteractAnimation} " +
                    $"enteringSpecialAnimation={player.enteringSpecialAnimation}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ExitPosture] sample failed: {ex.Message}");
            }

            if (_exitPostureSampleIndex == 0)
            {
                _exitPostureSampleIndex = 1;
                _exitPostureLogAt = _exitPostureRestoredAt + 1f;
            }
            else
            {
                _exitPostureLogPending = false;
                _exitPosturePlayer = null;
            }
        }

        /// <summary>
        /// Folds a residual gameplay-camera local yaw into the player root so
        /// movement and view forward agree, then leaves vanilla's cameraUp pitch
        /// untouched while hard-zeroing local yaw and roll. The deferred rig
        /// rebuild calls this immediately after Animator.Update(0f) and requests
        /// one final assertion on the following frame.
        /// </summary>
        internal static void EnforceExitCameraLocalYawInvariant(
            PlayerControllerB player,
            string reason,
            bool reassertNextFrame = false)
        {
            if (reassertNextFrame)
            {
                _exitCameraInvariantPlayer = player;
                _exitCameraInvariantScheduledFrame = Time.frameCount;
            }

            if (player == null || player.gameplayCamera == null)
                return;

            try
            {
                Transform playerRoot = player.transform;
                Transform gameplayCamera = player.gameplayCamera.transform;
                Vector3 beforeLocalEuler = gameplayCamera.localEulerAngles;
                float residualYaw = Mathf.DeltaAngle(0f, beforeLocalEuler.y);
                float beforeBodyYaw = playerRoot.eulerAngles.y;
                float beforeViewYaw = GetPlanarYaw(gameplayCamera.forward);

                if (Mathf.Abs(residualYaw) > 0.0001f)
                {
                    playerRoot.rotation =
                        Quaternion.AngleAxis(residualYaw, Vector3.up) * playerRoot.rotation;
                }

                gameplayCamera.localEulerAngles = new Vector3(beforeLocalEuler.x, 0f, 0f);

                // Euler decomposition and a non-identity intermediate camera
                // parent can leave a tiny world-yaw error. Correct it on the body,
                // never on the camera, so the local-yaw invariant remains exact.
                float viewYawCorrection = Mathf.DeltaAngle(
                    GetPlanarYaw(gameplayCamera.forward),
                    beforeViewYaw);
                if (Mathf.Abs(viewYawCorrection) > 0.0001f)
                {
                    playerRoot.rotation =
                        Quaternion.AngleAxis(viewYawCorrection, Vector3.up) * playerRoot.rotation;
                    gameplayCamera.localEulerAngles = new Vector3(beforeLocalEuler.x, 0f, 0f);
                }

                // LogMessage, not LogInfo: the test profile's BepInEx.cfg drops Info,
                // and whether this invariant fires during an F1 viewpoint edit is the
                // single question the radar-editor investigation needs answered.
                SurveillanceBootstrap.Log?.LogMessage(
                    $"[LethalCCTV][ExitCameraInvariant] reason={reason} " +
                    $"residualLocalYaw={residualYaw:F2} viewYawCorrection={viewYawCorrection:F3} " +
                    $"bodyYaw={beforeBodyYaw:F1}->{playerRoot.eulerAngles.y:F1} " +
                    $"viewYaw={beforeViewYaw:F1}->{GetPlanarYaw(gameplayCamera.forward):F1} " +
                    $"cameraLocal={FormatDebugVector(beforeLocalEuler)}->{FormatDebugVector(gameplayCamera.localEulerAngles)} " +
                    $"reassertNextFrame={reassertNextFrame}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ExitCameraInvariant] enforcement failed ({reason}): {ex.Message}");
            }
        }

        private static void TickExitCameraLocalYawInvariantReassert()
        {
            if (_exitCameraInvariantScheduledFrame < 0 ||
                Time.frameCount <= _exitCameraInvariantScheduledFrame)
            {
                return;
            }

            PlayerControllerB player = _exitCameraInvariantPlayer;
            _exitCameraInvariantPlayer = null;
            _exitCameraInvariantScheduledFrame = -1;
            EnforceExitCameraLocalYawInvariant(
                player,
                "frame-after-deferred-rig-rebuild");
        }

        private static float GetPlanarYaw(Vector3 direction)
        {
            direction.y = 0f;
            return direction.sqrMagnitude > 0.000001f
                ? Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg
                : 0f;
        }

        private static float GetSignedCameraPitchDegrees(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            float planarMagnitude = new Vector2(forward.x, forward.z).magnitude;
            return Mathf.Atan2(-forward.y, planarMagnitude) * Mathf.Rad2Deg;
        }

        // Presentation calibration, promoted from config to a constant for 1.0
        // (#575). 16 degrees is the settle/enter/monitor-arrival pitch the
        // first-person arms were authored against.
        private const float MAX_PRESENTATION_CAMERA_PITCH_DEGREES = 16f;

        private static float GetMaxPresentationCameraPitchDegrees()
        {
            return MAX_PRESENTATION_CAMERA_PITCH_DEGREES;
        }

        private static float ClampPresentationCameraPitchDegrees(float pitchDegrees)
        {
            return Mathf.Min(pitchDegrees, GetMaxPresentationCameraPitchDegrees());
        }

        private static Quaternion ClampPresentationCameraPitch(Quaternion rotation)
        {
            float pitch = GetSignedCameraPitchDegrees(rotation);
            float clampedPitch = ClampPresentationCameraPitchDegrees(pitch);
            if (Mathf.Abs(clampedPitch - pitch) <= 0.0001f)
                return rotation;

            float yaw = GetPlanarYaw(rotation * Vector3.forward);
            return Quaternion.Euler(clampedPitch, yaw, 0f);
        }

        internal static void LogPendingViewFraming(
            Camera camera,
            Transform leftShoulder,
            Transform rightShoulder)
        {
            if ((!_settleViewFramingLogPending && !_arrivalViewFramingLogPending) || camera == null)
                return;

            Vector3? buttonPoint = Y4NGZPlayerAnimationBridge.ResolveAccessButtonPressPoint();
            bool monitorResolved = CCTVOperatorStation.TryResolveMainMonitorScreenFrame(
                out Vector3 monitorCenter,
                out _,
                out _,
                out _,
                out _,
                out _);

            void LogPhase(string phase)
            {
                string buttonViewport = buttonPoint.HasValue
                    ? FormatDebugVector(camera.WorldToViewportPoint(buttonPoint.Value))
                    : "<missing>";
                string monitorViewport = monitorResolved
                    ? FormatDebugVector(camera.WorldToViewportPoint(monitorCenter))
                    : "<missing>";
                string leftShoulderViewport = leftShoulder != null
                    ? FormatDebugVector(camera.WorldToViewportPoint(leftShoulder.position))
                    : "<missing>";
                string rightShoulderViewport = rightShoulder != null
                    ? FormatDebugVector(camera.WorldToViewportPoint(rightShoulder.position))
                    : "<missing>";
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ViewFraming] phase={phase} frame={Time.frameCount} " +
                    $"cameraPitch={GetSignedCameraPitchDegrees(camera.transform.rotation):F2} " +
                    $"maxPitch={GetMaxPresentationCameraPitchDegrees():F2} " +
                    $"button.viewport={buttonViewport} " +
                    $"monitor-screen-center.viewport={monitorViewport} " +
                    $"shoulder.L.viewport={leftShoulderViewport} " +
                    $"shoulder.R.viewport={rightShoulderViewport}.");
            }

            if (_settleViewFramingLogPending)
            {
                _settleViewFramingLogPending = false;
                LogPhase("settle-complete");
            }
            if (_arrivalViewFramingLogPending)
            {
                _arrivalViewFramingLogPending = false;
                LogPhase("arrival");
            }
        }

        // T6b — arrow handlers. ←/→ and ↑/↓ both step the single station feed
        // (see NavigateSingleCamera).
        private static void TickStationFocusMaintenance(bool force = false)
        {
            float now = Time.unscaledTime;

            RunFocusPerfStep("EnsureActiveFeedAvailable", EnsureActiveFeedAvailable);

            RunFocusPerfStep("ApplyFocusHudDimming", () => ApplyFocusHudDimming(force));

            RunFocusPerfStep("SyncCameraAudioProxy", SyncCameraAudioProxy);

            if (force || now >= _nextStationFocusOverlayRefreshAt)
            {
                _nextStationFocusOverlayRefreshAt = now + STATION_FOCUS_OVERLAY_REFRESH_INTERVAL;
                RunFocusPerfStep("RefreshTurretFocusPage", RefreshTurretFocusPage);
                RunFocusPerfStep("RefreshBodycamFocusSlot", RefreshBodycamFocusSlot);
            }

        }

        private static void RunFocusPerfStep(string stepName, Action action)
        {
            if (action == null) return;
            action();
            EntryPerfMark(stepName);
        }

    }
}
