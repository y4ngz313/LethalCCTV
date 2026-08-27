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
        private static bool TryEnterFocusPreflight(
            bool allowPoseSettleOwner,
            out PlayerControllerB player,
            out string failureReason)
        {
            player = null;
            failureReason = null;

            if (IsFocused)
            {
                failureReason = "already-focused";
                return false;
            }
            if (QuadMonitor.MonitorRoot == null || QuadMonitor.QuadRTs == null)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] MonitorFocus.EnterFocus: monitor not spawned, ignoring.");
                failureReason = "monitor-not-spawned";
                return false;
            }

            player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] MonitorFocus.EnterFocus: local player not resolvable, ignoring.");
                failureReason = "local-player-unavailable";
                return false;
            }
            if (player.inTerminalMenu)
            {
                failureReason = "terminal-menu-open";
                return false;
            }
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
            {
                failureReason = "quick-menu-open";
                return false;
            }
            if (player.isTypingChat)
            {
                failureReason = "chat-open";
                return false;
            }

            bool poseSettleOwned = allowPoseSettleOwner &&
                _stationPoseSettleActive &&
                _stationPoseSettleTransferToFocus &&
                ReferenceEquals(player, _stationPoseSettlePlayer);
            if (!poseSettleOwned)
            {
                RecoverStaleFocusInputLock(player, "enter-focus-preflight");
                ClearResidualFocusAnimation(player, "enter-focus-preflight");
            }
            if (!poseSettleOwned &&
                (player.inSpecialInteractAnimation || player.enteringSpecialAnimation))
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] MonitorFocus.EnterFocus ignored while the player is already in a special animation.");
                failureReason = "special-animation-active";
                return false;
            }

            return true;
        }

        internal static bool BeginEntryPoseSettle(PlayerControllerB requestedPlayer)
        {
            long startCostStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_stationPoseSettleActive)
                return RejectEntryPoseSettle(requestedPlayer, "already-settling");

            if (!TryEnterFocusPreflight(
                    allowPoseSettleOwner: false,
                    out PlayerControllerB player,
                    out string failureReason))
            {
                return RejectEntryPoseSettle(requestedPlayer, "preflight-" + failureReason);
            }
            if (requestedPlayer != null && !ReferenceEquals(requestedPlayer, player))
                return RejectEntryPoseSettle(requestedPlayer, "local-player-changed");
            if (player.isPlayerDead || !player.isPlayerControlled)
                return RejectEntryPoseSettle(player, "player-invalid");
            if (!CCTVMonitorFeedSync.IsShipLandedForCctv())
                return RejectEntryPoseSettle(player, "round-not-landed");
            if (!CCTVMonitorFeedSync.IsLocalOperator)
                return RejectEntryPoseSettle(player, "entry-claim-not-owned");

            Transform anchor = CCTVOperatorStation.PlayerRootAnchor;
            if (!CCTVOperatorStation.IsReady || anchor == null)
                return RejectEntryPoseSettle(player, "operator-anchor-unavailable");
            if (player.gameplayCamera == null)
                return RejectEntryPoseSettle(player, "gameplay-camera-unavailable");

            _introCameraRenderedFramesLogged = 0;
            _introCameraLastRenderedFrame = -1;
            Transform gameplayCameraTransform = player.gameplayCamera.transform;
            _stationPoseSettleEntryCameraPoseCaptured = true;
            _stationPoseSettleEntryCameraWorldPosition = gameplayCameraTransform.position;
            _stationPoseSettleEntryCameraWorldRotation = gameplayCameraTransform.rotation;
            Y4NGZPlayerAnimationBridge.CaptureInteractionBeginPresentation(player);
            LogIntroCameraPose(
                "interaction-begin/pre-root-snap",
                _stationPoseSettleEntryCameraWorldPosition,
                _stationPoseSettleEntryCameraWorldRotation);

            long snapshotCostStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            StationPlayerPoseSnapshot snapshot = CaptureStationPlayerPoseSnapshot(player);
            long snapshotCostCompletedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            double snapshotCostMs =
                (snapshotCostCompletedAt - snapshotCostStartedAt) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            if (!snapshot.Valid)
            {
                Y4NGZPlayerAnimationBridge.AbortInteractionBeginPresentation(
                    player,
                    "pose-settle-pose-snapshot-failed");
                return RejectEntryPoseSettle(player, "pose-snapshot-failed");
            }

            Vector3 settleTargetPosition = new Vector3(
                anchor.position.x,
                snapshot.Position.y,
                anchor.position.z);
            // Preserve the live world look while the body moves to the station:
            // yaw belongs to the body root and pitch belongs to publicized
            // cameraUp. The 16-degree presentation clamp remains exclusively on
            // the final focus-view settle knot.
            Quaternion settleTargetRotation = FlattenToYaw(
                _stationPoseSettleEntryCameraWorldRotation);
            float settleTargetCameraUp = GetSignedCameraPitchDegrees(
                _stationPoseSettleEntryCameraWorldRotation);
            string cameraTargetKind = "live-look-preserved";
            string anchorKind = ReferenceEquals(anchor, CCTVOperatorStation.OperatorPoseAnchor)
                ? "operator-pose-fallback"
                : "player-root";
            string airborneAtStart = player.thisController == null
                ? "unknown"
                : (player.thisController.isGrounded ? "false" : "true");

            _stationPoseSettleActive = true;
            _stationPoseSettleTransferToFocus = false;
            _stationPoseSettlePlayer = player;
            _stationPoseSettleAnchor = anchor;
            _stationPoseSettleTargetPosition = settleTargetPosition;
            _stationPoseSettleTargetRotation = settleTargetRotation;
            _stationPoseSettleTargetCameraUp = settleTargetCameraUp;
            _stationPoseSettleSnapshot = snapshot;
            _stationPoseSettleStartedAt = Time.unscaledTime;
            _stationPoseSettleFrames = 0;
            _stationPoseSettleHardSetFrame = -1;
            _settleViewFramingLogPending = false;
            _arrivalViewFramingLogPending = false;

            try
            {
                float startDistance = Vector3.Distance(player.transform.position, settleTargetPosition);
                float startYawDelta = AbsAngleDelta(
                    player.transform.eulerAngles.y,
                    settleTargetRotation.eulerAngles.y);
                Vector3 cameraLocalEuler = player.gameplayCamera.transform.localEulerAngles;

                ApplyEntryPoseSettleFlags(player, anchor, notifyRemote: true);
                _localObstructorCallLogs.Clear();
                _boundaryInputRecoveryLogs.Clear();
                _visorSnapProbeLogsRemaining = VISOR_SNAP_PROBE_LOG_BUDGET;
                long suppressionCostStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                SuppressLocalPlayerObstructors(player, "pose-settle-start");
                long suppressionCostCompletedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                double suppressionCostMs =
                    (suppressionCostCompletedAt - suppressionCostStartedAt) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][PoseSettle] start " +
                    $"savedWorld={FormatDebugVector(snapshot.Position)}/yaw={snapshot.Rotation.eulerAngles.y:F2} " +
                    $"anchorWorld={FormatDebugVector(anchor.position)}/yaw={anchor.eulerAngles.y:F2} " +
                    $"settleY={settleTargetPosition.y:F3} anchorKind={anchorKind} airborneAtStart={airborneAtStart} " +
                    $"cameraTargetKind={cameraTargetKind} targetCamLocalEuler={FormatDebugVector(new Vector3(settleTargetCameraUp, 0f, 0f))} " +
                    $"startDistance={startDistance:F4} startYawDelta={startYawDelta:F3} " +
                    $"camLocalEuler={FormatDebugVector(cameraLocalEuler)}.");
                long startCostCompletedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                double totalStartCostMs =
                    (startCostCompletedAt - startCostStartedAt) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][PoseSettle] startCost=" +
                    $"total={totalStartCostMs:F3}ms suppression={suppressionCostMs:F3}ms " +
                    $"snapshot={snapshotCostMs:F3}ms.");
                return true;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Pose settle start failed: {ex.Message}");
                AbortEntryPoseSettle("start-failed");
                return false;
            }
        }

        private static bool RejectEntryPoseSettle(PlayerControllerB player, string reason)
        {
            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][PoseSettle] abort reason={reason}.");
            CCTVMonitorFeedSync.ReleaseLocalEntryClaim(player, "pose-settle-" + reason);
            return false;
        }

        private static StationPlayerPoseSnapshot CaptureStationPlayerPoseSnapshot(PlayerControllerB player)
        {
            if (player == null)
                return default;

            bool hadController = player.thisController != null;
            bool cameraBaselineCaptured = player.gameplayCamera != null;
            return new StationPlayerPoseSnapshot
            {
                Valid = true,
                Player = player,
                Position = player.transform.position,
                Rotation = player.transform.rotation,
                ServerPosition = player.serverPlayerPosition,
                SnapToServerPosition = player.snapToServerPosition,
                DisableSyncInAnimation = player.disableSyncInAnimation,
                DisableLookInput = player.disableLookInput,
                FreeRotationInInteractAnimation = player.freeRotationInInteractAnimation,
                ClampLooking = player.clampLooking,
                MinVerticalClamp = player.minVerticalClamp,
                MaxVerticalClamp = player.maxVerticalClamp,
                HorizontalClamp = player.horizontalClamp,
                InSpecialInteractAnimation = player.inSpecialInteractAnimation,
                EnteringSpecialAnimation = player.enteringSpecialAnimation,
                HadController = hadController,
                ControllerDetectCollisions = hadController && player.thisController.detectCollisions,
                ControllerEnabled = hadController && player.thisController.enabled,
                WasCrouching = player.isCrouching,
                CameraBaselineCaptured = cameraBaselineCaptured,
                CameraPlayerLocalPosition = cameraBaselineCaptured
                    ? player.transform.InverseTransformPoint(player.gameplayCamera.transform.position)
                    : Vector3.zero,
            };
        }

        private static void ApplyEntryPoseSettleFlags(
            PlayerControllerB player,
            Transform anchor,
            bool notifyRemote)
        {
            if (notifyRemote)
                player.UpdateSpecialAnimationValue(true, (short)anchor.eulerAngles.y);
            player.serverPlayerPosition = player.transform.localPosition;
            player.snapToServerPosition = false;
            player.disableSyncInAnimation = true;
            // Settle owns both body yaw and camera pitch. PlayerLookInput otherwise
            // writes body yaw/cameraUp even during special interaction, while the
            // vanilla special-animation LateUpdate branch writes cameraUp back to
            // the camera Euler. Disable input and trip the clampLooking guard so
            // TickEntryPoseSettle remains the one camera-pose writer.
            player.disableLookInput = true;
            player.freeRotationInInteractAnimation = false;
            player.clampLooking = true;
            player.minVerticalClamp = _stationPoseSettleTargetCameraUp;
            player.maxVerticalClamp = _stationPoseSettleTargetCameraUp;
            player.horizontalClamp = 0f;
            player.inSpecialInteractAnimation = true;
            player.enteringSpecialAnimation = false;
            if (player.isCrouching)
                player.Crouch(false);
            player.ResetFallGravity();
        }

        private static void TickEntryPoseSettle()
        {
            if (!_stationPoseSettleActive)
                return;

            string abortReason = GetEntryPoseSettleAbortReason();
            if (abortReason != null)
            {
                AbortEntryPoseSettle(abortReason);
                return;
            }

            PlayerControllerB player = _stationPoseSettlePlayer;
            Transform anchor = _stationPoseSettleAnchor;
            Transform cameraTransform = player.gameplayCamera.transform;
            ReleaseEntryPoseSettleCameraRenderPin(cameraTransform);

            if (_stationPoseSettleHardSetFrame >= 0)
            {
                HardSetEntryPose(player, anchor, cameraTransform);
                EntryPerfMark("PoseSettle.hard-set");
                if (Time.frameCount <= _stationPoseSettleHardSetFrame)
                {
                    ApplyEntryPoseSettleCameraRenderPin(cameraTransform);
                    return;
                }

                RestoreEntryPoseSettleLookState(player, _stationPoseSettleSnapshot);
                _stationPoseSettleTransferToFocus = true;
                EntryPerfMark("PoseSettle.handoff-prep");
                try
                {
                    EnterFocus(player);
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Pose settle handoff to EnterFocus failed: {ex.Message}");
                }
                EntryPerfMark("EnterFocus.log-tail");

                if (IsFocused)
                {
                    ResetEntryPoseSettleState();
                    // Session-begin work inside EnterFocus (controller swap,
                    // animator Update(0), rig rebuild) rewrites the camera to
                    // the operator pose AFTER BeginPhysicalFocusView already
                    // applied the path start (Test 34: the begin frame
                    // rendered the desk pose). Reassert the path pose as this
                    // frame's final camera writer.
                    ApplyPhysicalFocusView();
                    EntryPerfMark("PoseSettle.post-enter-view");
                    return;
                }

                _stationPoseSettleTransferToFocus = false;
                AbortEntryPoseSettle("enter-focus-rejected");
                return;
            }

            _stationPoseSettleFrames++;
            float lerpRate = Time.deltaTime * STATION_POSE_SETTLE_RATE;
            Transform playerTransform = player.transform;
            Transform parent = playerTransform.parent;
            if (parent != null)
            {
                Vector3 targetLocalPosition = parent.InverseTransformPoint(_stationPoseSettleTargetPosition);
                playerTransform.localPosition = Vector3.Lerp(
                    playerTransform.localPosition,
                    targetLocalPosition,
                    lerpRate);
            }
            else
            {
                playerTransform.position = Vector3.Lerp(
                    playerTransform.position,
                    _stationPoseSettleTargetPosition,
                    lerpRate);
            }
            ApplyEntryPosePreservedLook(player, cameraTransform);
            player.serverPlayerPosition = playerTransform.localPosition;
            player.snapToServerPosition = false;

            float positionDelta = Vector3.Distance(playerTransform.position, _stationPoseSettleTargetPosition);
            float yawDelta = AbsAngleDelta(
                playerTransform.eulerAngles.y,
                _stationPoseSettleTargetRotation.eulerAngles.y);
            Vector3 actualCameraLocalEuler = cameraTransform.localEulerAngles;
            float cameraPitchDelta = AbsAngleDelta(
                actualCameraLocalEuler.x,
                _stationPoseSettleTargetCameraUp);
            float cameraYawDelta = AbsAngleDelta(actualCameraLocalEuler.y, 0f);
            float cameraRollDelta = AbsAngleDelta(actualCameraLocalEuler.z, 0f);
            bool cameraSettled = cameraPitchDelta <= STATION_POSE_SETTLE_CAMERA_TOLERANCE_DEG &&
                cameraYawDelta <= STATION_POSE_SETTLE_CAMERA_TOLERANCE_DEG &&
                cameraRollDelta <= STATION_POSE_SETTLE_CAMERA_TOLERANCE_DEG;
            float elapsed = Time.unscaledTime - _stationPoseSettleStartedAt;
            bool timedOut = elapsed >= STATION_POSE_SETTLE_TIMEOUT_SECONDS;
            bool withinGate = positionDelta <= STATION_POSE_SETTLE_POSITION_TOLERANCE_M &&
                yawDelta <= STATION_POSE_SETTLE_YAW_TOLERANCE_DEG &&
                cameraSettled;
            if (_stationPoseSettleFrames > 60 && _stationPoseSettleFrames % 30 == 0)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][PoseSettle] fight " +
                    $"frames={_stationPoseSettleFrames} elapsed={elapsed:F3} " +
                    $"rootYawDelta={yawDelta:F4} camPitchDelta={cameraPitchDelta:F4}.");
            }
            if (!withinGate && !timedOut)
            {
                ApplyEntryPoseSettleCameraRenderPin(cameraTransform);
                return;
            }

            HardSetEntryPose(player, anchor, cameraTransform);
            LogIntroCameraPose(
                "post-root-snap",
                cameraTransform.position,
                cameraTransform.rotation);
            _stationPoseSettleHardSetFrame = Time.frameCount;
            float finalPositionDelta = Vector3.Distance(
                playerTransform.position,
                _stationPoseSettleTargetPosition);
            float finalYawDelta = AbsAngleDelta(
                playerTransform.eulerAngles.y,
                _stationPoseSettleTargetRotation.eulerAngles.y);
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][PoseSettle] complete " +
                $"frames={_stationPoseSettleFrames} elapsed={elapsed:F3} " +
                $"finalPosDelta={finalPositionDelta:F5} finalYawDelta={finalYawDelta:F4} " +
                $"finalRootY={playerTransform.position.y:F3} " +
                $"finalCamLocalEuler={FormatDebugVector(cameraTransform.localEulerAngles)} " +
                $"timedOut={(timedOut ? "true" : "false")}.");
            _settleViewFramingLogPending = true;
            // The hard-set pose still renders this frame; keep the entry view.
            ApplyEntryPoseSettleCameraRenderPin(cameraTransform);
        }

        private static void ApplyEntryPoseSettleCameraRenderPin(Transform cameraTransform)
        {
            if (!_stationPoseSettleEntryCameraPoseCaptured || cameraTransform == null)
                return;

            _stationPoseSettleCameraRenderPinSavedPosition = cameraTransform.position;
            cameraTransform.position = _stationPoseSettleEntryCameraWorldPosition;
            _stationPoseSettleCameraRenderPinActive = true;
            // The pin moves the rendered camera; the visor must ride it or the
            // mask floats at the body's camera during the settle frames.
            SnapLocalVisorToCameraTarget("settle-render-pin");
        }

        private static void ReleaseEntryPoseSettleCameraRenderPin(Transform cameraTransform)
        {
            if (!_stationPoseSettleCameraRenderPinActive)
                return;

            if (cameraTransform != null)
                cameraTransform.position = _stationPoseSettleCameraRenderPinSavedPosition;
            _stationPoseSettleCameraRenderPinActive = false;
        }

        // Promoted from config to a constant for 1.0 (#575): the visor matches
        // the vanilla terminal's behaviour, and the legacy hide-for-the-session
        // path only ever existed as an A/B lever.
        private static bool KeepLocalVisorDuringSessionEnabled => true;

        // A visor farther than this from its camera target point is parked
        // (our legacy hide, vanilla's notSpawnedPosition death park) — never
        // fight a park by snapping it back onto the camera.
        private const float VISOR_SNAP_MAX_TRACK_DISTANCE_M = 2f;
        private const int VISOR_SNAP_PROBE_LOG_BUDGET = 3;
        private static int _visorSnapProbeLogsRemaining;

        /// <summary>
        /// Visor parity (Test 36 Issue D, research handoff 2026-07-22): vanilla
        /// glues localVisor to localVisorTargetPoint (a child of the gameplay
        /// camera) every owner LateUpdate — position hard-snap but rotation
        /// Lerp at 53/s (decompiled PlayerControllerB :7621). Scripted camera
        /// writes swing faster than that lerp catches up, sweeping the mask
        /// edge into frame — the historical reason every session hid the mask.
        /// Re-glue HARD (position and rotation) after each scripted camera
        /// write; scripted moves are deterministic so the smoothing has no
        /// purpose. The pre-snap deltas double as the glue-liveness probe the
        /// research handoff asked for ([VisorGlue], first frames per enter).
        /// </summary>
        private static void SnapLocalVisorToCameraTarget(string phase)
        {
            if (!KeepLocalVisorDuringSessionEnabled)
                return;
            if (_focusHiddenLocalVisor != null)
                return;

            PlayerControllerB player = _focusedPlayer != null ? _focusedPlayer : _stationPoseSettlePlayer;
            if (player == null)
                return;

            try
            {
                Transform visor = player.localVisor;
                Transform target = player.localVisorTargetPoint;
                if (visor == null || target == null)
                    return;

                Vector3 targetPosition = target.position;
                float positionDelta = Vector3.Distance(visor.position, targetPosition);
                if (positionDelta > VISOR_SNAP_MAX_TRACK_DISTANCE_M)
                {
                    if (_visorSnapProbeLogsRemaining > 0)
                    {
                        _visorSnapProbeLogsRemaining--;
                        SurveillanceBootstrap.Log?.LogInfo(
                            $"[LethalCCTV][VisorGlue] phase={phase} frame={Time.frameCount} " +
                            $"skipped=parked positionDelta={positionDelta:F2}m (visor parked elsewhere; not snapping).");
                    }
                    return;
                }

                if (_visorSnapProbeLogsRemaining > 0)
                {
                    _visorSnapProbeLogsRemaining--;
                    // positionDelta≈0 pre-snap ⇒ vanilla's glue ran this frame
                    // before us (ordering is load-bearing); rotationDelta is
                    // the lag our hard snap removes.
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][VisorGlue] phase={phase} frame={Time.frameCount} " +
                        $"preSnapPositionDelta={positionDelta:F4}m " +
                        $"preSnapRotationDelta={Quaternion.Angle(visor.rotation, target.rotation):F2}deg.");
                }

                visor.SetPositionAndRotation(targetPosition, target.rotation);
            }
            catch { }
        }

        private static string GetEntryPoseSettleAbortReason()
        {
            PlayerControllerB player = _stationPoseSettlePlayer;
            if (player == null)
                return "player-destroyed";
            if (player.isPlayerDead || !player.isPlayerControlled)
                return "player-invalid";
            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (!ReferenceEquals(local, player))
                return "local-player-changed";
            if (player.teleportedLastFrame)
                return "player-teleported";
            if (!CCTVMonitorFeedSync.IsShipLandedForCctv())
                return "round-ended";
            if (!CCTVMonitorFeedSync.IsLocalOperator)
                return "entry-claim-lost";
            if (!CCTVOperatorStation.IsReady ||
                _stationPoseSettleAnchor == null ||
                !ReferenceEquals(CCTVOperatorStation.PlayerRootAnchor, _stationPoseSettleAnchor))
            {
                return "operator-station-changed";
            }
            if (player.gameplayCamera == null)
                return "gameplay-camera-lost";
            if (player.inTerminalMenu)
                return "terminal-menu-opened";
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return "quick-menu-opened";
            if (player.isTypingChat)
                return "chat-opened";
            if (!player.inSpecialInteractAnimation || player.enteringSpecialAnimation)
                return "special-animation-state-lost";
            return null;
        }

        private static void HardSetEntryPose(
            PlayerControllerB player,
            Transform anchor,
            Transform cameraTransform)
        {
            player.transform.SetPositionAndRotation(
                _stationPoseSettleTargetPosition,
                _stationPoseSettleTargetRotation);
            ApplyEntryPosePreservedLook(player, cameraTransform);
            ApplyEntryPoseSettleFlags(player, anchor, notifyRemote: false);
        }

        private static void ApplyEntryPosePreservedLook(
            PlayerControllerB player,
            Transform cameraTransform)
        {
            player.transform.rotation = _stationPoseSettleTargetRotation;
            player.cameraUp = _stationPoseSettleTargetCameraUp;
            cameraTransform.localEulerAngles = new Vector3(
                _stationPoseSettleTargetCameraUp,
                0f,
                0f);

            // Match the proven zero-local-yaw exit write class. If an
            // intermediate parent contributes a tiny yaw, absorb that residual
            // into the body root and leave the camera at pitch-only local Euler.
            float targetViewYaw = GetPlanarYaw(
                _stationPoseSettleEntryCameraWorldRotation * Vector3.forward);
            float yawCorrection = Mathf.DeltaAngle(
                GetPlanarYaw(cameraTransform.forward),
                targetViewYaw);
            if (Mathf.Abs(yawCorrection) > 0.0001f)
            {
                player.transform.rotation =
                    Quaternion.AngleAxis(yawCorrection, Vector3.up) * player.transform.rotation;
                cameraTransform.localEulerAngles = new Vector3(
                    _stationPoseSettleTargetCameraUp,
                    0f,
                    0f);
            }
        }

        private static void RestoreEntryPoseSettleLookState(
            PlayerControllerB player,
            StationPlayerPoseSnapshot snapshot)
        {
            if (player == null || !snapshot.Valid || !ReferenceEquals(snapshot.Player, player))
                return;

            player.disableLookInput = snapshot.DisableLookInput;
            player.freeRotationInInteractAnimation = snapshot.FreeRotationInInteractAnimation;
            player.clampLooking = snapshot.ClampLooking;
            player.minVerticalClamp = snapshot.MinVerticalClamp;
            player.maxVerticalClamp = snapshot.MaxVerticalClamp;
            player.horizontalClamp = snapshot.HorizontalClamp;
        }

        private static void AbortEntryPoseSettle(string reason)
        {
            if (!_stationPoseSettleActive)
                return;

            PlayerControllerB player = _stationPoseSettlePlayer;
            if (player != null && player.gameplayCamera != null)
                ReleaseEntryPoseSettleCameraRenderPin(player.gameplayCamera.transform);
            Y4NGZPlayerAnimationBridge.AbortInteractionBeginPresentation(
                player,
                "pose-settle-" + reason);
            StationPlayerPoseSnapshot snapshot = _stationPoseSettleSnapshot;
            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][PoseSettle] abort reason={reason}.");

            if (player != null && snapshot.Valid && ReferenceEquals(snapshot.Player, player))
            {
                try
                {
                    if (!snapshot.InSpecialInteractAnimation)
                        player.UpdateSpecialAnimationValue(false, 0);
                    player.serverPlayerPosition = player.transform.localPosition;
                    player.snapToServerPosition = snapshot.SnapToServerPosition;
                    player.disableSyncInAnimation = snapshot.DisableSyncInAnimation;
                    RestoreEntryPoseSettleLookState(player, snapshot);
                    player.inSpecialInteractAnimation = snapshot.InSpecialInteractAnimation;
                    player.enteringSpecialAnimation = snapshot.EnteringSpecialAnimation;
                    if (player.isCrouching != snapshot.WasCrouching)
                        player.Crouch(snapshot.WasCrouching);
                    player.ResetFallGravity();
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Pose settle abort flag cleanup failed: {ex.Message}");
                }
            }

            RestoreLocalPlayerObstructors("pose-settle-abort");
            ResetEntryPoseSettleState();
            CCTVMonitorFeedSync.ReleaseLocalEntryClaim(player, "pose-settle-" + reason);
        }

        private static void ResetEntryPoseSettleState()
        {
            _stationPoseSettleActive = false;
            _stationPoseSettleTransferToFocus = false;
            _stationPoseSettlePlayer = null;
            _stationPoseSettleAnchor = null;
            _stationPoseSettleTargetPosition = Vector3.zero;
            _stationPoseSettleTargetRotation = Quaternion.identity;
            _stationPoseSettleTargetCameraUp = 0f;
            _stationPoseSettleEntryCameraPoseCaptured = false;
            _stationPoseSettleEntryCameraWorldPosition = Vector3.zero;
            _stationPoseSettleEntryCameraWorldRotation = Quaternion.identity;
            _stationPoseSettleSnapshot = default;
            _stationPoseSettleStartedAt = 0f;
            _stationPoseSettleFrames = 0;
            _stationPoseSettleHardSetFrame = -1;
            // Pin release without position restore: by the time state resets,
            // either the enter path owns the camera or vanilla motion resumes.
            _stationPoseSettleCameraRenderPinActive = false;
        }

        private static float AbsAngleDelta(float a, float b)
        {
            return Mathf.Abs(Mathf.DeltaAngle(a, b));
        }
    }
}
