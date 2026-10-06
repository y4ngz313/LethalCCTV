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
        internal bool Begin(RuntimeAnimatorController controller, bool enterFlourish = false)
        {
            if (Player == null)
                return false;

            string apiFallbackReason;
            if (CCTVOperatorInteractionsBridge.ConfigEnabled && enterFlourish)
            {
                bool useViewmodel = _isLocal &&
                    !Y4NGZPlayerAnimationBridge.UseLiveArmsForLocalOperator;
                // #576: the API's TryStart takes its scoped first-person
                // snapshot (local pos/rot/SCALE of the playerModelArmsMetarig
                // subtree) and runs RigBuilder.Build(), which permanently bakes
                // IK chain link lengths from the current pose. The right-arm
                // presentation hide has already collapsed shoulder.R to ~0 by
                // this point (MonitorFocus.EntryPoseSettle ->
                // CaptureInteractionBeginPresentation), so lift it across the
                // call and re-hide immediately; the round trip is same-frame and
                // therefore visually invisible.
                Transform apiStartLiftedShoulder =
                    LiftRightArmPresentationHideForApiBoundary("api-start-window-lift");
                bool apiStarted;
                object interactionsHandle;
                try
                {
                    apiStarted = CCTVOperatorInteractionsBridge.TryStart(
                        Player,
                        useViewmodel,
                        out interactionsHandle,
                        out apiFallbackReason);
                }
                finally
                {
                    ReapplyRightArmPresentationHideForApiBoundary(
                        apiStartLiftedShoulder,
                        "api-start-window-rehide");
                }

                if (apiStarted)
                {
                    return BeginInteractionsApiSession(
                        interactionsHandle,
                        useDedicatedLocalViewmodel: useViewmodel);
                }
            }
            else
            {
                apiFallbackReason = CCTVOperatorInteractionsBridge.ConfigEnabled
                    ? "authored_enter_not_requested"
                    : "kill_switch_disabled";
            }

            RestorePendingHeadPresentationForApiFallback(Player);
            // #593: must precede CaptureManualArmIkChainsBeforeControllerSwap
            // below - that capture reads bone lengths, and the
            // interaction-begin hide has shoulder.R collapsed to ~0 until this
            // runs. The legacy enter hide is re-applied a few lines later by
            // ApplyRightArmPresentationHideImmediately.
            RestorePendingRightArmPresentationForApiFallback(Player);

            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV][ApiPort] fallback_legacy scope={(_isLocal ? "local" : "remote")} " +
                $"player={FormatPlayerForLog(Player)} reason='{apiFallbackReason}'.");

            _animator = Player.playerBodyAnimator;
            if (_animator == null)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Operator anim session skipped; playerBodyAnimator is missing.");
                return false;
            }

            if (_isLocal)
                _postExitArmsTelemetry = null;
            CaptureFirstPersonArmsRootPose();
            LogFirstPersonLeftArmChainPose("pre-swap-vanilla");
            CaptureScopedFirstPersonPose();
            CaptureHandDriveStartPoseBeforeControllerSwap();
            LogChainIkConstraintBindings("pre-swap");
            CaptureManualArmIkChainsBeforeControllerSwap();
            CaptureRightArmPresentationScale();
            if (controller != null)
                ApplyRightArmPresentationHideImmediately();
            _savedController = _animator.runtimeAnimatorController;
            _savedControllerName = _savedController != null ? _savedController.name : "<null>";
            SaveAnimatorState(_animator);

            if (controller != null)
            {
                try
                {
                    _animator.runtimeAnimatorController = controller;
                    _controllerApplied = true;
                    RebuildArmsRigAfterControllerSwap("cctv-controller-applied");
                    // Ported from Y4NGZInteractions LiveBodyAnimatorPresenter: the
                    // rebuilt graph must see one animator evaluation before render.
                    EvaluateAnimatorAfterRigRebuild(_animator, "cctv-controller-applied");
                    LogChainIkConstraintBindings("post-build");
                }
                catch (Exception ex)
                {
                    _controllerApplied = false;
                    RestoreRightArmPresentationScale("controller-apply-failed");
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] Operator anim controller apply failed for player {FormatPlayerForLog(Player)}: {ex.Message}");
                }
            }

            CacheParameters(_animator);
            if (_controllerApplied)
            {
                Y4NGZPlayerAnimationBridge.LogAnimatorControllerDiagnosticsOnce(_animator, controller);
                if (!Y4NGZPlayerAnimationBridge.ValidateAnimatorControllerContract(
                        _animator,
                        controller,
                        _isLocal ? "local-session" : "remote-session"))
                {
                    try
                    {
                        _animator.runtimeAnimatorController = _savedController;
                        RestoreAnimatorState(_animator);
                        RebuildArmsRigAfterControllerSwap("rejected-controller-restored");
                        EvaluateAnimatorAfterRigRebuild(_animator, "rejected-controller-restored");
                    }
                    catch (Exception ex)
                    {
                        SurveillanceBootstrap.Log?.LogWarning(
                            $"[LethalCCTV] Operator anim rejected-controller restore failed for player {FormatPlayerForLog(Player)}: {ex.Message}");
                    }

                    _controllerApplied = false;
                    RestoreRightArmPresentationScale("controller-rejected");
                    CacheParameters(_animator);
                }
            }

            if (_isLocal && _controllerApplied)
                MonitorFocus.RetainLocalPlayerVisualStateForControllerRestore(Player);

            _active = true;
            _windingDown = false;
            _exitTriggered = false;
            _cameraControlActive = false;
            _radarLookActive = false;
            _joystickSmoothed = Vector2.zero;
            _joystickPhase.Reset();
            _lastJoystickMoveAt = Time.unscaledTime;
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
            _windDownTargetHoldCaptured = false;
            _windDownLeftTargetHoldPosition = Vector3.zero;
            _windDownLeftTargetHoldRotation = Quaternion.identity;
            _windDownRightTargetHoldPosition = Vector3.zero;
            _windDownRightTargetHoldRotation = Quaternion.identity;
            _handRestFirstPinLogged = false;
            _leverLeftShoulderBlend = 0f;
            _leverLeftShoulderFullOffsetLogged = false;
            ResetHandTrace();
            CacheCctvLayerIndices();

            _runtimePose = CCTVOperatorRuntimePose.Attach(Player, _animator);
            if (_runtimePose != null)
                _runtimePose.BeginSession(Player, _animator, useIkFallback: !_controllerApplied);

            SetBool(SeatedHash, true);
            SetBool(CameraControlHash, false);
            SetBool(RadarLookHash, false);
            SetInt(ActiveSlotHash, MonitorFocus.ActiveSlot);
            SetInt(ActionButtonHash, 0);
            InitializeOperatorPose();
            ApplyOperatorAnimatorOverrides();
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Operator anim session began: scope={(_isLocal ? "local" : "remote")}, " +
                $"player={FormatPlayerForLog(Player)}, controllerApplied={_controllerApplied}, " +
                $"requestedController='{(controller != null ? controller.name : "<none>")}', " +
                $"savedController='{_savedControllerName}', layerCount={_animator.layerCount}, " +
                $"cctvLayers=full:{_operatorFullBodyLayer} fpArms:{_firstPersonArmsLayer} " +
                $"button:{_buttonPressLayer} glance:{_glanceLayer}.");
            return true;
        }

        private bool BeginInteractionsApiSession(
            object handle,
            bool useDedicatedLocalViewmodel)
        {
            _interactionsApiMode = true;
            _interactionsApiUsesDedicatedViewmodel = useDedicatedLocalViewmodel;
            _interactionsApiHandle = handle;
            _interactionsApiStartedAt = Time.unscaledTime;
            _interactionsApiExitStartedAt = 0f;
            _interactionsApiFeedFlipFired = false;
            _active = true;
            _windingDown = false;
            _exitTriggered = false;
            _cameraControlActive = false;
            _radarLookActive = false;
            _joystickSmoothed = Vector2.zero;
            _joystickPhase.Reset();
            _lastJoystickMoveAt = Time.unscaledTime;
            _animator = useDedicatedLocalViewmodel || Player == null
                ? null
                : Player.playerBodyAnimator;
            _apiRightLastValid = false;
            _apiLastPressWindow = -1f;
            _apiEnterEvaluatedClipSeconds = 0f;
            _apiEnterPressRotationResolved = false;
            _apiEnterIndexTip = null;
            _apiEnterSpeedOwned = _animator != null;
            if (_apiEnterSpeedOwned)
            {
                _apiSavedAnimatorSpeed = _animator.speed;
                // Local entry is evaluated before the camera from its exact clock.
                // A fixed speed alone lets finger/body animation run ahead on hitches.
                _animator.speed = _isLocal ? 0f : _apiSavedAnimatorSpeed *
                    CCTVOperatorInteractionsBridge.EnterClipLengthSeconds / MonitorFocus.OperatorEnterDurationSeconds;
            }
            // The presenter has already swapped the CCTV controller onto
            // playerBodyAnimator (TryStart precedes this call), so caching here
            // picks up the controller's JoystickX/Y floats for SetFloat.
            CacheParameters(_animator);
            CacheCctvLayerIndices();
            _buttonPressLayerUntil = 0f;
            SetLayerWeight(_buttonPressLayer, 0f);
            _apiLeverControlReachAssistSmoothed = 0f;
            _apiLeverControlReachAssistLastDirection = Vector3.zero;
            _apiLeverControlReachAssistLogged = false;
            _apiPressContactSnapLogged = false;
            _apiPressContactSamplePending = false;
            _apiLeverContactLogged = false;
            _apiLeverPalmOffsetResolved = false;
            _apiLeverPalmOffsetSource = null;
            _apiLeverContactSamplePending = false;
            _apiLeverContactPostSolveLogged = false;
            _firstPersonLeftHandTarget = null;
            _firstPersonRightHandTarget = null;
            _firstPersonLeftHandTargetMissingLogged = false;
            _firstPersonRightHandTargetMissingLogged = false;
            _apiManualArmIkChainsUnavailableLogged = false;
            _apiManualArmIkFirstSolvedLogged = false;
            _apiManualArmIkNextCaptureAttemptAt = 0f;
            if (!useDedicatedLocalViewmodel)
            {
                CaptureFirstPersonArmsRootPose();
                CaptureManualArmIkChainsBeforeControllerSwap();
                CaptureRightArmPresentationScale();
                CaptureHeadPresentationScale();
                // Chain capture MUST precede the presentation hides: the
                // right-arm hide collapses shoulder.R localScale to ~0, which
                // made this recapture reject the right chain and killed the
                // anchor pin + manual IK for the whole enter (Test 32
                // floating-arms regression, 2026-07-22).
                if (Y4NGZPlayerAnimationBridge.UseManualFirstPersonArmIk)
                    EnsureInteractionsApiManualArmIkChains("session-begin");
                ApplyRightArmPresentationHideImmediately("session-begin");
                ApplyHeadPresentationHideImmediately("session-begin");
                CaptureInteractionsApiPoseChain();
                SyncApiShoulderCapPlugs();
            }

            InitializeOperatorPose();
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][ApiPort] session_started scope={(_isLocal ? "local" : "remote")} " +
                $"presentation={(useDedicatedLocalViewmodel ? "dedicated-viewmodel" : "world-body")} " +
                $"player={FormatPlayerForLog(Player)} " +
                $"handle={CCTVOperatorInteractionsBridge.FormatHandle(handle)} " +
                $"clipLength={CCTVOperatorInteractionsBridge.EnterClipLengthSeconds:0.000}s.");
            return true;
        }

        private void CaptureFirstPersonArmsRootPose()
        {
            _firstPersonArmsRoot = null;
            _firstPersonArmsRenderer = Player != null ? Player.thisPlayerModelArms : null;
            _firstPersonArmsRootPoseCaptured = false;
            _savedFirstPersonArmsRootLocalPosition = Vector3.zero;
            _savedFirstPersonArmsRootLocalRotation = Quaternion.identity;
            if (!_isLocal || _animator == null)
                return;

            try
            {
                Transform root = _animator.transform.Find("ScavengerModelArmsOnly");
                if (root == null)
                {
                    Transform current = _firstPersonArmsRenderer != null
                        ? _firstPersonArmsRenderer.transform
                        : null;
                    while (current != null)
                    {
                        if (string.Equals(current.name, "ScavengerModelArmsOnly", StringComparison.OrdinalIgnoreCase))
                        {
                            root = current;
                            break;
                        }
                        current = current.parent;
                    }
                }

                if (root == null)
                    throw new InvalidOperationException("ScavengerModelArmsOnly is missing");

                _firstPersonArmsRoot = root;
                if (!MonitorFocus.TryGetFirstPersonArmsFocusStartRootPose(
                        root,
                        out _savedFirstPersonArmsRootLocalPosition,
                        out _savedFirstPersonArmsRootLocalRotation))
                {
                    _savedFirstPersonArmsRootLocalPosition = root.localPosition;
                    _savedFirstPersonArmsRootLocalRotation = root.localRotation;
                }
                _firstPersonArmsRootPoseCaptured = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][ExitVisualRestore] Captured pre-focus arms-root pose: " +
                    $"localPosition={FormatTraceVector(_savedFirstPersonArmsRootLocalPosition)} " +
                    $"localEulerAngles={FormatTraceVector(_savedFirstPersonArmsRootLocalRotation.eulerAngles)}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ExitVisualRestore] Arms-root pose capture failed: {ex.Message}");
            }
        }

        private void RestoreFirstPersonArmsRootPose()
        {
            if (!_firstPersonArmsRootPoseCaptured || _firstPersonArmsRoot == null)
                return;

            try
            {
                _firstPersonArmsRoot.localPosition = _savedFirstPersonArmsRootLocalPosition;
                _firstPersonArmsRoot.localRotation = _savedFirstPersonArmsRootLocalRotation;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][ExitVisualRestore] Restored pre-focus arms-root pose before vanilla rig rebuild: " +
                    $"localPosition={FormatTraceVector(_firstPersonArmsRoot.localPosition)} " +
                    $"localEulerAngles={FormatTraceVector(_firstPersonArmsRoot.localEulerAngles)}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ExitVisualRestore] Arms-root pose restore failed: {ex.Message}");
            }
        }

        private void CaptureScopedFirstPersonPose()
        {
            _scopedFirstPersonPoseSnapshot = null;
            if (!_isLocal || Player == null || Player.playerModelArmsMetarig == null)
                return;

            try
            {
                // Pattern ported from Y4NGZInteractions LiveBodyAnimatorPresenter:
                // controller swaps may rewrite any descendant in the FP metarig.
                _scopedFirstPersonPoseSnapshot =
                    TransformPoseSnapshot.CaptureDescendants(Player.playerModelArmsMetarig);
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] Scoped first-person pose captured before controller swap: " +
                    $"transforms={_scopedFirstPersonPoseSnapshot.Count}.");
            }
            catch (Exception ex)
            {
                _scopedFirstPersonPoseSnapshot = null;
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Scoped first-person pose capture failed: {ex.Message}");
            }
        }

        private void RestoreScopedFirstPersonPose()
        {
            if (_scopedFirstPersonPoseSnapshot == null)
                return;

            try
            {
                int restored = _scopedFirstPersonPoseSnapshot.Restore();
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] Scoped first-person pose restored before vanilla rig rebuild: " +
                    $"restored={restored}/{_scopedFirstPersonPoseSnapshot.Count}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Scoped first-person pose restore failed: {ex.Message}");
            }
        }

        private static void EvaluateAnimatorAfterRigRebuild(Animator animator, string reason)
        {
            if (animator == null)
                return;

            try
            {
                animator.Update(0f);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Animator zero-delta evaluation failed after rig rebuild ({reason}): {ex.Message}");
            }
        }

        private static void ScheduleDeferredVanillaRigRebuild(
            Animator animator,
            PlayerControllerB player,
            Component[] rigBuilders)
        {
            if (_deferredVanillaRigRebuildAnimator != null)
            {
                TickDeferredVanillaRigRebuild(
                    force: true,
                    forcedReason: "superseded-exit-rebuild");
            }

            _deferredVanillaRigRebuildAnimator = animator;
            _deferredVanillaRigRebuildPlayer = player;
            _deferredVanillaRigRebuildBuilders = rigBuilders ?? Array.Empty<Component>();
            _deferredVanillaRigRebuildQueuedFrame = Time.frameCount;
            _deferredVanillaRigRebuildEarliestFrame =
                Time.frameCount + DeferredVanillaRigRebuildFrames;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Vanilla rig rebuild deferred until the exit posture settles: " +
                $"earliestFrame={_deferredVanillaRigRebuildEarliestFrame} " +
                $"builders={_deferredVanillaRigRebuildBuilders.Length}.");
        }

        internal static void TickDeferredVanillaRigRebuild(
            bool force = false,
            string forcedReason = null)
        {
            Animator animator = _deferredVanillaRigRebuildAnimator;
            if (animator == null)
            {
                ClearDeferredVanillaRigRebuild();
                return;
            }

            if (!force && Time.frameCount < _deferredVanillaRigRebuildEarliestFrame)
                return;

            PlayerControllerB player = _deferredVanillaRigRebuildPlayer;
            bool specialAnimationActive = false;
            bool cameraStabilizerActive = false;
            try
            {
                specialAnimationActive = player != null &&
                    (player.inSpecialInteractAnimation || player.enteringSpecialAnimation);
                Camera camera = player != null ? player.gameplayCamera : null;
                CCTVLocalCameraPositionStabilizer stabilizer = camera != null
                    ? camera.GetComponent<CCTVLocalCameraPositionStabilizer>()
                    : null;
                cameraStabilizerActive = stabilizer != null && stabilizer.enabled;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Deferred vanilla rig readiness check failed: {ex.Message}");
            }

            if (!force && (specialAnimationActive || cameraStabilizerActive))
                return;

            Component[] rigBuilders = _deferredVanillaRigRebuildBuilders;
            int waitedFrames = _deferredVanillaRigRebuildQueuedFrame >= 0
                ? Time.frameCount - _deferredVanillaRigRebuildQueuedFrame
                : 0;
            string releaseReason = force
                ? (string.IsNullOrWhiteSpace(forcedReason) ? "forced" : forcedReason)
                : "exit-settled";
            ClearDeferredVanillaRigRebuild();

            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Running deferred vanilla rig rebuild: " +
                $"reason={releaseReason} waitedFrames={waitedFrames} " +
                $"inSpecialAnimation={specialAnimationActive} " +
                $"cameraStabilizerActive={cameraStabilizerActive}.");
            float yawBeforeBuild = ReadGameplayCameraLocalYaw(player);
            string poseBeforeBuild = DescribeDeferredExitRotationPose(player);
            RebuildArmsRigComponents(
                rigBuilders,
                "vanilla-controller-restored-deferred-" + releaseReason);
            float yawAfterBuild = ReadGameplayCameraLocalYaw(player);
            string poseAfterBuild = DescribeDeferredExitRotationPose(player);
            EvaluateAnimatorAfterRigRebuild(
                animator,
                "vanilla-controller-restored-deferred-" + releaseReason);
            float yawAfterAnimatorUpdate = ReadGameplayCameraLocalYaw(player);
            string poseAfterAnimatorUpdate = DescribeDeferredExitRotationPose(player);
            float buildYawDelta = Mathf.DeltaAngle(yawBeforeBuild, yawAfterBuild);
            float animatorYawDelta = Mathf.DeltaAngle(yawAfterBuild, yawAfterAnimatorUpdate);
            Action<string> yawLog = Mathf.Abs(buildYawDelta) > 0.05f ||
                                    Mathf.Abs(animatorYawDelta) > 0.05f
                ? message => SurveillanceBootstrap.Log?.LogWarning(message)
                : message => SurveillanceBootstrap.Log?.LogInfo(message);
            yawLog(
                "[LethalCCTV][ExitYawWriter] deferred rebuild attribution: " +
                $"Build.localYawDelta={buildYawDelta:F2} " +
                $"Animator.Update(0f).localYawDelta={animatorYawDelta:F2} | " +
                $"beforeBuild={poseBeforeBuild} | afterBuild={poseAfterBuild} | " +
                $"afterAnimatorUpdate={poseAfterAnimatorUpdate}.");
            MonitorFocus.EnforceExitCameraLocalYawInvariant(
                player,
                "deferred-rig-rebuild-" + releaseReason,
                reassertNextFrame: true);
        }

        private static float ReadGameplayCameraLocalYaw(PlayerControllerB player)
        {
            return player != null && player.gameplayCamera != null
                ? player.gameplayCamera.transform.localEulerAngles.y
                : 0f;
        }

        private static string DescribeDeferredExitRotationPose(PlayerControllerB player)
        {
            if (player == null)
                return "<player-null>";

            Transform camera = player.gameplayCamera != null
                ? player.gameplayCamera.transform
                : null;
            Transform parent = camera != null ? camera.parent : null;
            Transform armsMetarig = player.playerModelArmsMetarig;
            return
                $"cameraLocalEuler={(camera != null ? FormatVector(camera.localEulerAngles) : "<missing>")}" +
                $"/cameraWorldEuler={(camera != null ? FormatVector(camera.eulerAngles) : "<missing>")}" +
                $"/cameraParent={(parent != null ? GetTransformPath(parent) : "<missing>")}" +
                $"/parentLocalEuler={(parent != null ? FormatVector(parent.localEulerAngles) : "<missing>")}" +
                $"/bodyWorldEuler={FormatVector(player.transform.eulerAngles)}" +
                $"/armsLocalEuler={(armsMetarig != null ? FormatVector(armsMetarig.localEulerAngles) : "<missing>")}";
        }

        private static void ClearDeferredVanillaRigRebuild()
        {
            _deferredVanillaRigRebuildAnimator = null;
            _deferredVanillaRigRebuildPlayer = null;
            _deferredVanillaRigRebuildBuilders = Array.Empty<Component>();
            _deferredVanillaRigRebuildQueuedFrame = -1;
            _deferredVanillaRigRebuildEarliestFrame = -1;
        }

        private void CaptureHandDriveStartPoseBeforeControllerSwap()
        {
            if (!_isLocal || _animator == null)
                return;

            Transform left = FindChildRecursive(_animator.transform, "ArmsLeftArm_target");
            Transform right = FindChildRecursive(_animator.transform, "ArmsRightArm_target");
            if (left == null || right == null)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV][EnterAnim] Could not capture the vanilla lowered FP hand targets before the controller swap; " +
                    "telemetry is unavailable; designed hand rests remain authoritative.");
                return;
            }

            _handDriveStartLeftPosition = left.position;
            _handDriveStartLeftRotation = left.rotation;
            _handDriveStartRightPosition = right.position;
            _handDriveStartRightRotation = right.rotation;

            Camera camera = Player != null ? Player.gameplayCamera : null;
            Vector3 leftViewport = camera != null
                ? camera.WorldToViewportPoint(_handDriveStartLeftPosition)
                : Vector3.zero;
            Vector3 rightViewport = camera != null
                ? camera.WorldToViewportPoint(_handDriveStartRightPosition)
                : Vector3.zero;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][EnterAnim] captured vanilla lowered hands (telemetry-only parked targets): " +
                $"left={FormatVector(_handDriveStartLeftPosition)} leftEuler={FormatVector(_handDriveStartLeftRotation.eulerAngles)} " +
                $"leftViewport={FormatVector(leftViewport)} right={FormatVector(_handDriveStartRightPosition)} " +
                $"rightEuler={FormatVector(_handDriveStartRightRotation.eulerAngles)} rightViewport={FormatVector(rightViewport)}.");
        }

    }
}
