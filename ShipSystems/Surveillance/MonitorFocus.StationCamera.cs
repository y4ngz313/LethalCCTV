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
                _stationPlayerPoseHandbackRotation = FlattenToYaw(snapshot.Rotation);
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

            // Release the camera clamp ownership on every exit/abort. The body and
            // pitch hand-back above must not be replaced by the old entry pose.
            if (_stationViewpointChannelOverrideActive && player != null)
            {
                player.minVerticalClamp = _stationViewpointSavedMinVerticalClamp;
                player.maxVerticalClamp = _stationViewpointSavedMaxVerticalClamp;
            }
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
            _sessionFocusPoseValid = false;
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

        /// <summary>Travel from the captured eye to the operating eye with one ease.</summary>
        private static void BuildStationEnterCameraPath(
            PlayerControllerB player, Vector3 startPosition, Quaternion startRotation,
            Vector3 focusPosition, Quaternion focusRotation)
        {
            // One continuous eye trajectory. Hand reach belongs to the arm solve,
            // never to an abrupt camera push toward the control surface.
            _focusViewEnterPath = new[]
            {
                new FocusPathKnot { T = 0f, Position = startPosition, Rotation = startRotation },
                new FocusPathKnot { T = 1f, Position = focusPosition, Rotation = focusRotation }
            };
        }

        private static void EvaluateStationEnterCameraPath(float rawT, out Vector3 position, out Quaternion rotation)
        {
            FocusPathKnot from = _focusViewEnterPath[0];
            FocusPathKnot to = _focusViewEnterPath[_focusViewEnterPath.Length - 1];
            // Quintic easing has zero velocity AND acceleration at both seams.
            float eased = CctvIntroTiming.Ease(rawT);
            position = Vector3.Lerp(from.Position, to.Position, eased);
            rotation = BlendLevelStationView(from.Rotation, to.Rotation, eased);
        }

        // Interpolate heading and pitch separately. Slerping banked authoring
        // poses rolled the horizon during entry, then vanilla discarded the roll.
        private static Quaternion BlendLevelStationView(Quaternion from, Quaternion to, float t)
            => Quaternion.Euler(
                Mathf.Lerp(GetSignedCameraPitchDegrees(from), GetSignedCameraPitchDegrees(to), t),
                Mathf.LerpAngle(GetPlanarYaw(from * Vector3.forward), GetPlanarYaw(to * Vector3.forward), t),
                0f);

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
                    ApplyStationViewpointThroughVanillaChannels(_physicalFocusCameraTransform.rotation);
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
                // Animator.Update also writes camera ancestors. Evaluate it before
                // the final world camera pose, never in the post-camera IK pass.
                if (!_focusViewExitPending && _focusViewEnterPath != null)
                    Y4NGZPlayerAnimationBridge.EvaluateLocalEnterAnimationBeforeCamera(
                        _focusViewAnimationElapsedSeconds, rawT >= 1f);
                float t = SmoothFocusTransition(rawT);
                if (_focusViewEnterPath != null && _focusViewEnterPath.Length >= 2)
                {
                    EvaluateStationEnterCameraPath(rawT, out Vector3 pathPosition, out Quaternion pathRotation);
                    _physicalFocusCameraTransform.SetPositionAndRotation(pathPosition, pathRotation);
                }
                else
                {
                    Quaternion animationRotation = BlendLevelStationView(
                        _focusViewAnimationStartRotation,
                        _focusViewAnimationTargetRotation,
                        t);

                    _physicalFocusCameraTransform.SetPositionAndRotation(
                        Vector3.Lerp(_focusViewAnimationStartPosition, _focusViewAnimationTargetPosition, t),
                        animationRotation);
                }
                _physicalFocusCamera.fieldOfView = Mathf.Clamp(
                    Mathf.Lerp(_focusViewAnimationStartFov, _focusViewAnimationTargetFov, t),
                    1f,
                    179f);
                ApplyStationViewpointThroughVanillaChannels(_physicalFocusCameraTransform.rotation);
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
                ApplyStationViewpointThroughVanillaChannels(focusRotation);
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

            // Entry, normal control and radar glance share one pose authority.
            // Releasing to the legacy body yaw/cameraUp at blend zero discarded
            // the arrival angle and hid the controls below the screen.
            Quaternion steadyRotation = BlendLevelStationView(focusRotation, radarTargetRotation, eased);
            _physicalFocusCameraTransform.SetPositionAndRotation(focusPosition, steadyRotation);
            _physicalFocusCamera.fieldOfView = GetPreservedFocusFov();
            ApplyStationViewpointThroughVanillaChannels(steadyRotation);
            _radarEditDiagBranch = $"steadyGlance(blend={_stationRadarLookBlend:0.00})";
            _radarEditDiagWrotePreviousFrame = true;
            _radarEditDiagLastWrittenRotation = steadyRotation;
        }

        // Save/restore for the viewpoint-edit override of the station pose channels.
        private static bool _stationViewpointChannelOverrideActive;
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
                _stationViewpointSavedMinVerticalClamp = player.minVerticalClamp;
                _stationViewpointSavedMaxVerticalClamp = player.maxVerticalClamp;
            }

            float yaw = GetPlanarYaw(worldRotation * Vector3.forward);
            float pitch = GetSignedCameraPitchDegrees(worldRotation);
            Vector3 eye = _physicalFocusCameraTransform.position;

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
            // Rotating the parent also moves/rotates the camera child. Reapply the
            // resolved world pose after both channels have changed in this frame.
            _physicalFocusCameraTransform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, yaw, 0f));
        }

        private static bool _sessionFocusPoseValid;
        private static Vector3 _sessionFocusLocalPosition;
        private static Quaternion _sessionFocusLocalRotation;

        private static void ResolveStationFocusCameraPose(Transform anchor, out Vector3 position, out Quaternion rotation)
        {
            position = anchor != null ? anchor.position : Vector3.zero;
            rotation = anchor != null ? anchor.rotation : Quaternion.identity;
            bool sessionPose = _physicalFocusViewActive && anchor == CCTVOperatorStation.FocusViewAnchor && anchor != null;
            bool editing = CCTVOperatorStation.IsEditingDebugPlacement("focus");
            if (editing) _sessionFocusPoseValid = false;
            if (sessionPose && _sessionFocusPoseValid && !editing)
            {
                position = anchor.parent.TransformPoint(_sessionFocusLocalPosition);
                rotation = anchor.parent.rotation * _sessionFocusLocalRotation;
                return;
            }
            // Saved station viewpoints are authored camera poses. Applying the
            // general presentation clamp here made the editor's angle change
            // while the rendered camera appeared stuck at the clamp boundary.
            // Non-station fallback anchors retain the safety clamp.
            bool exactStationView = anchor != null &&
                CCTVOperatorStation.ShouldUseExactStationFocusPose(anchor);
            if (!exactStationView)
                rotation = ClampPresentationCameraPitch(rotation);
            rotation = BlendLevelStationView(rotation, rotation, 1f);
            if (sessionPose && !editing)
            {
                FitSessionMonitor(ref position, rotation);
                _sessionFocusLocalPosition = anchor.parent.InverseTransformPoint(position);
                _sessionFocusLocalRotation = Quaternion.Inverse(anchor.parent.rotation) * rotation;
                _sessionFocusPoseValid = true;
            }
        }

        internal static void ApplyLegacyFocusPresentationOffset(ref Vector3 position, ref Quaternion rotation)
        {
            rotation *= Quaternion.Euler(-STATION_FOCUS_EYE_PITCH_UP_DEG, 0f, 0f);
            position += FlattenToYaw(rotation) * Vector3.back * STATION_FOCUS_EYE_PULLBACK_M +
                Vector3.up * STATION_FOCUS_EYE_RAISE_M;
        }

        // Evaluate once at entry using the actual screen face, preserving player FOV.
        // The inset leaves room for the visor; live visor/suit clearance is a playtest gate.
        private static void FitSessionMonitor(ref Vector3 eye, Quaternion rotation)
        {
            Camera camera = _physicalFocusCamera;
            if (camera == null || !CCTVOperatorStation.TryResolveMainMonitorScreenFrame(
                out Vector3 center, out _, out Vector3 right, out Vector3 up,
                out float halfWidth, out float halfHeight)) return;
            eye = CctvControlGeometry.FitMonitorEye(eye, rotation, center, right, up,
                halfWidth, halfHeight, GetPreservedFocusFov(), camera.aspect);
        }

        /// <summary>
        /// The fitted session eye used by entry, operation and radar return.
        /// Legacy saved placements retain their presentation offset before the
        /// one-time fit; new placements use their authored eye directly.
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
            // Return to the saved entry eye and look so the glide and player
            // handback agree. The pre-settle player snapshot stays intact.
            Quaternion handbackRotation = _priorFocusCameraWorldRotation;
            if (!_stationPlayerPoseLockActive)
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
