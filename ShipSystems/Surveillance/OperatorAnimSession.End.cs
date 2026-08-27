using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed partial class OperatorAnimSession
    {
        /// <summary>
        /// Smooth exit: fire the controller's exit crossfade and ease the body
        /// back, then restore the vanilla controller once the pose has settled.
        /// </summary>
        internal void BeginGracefulEnd(string reason)
        {
            if (!_active || _windingDown)
            {
                if (!_windingDown)
                    EndImmediate(reason);
                return;
            }

            if (_interactionsApiMode)
            {
                _active = false;
                _windingDown = true;
                _cameraControlActive = false;
                _radarLookActive = false;
                _joystickPhase.SetRequested(Vector2.zero);
                _interactionsApiExitStartedAt = Time.unscaledTime;
                CCTVOperatorInteractionsBridge.TrySetBool(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.SeatedBool,
                    false);
                if (CCTVOperatorInteractionsBridge.TryBeginExit(
                        _interactionsApiHandle,
                        out string exitReason))
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][ApiPort] exit_begun " +
                        $"handle={CCTVOperatorInteractionsBridge.FormatHandle(_interactionsApiHandle)} " +
                        $"exitSeconds={CCTVOperatorInteractionsBridge.ExitClipLengthSeconds:0.00} " +
                        $"reason='{reason}'.");
                    return;
                }

                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ApiPort] exit_begin_failed " +
                    $"handle={CCTVOperatorInteractionsBridge.FormatHandle(_interactionsApiHandle)} " +
                    $"reason='{exitReason}'; forcing immediate stop.");
                EndImmediate(reason + "-api-exit-failed");
                return;
            }

            if (!_controllerApplied || _animator == null || Player == null ||
                Player.isPlayerDead || !Player.isPlayerControlled)
            {
                EndImmediate(reason);
                return;
            }

            CaptureWindDownHandTargetHoldPose();
            _active = false;
            _windingDown = true;
            _exitArmsTelemetryEnabled = _isLocal && _controllerApplied;
            _exitArmsLastLoggedFrame = -1;
            float now = Time.unscaledTime;
            _restoreAt = now + GracefulRestoreSeconds;
            _exitReleaseAt = now + GracefulExitNeutralizeSeconds;
            _cameraControlActive = false;
            _radarLookActive = false;
            _joystickPhase.SetRequested(Vector2.zero);
            SetBool(SeatedHash, true);
            SetBool(CameraControlHash, false);
            SetBool(RadarLookHash, false);
            ApplyOperatorAnimatorOverrides();
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Operator anim session winding down ({reason}); " +
                $"neutralize={GracefulExitNeutralizeSeconds:0.00}s restore={GracefulRestoreSeconds:0.00}s.");
        }

        private void TriggerExitPose()
        {
            if (_exitTriggered)
                return;

            _exitTriggered = true;
            _exitTriggeredAt = Time.unscaledTime;
            FireTrigger(JoystickReleaseHash);
            FireTrigger(ExitHash);
            _runtimePose?.BeginEaseOut();

            if (_isLocal && _controllerApplied)
            {
                if (_windDownTargetHoldCaptured)
                {
                    _handDriveExitActive = true;
                    _handDriveExitFromPosition = _windDownLeftTargetHoldPosition;
                    _handDriveExitFromRotation = _windDownLeftTargetHoldRotation;
                }
                _handDriveEnterActive = false;
            }
        }

        private void CaptureWindDownHandTargetHoldPose()
        {
            _windDownTargetHoldCaptured = false;
            if (!_isLocal || !_controllerApplied)
                return;

            Transform leftTarget = ResolveFirstPersonLeftHandTarget();
            Transform rightTarget = ResolveFirstPersonRightHandTarget();
            if (leftTarget == null || rightTarget == null)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV][ExitArms] Could not capture both hand targets at wind-down start; " +
                    $"left={leftTarget != null} right={rightTarget != null}.");
                return;
            }

            _windDownLeftTargetHoldPosition = leftTarget.position;
            _windDownLeftTargetHoldRotation = leftTarget.rotation;
            _windDownRightTargetHoldPosition = rightTarget.position;
            _windDownRightTargetHoldRotation = rightTarget.rotation;
            _windDownTargetHoldCaptured = true;
        }

        private void HoldWindDownHandTargets()
        {
            if (!_windDownTargetHoldCaptured || !_isLocal || !_controllerApplied)
                return;

            Transform leftTarget = ResolveFirstPersonLeftHandTarget();
            Transform rightTarget = ResolveFirstPersonRightHandTarget();
            if (leftTarget == null || rightTarget == null)
                return;

            try
            {
                leftTarget.SetPositionAndRotation(
                    _windDownLeftTargetHoldPosition,
                    _windDownLeftTargetHoldRotation);
                rightTarget.SetPositionAndRotation(
                    _windDownRightTargetHoldPosition,
                    _windDownRightTargetHoldRotation);
                _handTraceDriveApplicationCount++;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Wind-down hand-target hold failed: {ex.Message}");
            }
        }

        private void HoldWindDownLeftHandTargetDuringRightHandPress()
        {
            if (!_windDownTargetHoldCaptured || !_isLocal || !_controllerApplied)
                return;

            Transform leftTarget = ResolveFirstPersonLeftHandTarget();
            if (leftTarget == null)
                return;

            try
            {
                leftTarget.SetPositionAndRotation(
                    _windDownLeftTargetHoldPosition,
                    _windDownLeftTargetHoldRotation);
                _handTraceDriveApplicationCount++;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug(
                    $"[LethalCCTV] Wind-down left hand-target hold failed during right-hand press: {ex.Message}");
            }
        }

        internal void EndImmediate(string reason)
        {
            if (_interactionsApiMode)
            {
                CompleteInteractionsApiSession(
                    reason,
                    requestStop: true,
                    stopReasonName: string.Equals(reason, "shutdown", StringComparison.Ordinal)
                        ? "Shutdown"
                        : "Interrupted");
                return;
            }

            if (!_active && !_windingDown && _animator == null)
            {
                RestoreRightArmPresentationScale("abort");
                return;
            }

            Animator animator = _animator;
            bool controllerWasApplied = _controllerApplied;
            bool restoreAttempted = animator != null && controllerWasApplied;
            bool restoreSucceeded = !restoreAttempted;
            bool finalSessionEnd = IsFinalSessionEndReason(reason);
            bool immediateControllerRestore = finalSessionEnd &&
                !string.Equals(reason, "wind-down-complete", StringComparison.Ordinal);
            string rightArmEndReason = string.Equals(
                reason,
                "wind-down-complete",
                StringComparison.Ordinal)
                    ? "restore"
                    : "abort";
            if (_runtimePose != null)
            {
                _runtimePose.EndSession(reason);
                _runtimePose = null;
            }

            if (_isLocal && immediateControllerRestore)
                MonitorFocus.RestoreStationPoseForImmediateControllerRestore(Player, reason);

            if (animator != null && _controllerApplied)
            {
                try
                {
                    animator.runtimeAnimatorController = _savedController;
                    RestoreAnimatorState(animator);
                    RestoreFirstPersonControllerExitVisualState(finalSessionEnd, rightArmEndReason);
                    if (_isLocal && string.Equals(reason, "wind-down-complete", StringComparison.Ordinal))
                    {
                        ScheduleDeferredVanillaRigRebuild(
                            animator,
                            Player,
                            ResolveFirstPersonRightHandRigBuilders());
                    }
                    else
                    {
                        RebuildArmsRigAfterControllerSwap("vanilla-controller-restored");
                        EvaluateAnimatorAfterRigRebuild(animator, "vanilla-controller-restored");
                    }
                    restoreSucceeded = true;
                }
                catch (Exception ex)
                {
                    restoreSucceeded = false;
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Operator anim session restore failed: {ex.Message}");
                    RestoreFirstPersonControllerExitVisualState(finalSessionEnd, rightArmEndReason);
                }
            }

            if (controllerWasApplied && !restoreAttempted && finalSessionEnd)
                RestoreFirstPersonControllerExitVisualState(finalSessionEnd, rightArmEndReason);

            // Belt-and-suspenders parity for every abort/restart path, including
            // failures before or during the controller restore block.
            RestoreRightArmPresentationScale(rightArmEndReason);

            if (_isLocal && immediateControllerRestore)
            {
                MonitorFocus.ReassertCameraStabilizerAfterImmediateControllerRestore(Player, reason);
                MonitorFocus.EnforceExitCameraLocalYawInvariant(
                    Player,
                    "immediate-controller-restored-" + reason,
                    reassertNextFrame: true);
            }

            if (controllerWasApplied && finalSessionEnd)
                StartPostExitArmsTelemetry();

            string currentControllerName = animator != null && animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.name
                : "<null>";

            Player = null;
            _animator = null;
            _savedController = null;
            _savedControllerName = null;
            _controllerApplied = false;
            _savedParameters = Array.Empty<SavedAnimatorParameter>();
            _savedStates = Array.Empty<SavedAnimatorState>();
            _parameterTypes.Clear();
            _active = false;
            _windingDown = false;
            _cameraControlActive = false;
            _radarLookActive = false;
            _handDriveEnterActive = false;
            _handDriveEnterStartedAt = 0f;
            _handDriveContactFired = false;
            _handDriveEnterLogged = false;
            _handDriveLeverGripCaptureAttempted = false;
            _handDriveLeverGripCaptured = false;
            _handDriveMovingLever = null;
            _handDriveLeverGripLocalPosition = Vector3.zero;
            _handDriveLeverGripLocalRotation = Quaternion.identity;
            _handDriveLeverNeutralGripFrame = null;
            _handDriveLeverNeutralGripFramePosition = Vector3.zero;
            _handDriveLeverNeutralGripFrameRotation = Quaternion.identity;
            _handDriveLeverHandoffPosition = Vector3.zero;
            _handDriveLeverHandoffRotation = Quaternion.identity;
            _handDriveLeverHandoffPoseCaptured = false;
            _handDriveLeverFirstFollowLogged = false;
            _leverLeftShoulderBlend = 0f;
            _leverLeftShoulderFullOffsetLogged = false;
            _handDriveExitActive = false;
            _windDownTargetHoldCaptured = false;
            _windDownLeftTargetHoldPosition = Vector3.zero;
            _windDownLeftTargetHoldRotation = Quaternion.identity;
            _windDownRightTargetHoldPosition = Vector3.zero;
            _windDownRightTargetHoldRotation = Quaternion.identity;
            _handDriveStartLeftPosition = Vector3.zero;
            _handDriveStartLeftRotation = Quaternion.identity;
            _handDriveStartRightPosition = Vector3.zero;
            _handDriveStartRightRotation = Quaternion.identity;
            _handRestFirstPinLogged = false;
            _joystickSmoothed = Vector2.zero;
            _joystickPhase.Reset();
            _restoreAt = 0f;
            _exitReleaseAt = 0f;
            _buttonPressLayerUntil = 0f;
            _buttonPressActionId = 0;
            _rightHandEditPreviewActionId = -1;
            _firstPersonRightHandTarget = null;
            _firstPersonLeftHandTarget = null;
            _firstPersonRightHandTuningApplication = default;
            _firstPersonLeftHandTuningApplication = default;
            _firstPersonRightHandTargetMissingLogged = false;
            _firstPersonLeftHandTargetMissingLogged = false;
            _firstPersonRightHandRigBuilders = Array.Empty<Component>();
            _firstPersonRightHandRigBuildersResolved = false;
            _firstPersonRightHandRigBuilderMissingLogged = false;
            _scopedFirstPersonPoseSnapshot = null;
            _manualLeftArmIk = null;
            _manualRightArmIk = null;
            _firstPersonArmsRoot = null;
            _savedFirstPersonArmsRootLocalPosition = Vector3.zero;
            _savedFirstPersonArmsRootLocalRotation = Quaternion.identity;
            _firstPersonArmsRootPoseCaptured = false;
            _firstPersonArmsRenderer = null;
            _firstPersonArmsHiddenForMissingAnchor = false;
            _firstPersonArmsEnabledBeforeMissingAnchor = false;
            _rightArmPresentationShoulder = null;
            _rightArmPresentationCachedLocalScale = Vector3.one;
            _rightArmPresentationScaleCaptured = false;
            _rightArmPresentationHidden = false;
            _shoulderAnchorAppliedFrame = -1;
            _exitArmsTelemetryEnabled = false;
            _exitArmsLastLoggedFrame = -1;
            ResetHandTrace();
            _operatorFullBodyLayer = -1;
            _firstPersonArmsLayer = -1;
            _buttonPressLayer = -1;
            _glanceLayer = -1;

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Operator anim session ended: {reason}; " +
                $"restoreAttempted={restoreAttempted}, restoreSucceeded={restoreSucceeded}, " +
                $"currentController='{currentControllerName}'.");
        }

        private void CompleteInteractionsApiSession(
            string reason,
            bool requestStop,
            string stopReasonName)
        {
            object handle = _interactionsApiHandle;
            PlayerControllerB sessionPlayer = Player;
            string rightArmRestoreReason = !requestStop && _windingDown
                ? "session-teardown"
                : "abort";
            // #576: the API's restore phase runs inside TryStop and ends with a
            // RigBuilder.Build(), which permanently re-bakes the IK chain link
            // lengths from the pose it sees. Restore shoulder.R's real scale
            // BEFORE that call so the bake is non-degenerate; the post-TryStop
            // restore below stays as an idempotent re-assert, guarding the case
            // where an older Interactions DLL captured its scoped snapshot under
            // the collapse and replays scale≈0 during its own restore.
            RestoreRightArmPresentationScale(rightArmRestoreReason);
            bool stopSucceeded = !requestStop ||
                CCTVOperatorInteractionsBridge.TryStop(handle, stopReasonName);

            RestoreRightArmPresentationScale(rightArmRestoreReason);
            DestroyApiShoulderCapPlugs();
            RestoreInteractionsApiPoseChain(reason);
            ReleaseInteractionsApiArmsRootStationPin(reason);
            ReleaseInteractionsApiManualArmIkChains();
            _rightArmPresentationShoulder = null;
            _rightArmPresentationCachedLocalScale = Vector3.one;
            _rightArmPresentationScaleCaptured = false;
            _rightArmPresentationHidden = false;
            _apiPoseChainSnapshots = Array.Empty<ApiPoseTransformSnapshot>();

            Player = null;
            _interactionsApiMode = false;
            _interactionsApiUsesDedicatedViewmodel = false;
            _interactionsApiHandle = null;
            _interactionsApiStartedAt = 0f;
            _interactionsApiExitStartedAt = 0f;
            _interactionsApiFeedFlipFired = false;
            _active = false;
            _windingDown = false;
            _cameraControlActive = false;
            _radarLookActive = false;
            _joystickSmoothed = Vector2.zero;
            _joystickPhase.Reset();
            RestoreHeadPresentationScale(rightArmRestoreReason);
            RestorePendingHeadPresentationForPlayer(
                sessionPlayer,
                rightArmRestoreReason);
            ClearHeadPresentationState();

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][ApiPort] restored " +
                $"handle={CCTVOperatorInteractionsBridge.FormatHandle(handle)} " +
                $"reason='{reason}' requestStop={requestStop} stopSucceeded={stopSucceeded}.");
            if (_isLocal)
            {
                MonitorFocus.ReleaseLocalPlayerObstructorsAfterInteractionsApiRestore(
                    sessionPlayer,
                    reason);
                // The API's controller restore re-applies the preserved view
                // rotation as gameplay-camera LOCAL yaw, landing after
                // MonitorFocus's own exit invariant already ran (round-6
                // Test 3 log: residue 1.8/4.8/-39.6 deg per session, walk
                // direction diverges from view). Re-enforce here, with the
                // same final-reason gate as the legacy EndImmediate path:
                // "replaced" runs INSIDE the next EnterFocus (folding yaw
                // mid-intro), "shutdown" has no tick left to drain the
                // reassert, and a dead player's camera belongs to the
                // death/spectate transition.
                if (IsFinalSessionEndReason(reason) &&
                    !string.Equals(reason, "shutdown", StringComparison.Ordinal) &&
                    sessionPlayer != null &&
                    !sessionPlayer.isPlayerDead &&
                    sessionPlayer.isPlayerControlled)
                {
                    MonitorFocus.EnforceExitCameraLocalYawInvariant(
                        sessionPlayer,
                        "interactions-api-restored-" + reason,
                        reassertNextFrame: true);
                }
            }
        }

        private void ReleaseInteractionsApiManualArmIkChains()
        {
            _manualLeftArmIk = null;
            _manualRightArmIk = null;
            _firstPersonLeftHandTarget = null;
            _firstPersonRightHandTarget = null;
            _animator = null;
            _apiManualArmIkChainsUnavailableLogged = false;
            _apiManualArmIkFirstSolvedLogged = false;
            _apiManualArmIkNextCaptureAttemptAt = 0f;
        }

        private static bool IsFinalSessionEndReason(string reason)
        {
            return !string.Equals(reason, "replaced", StringComparison.Ordinal) &&
                   (string.IsNullOrEmpty(reason) ||
                   !reason.EndsWith("-restart", StringComparison.Ordinal));
        }

        private void RestoreFirstPersonControllerExitVisualState(
            bool finalSessionEnd,
            string rightArmRestoreReason)
        {
            RestoreScopedFirstPersonPose();
            RestoreFirstPersonArmsRootPose();
            RestoreRightArmPresentationScale(rightArmRestoreReason);
            RestoreFirstPersonArmsRendererAfterMissingAnchor();
            if (_isLocal && finalSessionEnd)
                MonitorFocus.RestoreLocalPlayerVisualStateAtControllerRestore(Player);
        }

        private void CacheCctvLayerIndices()
        {
            _operatorFullBodyLayer = FindLayerIndex("CCTVOperatorFullBody");
            _firstPersonArmsLayer = FindLayerIndex("CCTVFirstPersonArms");
            // The layer name is kept for controller compatibility; the generated
            // clip on it now contains the right-hand button press curves.
            _buttonPressLayer = FindLayerIndex("CCTVLeftHand");
            _glanceLayer = FindLayerIndex("CCTVGlance");
        }

        private void StartDirectOperatorIdlePose()
        {
            if (_animator == null || !_controllerApplied)
                return;

            PlayLayerState(_operatorFullBodyLayer, OperatorIdleStateHash, OperatorIdleShortStateHash);
            PlayLayerState(_firstPersonArmsLayer, RealArmsIdleStateHash, RealArmsIdleShortStateHash);
            try { _animator.Update(0f); } catch { }
        }

        private void PlayLayerState(int layerIndex, int stateHash, int fallbackStateHash)
        {
            if (_animator == null || layerIndex < 0 || layerIndex >= _animator.layerCount)
                return;

            try
            {
                int playableHash = _animator.HasState(layerIndex, stateHash)
                    ? stateHash
                    : (_animator.HasState(layerIndex, fallbackStateHash) ? fallbackStateHash : 0);
                if (playableHash == 0)
                    return;
                _animator.Play(playableHash, layerIndex, 0f);
            }
            catch { }
        }

        private int FindLayerIndex(string layerName)
        {
            if (_animator == null || string.IsNullOrEmpty(layerName))
                return -1;

            try
            {
                for (int i = 0; i < _animator.layerCount; i++)
                {
                    if (string.Equals(_animator.GetLayerName(i), layerName, StringComparison.Ordinal))
                        return i;
                }
            }
            catch { }

            return -1;
        }

        private void ApplyOperatorAnimatorOverrides()
        {
            if (_animator == null || !_controllerApplied)
                return;

            // PlayerControllerB keeps writing locomotion params from input every
            // frame. CCTV operation is stationary, so pin these after vanilla
            // Update to keep the copied base layer from stepping/bobbing.
            SetBool(WalkingHash, false);
            SetBool(SprintingHash, false);
            SetBool(SidewaysHash, false);
            SetBool(CrouchingHash, false);
            SetBool(JumpingHash, false);
            SetBool(FallNoJumpHash, false);
            SetBool(ClimbingLadderHash, false);
            SetFloat(AnimationSpeedHash, 0f);

            SetLayerWeight(_operatorFullBodyLayer, 1f);
            SetLayerWeight(_firstPersonArmsLayer, ShouldShowFirstPersonArmsLayer() ? 1f : 0f);
            SetLayerWeight(_buttonPressLayer, Time.unscaledTime <= _buttonPressLayerUntil ? 1f : 0f);
            SetLayerWeight(_glanceLayer, _radarLookActive ? 1f : 0f);
        }

    }
}
