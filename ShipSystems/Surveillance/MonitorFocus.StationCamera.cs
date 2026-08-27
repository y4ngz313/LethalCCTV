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
        private static void BeginStationPlayerPoseTracking(PlayerControllerB player)
        {
            if (_stationPlayerPoseLockActive)
                RestoreStationPlayerPoseTracking("station-reentry");

            if (player == null)
                return;

            Transform pose = CCTVOperatorStation.OperatorPoseAnchor;
            if (pose == null)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Station player pose lock skipped: CCTVStationOperatorPoseAnchor is missing.");
                return;
            }

            try
            {
                bool useSettleStartSnapshot = _stationPoseSettleTransferToFocus &&
                    _stationPoseSettleSnapshot.Valid &&
                    ReferenceEquals(_stationPoseSettleSnapshot.Player, player);
                StationPlayerPoseSnapshot snapshot = useSettleStartSnapshot
                    ? _stationPoseSettleSnapshot
                    : CaptureStationPlayerPoseSnapshot(player);
                if (!snapshot.Valid)
                    throw new InvalidOperationException("station pose snapshot unavailable");

                bool apiModeConfigured = Y4NGZPlayerAnimationBridge.UseInteractionsApiOperatorSession;
                bool floorRootRequested = apiModeConfigured &&
                    Y4NGZPlayerAnimationBridge.UseApiOperatorRootAtFloorLevel;
                Transform floorAnchor = CCTVOperatorStation.PlayerRootAnchor;
                bool useFloorRoot = floorRootRequested && floorAnchor != null &&
                    !ReferenceEquals(floorAnchor, pose);
                if (floorRootRequested && !useFloorRoot)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV][ApiPose] floor root requested but a distinct " +
                        "CCTVStationPlayerRootAnchor is unavailable; using OperatorPoseAnchor.");
                }

                float groundedWorldY = useSettleStartSnapshot
                    ? _stationPoseSettleTargetPosition.y
                    : snapshot.Position.y;
                Vector3 targetPosition = useFloorRoot
                    ? new Vector3(floorAnchor.position.x, groundedWorldY, floorAnchor.position.z)
                    : pose.position;
                Quaternion targetRotation = useFloorRoot
                    ? Quaternion.Euler(0f, pose.eulerAngles.y, 0f)
                    : pose.rotation;
                Vector3 preRootWorldPosition = player.transform.position;

                _stationPlayerPosePlayer = player;
                _stationPlayerPoseTargetPosition = targetPosition;
                _stationPlayerPoseTargetRotation = targetRotation;
                _stationPlayerPoseTargetAnchorKind = useFloorRoot
                    ? "player-root-grounded-y+operator-pose-yaw"
                    : "operator-pose";
                _stationPlayerPoseApiModeConfigured = apiModeConfigured;
                _stationPlayerPoseUsesApiFloorRoot = useFloorRoot;
                _stationPlayerPoseFloorDeltaWorldY = 0f;
                _stationPlayerPoseNextRootTelemetryAt = float.PositiveInfinity;
                _stationPlayerPoseSavedPosition = snapshot.Position;
                _stationPlayerPoseSavedRotation = snapshot.Rotation;
                _stationPlayerPoseHandbackRotation = useSettleStartSnapshot
                    ? FlattenToYaw(_stationPoseSettleTargetRotation)
                    : FlattenToYaw(player.transform.rotation);
                _stationPlayerPoseHandbackCameraPitch = 0f;
                _stationPlayerPoseSavedServerPosition = snapshot.ServerPosition;
                _stationPlayerPoseSavedSnapToServerPosition = snapshot.SnapToServerPosition;
                _stationPlayerPoseSavedDisableSyncInAnimation = snapshot.DisableSyncInAnimation;
                _stationPlayerPoseSavedFreeRotationInInteractAnimation = snapshot.FreeRotationInInteractAnimation;
                _stationPlayerPoseSavedInSpecialInteractAnimation = snapshot.InSpecialInteractAnimation;
                _stationPlayerPoseSavedEnteringSpecialAnimation = snapshot.EnteringSpecialAnimation;
                _stationPlayerPoseHadController = snapshot.HadController;
                _stationPlayerPoseSavedControllerDetectCollisions = snapshot.ControllerDetectCollisions;
                _stationPlayerPoseSavedControllerEnabled = snapshot.ControllerEnabled;
                _stationPlayerPoseSavedWasCrouching = snapshot.WasCrouching;
                _stationPlayerCameraBaselineCaptured = snapshot.CameraBaselineCaptured;
                _stationPlayerCameraPlayerLocalPosition = snapshot.CameraPlayerLocalPosition;
                ArmStationPlayerCameraStabilizer(player);
                _stationPlayerPoseLockActive = true;

                ApplyStationPlayerPoseLock();

                // Canonical exit (user decision 2026-07-22, Test 33): the exit
                // used to restore the pre-settle walk-up pose, so its read
                // varied with where the player entered from. With Exit To
                // Station Pose on, the "saved" restore pose IS the canonical
                // locked station pose the settle already moved the player to
                // (position, yaw and server-sync position all consistent), so
                // every exit ends at the same stand spot with the same read.
                if (_stationPlayerPoseLockActive && UseExitToStationPose)
                {
                    _stationPlayerPoseSavedPosition = player.transform.position;
                    _stationPlayerPoseSavedRotation = player.transform.rotation;
                    _stationPlayerPoseSavedServerPosition = player.transform.localPosition;
                }

                if (_stationPlayerPoseLockActive && apiModeConfigured)
                {
                    Vector3 postRootWorldPosition = player.transform.position;
                    _stationPlayerPoseFloorDeltaWorldY = useFloorRoot
                        ? pose.position.y - postRootWorldPosition.y
                        : 0f;
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV][ApiPose] root-snap " +
                        $"anchor={_stationPlayerPoseTargetAnchorKind} " +
                        $"preRootWorld={FormatDebugVector(preRootWorldPosition)} " +
                        $"postRootWorld={FormatDebugVector(postRootWorldPosition)} " +
                        $"delta={FormatDebugVector(postRootWorldPosition - preRootWorldPosition)} " +
                        $"poseAnchorWorldY={pose.position.y:F3} " +
                        $"floorDeltaWorldY={_stationPlayerPoseFloorDeltaWorldY:F3} " +
                        $"targetYaw={targetRotation.eulerAngles.y:F1}.");
                    _stationPlayerPoseNextRootTelemetryAt =
                        Time.unscaledTime + API_ROOT_WORLD_TELEMETRY_INTERVAL_SECONDS;
                }

                if (!_stationPoseTrackingLogged)
                {
                    _stationPoseTrackingLogged = true;
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV] Station player pose lock active: " +
                        $"targetAnchor={_stationPlayerPoseTargetAnchorKind}, " +
                        $"targetWorld={FormatDebugVector(_stationPlayerPoseTargetPosition)}, " +
                        $"operatorPose={FormatDebugVector(pose.position)}, " +
                        $"savedWorld={FormatDebugVector(_stationPlayerPoseSavedPosition)}, " +
                        $"cameraPlayerLocal={(_stationPlayerCameraBaselineCaptured ? FormatDebugVector(_stationPlayerCameraPlayerLocalPosition) : "<missing>")}.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Station player pose lock failed to start: {ex.Message}");
                RestoreStationPlayerPoseTracking("begin-failed");
            }
        }

        private static void EndStationPlayerPoseTracking()
        {
            RestoreStationPlayerPoseTracking("end");
        }

        private static void ArmStationPlayerCameraStabilizer(PlayerControllerB player)
        {
            _stationPlayerCameraPositionStabilizer = null;
            if (!_stationPlayerCameraBaselineCaptured || player == null || player.gameplayCamera == null)
                return;

            try
            {
                GameObject cameraObject = player.gameplayCamera.gameObject;
                _stationPlayerCameraPositionStabilizer =
                    cameraObject.GetComponent<CCTVLocalCameraPositionStabilizer>();
                if (_stationPlayerCameraPositionStabilizer == null)
                {
                    _stationPlayerCameraPositionStabilizer =
                        cameraObject.AddComponent<CCTVLocalCameraPositionStabilizer>();
                }
                _stationPlayerCameraPositionStabilizer.Initialize(
                    player,
                    player.transform,
                    player.gameplayCamera.transform,
                    _stationPlayerCameraPlayerLocalPosition);
            }
            catch (Exception ex)
            {
                _stationPlayerCameraPositionStabilizer = null;
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Station camera baseline guard failed to arm: {ex.Message}");
            }
        }

        private static void ApplyStationPlayerPoseLock()
        {
            if (!_stationPlayerPoseLockActive)
                return;

            PlayerControllerB player = _stationPlayerPosePlayer;
            if (player == null)
                return;

            try
            {
                player.transform.SetPositionAndRotation(
                    _stationPlayerPoseTargetPosition,
                    _stationPlayerPoseTargetRotation);
                player.serverPlayerPosition = player.transform.localPosition;
                player.snapToServerPosition = false;
                player.disableSyncInAnimation = true;
                player.freeRotationInInteractAnimation = true;
                player.inSpecialInteractAnimation = true;
                player.enteringSpecialAnimation = false;
                if (player.isCrouching)
                    player.Crouch(false);
                player.ResetFallGravity();

                if (_stationPlayerPoseApiModeConfigured &&
                    Time.unscaledTime >= _stationPlayerPoseNextRootTelemetryAt)
                {
                    _stationPlayerPoseNextRootTelemetryAt =
                        Time.unscaledTime + API_ROOT_WORLD_TELEMETRY_INTERVAL_SECONDS;
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV][ApiPose] root-world " +
                        $"anchor={_stationPlayerPoseTargetAnchorKind} " +
                        $"world={FormatDebugVector(player.transform.position)} " +
                        $"target={FormatDebugVector(_stationPlayerPoseTargetPosition)} " +
                        $"residual={Vector3.Distance(player.transform.position, _stationPlayerPoseTargetPosition):F4}m.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Station player pose lock failed while focused: {ex.Message}");
                RestoreStationPlayerPoseTracking("lock-failed");
            }
        }

        private static void RestoreStationPlayerPoseTracking(string reason)
        {
            PlayerControllerB player = _stationPlayerPosePlayer;
            if (_stationPlayerPoseLockActive && player != null)
            {
                try
                {
                    if (_stationPlayerPoseHadController && player.thisController != null)
                    {
                        player.thisController.detectCollisions = _stationPlayerPoseSavedControllerDetectCollisions;
                        player.thisController.enabled = _stationPlayerPoseSavedControllerEnabled;
                    }

                    player.transform.SetPositionAndRotation(
                        _stationPlayerPoseSavedPosition,
                        _stationPlayerPoseHandbackRotation);
                    player.serverPlayerPosition = _stationPlayerPoseSavedServerPosition;
                    player.snapToServerPosition = _stationPlayerPoseSavedSnapToServerPosition;
                    player.disableSyncInAnimation = _stationPlayerPoseSavedDisableSyncInAnimation;
                    player.freeRotationInInteractAnimation = _stationPlayerPoseSavedFreeRotationInInteractAnimation;
                    player.inSpecialInteractAnimation = _stationPlayerPoseSavedInSpecialInteractAnimation;
                    player.enteringSpecialAnimation = _stationPlayerPoseSavedEnteringSpecialAnimation;
                    if (player.isCrouching != _stationPlayerPoseSavedWasCrouching)
                        player.Crouch(_stationPlayerPoseSavedWasCrouching);
                    if (!_stationPlayerPoseSavedInSpecialInteractAnimation)
                        player.UpdateSpecialAnimationValue(false, 0);
                    player.ResetFallGravity();
                    if (_stationPlayerCameraBaselineCaptured && player.gameplayCamera != null)
                    {
                        player.gameplayCamera.transform.position =
                            player.transform.TransformPoint(_stationPlayerCameraPlayerLocalPosition);
                    }
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] Station player pose restored ({reason}) " +
                        $"world={FormatDebugVector(player.transform.position)} " +
                        $"bodyYaw={player.transform.eulerAngles.y:F1}.");
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Station player pose restore failed ({reason}): {ex.Message}");
                }
            }

            CCTVLocalCameraPositionStabilizer cameraStabilizer =
                _stationPlayerCameraPositionStabilizer;
            _stationPlayerCameraPositionStabilizer = null;
            if (cameraStabilizer != null)
            {
                try
                {
                    cameraStabilizer.ApplyNow();
                    cameraStabilizer.ReleaseAfterLateUpdates(2);
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV] Station camera baseline guard activated after pose restore; " +
                        "it will hold through controller restore, then release across 2 final LateUpdates " +
                        "before the deferred vanilla rig rebuild.");
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] Station camera baseline guard release failed ({reason}): {ex.Message}");
                }
            }

            // Drop the viewpoint-edit override without replaying it: the pose hand-back
            // above already restored the body and camera, so re-applying the saved
            // channels here would fight it.
            _stationViewpointChannelOverrideActive = false;
            _stationPlayerPoseLockActive = false;
            _stationPlayerPosePlayer = null;
            _stationPlayerPoseTargetPosition = Vector3.zero;
            _stationPlayerPoseTargetRotation = Quaternion.identity;
            _stationPlayerPoseTargetAnchorKind = null;
            _stationPlayerPoseApiModeConfigured = false;
            _stationPlayerPoseUsesApiFloorRoot = false;
            _stationPlayerPoseFloorDeltaWorldY = 0f;
            _stationPlayerPoseNextRootTelemetryAt = 0f;
            _stationPlayerPoseSavedPosition = Vector3.zero;
            _stationPlayerPoseSavedRotation = Quaternion.identity;
            _stationPlayerPoseHandbackRotation = Quaternion.identity;
            _stationPlayerPoseHandbackCameraPitch = 0f;
            _stationPlayerPoseSavedServerPosition = Vector3.zero;
            _stationPlayerPoseSavedSnapToServerPosition = false;
            _stationPlayerPoseSavedDisableSyncInAnimation = false;
            _stationPlayerPoseSavedFreeRotationInInteractAnimation = false;
            _stationPlayerPoseSavedInSpecialInteractAnimation = false;
            _stationPlayerPoseSavedEnteringSpecialAnimation = false;
            _stationPlayerPoseHadController = false;
            _stationPlayerPoseSavedControllerDetectCollisions = false;
            _stationPlayerPoseSavedControllerEnabled = false;
            _stationPlayerPoseSavedWasCrouching = false;
            _stationPlayerCameraBaselineCaptured = false;
            _stationPlayerCameraPlayerLocalPosition = Vector3.zero;
            _stationPlayerCameraPositionStabilizer = null;
            _stationPoseTrackingLogged = false;
        }

        private static void BeginPhysicalFocusView(PlayerControllerB player)
        {
            if (player == null || player.gameplayCamera == null)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Physical focus view: gameplay camera unavailable; staying in normal player camera pose.");
                return;
            }

            Camera camera = player.gameplayCamera;
            _physicalFocusCamera = camera;
            _physicalFocusCameraTransform = camera.transform;
            bool usePreSnapEntryLook = _stationPoseSettleTransferToFocus &&
                _stationPoseSettleEntryCameraPoseCaptured &&
                ReferenceEquals(_stationPoseSettlePlayer, player);
            Vector3 currentFocusWorldPosition = _physicalFocusCameraTransform.position;
            Vector3 enterStartPosition = usePreSnapEntryLook
                ? _stationPoseSettleEntryCameraWorldPosition
                : currentFocusWorldPosition;
            Quaternion enterStartRotation = usePreSnapEntryLook
                ? _stationPoseSettleEntryCameraWorldRotation
                : _physicalFocusCameraTransform.rotation;
            _priorFocusCameraLocalPosition = _physicalFocusCameraTransform.localPosition;
            _priorFocusCameraLocalRotation = _physicalFocusCameraTransform.localRotation;
            // Exit/handback continues to use the established post-snap player eye.
            // Only the enter path and its first physical render start at the
            // interaction-begin pose captured before the root snap.
            _priorFocusCameraWorldPosition = currentFocusWorldPosition;
            _priorFocusCameraWorldRotation = enterStartRotation;
            _priorFocusCameraFov = camera.fieldOfView;
            _physicalFocusViewActive = true;
            // A reassert scheduled by the previous session's API restore must
            // not fire into this session's intro camera: the tick runs whether
            // or not focus is active, and it would re-zero the camera's local
            // yaw and rotate the root mid-flourish.
            _exitCameraInvariantPlayer = null;
            _exitCameraInvariantScheduledFrame = -1;

            CaptureFirstPersonArmsCameraPose(player);
            EntryPerfMark("BPFV.capture-arms-pose");
            if (!HasActiveLocalPlayerObstructors())
            {
                _localObstructorCallLogs.Clear();
                // Non-settle entry (settle already reset it on its own path).
                _visorSnapProbeLogsRemaining = VISOR_SNAP_PROBE_LOG_BUDGET;
            }
            SuppressLocalPlayerObstructors(player, "focus-view-begin");
            EntryPerfMark("BPFV.suppress-obstructors");
            ArmLocalFirstPersonArmsPresentation(player);
            EntryPerfMark("BPFV.arm-first-person-arms");

            Transform anchor = CCTVOperatorStation.FocusViewAnchor;
            if (anchor != null)
            {
                ResolveStationFocusCameraPose(
                    anchor,
                    out Vector3 stationFocusPosition,
                    out Quaternion stationFocusRotation);
                EntryPerfMark("BPFV.resolve-station-pose");
                // The authored focus placement is the camera authority.
                // Lever reach is solved by the hidden first-person shoulder
                // anchor, never by moving the player's approved viewpoint.
                StartFocusViewAnimation(
                    enterStartPosition,
                    enterStartRotation,
                    camera.fieldOfView,
                    stationFocusPosition,
                    stationFocusRotation,
                    GetPreservedFocusFov(),
                    STATION_FOCUS_ENTER_TOTAL_SECONDS,
                    exiting: false,
                    delaySeconds: STATION_FOCUS_ENTER_BUTTON_PRESS_DELAY);
                EntryPerfMark("BPFV.start-focus-view-animation");
                BuildStationEnterCameraPath(
                    player,
                    enterStartPosition,
                    enterStartRotation,
                    stationFocusPosition,
                    stationFocusRotation);
                EntryPerfMark("BPFV.build-enter-camera-path");
                _introCameraRenderedFramesLogged = 0;
                _introCameraLastRenderedFrame = -1;
                LogIntroCameraPose(
                    "enter-start-knot",
                    _focusViewAnimationStartPosition,
                    _focusViewAnimationStartRotation);
                ApplyPhysicalFocusView();
                EntryPerfMark("BPFV.apply-physical-focus-view");
                return;
            }
            SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Physical focus view: CCTVFocusViewAnchor missing from terminal prefab; falling back to FOV-only focus.");

            ApplyPhysicalFocusView();
        }

        /// <summary>
        /// Enter camera choreography (camera-lean redesign, user spec
        /// 2026-07-22): lean the camera IN toward the access button so the
        /// camera-pinned shoulder anchor comes within arm reach of the
        /// contact, hold still through the press, then lean out and travel
        /// to the settle eye while looking at the desk lever, and finish
        /// with a pitch-only rise to the monitors (the lever knot shares the
        /// settle position, so the planted hand never loses reach). Reach is
        /// always achieved by moving the camera - never the arms relative to
        /// the camera. Falls back to the single lever-look waypoint when the
        /// button or lever transform has not resolved yet.
        /// </summary>
        private static void BuildStationEnterCameraPath(
            PlayerControllerB player,
            Vector3 startPosition,
            Quaternion startRotation,
            Vector3 focusPosition,
            Quaternion focusRotation)
        {
            _focusViewEnterPath = null;
            ResetStationIntroHandFollow();

            Transform lever = CCTVOperatorStation.RightHandTarget != null
                ? CCTVOperatorStation.RightHandTarget
                : CCTVOperatorStation.JoystickTiltPivot;
            Vector3? buttonPoint = Y4NGZPlayerAnimationBridge.ResolveAccessButtonPressPoint();

            if (buttonPoint == null || lever == null)
            {
                if (lever != null)
                {
                    Vector3 sweepMid = Vector3.Lerp(startPosition, focusPosition, 0.5f);
                    Vector3 toLeverMid = lever.position - sweepMid;
                    if (toLeverMid.sqrMagnitude > 0.01f)
                    {
                        _focusViewWaypointPosition = sweepMid;
                        _focusViewWaypointRotation = Quaternion.LookRotation(
                            toLeverMid.normalized,
                            Vector3.up);
                        _focusViewAnimationHasWaypoint = true;
                    }
                }
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Enter camera path fallback (button={(buttonPoint != null ? "ok" : "missing")}, " +
                    $"lever={(lever != null ? "ok" : "missing")}); using single-waypoint sweep.");
                return;
            }

            // Canonical enter beats (Test 36 Issue B): the baseline used to be
            // derived from the live walk-up pose, so every beat inherited
            // walk-up variance (re-entering from the canonical exit stand spot
            // framed the press completely differently from a fresh walk-up:
            // lean 0.352m/pitch 53.4 vs 0.831m/35.7). Derive the baseline from
            // the canonical stand eye instead — the same stand the pose lock
            // and canonical exit use — so press/lever/settle framing is
            // identical for every enter. Knot 0 stays the live pose: the
            // approach segment is the only position-dependent beat.
            string buttonEyeBasis = "live-start";
            Vector3 leanBaselineOrigin = startPosition;
            if (TryResolveCanonicalStationStandEye(player, out Vector3 canonicalStandEye))
            {
                leanBaselineOrigin = canonicalStandEye;
                buttonEyeBasis = "canonical-stand";
            }
            Vector3 baselineButtonEye = Vector3.Lerp(leanBaselineOrigin, focusPosition, STATION_FOCUS_ENTER_BUTTON_EYE_FRACTION);
            Vector3 buttonEye = ResolveEnterLeanEye(
                baselineButtonEye,
                buttonPoint.Value,
                STATION_FOCUS_ENTER_PRESS_REACH_BOUND_M,
                out float buttonAnchorDistance);
            // Beat 3 is pitch-only: the lever knot shares the settle eye so the
            // rise to the monitors never translates the (yaw-flat) anchor and
            // the planted hand keeps its reach for free.
            Vector3 leverEye = focusPosition;
            Vector3 toButton = buttonPoint.Value - buttonEye;
            Vector3 toLever = lever.position - leverEye;
            if (toButton.sqrMagnitude < 0.0025f || toLever.sqrMagnitude < 0.0025f)
                return;

            float leverAnchorDistance = Vector3.Distance(
                ComputeApiAnchorWorldForEye(leverEye, toLever),
                lever.position);
            float settleAnchorDistance = Vector3.Distance(
                ComputeApiAnchorWorldForEye(focusPosition, focusRotation * Vector3.forward),
                lever.position);
            if (Mathf.Max(leverAnchorDistance, settleAnchorDistance) > STATION_FOCUS_ENTER_LEVER_REACH_BOUND_M)
            {
                // This is expected when the approved CCTV framing sits beyond
                // the raw arm span. The control-phase shoulder reach assist owns
                // that shortfall without translating the camera.
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Enter camera path exceeds unassisted lever reach " +
                    $"(anchor->lever leverLook={leverAnchorDistance:0.000}m settleLook={settleAnchorDistance:0.000}m " +
                    $"bound={STATION_FOCUS_ENTER_LEVER_REACH_BOUND_M:0.00}m); " +
                    $"the first-person shoulder assist will compensate.");
            }

            Quaternion buttonRotation = Quaternion.LookRotation(toButton.normalized, Vector3.up);
            Quaternion leverRotation = Quaternion.LookRotation(toLever.normalized, Vector3.up);
            _focusViewEnterPath = new[]
            {
                new FocusPathKnot { T = 0f, Position = startPosition, Rotation = startRotation },
                new FocusPathKnot { T = STATION_FOCUS_ENTER_KNOT_BUTTON, Position = buttonEye, Rotation = buttonRotation },
                new FocusPathKnot { T = STATION_FOCUS_ENTER_KNOT_BUTTON_HOLD, Position = buttonEye, Rotation = buttonRotation },
                new FocusPathKnot { T = STATION_FOCUS_ENTER_KNOT_LEVER, Position = leverEye, Rotation = leverRotation },
                new FocusPathKnot { T = 1f, Position = focusPosition, Rotation = focusRotation }
            };
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Enter camera path built (camera-lean): button={FormatDebugVector(buttonPoint.Value)} " +
                $"lever={FormatDebugVector(lever.position)} " +
                $"buttonEye={FormatDebugVector(buttonEye)} " +
                $"buttonEyeBasis={buttonEyeBasis} " +
                $"baselineOrigin={FormatDebugVector(leanBaselineOrigin)} " +
                $"lean={Vector3.Distance(baselineButtonEye, buttonEye):0.000}m " +
                $"anchorToButton={buttonAnchorDistance:0.000}m " +
                $"anchorToLever={leverAnchorDistance:0.000}m " +
                $"buttonPitch={GetSignedCameraPitchDegrees(buttonRotation):0.0} " +
                $"leverPitch={GetSignedCameraPitchDegrees(leverRotation):0.0} " +
                $"total={STATION_FOCUS_ENTER_TOTAL_SECONDS:0.00}s.");
        }

        /// <summary>
        /// Canonical station stand eye: the camera position the operator ends
        /// at after the canonical exit (locked root target + the standing
        /// camera-player baseline). Mirrors BeginStationPlayerPoseTracking's
        /// target math because the enter path is built BEFORE the pose lock
        /// activates (BeginPhysicalFocusView precedes BeginStationPlayerPoseTracking
        /// in the focus-enter sequence), so the locked fields cannot be read
        /// here yet. Used to make the button/lever/settle beats position-
        /// independent (Test 36 Issue B).
        /// </summary>
        private static bool TryResolveCanonicalStationStandEye(
            PlayerControllerB player,
            out Vector3 standEye)
        {
            standEye = Vector3.zero;
            Transform pose = CCTVOperatorStation.OperatorPoseAnchor;
            if (player == null || pose == null)
                return false;

            bool apiModeConfigured = Y4NGZPlayerAnimationBridge.UseInteractionsApiOperatorSession;
            bool floorRootRequested = apiModeConfigured &&
                Y4NGZPlayerAnimationBridge.UseApiOperatorRootAtFloorLevel;
            Transform floorAnchor = CCTVOperatorStation.PlayerRootAnchor;
            bool useFloorRoot = floorRootRequested && floorAnchor != null &&
                !ReferenceEquals(floorAnchor, pose);
            bool useSettleSnapshot = _stationPoseSettleTransferToFocus &&
                _stationPoseSettleSnapshot.Valid &&
                ReferenceEquals(_stationPoseSettleSnapshot.Player, player);
            // Post-settle the root already stands at the station, so its live
            // Y is the grounded height the pose lock will adopt moments later.
            float groundedWorldY = useSettleSnapshot
                ? _stationPoseSettleTargetPosition.y
                : player.transform.position.y;
            Vector3 rootPosition = useFloorRoot
                ? new Vector3(floorAnchor.position.x, groundedWorldY, floorAnchor.position.z)
                : pose.position;
            Quaternion rootRotation = useFloorRoot
                ? Quaternion.Euler(0f, pose.eulerAngles.y, 0f)
                : pose.rotation;
            Vector3 cameraPlayerLocal;
            if (useSettleSnapshot && _stationPoseSettleSnapshot.CameraBaselineCaptured)
            {
                cameraPlayerLocal = _stationPoseSettleSnapshot.CameraPlayerLocalPosition;
            }
            else if (player.gameplayCamera != null)
            {
                cameraPlayerLocal = player.transform.InverseTransformPoint(
                    player.gameplayCamera.transform.position);
            }
            else
            {
                return false;
            }

            standEye = rootPosition + rootRotation * cameraPlayerLocal;
            return true;
        }

        /// <summary>
        /// World position of the arms-root shoulder anchor for a camera eye at
        /// <paramref name="eyePosition"/> looking along <paramref name="lookDirection"/>.
        /// Mirrors Y4NGZPlayerAnimationBridge.ResolveApiShoulderAnchorTarget's
        /// camera-hold frame (the yaw-flat offset; the pitch-relative variant
        /// was deleted for 1.0, #575) so the path builder can size the lean
        /// against the same anchor the pin will actually hold.
        /// </summary>
        private static Vector3 ComputeApiAnchorWorldForEye(Vector3 eyePosition, Vector3 lookDirection)
        {
            Vector3 flattened = Vector3.ProjectOnPlane(lookDirection, Vector3.up);
            if (flattened.sqrMagnitude < 0.0001f)
                flattened = Vector3.forward;
            Quaternion yawFlatRotation = Quaternion.LookRotation(flattened.normalized, Vector3.up);
            return eyePosition + yawFlatRotation * Y4NGZPlayerAnimationBridge.ApiCameraAnchorOffset;
        }

        /// <summary>
        /// Slides the eye from <paramref name="baselineEye"/> toward
        /// <paramref name="contactPoint"/> along their connecting line until
        /// the shoulder anchor for that eye is within
        /// <paramref name="reachBound"/> of the contact (least lean that
        /// satisfies reach; no lean when the baseline already reaches). The
        /// camera-frame rotation is constant along the line (the look
        /// direction to the contact does not change), so the anchor offset is
        /// a fixed vector c and the bound is the quadratic |s*v + c| <= r in
        /// the line parameter s.
        /// </summary>
        private static Vector3 ResolveEnterLeanEye(
            Vector3 baselineEye,
            Vector3 contactPoint,
            float reachBound,
            out float anchorDistance)
        {
            Vector3 v = baselineEye - contactPoint;
            float vLength = v.magnitude;
            anchorDistance = Vector3.Distance(
                ComputeApiAnchorWorldForEye(baselineEye, -v),
                contactPoint);
            if (vLength < STATION_FOCUS_ENTER_MIN_CONTACT_EYE_DISTANCE_M)
                return baselineEye;
            if (anchorDistance <= reachBound)
                return baselineEye;

            // World-space anchor offset from the eye; constant along the line,
            // so anchor(s) - contact = s*v + c with eye(s) = contact + s*v.
            Vector3 c = ComputeApiAnchorWorldForEye(baselineEye, -v) - baselineEye;
            float a = Vector3.Dot(v, v);
            float b = 2f * Vector3.Dot(v, c);
            float k = Vector3.Dot(c, c) - reachBound * reachBound;
            float discriminant = b * b - 4f * a * k;
            if (discriminant < 0f || a < 0.0001f)
                return baselineEye;

            float s = (-b + Mathf.Sqrt(discriminant)) / (2f * a);
            float sMin = STATION_FOCUS_ENTER_MIN_CONTACT_EYE_DISTANCE_M / vLength;
            s = Mathf.Clamp(s, sMin, 1f);
            Vector3 leanEye = contactPoint + v * s;
            anchorDistance = Vector3.Distance(
                ComputeApiAnchorWorldForEye(leanEye, contactPoint - leanEye),
                contactPoint);
            return leanEye;
        }

        private static void EvaluateStationEnterCameraPath(float rawT, out Vector3 position, out Quaternion rotation)
        {
            FocusPathKnot[] path = _focusViewEnterPath;
            rawT = Mathf.Clamp01(rawT);
            int last = path.Length - 1;
            int segment = 0;
            while (segment < last - 1 && rawT > path[segment + 1].T)
                segment++;

            FocusPathKnot from = path[segment];
            FocusPathKnot to = path[segment + 1];
            float span = Mathf.Max(0.0001f, to.T - from.T);
            float eased = SmoothFocusTransition(Mathf.Clamp01((rawT - from.T) / span));
            position = Vector3.Lerp(from.Position, to.Position, eased);
            rotation = Quaternion.Slerp(from.Rotation, to.Rotation, eased);

            if (rawT <= STATION_FOCUS_ENTER_KNOT_BUTTON_HOLD)
            {
                Vector3? buttonPoint = Y4NGZPlayerAnimationBridge.ResolveAccessButtonPressPoint();
                Vector3 toButton = buttonPoint.HasValue
                    ? buttonPoint.Value - position
                    : Vector3.zero;
                if (toButton.sqrMagnitude >= STATION_INTRO_HAND_FOLLOW_MIN_DIRECTION_SQR_M)
                {
                    Quaternion liveButtonRotation = Quaternion.LookRotation(
                        toButton.normalized,
                        Vector3.up);
                    float startBlend = SmoothFocusTransition(Mathf.Clamp01(
                        rawT / STATION_FOCUS_ENTER_BUTTON_LOOK_BLEND_END_T));
                    rotation = Quaternion.Slerp(path[0].Rotation, liveButtonRotation, startBlend);
                }
            }
        }

        private static float EvaluateStationEnterFovWidening(float rawT)
        {
            rawT = Mathf.Clamp01(rawT);
            if (rawT <= STATION_FOCUS_ENTER_KNOT_BUTTON)
            {
                float segmentT = rawT / Mathf.Max(0.0001f, STATION_FOCUS_ENTER_KNOT_BUTTON);
                return Mathf.Lerp(
                    0f,
                    STATION_FOCUS_ENTER_BUTTON_FOV_WIDEN_DEG,
                    SmoothFocusTransition(segmentT));
            }

            if (rawT <= STATION_FOCUS_ENTER_KNOT_BUTTON_HOLD)
                return STATION_FOCUS_ENTER_BUTTON_FOV_WIDEN_DEG;

            if (rawT <= STATION_FOCUS_ENTER_KNOT_LEVER)
            {
                float segmentT =
                    (rawT - STATION_FOCUS_ENTER_KNOT_BUTTON_HOLD) /
                    Mathf.Max(
                        0.0001f,
                        STATION_FOCUS_ENTER_KNOT_LEVER - STATION_FOCUS_ENTER_KNOT_BUTTON_HOLD);
                return Mathf.Lerp(
                    STATION_FOCUS_ENTER_BUTTON_FOV_WIDEN_DEG,
                    STATION_FOCUS_ENTER_LEVER_FOV_WIDEN_DEG,
                    SmoothFocusTransition(segmentT));
            }

            float settleT =
                (rawT - STATION_FOCUS_ENTER_KNOT_LEVER) /
                Mathf.Max(0.0001f, 1f - STATION_FOCUS_ENTER_KNOT_LEVER);
            return Mathf.Lerp(
                STATION_FOCUS_ENTER_LEVER_FOV_WIDEN_DEG,
                0f,
                SmoothFocusTransition(settleT));
        }

        private static void ResetStationIntroHandFollow()
        {
            _stationIntroHandFollowTarget = null;
            _stationIntroHandFollowSmoothedRotation = Quaternion.identity;
            _stationIntroHandFollowRotationInitialized = false;
            _stationIntroHandFollowResolutionAttempted = false;
            _stationIntroHandFollowTargetWasResolved = false;
            _stationIntroHandFollowActivated = false;
            _stationIntroHandFollowActiveLogged = false;
            _stationIntroHandFollowFallbackLogged = false;
            _stationIntroHandFollowSettleLogged = false;
        }

        // The hand-follow intro camera was an experimental alternative to the
        // authored knot look-at and shipped off. Its four config keys (the
        // switch plus the station-space look bias) left the file for 1.0
        // (#575); the gate stays as a constant so the authored knot path is the
        // only one that can run.
        private static bool IsStationIntroHandFollowEnabled() => false;

        private static bool UseExitToStationPose => true;

        private static Vector3 GetStationIntroHandFollowLookBias() =>
            new Vector3(0f, 0f, -0.25f);

        private static Quaternion ResolveStationIntroHandFollowRotation(
            float rawT,
            Vector3 eyePosition,
            Quaternion knotRotation)
        {
            FocusPathKnot[] path = _focusViewEnterPath;
            Quaternion settleRotation = path[path.Length - 1].Rotation;
            // Once live following has owned any intro frame, always hard-land
            // on the authored/clamped knot even if config or API state changes.
            if (_stationIntroHandFollowActivated && rawT >= 1f)
                return settleRotation;

            if (!IsStationIntroHandFollowEnabled() ||
                !Y4NGZPlayerAnimationBridge.IsLocalInteractionsApiSessionActive)
            {
                return knotRotation;
            }

            if (rawT >= 1f)
                return settleRotation;

            if (!TryResolveStationIntroHandFollowTarget())
                return knotRotation;

            Vector3 trackedHandPosition = _stationIntroHandFollowTarget.position;
            Transform operatorPose = CCTVOperatorStation.OperatorPoseAnchor;
            Transform stationRoot = operatorPose != null ? operatorPose.parent : null;
            if (stationRoot != null)
            {
                trackedHandPosition += stationRoot.TransformVector(
                    GetStationIntroHandFollowLookBias());
            }
            Vector3 direction = trackedHandPosition - eyePosition;
            Quaternion desiredRotation;
            if (direction.sqrMagnitude < STATION_INTRO_HAND_FOLLOW_MIN_DIRECTION_SQR_M)
            {
                if (!_stationIntroHandFollowRotationInitialized)
                    return knotRotation;
                desiredRotation = _stationIntroHandFollowSmoothedRotation;
            }
            else
            {
                // The scripted intro is allowed to pitch past the presentation
                // clamp; only the authored settle endpoint remains clamped.
                desiredRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            }
            if (!_stationIntroHandFollowRotationInitialized)
            {
                _stationIntroHandFollowSmoothedRotation = _focusViewAnimationStartRotation;
                _stationIntroHandFollowRotationInitialized = true;
            }

            float smoothingAlpha = 1f - Mathf.Exp(
                -STATION_INTRO_HAND_FOLLOW_SMOOTHING_RATE * Mathf.Max(0f, Time.unscaledDeltaTime));
            _stationIntroHandFollowSmoothedRotation = Quaternion.Slerp(
                _stationIntroHandFollowSmoothedRotation,
                desiredRotation,
                smoothingAlpha);

            float elapsedSeconds = Mathf.Clamp01(rawT) * STATION_FOCUS_ENTER_TOTAL_SECONDS;
            float blendIn = SmoothFocusTransition(Mathf.Clamp01(
                elapsedSeconds / STATION_INTRO_HAND_FOLLOW_BLEND_IN_SECONDS));
            Quaternion followedRotation = Quaternion.Slerp(
                _focusViewAnimationStartRotation,
                _stationIntroHandFollowSmoothedRotation,
                blendIn);

            float blendOut = SmoothFocusTransition(Mathf.InverseLerp(
                STATION_INTRO_HAND_FOLLOW_BLEND_OUT_START_T,
                1f,
                Mathf.Clamp01(rawT)));
            return Quaternion.Slerp(followedRotation, settleRotation, blendOut);
        }

        private static bool TryResolveStationIntroHandFollowTarget()
        {
            if (_stationIntroHandFollowTarget != null)
                return true;

            string attemptReason;
            if (!_stationIntroHandFollowResolutionAttempted)
            {
                _stationIntroHandFollowResolutionAttempted = true;
                attemptReason = "enter_start";
            }
            else if (_stationIntroHandFollowTargetWasResolved)
            {
                attemptReason = "cached_target_destroyed";
            }
            else
            {
                return false;
            }

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null)
            {
                LogStationIntroHandFollowFallback(attemptReason + ":local_player_missing");
                _stationIntroHandFollowTargetWasResolved = false;
                return false;
            }

            Transform searchRoot = player.localArmsTransform;
            if (searchRoot == null && player.gameplayCamera != null)
                searchRoot = player.gameplayCamera.transform.root;
            if (searchRoot == null)
            {
                LogStationIntroHandFollowFallback(attemptReason + ":local_arms_root_missing");
                _stationIntroHandFollowTargetWasResolved = false;
                return false;
            }

            Transform[] transforms = searchRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate != null &&
                    string.Equals(candidate.name, STATION_INTRO_HAND_FOLLOW_TARGET_NAME, StringComparison.Ordinal))
                {
                    _stationIntroHandFollowTarget = candidate;
                    _stationIntroHandFollowTargetWasResolved = true;
                    _stationIntroHandFollowActivated = true;
                    if (!_stationIntroHandFollowActiveLogged)
                    {
                        _stationIntroHandFollowActiveLogged = true;
                        SurveillanceBootstrap.Log?.LogInfo(
                            $"[LethalCCTV][IntroFollow] active target={GetTransformPath(candidate)} " +
                            $"smoothing={STATION_INTRO_HAND_FOLLOW_SMOOTHING_RATE:0.0}/s " +
                            $"blendIn={STATION_INTRO_HAND_FOLLOW_BLEND_IN_SECONDS:0.00}s " +
                            $"blendOut={STATION_INTRO_HAND_FOLLOW_BLEND_OUT_START_T:0.00}->1.00.");
                    }
                    return true;
                }
            }

            LogStationIntroHandFollowFallback(
                attemptReason + ":target_missing path=" + STATION_INTRO_HAND_FOLLOW_TARGET_PATH);
            _stationIntroHandFollowTargetWasResolved = false;
            return false;
        }

        private static void LogStationIntroHandFollowFallback(string reason)
        {
            if (_stationIntroHandFollowFallbackLogged)
                return;

            _stationIntroHandFollowFallbackLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV][IntroFollow] fallback_knots reason={reason}.");
        }

        private static void LogStationIntroHandFollowSettle(Quaternion appliedRotation)
        {
            if (!_stationIntroHandFollowActivated || _stationIntroHandFollowSettleLogged ||
                _focusViewEnterPath == null || _focusViewEnterPath.Length < 1)
            {
                return;
            }

            _stationIntroHandFollowSettleLogged = true;
            Quaternion settleRotation = _focusViewEnterPath[_focusViewEnterPath.Length - 1].Rotation;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][IntroFollow] settle finalRotationDelta={Quaternion.Angle(appliedRotation, settleRotation):F3}deg.");
        }

        private static float GetPreservedFocusFov()
        {
            if (_priorFocusCameraFov > 1f)
                return _priorFocusCameraFov;
            return _physicalFocusCamera != null
                ? Mathf.Clamp(_physicalFocusCamera.fieldOfView, 30f, 90f)
                : 60f;
        }

        private static void ApplyStationFocusRenderBudget(Camera camera)
        {
            if (camera == null || _focusRenderBudgetActive)
                return;

            _focusRenderBudgetCamera = camera;
            _focusRenderBudgetSavedFarClip = camera.farClipPlane;
            camera.farClipPlane = Mathf.Min(camera.farClipPlane, STATION_FOCUS_GAMEPLAY_FAR_CLIP_M);

            _focusRenderBudgetActive = true;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Station focus render budget applied: farClip {_focusRenderBudgetSavedFarClip:F1}m -> {camera.farClipPlane:F1}m, scalarOnly=True.");
        }

        private static void RestoreStationFocusRenderBudget()
        {
            if (!_focusRenderBudgetActive)
                return;

            if (_focusRenderBudgetCamera != null)
                _focusRenderBudgetCamera.farClipPlane = _focusRenderBudgetSavedFarClip;

            _focusRenderBudgetActive = false;
            _focusRenderBudgetCamera = null;
            _focusRenderBudgetSavedFarClip = 0f;
        }

        // Radar-viewpoint editor diagnostic. Two runs showed the anchor rotating
        // (proved by [ViewEdit] rotation input) while the operator saw no camera
        // movement, which means the anchor-to-camera link is broken somewhere
        // between the gates below. This records which branch actually ran and
        // whether our write survived to the next frame, so one run settles it.
        // Only ever active while the radar placement editor is open.
        private static float _radarEditDiagNextLogAt;
        private static string _radarEditDiagBranch = "<none>";
        private static bool _radarEditDiagWrotePreviousFrame;
        private static Quaternion _radarEditDiagLastWrittenRotation = Quaternion.identity;
        private static bool _radarEditDiagHavePreviousSample;
        private static float _radarEditDiagPreviousAnchorYaw;
        private static float _radarEditDiagPreviousCamYaw;

        private static void LogRadarEditCameraDiagnostic()
        {
            if (Time.unscaledTime < _radarEditDiagNextLogAt)
                return;
            _radarEditDiagNextLogAt = Time.unscaledTime + 0.5f;

            Transform radarAnchor = CCTVOperatorStation.RadarViewAnchor;
            bool haveCamera = _physicalFocusCameraTransform != null;
            // Read BEFORE this frame's camera write, so a non-zero drift means an
            // owner outside our LateUpdate step moved the camera since we wrote it.
            string drift = _radarEditDiagWrotePreviousFrame && haveCamera
                ? $"{Quaternion.Angle(_radarEditDiagLastWrittenRotation, _physicalFocusCameraTransform.rotation):0.00}deg"
                : "n/a";

            // The editor's Nudge runs earlier in the same LateUpdate (bootstrap step
            // "TickStation") than this read, so these two steps decide the whole
            // question on their own: anchorYawStep is what survived the editor's write,
            // camYawStep is what the rendered camera actually did with it.
            float anchorYaw = radarAnchor != null ? GetPlanarYaw(radarAnchor.forward) : 0f;
            float camYaw = haveCamera ? GetPlanarYaw(_physicalFocusCameraTransform.forward) : 0f;
            string anchorYawStep = _radarEditDiagHavePreviousSample && radarAnchor != null
                ? $"{Mathf.DeltaAngle(_radarEditDiagPreviousAnchorYaw, anchorYaw):0.00}deg"
                : "n/a";
            string camYawStep = _radarEditDiagHavePreviousSample && haveCamera
                ? $"{Mathf.DeltaAngle(_radarEditDiagPreviousCamYaw, camYaw):0.00}deg"
                : "n/a";
            _radarEditDiagHavePreviousSample = true;
            _radarEditDiagPreviousAnchorYaw = anchorYaw;
            _radarEditDiagPreviousCamYaw = camYaw;

            // camLocalEuler is the signature test for EnforceExitCameraLocalYawInvariant
            // (and anything else honouring vanilla's "all yaw lives on the body"
            // contract): local yaw/roll pinned at 0 with only pitch surviving is that
            // invariant, and bodyYaw shows where the folded yaw went.
            PlayerControllerB diagPlayer = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;

            SurveillanceBootstrap.Log?.LogMessage(
                "[LethalCCTV][RadarEditDiag] " +
                $"branchLastFrame={_radarEditDiagBranch} " +
                $"viewActive={_physicalFocusViewActive} haveCam={haveCamera} " +
                $"animating={_focusViewAnimating} " +
                $"tpPreview={_stationThirdPersonPreviewActive} " +
                $"editRadar={CCTVOperatorStation.IsEditingDebugPlacement("radar")} " +
                $"anchorEuler={(radarAnchor != null ? FormatDebugVector(radarAnchor.rotation.eulerAngles) : "<null>")} " +
                $"camEuler={(haveCamera ? FormatDebugVector(_physicalFocusCameraTransform.rotation.eulerAngles) : "<null>")} " +
                $"camLocalEuler={(haveCamera ? FormatDebugVector(_physicalFocusCameraTransform.localEulerAngles) : "<null>")} " +
                $"bodyYaw={(diagPlayer != null ? diagPlayer.transform.eulerAngles.y.ToString("0.00") : "<null>")} " +
                $"anchorYawStep={anchorYawStep} camYawStep={camYawStep} " +
                $"camPos={(haveCamera ? FormatDebugVector(_physicalFocusCameraTransform.position) : "<null>")} " +
                $"driftSinceOurWrite={drift}");
        }

        private static void ApplyPhysicalFocusView()
        {
            if (CCTVOperatorStation.IsEditingDebugPlacement("radar"))
                LogRadarEditCameraDiagnostic();
            else
                _radarEditDiagHavePreviousSample = false;
            // NOTE: do not release the viewpoint channel override here. This runs on
            // every non-editing frame, which is also every frame of the SPACE glance,
            // and releasing mid-glance would both cancel the turn and re-baseline the
            // saved pose to already-overridden values. The steady-glance branch owns
            // the release, at blend 0.

            ApplyPhysicalFocusViewCore();
            // Runs after every scripted camera write this method performs
            // (enter path, waypoint, plain glide, steady-state hold, exit
            // glide, control phase) — the visor must re-glue on the same
            // frame the camera moved.
            SnapLocalVisorToCameraTarget("focus-view");
        }

        private static void ApplyPhysicalFocusViewCore()
        {
            _radarEditDiagBranch = "entered";
            _radarEditDiagWrotePreviousFrame = false;
            if (!_physicalFocusViewActive) { _radarEditDiagBranch = "bail:viewInactive"; return; }
            if (_physicalFocusCamera == null || _physicalFocusCameraTransform == null)
            {
                _radarEditDiagBranch = "bail:noCamera";
                return;
            }

            if (_focusViewAnimating)
            {
                _radarEditDiagBranch = "animating";
                if (Time.unscaledTime < _focusViewAnimationDelayUntil)
                {
                    _physicalFocusCameraTransform.SetPositionAndRotation(
                        _focusViewAnimationStartPosition,
                        _focusViewAnimationStartRotation);
                    _physicalFocusCamera.fieldOfView = _focusViewAnimationStartFov;
                    LogIntroCameraRenderedFrame(0f, "delay");
                    return;
                }

                float duration = Mathf.Max(0.0001f, _focusViewAnimationDuration);
                if (Time.frameCount != _focusViewAnimationLastAdvanceFrame)
                {
                    _focusViewAnimationLastAdvanceFrame = Time.frameCount;
                    _focusViewAnimationElapsedSeconds += Mathf.Min(
                        Mathf.Max(0f, Time.unscaledDeltaTime),
                        FOCUS_ANIMATION_MAX_FRAME_STEP_SECONDS);
                }
                float rawT = Mathf.Clamp01(_focusViewAnimationElapsedSeconds / duration);
                float t = SmoothFocusTransition(rawT);
                float enterFovWidening = 0f;
                if (_focusViewEnterPath != null && _focusViewEnterPath.Length >= 2)
                {
                    EvaluateStationEnterCameraPath(rawT, out Vector3 pathPosition, out Quaternion pathRotation);
                    enterFovWidening = EvaluateStationEnterFovWidening(rawT);
                    Quaternion appliedRotation = ResolveStationIntroHandFollowRotation(
                        rawT,
                        pathPosition,
                        pathRotation);
                    _physicalFocusCameraTransform.SetPositionAndRotation(pathPosition, appliedRotation);
                    if (rawT >= 1f)
                        LogStationIntroHandFollowSettle(_physicalFocusCameraTransform.rotation);
                }
                else if (_focusViewAnimationHasWaypoint)
                {
                    // Quadratic de-Casteljau through the throttle-look waypoint:
                    // smooth C1 path that sweeps the lever mid-glide.
                    Vector3 posA = Vector3.Lerp(_focusViewAnimationStartPosition, _focusViewWaypointPosition, t);
                    Vector3 posB = Vector3.Lerp(_focusViewWaypointPosition, _focusViewAnimationTargetPosition, t);
                    Quaternion rotA = Quaternion.Slerp(_focusViewAnimationStartRotation, _focusViewWaypointRotation, t);
                    Quaternion rotB = Quaternion.Slerp(_focusViewWaypointRotation, _focusViewAnimationTargetRotation, t);
                    _physicalFocusCameraTransform.SetPositionAndRotation(
                        Vector3.Lerp(posA, posB, t),
                        Quaternion.Slerp(rotA, rotB, t));
                }
                else
                {
                    Quaternion animationRotation = Quaternion.Slerp(
                        _focusViewAnimationStartRotation,
                        _focusViewAnimationTargetRotation,
                        t);

                    _physicalFocusCameraTransform.SetPositionAndRotation(
                        Vector3.Lerp(_focusViewAnimationStartPosition, _focusViewAnimationTargetPosition, t),
                        animationRotation);
                }
                _physicalFocusCamera.fieldOfView = Mathf.Clamp(
                    Mathf.Lerp(_focusViewAnimationStartFov, _focusViewAnimationTargetFov, t) +
                        enterFovWidening,
                    1f,
                    179f);
                LogIntroCameraRenderedFrame(rawT, "path");

                if (rawT >= 1f)
                {
                    _focusViewAnimating = false;
                    if (_focusViewExitPending)
                    {
                        _exitHandbackAnimationCompletePoseCaptured = true;
                        _exitHandbackAnimationCompletePosition = _physicalFocusCameraTransform.position;
                        _exitHandbackAnimationCompleteRotation = _physicalFocusCameraTransform.rotation;
                        SurveillanceBootstrap.Log?.LogInfo(
                            $"[LethalCCTV][ExitSeam] exit anim complete: cam={FormatDebugVector(_physicalFocusCameraTransform.position)} " +
                            $"yaw={GetPlanarYaw(_physicalFocusCameraTransform.forward):F1} " +
                            $"pitch={GetSignedCameraPitchDegrees(_physicalFocusCameraTransform.rotation):F1} " +
                            $"rotationDelta={Quaternion.Angle(_exitSeamReferenceRotation, _physicalFocusCameraTransform.rotation):F3}deg; " +
                            "running cleanup.");
                        CompleteExitFocusCleanup();
                    }
                    else
                    {
                        _arrivalViewFramingLogPending = true;
                    }
                }
                return;
            }


            ApplyStationPhysicalFocusView();
        }

        private static void ApplyStationPhysicalFocusView()
        {
            // The operator-station anchor owns the standing lean-in pose. Steady-state
            // holds that anchor, with SPACE temporarily glancing toward the radar
            // monitor without turning off automatic camera control.
            CCTVOperatorStation.EnsureFocusAnchorAimed();
            Transform anchor = CCTVOperatorStation.FocusViewAnchor != null
                ? CCTVOperatorStation.FocusViewAnchor
                : QuadMonitor.FocusViewAnchor;
            if (anchor == null)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Station focus view: CCTVFocusViewAnchor missing from operator station.");
                return;
            }

            if (_stationThirdPersonPreviewActive)
            {
                _radarEditDiagBranch = "thirdPersonPreview";
                ApplyStationThirdPersonPreviewView(anchor);
                return;
            }

            ResolveStationFocusCameraPose(
                anchor,
                out Vector3 focusPosition,
                out Quaternion focusRotation);
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1f / 240f);
            CCTVOperatorStation.EnsureRadarAnchorAimed();
            Transform radarAnchor = CCTVOperatorStation.RadarViewAnchor;

            // Normal-view placement is also a live WYSIWYG camera editor.
            // Short-circuit the glance blend and every fallback writer so the
            // anchor being authored is exactly what the player sees.
            if (CCTVOperatorStation.IsEditingDebugPlacement("focus"))
            {
                _physicalFocusCameraTransform.SetPositionAndRotation(
                    focusPosition,
                    focusRotation);
                _physicalFocusCamera.fieldOfView = GetPreservedFocusFov();
                _radarEditDiagBranch = "focusWysiwyg";
                _radarEditDiagWrotePreviousFrame = true;
                _radarEditDiagLastWrittenRotation = focusRotation;
                return;
            }

            // Radar placement is a live camera editor. Auto-aim is suspended
            // by CCTVOperatorStation while this mode is active, and the camera
            // renders the edited anchor directly so every nudge is WYSIWYG.
            if (CCTVOperatorStation.IsEditingDebugPlacement("radar") &&
                radarAnchor != null)
            {
                ResolveStationFocusCameraPose(
                    radarAnchor,
                    out _,
                    out Quaternion editRotation);
                _physicalFocusCameraTransform.SetPositionAndRotation(
                    focusPosition,
                    editRotation);
                _physicalFocusCamera.fieldOfView = GetPreservedFocusFov();
                // The camera-transform write above only carries POSITION. Rotation
                // has to go through the body/cameraUp channels or vanilla discards
                // it before the frame renders — see the method comment.
                ApplyStationViewpointThroughVanillaChannels(editRotation);
                _radarEditDiagBranch = "radarWysiwyg";
                _radarEditDiagWrotePreviousFrame = true;
                _radarEditDiagLastWrittenRotation = editRotation;
                return;
            }

            bool forceFocusAnchor = Time.unscaledTime < _stationFocusAnchorForcedUntil;
            bool radarLookAllowed = !forceFocusAnchor && Time.unscaledTime >= _stationRadarLookSuppressedUntil;
            bool spaceHeld = !forceFocusAnchor && IsStationRadarLookHeld();
            bool wantRadarLook = radarLookAllowed &&
                spaceHeld &&
                !_stationEditMenuOpen &&
                !Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive &&
                !CCTVOperatorStation.IsDebugPlacementActive;
            if (_radarLookEventState != wantRadarLook)
            {
                _radarLookEventState = wantRadarLook;
                CCTVStationEvents.RaiseRadarViewChanged(wantRadarLook);
            }

            _stationRadarLookBlend = Mathf.MoveTowards(
                _stationRadarLookBlend,
                wantRadarLook ? 1f : 0f,
                dt / STATION_RADAR_GLANCE_SECONDS);
            float eased = SmoothFocusTransition(_stationRadarLookBlend);

            Quaternion radarTargetRotation = focusRotation * Quaternion.Euler(
                STATION_RADAR_LOOK_PITCH_DEG,
                STATION_RADAR_LOOK_YAW_DEG,
                STATION_RADAR_LOOK_ROLL_DEG);
            if (radarAnchor != null)
            {
                ResolveStationFocusCameraPose(radarAnchor, out _, out radarTargetRotation);
            }
            else
            {
                radarTargetRotation = ClampPresentationCameraPitch(radarTargetRotation);
            }

            // The glance has to blend out of the pose the station actually
            // RENDERS, not out of focusRotation. Only the body-yaw + cameraUp
            // pair reaches the screen (see ApplyStationViewpointThroughVanilla-
            // Channels), and with SPACE up those hold the OperatorPoseAnchor yaw
            // and the entry settle's cameraUp — a different authored pose than
            // the FocusViewAnchor the blend used to start from. Slerping out of
            // focusRotation therefore inserted that anchor-to-anchor delta on the
            // first glance frame and pulled it back out on the last. Measured on
            // the 2026-08-03 capture: a fixed one-frame yaw pop, -11.70/-11.38deg
            // on press and +11.68/+11.25deg on release. The press pop hid inside
            // the 38deg outgoing turn; the release pop fired after the smoothstep
            // had already decelerated to zero, which is the jerk being chased.
            // Sampling the rest channels while the glance is idle and blending
            // the two scalars makes blend 0 write exactly what the release hands
            // back, so the handoff is a no-op in both directions.
            PlayerControllerB glancePlayer = _stationPlayerPosePlayer;
            if (!_stationViewpointChannelOverrideActive && glancePlayer != null)
            {
                _stationGlanceRestYawDeg =
                    GetPlanarYaw(_stationPlayerPoseTargetRotation * Vector3.forward);
                _stationGlanceRestPitchDeg = glancePlayer.cameraUp;
            }

            float radarYawDeg = GetPlanarYaw(radarTargetRotation * Vector3.forward);
            float radarPitchDeg = GetSignedCameraPitchDegrees(radarTargetRotation);
            float glanceYawDeg = _stationGlanceRestYawDeg +
                Mathf.DeltaAngle(_stationGlanceRestYawDeg, radarYawDeg) * eased;
            float glancePitchDeg = Mathf.Lerp(_stationGlanceRestPitchDeg, radarPitchDeg, eased);
            Quaternion steadyRotation = Quaternion.Euler(glancePitchDeg, glanceYawDeg, 0f);
            _physicalFocusCameraTransform.SetPositionAndRotation(focusPosition, steadyRotation);
            _physicalFocusCamera.fieldOfView = GetPreservedFocusFov();
            // The SPACE glance has exactly the same problem the radar editor had: the
            // camera-transform write above carries position only, so the blended
            // rotation never reaches the rendered view. Route it through the body-yaw
            // and cameraUp channels instead. Still released at blend 0 rather than
            // held, so the idle frames re-sample the rest channels above and the
            // glance keeps tracking whatever the settle or a pose change leaves
            // behind. The release is now a no-op: blend 0 writes the sampled rest
            // yaw/pitch, which is what ReleaseStationViewpointChannelOverride
            // restores.
            if (_stationRadarLookBlend > 0.0001f)
                ApplyStationViewpointThroughVanillaChannels(steadyRotation);
            else
                ReleaseStationViewpointChannelOverride();
            _radarEditDiagBranch = $"steadyGlance(blend={_stationRadarLookBlend:0.00})";
            _radarEditDiagWrotePreviousFrame = true;
            _radarEditDiagLastWrittenRotation = steadyRotation;
        }

        // Save/restore for the viewpoint-edit override of the station pose channels.
        private static bool _stationViewpointChannelOverrideActive;
        private static Quaternion _stationViewpointSavedPoseRotation = Quaternion.identity;
        private static float _stationViewpointSavedCameraUp;
        private static float _stationViewpointSavedMinVerticalClamp;
        private static float _stationViewpointSavedMaxVerticalClamp;

        /// <summary>
        /// Pushes an authored station viewpoint through the only two rotation
        /// channels the station camera model actually honours: <b>yaw on the player
        /// body root</b> and <b>pitch on the publicized <c>cameraUp</c></b>.
        ///
        /// Writing <c>gameplayCamera.transform.rotation</c> does not work here and
        /// never did. <see cref="ApplyStationPlayerPoseLock"/> holds
        /// <c>inSpecialInteractAnimation = true</c>, which keeps vanilla's
        /// special-animation LateUpdate branch live; that branch rewrites the
        /// camera's local Euler from <c>cameraUp</c> every frame, and it runs after
        /// this plugin's LateUpdate. Measured 2026-08-01 during a radar edit: the
        /// anchor swung ±21 deg per sample while <c>camLocalEuler</c> stayed exactly
        /// (0,0,0) and <c>camEuler</c> stayed pinned to the body yaw, with
        /// ~100 deg of drift off our write every single frame. The position half of
        /// the SetPositionAndRotation call does survive — the mod owns camera
        /// position (that is what CCTVLocalCameraPositionStabilizer exists for),
        /// which is why this looked like a partial failure rather than a total one.
        ///
        /// Roll has no channel in this model — vanilla zeroes the camera's local Z
        /// unconditionally — so an authored roll cannot be presented. The editor
        /// still records it on the anchor; it simply will not render.
        /// </summary>
        private static void ApplyStationViewpointThroughVanillaChannels(Quaternion worldRotation)
        {
            PlayerControllerB player = _stationPlayerPosePlayer;
            if (!_stationPlayerPoseLockActive || player == null)
                return;

            if (!_stationViewpointChannelOverrideActive)
            {
                _stationViewpointChannelOverrideActive = true;
                _stationViewpointSavedPoseRotation = _stationPlayerPoseTargetRotation;
                _stationViewpointSavedCameraUp = player.cameraUp;
                _stationViewpointSavedMinVerticalClamp = player.minVerticalClamp;
                _stationViewpointSavedMaxVerticalClamp = player.maxVerticalClamp;
            }

            float yaw = GetPlanarYaw(worldRotation * Vector3.forward);
            float pitch = GetSignedCameraPitchDegrees(worldRotation);

            // Body yaw is consumed by ApplyStationPlayerPoseLock, which runs earlier
            // in TickFrame (Tick.cs step order: pose lock, then ApplyPhysicalFocusView),
            // so the target alone would land the turn on the NEXT frame while the
            // cameraUp pitch below lands on THIS one. That split the glance across two
            // frames and left a residual yaw step after the ease had stopped. Apply the
            // rotation directly as well so both halves render together; position is
            // untouched, so this cannot fight the pose lock's placement.
            _stationPlayerPoseTargetRotation = Quaternion.Euler(0f, yaw, 0f);
            player.transform.rotation = _stationPlayerPoseTargetRotation;

            // Entry settle pins both clamps to the arrival pitch (clampLooking is on),
            // so cameraUp is dragged straight back unless the window moves with it.
            // Same pin-to-target idiom the settle path uses.
            player.minVerticalClamp = pitch;
            player.maxVerticalClamp = pitch;
            player.cameraUp = pitch;
        }

        /// <summary>
        /// Hands the station pose channels back to whatever owned them before the
        /// viewpoint edit. Without this a cancelled edit would leave the operator's
        /// body yaw and pitch clamps stuck at the edited values for the rest of the
        /// focus session.
        /// </summary>
        private static void ReleaseStationViewpointChannelOverride()
        {
            if (!_stationViewpointChannelOverrideActive)
                return;

            _stationViewpointChannelOverrideActive = false;
            _stationPlayerPoseTargetRotation = _stationViewpointSavedPoseRotation;

            PlayerControllerB player = _stationPlayerPosePlayer;
            if (player == null)
                return;

            player.cameraUp = _stationViewpointSavedCameraUp;
            player.minVerticalClamp = _stationViewpointSavedMinVerticalClamp;
            player.maxVerticalClamp = _stationViewpointSavedMaxVerticalClamp;
        }

        private static void ResolveStationFocusCameraPose(Transform anchor, out Vector3 position, out Quaternion rotation)
        {
            position = anchor != null ? anchor.position : Vector3.zero;
            rotation = anchor != null ? anchor.rotation : Quaternion.identity;
            // Saved station viewpoints are authored camera poses. Applying the
            // general presentation clamp here made the editor's angle change
            // while the rendered camera appeared stuck at the clamp boundary.
            // Non-station fallback anchors retain the safety clamp.
            bool exactStationView = anchor != null &&
                CCTVOperatorStation.ShouldUseExactStationFocusPose(anchor);
            if (!exactStationView)
                rotation = ClampPresentationCameraPitch(rotation);
            // Test 39/41 viewpoint pull-back: offset the FOCUS placement
            // (only) back/up and tilt the look up so the settle frame shows
            // the FULL monitor AND the throttle hand. Applying it here keeps
            // the enter target, the path build and the steady-state hold on
            // the same eye, and keeps the F1 editor WYSIWYG (edits compose
            // with the offset).
            if (anchor != null && anchor == CCTVOperatorStation.FocusViewAnchor)
            {
                rotation = rotation *
                    Quaternion.Euler(-STATION_FOCUS_EYE_PITCH_UP_DEG, 0f, 0f);
                position += rotation * Vector3.back * STATION_FOCUS_EYE_PULLBACK_M +
                    Vector3.up * STATION_FOCUS_EYE_RAISE_M;
            }
        }

        /// <summary>
        /// The eye the station focus view actually renders from — the focus
        /// placement plus its presentation pitch-up, pullback and raise. The
        /// raw anchor is 0.22m in front of and 0.12m below this point, which is
        /// enough parallax to mis-aim the radar glance at conversational range,
        /// so anything aiming a station viewpoint must solve against this pose.
        /// </summary>
        internal static bool TryGetStationFocusPresentationEye(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            Transform anchor = CCTVOperatorStation.FocusViewAnchor;
            if (anchor == null)
                return false;

            ResolveStationFocusCameraPose(anchor, out position, out rotation);
            return true;
        }

        private static void ApplyStationThirdPersonPreviewView(Transform fallbackAnchor)
        {
            Transform pose = CCTVOperatorStation.OperatorPoseAnchor;
            Transform basis = pose != null ? pose : fallbackAnchor;
            if (basis == null || _physicalFocusCameraTransform == null || _physicalFocusCamera == null)
                return;

            Renderer previewRenderer = ForceLocalPlayerThirdPersonPreviewRenderers(_focusedPlayer);
            bool framedRendererBounds = TryGetPreviewRendererBounds(previewRenderer, out Bounds bounds);
            Vector3 target = framedRendererBounds
                ? bounds.center
                : basis.position + Vector3.up * STATION_THIRD_PERSON_PREVIEW_TARGET_HEIGHT;
            float span = framedRendererBounds
                ? Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z))
                : 0.9f;
            float distance = framedRendererBounds
                ? Mathf.Clamp(span * 1.45f + 0.55f, 1.05f, 2.45f)
                : STATION_THIRD_PERSON_PREVIEW_DISTANCE;
            float sideOffset = framedRendererBounds
                ? Mathf.Clamp(span * 0.48f, 0.45f, 0.95f)
                : STATION_THIRD_PERSON_PREVIEW_SIDE_OFFSET;
            float heightOffset = framedRendererBounds
                ? Mathf.Clamp(bounds.size.y * 0.18f + 0.16f, 0.24f, 0.62f)
                : STATION_THIRD_PERSON_PREVIEW_HEIGHT_OFFSET;

            Vector3 forward = basis.forward.sqrMagnitude > 0.0001f ? basis.forward.normalized : Vector3.forward;
            Vector3 right = basis.right.sqrMagnitude > 0.0001f ? basis.right.normalized : Vector3.right;
            Vector3 baseOffset =
                -forward * distance +
                right * sideOffset +
                Vector3.up * heightOffset;
            Vector3 cameraPosition = target + ApplyStationThirdPersonOrbit(baseOffset);

            Quaternion cameraRotation = Quaternion.LookRotation((target - cameraPosition).normalized, Vector3.up);
            _physicalFocusCameraTransform.SetPositionAndRotation(cameraPosition, cameraRotation);
            _physicalFocusCamera.fieldOfView = Mathf.Clamp(GetPreservedFocusFov() + 8f, 40f, 78f);
            LogStationThirdPersonPreviewFraming(previewRenderer, framedRendererBounds, bounds, target, cameraPosition);
        }

        internal static void ApplyStationThirdPersonOrbitDelta(Vector2 lookDeltaDeg)
        {
            _stationThirdPersonOrbitYawDeg = Mathf.Clamp(
                _stationThirdPersonOrbitYawDeg + lookDeltaDeg.x,
                STATION_THIRD_PERSON_ORBIT_YAW_MIN,
                STATION_THIRD_PERSON_ORBIT_YAW_MAX);
            _stationThirdPersonOrbitPitchDeg = Mathf.Clamp(
                _stationThirdPersonOrbitPitchDeg - lookDeltaDeg.y,
                STATION_THIRD_PERSON_ORBIT_PITCH_MIN,
                STATION_THIRD_PERSON_ORBIT_PITCH_MAX);
            _previewCameraFramingLogged = false;
        }

        private static Vector3 ApplyStationThirdPersonOrbit(Vector3 baseOffset)
        {
            if (baseOffset.sqrMagnitude < 0.0001f)
                baseOffset = Vector3.back;

            Vector3 yawed = Quaternion.AngleAxis(_stationThirdPersonOrbitYawDeg, Vector3.up) * baseOffset;
            Vector3 pitchAxis = Vector3.Cross(Vector3.up, -yawed.normalized);
            if (pitchAxis.sqrMagnitude < 0.0001f)
                pitchAxis = Vector3.right;
            return Quaternion.AngleAxis(_stationThirdPersonOrbitPitchDeg, pitchAxis.normalized) * yawed;
        }

        private static void ResetStationThirdPersonOrbit()
        {
            _stationThirdPersonOrbitYawDeg = 0f;
            _stationThirdPersonOrbitPitchDeg = 0f;
            _previewCameraFramingLogged = false;
            _stationGlobalThirdPersonFramingLogged = false;
        }

        private static void StartFocusViewAnimation(
            Vector3 startPosition,
            Quaternion startRotation,
            float startFov,
            Vector3 targetPosition,
            Quaternion targetRotation,
            float targetFov,
            float duration,
            bool exiting,
            float delaySeconds = 0f)
        {
            _focusViewAnimationStartPosition = startPosition;
            _focusViewAnimationStartRotation = startRotation;
            _focusViewAnimationStartFov = startFov;
            _focusViewAnimationTargetPosition = targetPosition;
            _focusViewAnimationTargetRotation = targetRotation;
            _focusViewAnimationTargetFov = targetFov;
            _focusViewAnimationStartedAt = Time.unscaledTime;
            _focusViewAnimationDelayUntil = delaySeconds > 0f
                ? _focusViewAnimationStartedAt + delaySeconds
                : 0f;
            _focusViewAnimationDuration = Mathf.Max(0.0001f, duration);
            _focusViewAnimationElapsedSeconds = 0f;
            // Same-frame first application must evaluate at rawT=0.
            _focusViewAnimationLastAdvanceFrame = Time.frameCount;
            _focusViewAnimationHasWaypoint = false;
            _focusViewEnterPath = null;
            _focusViewAnimating = true;
            _focusViewExitPending = exiting;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Focus view animation started: phase={(exiting ? "exit" : "enter")}, " +
                $"delay={Mathf.Max(0f, delaySeconds):0.00}s, duration={_focusViewAnimationDuration:0.00}s.");
        }

        private static float SmoothFocusTransition(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * value * (value * (value * 6f - 15f) + 10f);
        }

        private static bool BeginPhysicalFocusExitAnimation()
        {
            if (!_physicalFocusViewActive)
                return false;
            if (_physicalFocusCamera == null || _physicalFocusCameraTransform == null)
                return false;

            Vector3 startPosition = _physicalFocusCameraTransform.position;
            Quaternion startRotation = _physicalFocusCameraTransform.rotation;
            // Test-26 frame audit supersedes Round 22's zero-rotation exit:
            // retain the short monotonic translation, but rotate toward the
            // look that cleanup will impose. Canonical exit (Test 33):
            // that look is the station's yaw-level stand look, not the
            // entry-dependent interaction-begin capture, so every exit
            // glides to the identical pose regardless of walk-up.
            Quaternion handbackRotation = _priorFocusCameraWorldRotation;
            if (UseExitToStationPose && _stationPlayerPoseLockActive)
            {
                handbackRotation = Quaternion.Euler(
                    0f,
                    _stationPlayerPoseSavedRotation.eulerAngles.y,
                    0f);
                // EndPhysicalFocusView reapplies this field as the final
                // camera rotation; keep it in lockstep with the glide.
                _priorFocusCameraWorldRotation = handbackRotation;
            }
            else if (!_stationPlayerPoseLockActive)
            {
                // Pose tracking already restored and cleared its saved fields,
                // so a saved-pose target would degenerate to world origin.
                // The restore parked the camera at the standing eye, so keep
                // the current position and ease to a yaw-level look.
                handbackRotation = Quaternion.Euler(0f, startRotation.eulerAngles.y, 0f);
                _priorFocusCameraWorldRotation = handbackRotation;
            }
            Quaternion handbackBodyRotation = FlattenToYaw(handbackRotation);
            _stationPlayerPoseHandbackRotation = handbackBodyRotation;
            _stationPlayerPoseHandbackCameraPitch =
                GetSignedCameraPitchDegrees(handbackRotation);
            Vector3 stationTargetPosition = _stationPlayerPoseSavedPosition +
                handbackBodyRotation * _stationPlayerCameraPlayerLocalPosition;
            if (!_stationPlayerPoseLockActive)
            {
                stationTargetPosition = startPosition;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV][ExitSeam] exit anim begin after pose lock restore; " +
                    "holding camera position for the glide instead of the cleared saved pose.");
            }
            _exitHandbackAnimationCompletePoseCaptured = false;
            _exitHandbackAnimationCompletePosition = Vector3.zero;
            _exitHandbackAnimationCompleteRotation = Quaternion.identity;
            _exitSeamReferenceRotation = startRotation;
            _exitSeamStartedAt = Time.unscaledTime;
            _exitSeamLogUntil = Time.unscaledTime +
                STATION_FOCUS_EXIT_HAND_RELEASE_DELAY +
                STATION_FOCUS_EXIT_SECONDS +
                1.6f;
            _exitSeamNextLogAt = 0f;

            StartFocusViewAnimation(
                startPosition,
                startRotation,
                _physicalFocusCamera.fieldOfView,
                stationTargetPosition,
                handbackRotation,
                _priorFocusCameraFov,
                STATION_FOCUS_EXIT_SECONDS,
                exiting: true,
                delaySeconds: STATION_FOCUS_EXIT_HAND_RELEASE_DELAY);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][ExitSeam] exit anim begin: start={FormatDebugVector(startPosition)} " +
                $"rotStart={FormatDebugVector(startRotation.eulerAngles)} " +
                $"rotTarget={FormatDebugVector(handbackRotation.eulerAngles)} " +
                $"-> handbackTarget={FormatDebugVector(stationTargetPosition)} " +
                $"rotationDelta={Quaternion.Angle(startRotation, handbackRotation):F3}deg " +
                $"(savedWorld={FormatDebugVector(_stationPlayerPoseSavedPosition)}, " +
                $"savedBodyYaw={_stationPlayerPoseSavedRotation.eulerAngles.y:F1}, " +
                $"handbackBodyYaw={handbackBodyRotation.eulerAngles.y:F1}, " +
                $"cameraUp={_stationPlayerPoseHandbackCameraPitch:F1}, " +
                $"cameraPlayerLocal={FormatDebugVector(_stationPlayerCameraPlayerLocalPosition)}).");
            return true;
        }

        private static void EndPhysicalFocusView()
        {
            if (!_physicalFocusViewActive)
            {
                EndStationPlayerPoseTracking();
                ResetFirstPersonArmsCameraPose();
                return;
            }

            // Restore the root before applying the camera handback so the final
            // camera pose is evaluated under the restored standing transform.
            PlayerControllerB handbackPlayer = _stationPlayerPosePlayer ?? _focusedPlayer;
            float handbackCameraPitch = _stationPlayerPoseHandbackCameraPitch;
            Quaternion handbackWorldRotation = _priorFocusCameraWorldRotation;
            EndStationPlayerPoseTracking();

            if (_physicalFocusCamera != null && _physicalFocusCameraTransform != null)
            {
                // The glide target is built from the saved standing root
                // and camera-player-local baseline. Reapply that exact
                // world pose after root restore so both seam deltas are
                // zero before the invariant guard runs.
                _physicalFocusCameraTransform.SetPositionAndRotation(
                    _focusViewAnimationTargetPosition,
                    handbackWorldRotation);
                if (handbackPlayer != null)
                    handbackPlayer.cameraUp = handbackCameraPitch;
                // Establish the invariant once at the initial pose restore;
                // the deferred vanilla rig rebuild reasserts it after Build
                // plus Animator.Update(0f), then once more next frame.
                EnforceExitCameraLocalYawInvariant(
                    handbackPlayer,
                    "focus-view-restored");
                _physicalFocusCamera.fieldOfView = _priorFocusCameraFov;
                string handbackDelta = "<anim-complete-not-captured>";
                string positionDeltaTrace = "<anim-complete-not-captured>";
                string rotationDeltaTrace = "<anim-complete-not-captured>";
                if (_exitHandbackAnimationCompletePoseCaptured)
                {
                    float positionDelta = Vector3.Distance(
                        _exitHandbackAnimationCompletePosition,
                        _physicalFocusCameraTransform.position);
                    float rotationDelta = Quaternion.Angle(
                        _exitHandbackAnimationCompleteRotation,
                        _physicalFocusCameraTransform.rotation);
                    handbackDelta = $"{positionDelta:F4}m/{rotationDelta:F3}deg";
                    positionDeltaTrace = $"{positionDelta:F4}m";
                    rotationDeltaTrace = $"{rotationDelta:F3}deg";
                }
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ExitSeam] post-restore: cam={FormatDebugVector(_physicalFocusCameraTransform.position)} " +
                    $"yaw={GetPlanarYaw(_physicalFocusCameraTransform.forward):F1} " +
                    $"pitch={GetSignedCameraPitchDegrees(_physicalFocusCameraTransform.rotation):F1} " +
                    $"localRoll={_physicalFocusCameraTransform.localEulerAngles.z:F2} " +
                    $"positionDelta={positionDeltaTrace} rotationDelta={rotationDeltaTrace} " +
                    $"handbackDelta={handbackDelta} " +
                    "(handback pose restored under restored root).");
                if (_exitSeamStartedAt <= 0f)
                    _exitSeamStartedAt = Time.unscaledTime;
                _exitSeamLogUntil = Mathf.Max(
                    _exitSeamLogUntil,
                    Time.unscaledTime + 1.6f);
                _exitSeamNextLogAt = 0f;
            }
            RestoreStationFocusRenderBudget();

            if (Y4NGZPlayerAnimationBridge.IsLocalInteractionsApiSessionActive)
            {
                // The camera has handed back, but the API controller is still
                // restoring for its authored exit. Keep the mask hidden and
                // actively reassert it until CompleteInteractionsApiSession.
                _holdLocalObstructorsForInteractionsApiRestore = true;
                ReassertLocalPlayerObstructors();
                LogLocalObstructorCallOnce("hold", "interactions-api-restore");
            }
            else
            {
                RestoreLocalPlayerObstructors("focus-view-end");
            }
            ResetFirstPersonArmsCameraPose();

            _physicalFocusCamera = null;
            _physicalFocusCameraTransform = null;
            _priorFocusCameraLocalPosition = Vector3.zero;
            _priorFocusCameraLocalRotation = Quaternion.identity;
            _priorFocusCameraWorldPosition = Vector3.zero;
            _priorFocusCameraWorldRotation = Quaternion.identity;
            _priorFocusCameraFov = 0f;
            _focusViewAnimating = false;
            _focusViewExitPending = false;
            _focusViewAnimationDelayUntil = 0f;
            _physicalFocusViewActive = false;
            _exitHandbackAnimationCompletePoseCaptured = false;
            _exitHandbackAnimationCompletePosition = Vector3.zero;
            _exitHandbackAnimationCompleteRotation = Quaternion.identity;
        }

        private static void LogIntroCameraPose(
            string point,
            Vector3 worldPosition,
            Quaternion worldRotation)
        {
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][IntroCam] point={point} " +
                $"cameraWorldPos={FormatDebugVector(worldPosition)} " +
                $"cameraWorldEuler={FormatDebugVector(worldRotation.eulerAngles)}.");
        }

        private static void LogIntroCameraRenderedFrame(float rawT, string phase)
        {
            if (_focusViewExitPending ||
                _introCameraRenderedFramesLogged >= INTRO_CAMERA_FOCUS_RENDER_TELEMETRY_BUDGET ||
                Time.frameCount == _introCameraLastRenderedFrame)
            {
                return;
            }

            _introCameraLastRenderedFrame = Time.frameCount;
            _introCameraRenderedFramesLogged++;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][IntroCam] point=focus-render " +
                $"frame={_introCameraRenderedFramesLogged}/{INTRO_CAMERA_FOCUS_RENDER_TELEMETRY_BUDGET} unityFrame={Time.frameCount} " +
                $"cameraWorldPos={FormatDebugVector(_physicalFocusCameraTransform.position)} " +
                $"cameraWorldEuler={FormatDebugVector(_physicalFocusCameraTransform.eulerAngles)} " +
                $"rawT={rawT:F4} phase={phase}.");
        }

    }
}
