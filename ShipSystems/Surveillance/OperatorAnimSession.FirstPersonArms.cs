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
        internal void Tick()
        {
            if (_interactionsApiMode)
            {
                TickInteractionsApiSession();
                return;
            }

            if (_windingDown)
            {
                TickWindingDown();
                return;
            }

            if (!_active || _animator == null)
                return;

            if (Player == null || Player.isPlayerDead || !Player.isPlayerControlled)
            {
                EndImmediate("player-invalid");
                return;
            }

            if (!_cameraControlActive || Time.unscaledTime - _lastJoystickMoveAt > JoystickIdleAfterSeconds)
                _joystickPhase.SetRequested(Vector2.zero);

            TickJoystickPose(_cameraControlActive);
            TryBeginHandTraceMilestone();
        }

        private void TickInteractionsApiSession()
        {
            if (!_active && !_windingDown)
                return;

            if (Player == null || Player.isPlayerDead || !Player.isPlayerControlled)
            {
                EndImmediate("player-invalid");
                return;
            }

            if (!_interactionsApiUsesDedicatedViewmodel)
                SyncApiShoulderCapPlugs();

            if (_apiEnterSpeedOwned && (_windingDown || (!_isLocal &&
                Time.unscaledTime - _interactionsApiStartedAt >= MonitorFocus.OperatorEnterDurationSeconds)))
                RestoreApiEnterAnimatorSpeed();

            float clipTime = Mathf.Max(0f, MonitorFocus.EnterPresentationClockSeconds);
            if (_isLocal && !_interactionsApiFeedFlipFired &&
                clipTime >= CctvIntroTiming.PressContact)
            {
                _interactionsApiFeedFlipFired = true;
                bool pendingBefore = MonitorFocus.IsStationFeedFlipPending;
                MonitorFocus.CompleteStationFeedFlipAtPressContact();
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ApiPort] feed_flip_fired " +
                    $"handle={CCTVOperatorInteractionsBridge.FormatHandle(_interactionsApiHandle)} " +
                    $"clipTime={clipTime:0.000}s pendingBefore={pendingBefore}.");
            }

            if (!CCTVOperatorInteractionsBridge.IsActive(_interactionsApiHandle))
            {
                CompleteInteractionsApiSession(
                    _windingDown ? "graceful-exit-complete" : "api-session-inactive",
                    requestStop: false,
                    stopReasonName: null);
                return;
            }

            if (!_cameraControlActive || Time.unscaledTime - _lastJoystickMoveAt > JoystickIdleAfterSeconds)
                _joystickPhase.SetRequested(Vector2.zero);
            _joystickSmoothed = _joystickPhase.Tick(Mathf.Max(Time.unscaledDeltaTime, 1f / 240f));
            // Round 1 left JoystickX/Y at neutral because the installed API had
            // no float passthrough. The world-body path drives the CCTV
            // controller on playerBodyAnimator directly, so no passthrough is
            // needed — write the floats like the legacy path did and let the
            // controller's joystick blend move the left target (the round-7
            // reconstruction preserves animator-driven target motion).
            if (!_interactionsApiUsesDedicatedViewmodel)
            {
                // The API manifest weights full-body/first-person layers only.
                // CCTVLeftHand (the authored RIGHT-button layer) defaults to zero.
                SetLayerWeight(_buttonPressLayer, !_windingDown && IsRightHandActionPressWindowActive() ? 1f : 0f);
                SetFloat(JoystickXHash, _joystickSmoothed.x);
                SetFloat(JoystickYHash, _joystickSmoothed.y);
            }
        }

        // Presentation stash for the current frame: MonitorFocus glues the
        // arms root to the camera, but RigBuilder.Evaluate resets the root to
        // the animator pose, so the glued pose must be re-asserted after the
        // rig evaluates. Hand targets are written in raw world space: they
        // are children of the glued root, so the Evaluate root-reset already
        // carries their stored locals into animator space for the solve, and
        // the re-assert maps the solved arm back — an explicit animator-space
        // remap on top double-applies the glue delta (2026-07-15 trace: wrist
        // rendered one full delta above the button, arm swept over the view).
        private bool _presentationRemapValid;
        private Transform _presentationRoot;
        private Vector3 _presentationFinalPosition;
        private Quaternion _presentationFinalRotation = Quaternion.identity;

        private void RefreshFirstPersonPresentationRemap()
        {
            _presentationRemapValid = MonitorFocus.TryGetFirstPersonArmsPresentationThisFrame(
                out _presentationRoot,
                out _,
                out _,
                out _presentationFinalPosition,
                out _presentationFinalRotation);
        }

        private void ReassertFirstPersonArmsPresentation()
        {
            // Re-query MonitorFocus so a stale stash from an earlier frame
            // can never be re-asserted; valid only when the glue ran this
            // exact frame.
            RefreshFirstPersonPresentationRemap();
            if (!_presentationRemapValid || _presentationRoot == null)
                return;
            try
            {
                _presentationRoot.SetPositionAndRotation(
                    _presentationFinalPosition,
                    _presentationFinalRotation);
            }
            catch { }
        }

        private void ApplyFirstPersonShoulderAnchor()
        {
            if (!Y4NGZPlayerAnimationBridge.UseFirstPersonShoulderAnchor ||
                !_presentationRemapValid || _presentationRoot == null ||
                _manualLeftArmIk?.Root == null || _manualRightArmIk?.Root == null ||
                Player?.gameplayCamera == null)
            {
                return;
            }

            bool traceThisFrame = _handTraceFrame == Time.frameCount;
            try
            {
                Transform cameraTransform = Player.gameplayCamera.transform;
                Vector3 flattenedForward = Vector3.ProjectOnPlane(
                    cameraTransform.forward,
                    Vector3.up);
                if (flattenedForward.sqrMagnitude < 0.0001f)
                {
                    flattenedForward = Vector3.ProjectOnPlane(
                        cameraTransform.up,
                        Vector3.up);
                }
                if (flattenedForward.sqrMagnitude < 0.0001f)
                    flattenedForward = Player.transform.forward;

                Quaternion yawFlattenedCameraRotation = Quaternion.LookRotation(
                    flattenedForward.normalized,
                    Vector3.up);
                bool ignoreCameraPitch =
                    Y4NGZPlayerAnimationBridge.ShoulderAnchorIgnoresCameraPitch;
                Quaternion anchorFrameRotation = ignoreCameraPitch
                    ? yawFlattenedCameraRotation
                    : cameraTransform.rotation;
                Quaternion presentationRotationInYawFrame =
                    Quaternion.Inverse(yawFlattenedCameraRotation) * _presentationFinalRotation;
                _presentationRoot.rotation =
                    anchorFrameRotation * presentationRotationInYawFrame;

                Vector3 desired = cameraTransform.position +
                    anchorFrameRotation *
                    Y4NGZPlayerAnimationBridge.FirstPersonShoulderAnchorCameraOffset;
                Vector3 measured =
                    (_manualLeftArmIk.Root.position + _manualRightArmIk.Root.position) * 0.5f;
                Vector3 delta = desired - measured;
                _presentationRoot.position += delta;
                _shoulderAnchorAppliedFrame = Time.frameCount;

                if (traceThisFrame)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][ShoulderAnchor] measured={FormatTraceVector(measured)} " +
                        $"desired={FormatTraceVector(desired)} delta={FormatTraceVector(delta)} " +
                        $"frame={(ignoreCameraPitch ? "yaw-flat" : "full-camera")}.");
                }
            }
            catch (Exception ex)
            {
                if (traceThisFrame)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][ShoulderAnchor] failed: {ex.Message}");
                }
            }
        }

        private void ApplyLeverPhaseLeftShoulderOffset()
        {
            if (!_isLocal || !_controllerApplied ||
                _shoulderAnchorAppliedFrame != Time.frameCount ||
                _manualLeftArmIk?.Root == null ||
                Player?.gameplayCamera == null)
            {
                return;
            }

            float blend = ResolveLeverPhaseLeftShoulderBlend();
            _leverLeftShoulderBlend = blend;
            if (blend <= 0f)
                return;

            Transform shoulder = _manualLeftArmIk.Root.parent;
            if (shoulder == null)
                return;

            try
            {
                Transform cameraTransform = Player.gameplayCamera.transform;
                Vector3 flattenedForward = Vector3.ProjectOnPlane(
                    cameraTransform.forward,
                    Vector3.up);
                if (flattenedForward.sqrMagnitude < 0.0001f)
                {
                    flattenedForward = Vector3.ProjectOnPlane(
                        cameraTransform.up,
                        Vector3.up);
                }
                if (flattenedForward.sqrMagnitude < 0.0001f)
                    flattenedForward = Player.transform.forward;
                if (flattenedForward.sqrMagnitude < 0.0001f)
                    flattenedForward = Vector3.forward;

                Quaternion yawFlattenedCameraRotation = Quaternion.LookRotation(
                    flattenedForward.normalized,
                    Vector3.up);
                Quaternion offsetFrameRotation =
                    Y4NGZPlayerAnimationBridge.ShoulderAnchorIgnoresCameraPitch
                        ? yawFlattenedCameraRotation
                        : cameraTransform.rotation;
                Vector3 configuredOffset =
                    Y4NGZPlayerAnimationBridge.FirstPersonLeverLeftShoulderOffset;
                Vector3 worldDelta = offsetFrameRotation * configuredOffset * blend;
                shoulder.position += worldDelta;

                if (!_leverLeftShoulderFullOffsetLogged && blend >= 0.999f)
                {
                    _leverLeftShoulderFullOffsetLogged = true;
                    Transform target = _manualLeftArmIk.Target;
                    float rootTargetDistance = target != null
                        ? Vector3.Distance(_manualLeftArmIk.Root.position, target.position)
                        : -1f;
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV][LeverShoulder] full offset applied: " +
                        $"configured={FormatTraceVector(configuredOffset)} " +
                        $"worldDelta={FormatTraceVector(worldDelta)} " +
                        $"shoulder.viewport={FormatTraceViewport(shoulder.position, Player.gameplayCamera)} " +
                        $"root.viewport={FormatTraceViewport(_manualLeftArmIk.Root.position, Player.gameplayCamera)} " +
                        $"rootTarget={rootTargetDistance:F4}m " +
                        $"reach={_manualLeftArmIk.UpperLength + _manualLeftArmIk.LowerLength:F4}m " +
                        $"blendWindow={HandEnterReleaseEndSeconds:0.00}-{HandEnterSweepEndSeconds:0.00}s " +
                        $"frame={(Y4NGZPlayerAnimationBridge.ShoulderAnchorIgnoresCameraPitch ? "yaw-flat" : "full-camera")}.");
                }
            }
            catch (Exception ex)
            {
                if (!_leverLeftShoulderFullOffsetLogged)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][LeverShoulder] offset failed: {ex.Message}");
                }
            }
        }

        private float ResolveLeverPhaseLeftShoulderBlend()
        {
            // Wind-down holds the last presentation value so the round-15 held
            // target and retract path cannot acquire a shoulder-position seam.
            if (_windingDown)
                return _leverLeftShoulderBlend;
            if (_cameraControlActive)
                return 1f;
            if (!_handDriveEnterActive)
                return _leverLeftShoulderBlend;

            float elapsed = Mathf.Max(0f, Time.unscaledTime - _handDriveEnterStartedAt);
            return Smooth01((elapsed - HandEnterReleaseEndSeconds) /
                (HandEnterSweepEndSeconds - HandEnterReleaseEndSeconds));
        }

        private void EnforceFirstPersonArmsAnchoredOrHidden()
        {
            if (!_isLocal || !_controllerApplied)
            {
                RestoreFirstPersonArmsRendererAfterMissingAnchor();
                return;
            }

            Renderer renderer = _firstPersonArmsRenderer != null
                ? _firstPersonArmsRenderer
                : (Player != null ? Player.thisPlayerModelArms : null);
            if (renderer == null)
                return;

            bool anchoredThisFrame =
                _presentationRemapValid &&
                _shoulderAnchorAppliedFrame == Time.frameCount;
            if (anchoredThisFrame)
            {
                RestoreFirstPersonArmsRendererAfterMissingAnchor();
                return;
            }

            try
            {
                if (!_firstPersonArmsHiddenForMissingAnchor)
                {
                    _firstPersonArmsEnabledBeforeMissingAnchor = renderer.enabled;
                    _firstPersonArmsHiddenForMissingAnchor = true;
                }
                renderer.enabled = false;
            }
            catch { }
        }

        private void RestoreFirstPersonArmsRendererAfterMissingAnchor()
        {
            if (!_firstPersonArmsHiddenForMissingAnchor)
                return;

            Renderer renderer = _firstPersonArmsRenderer != null
                ? _firstPersonArmsRenderer
                : (Player != null ? Player.thisPlayerModelArms : null);
            if (renderer != null)
            {
                try { renderer.enabled = _firstPersonArmsEnabledBeforeMissingAnchor; }
                catch { }
            }

            _firstPersonArmsHiddenForMissingAnchor = false;
            _firstPersonArmsEnabledBeforeMissingAnchor = false;
        }

        private void CaptureManualArmIkChainsBeforeControllerSwap()
        {
            _manualLeftArmIk = null;
            _manualRightArmIk = null;
            if (!_isLocal || _animator == null)
                return;

            _manualLeftArmIk = CaptureManualArmIkChain(
                "L",
                FirstPersonLeftHandPath,
                ResolveFirstPersonLeftHandTarget());
            _manualRightArmIk = CaptureManualArmIkChain(
                "R",
                FirstPersonRightHandPath,
                ResolveFirstPersonRightHandTarget());
        }

        private void CaptureRightArmPresentationScale()
        {
            _rightArmPresentationShoulder = null;
            _rightArmPresentationCachedLocalScale = Vector3.one;
            _rightArmPresentationScaleCaptured = false;
            _rightArmPresentationHidden = false;
            if (!_isLocal)
                return;
            if (TryAdoptInteractionBeginRightArmPresentation())
                return;

            Transform shoulder = _manualRightArmIk?.Root != null
                ? _manualRightArmIk.Root.parent
                : null;
            bool verifiedUnderArmsOnly = shoulder != null &&
                _firstPersonArmsRoot != null &&
                shoulder.IsChildOf(_firstPersonArmsRoot);
            bool verifiedName = shoulder != null &&
                string.Equals(shoulder.name, "shoulder.R", StringComparison.Ordinal);
            if (!verifiedUnderArmsOnly || !verifiedName)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV][RightArmHide] shoulder.R scale cache rejected: " +
                    $"shoulder={GetTransformPath(shoulder)} " +
                    $"armsOnlyRoot={GetTransformPath(_firstPersonArmsRoot)} " +
                    $"verifiedUnderScavengerModelArmsOnly={verifiedUnderArmsOnly} " +
                    $"verifiedName={verifiedName}.");
                return;
            }

            _rightArmPresentationShoulder = shoulder;
            _rightArmPresentationCachedLocalScale = shoulder.localScale;
            _rightArmPresentationScaleCaptured = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][RightArmHide] cached local arms-only shoulder scale: " +
                $"shoulder={GetTransformPath(shoulder)} " +
                "verifiedUnderScavengerModelArmsOnly=True " +
                $"cachedLocalScale={FormatTraceVector(_rightArmPresentationCachedLocalScale)}.");
        }

        private void CaptureHeadPresentationScale()
        {
            ClearHeadPresentationState();
            if (!_isLocal || !_interactionsApiMode ||
                !Y4NGZPlayerAnimationBridge.HideLocalPlayerHeadDuringApiSession)
            {
                RestorePendingHeadPresentationForApiFallback(Player);
                return;
            }
            if (TryAdoptInteractionBeginHeadPresentation())
                return;

            Transform head = ResolveLocalThirdPersonHeadBone(Player);
            if (head == null)
                return;

            Vector3 capturedLocalScale = head.localScale;
            if (!TryResolveHeadPresentationBaseline(
                    head,
                    capturedLocalScale,
                    "session-begin",
                    out Vector3 baselineLocalScale))
            {
                return;
            }

            _headPresentationBone = head;
            _headPresentationCachedLocalScale = baselineLocalScale;
            _headPresentationScaleCaptured = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][HeadHide] cached local third-person head scale: " +
                $"head={GetTransformPath(head)} " +
                $"cachedLocalScale={FormatTraceVector(_headPresentationCachedLocalScale)}.");
        }

        private void SyncApiShoulderCapPlugs()
        {
            try
            {
                SyncApiShoulderCapPlugsCore();
            }
            catch (Exception ex)
            {
                bool shouldLog = !_shoulderCapPlugUnavailableLogged;
                DestroyApiShoulderCapPlugs();
                _shoulderCapPlugUnavailableLogged = true;
                if (shouldLog)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][ShoulderCapPlug] synchronization failed: {ex.Message}");
                }
            }
        }

        private void SyncApiShoulderCapPlugsCore()
        {
            if (!_isLocal || !_interactionsApiMode ||
                !Y4NGZPlayerAnimationBridge.UseApiShoulderCapPlugs)
            {
                DestroyApiShoulderCapPlugs();
                return;
            }

            Transform leftBone = ResolveApiShoulderCapBone(
                _manualLeftArmIk,
                FirstPersonLeftUpperArmPath,
                "arm.L_upper");
            Transform rightBone = ResolveApiShoulderCapBone(
                _manualRightArmIk,
                FirstPersonRightUpperArmPath,
                "arm.R_upper");
            SkinnedMeshRenderer armsRenderer = ResolveApiShoulderCapArmsRenderer();
            Material armsMaterial = armsRenderer != null
                ? armsRenderer.sharedMaterial
                : null;
            if (leftBone == null || rightBone == null || armsRenderer == null || armsMaterial == null)
            {
                if (!_shoulderCapPlugUnavailableLogged)
                {
                    _shoulderCapPlugUnavailableLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV][ShoulderCapPlug] spawn deferred: " +
                        $"leftBone={GetTransformPath(leftBone)} " +
                        $"rightBone={GetTransformPath(rightBone)} " +
                        $"armsRenderer={GetTransformPath(armsRenderer != null ? armsRenderer.transform : null)} " +
                        $"sharedMaterial={(armsMaterial != null ? armsMaterial.name : "<missing>")}.");
                }
                return;
            }

            _shoulderCapPlugUnavailableLogged = false;
            float diameter = Mathf.Max(0f, Y4NGZPlayerAnimationBridge.ApiShoulderCapPlugDiameter);
            float thickness = Mathf.Max(0f, Y4NGZPlayerAnimationBridge.ApiShoulderCapPlugThickness);
            EnsureApiShoulderCapPlug(
                ref _leftShoulderCapPlug,
                "L",
                leftBone,
                ResolveApiShoulderCapLowerBone(_manualLeftArmIk, leftBone, "arm.L_lower"),
                armsRenderer,
                armsMaterial,
                diameter,
                thickness);
            EnsureApiShoulderCapPlug(
                ref _rightShoulderCapPlug,
                "R",
                rightBone,
                ResolveApiShoulderCapLowerBone(_manualRightArmIk, rightBone, "arm.R_lower"),
                armsRenderer,
                armsMaterial,
                diameter,
                thickness);
        }

        private Transform ResolveApiShoulderCapBone(
            ManualArmIkChain chain,
            string path,
            string expectedName)
        {
            Transform bone = chain?.Root;
            if (bone == null && _animator != null)
                bone = _animator.transform.Find(path);
            if (bone == null ||
                !string.Equals(bone.name, expectedName, StringComparison.Ordinal) ||
                _firstPersonArmsRoot == null ||
                !bone.IsChildOf(_firstPersonArmsRoot))
            {
                return null;
            }
            return bone;
        }

        private static Transform ResolveApiShoulderCapLowerBone(
            ManualArmIkChain chain,
            Transform upperBone,
            string expectedName)
        {
            Transform lowerBone = chain?.Mid;
            if (lowerBone != null && ReferenceEquals(lowerBone.parent, upperBone))
                return lowerBone;
            if (upperBone == null)
                return null;
            for (int i = 0; i < upperBone.childCount; i++)
            {
                Transform child = upperBone.GetChild(i);
                if (string.Equals(child.name, expectedName, StringComparison.Ordinal))
                    return child;
            }
            return null;
        }

        private SkinnedMeshRenderer ResolveApiShoulderCapArmsRenderer()
        {
            SkinnedMeshRenderer renderer = _firstPersonArmsRoot != null
                ? _firstPersonArmsRoot.GetComponentInChildren<SkinnedMeshRenderer>(true)
                : null;
            if (renderer == null)
                renderer = _firstPersonArmsRenderer as SkinnedMeshRenderer;
            return renderer;
        }

        private static void EnsureApiShoulderCapPlug(
            ref GameObject plug,
            string side,
            Transform upperBone,
            Transform lowerBone,
            SkinnedMeshRenderer armsRenderer,
            Material armsMaterial,
            float diameter,
            float thickness)
        {
            if (plug != null && !ReferenceEquals(plug.transform.parent, upperBone))
            {
                UnityEngine.Object.Destroy(plug);
                plug = null;
            }

            bool spawned = false;
            if (plug == null)
            {
                Vector3 boneAxis = lowerBone != null
                    ? lowerBone.position - upperBone.position
                    : upperBone.forward;
                if (boneAxis.sqrMagnitude < 0.000001f)
                    boneAxis = upperBone.forward;
                boneAxis.Normalize();
                Vector3 capUp = upperBone.up;
                if (capUp.sqrMagnitude < 0.000001f ||
                    Mathf.Abs(Vector3.Dot(boneAxis, capUp.normalized)) > 0.98f)
                {
                    capUp = upperBone.right;
                }

                plug = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                plug.name = $"LethalCCTV_ApiShoulderCapPlug_{side}";
                plug.layer = armsRenderer.gameObject.layer;
                plug.transform.SetPositionAndRotation(
                    upperBone.position,
                    Quaternion.LookRotation(boneAxis, capUp));
                plug.transform.SetParent(upperBone, true);
                plug.transform.localPosition = Vector3.zero;

                Collider collider = plug.GetComponent<Collider>();
                if (collider != null)
                {
                    collider.enabled = false;
                    UnityEngine.Object.Destroy(collider);
                }
                spawned = true;
            }

            plug.layer = armsRenderer.gameObject.layer;
            plug.transform.localPosition = Vector3.zero;
            plug.transform.localScale = new Vector3(diameter, diameter, thickness);
            Renderer plugRenderer = plug.GetComponent<Renderer>();
            if (plugRenderer != null)
            {
                plugRenderer.sharedMaterial = armsMaterial;
                plugRenderer.shadowCastingMode = ShadowCastingMode.Off;
                plugRenderer.receiveShadows = false;
            }

            if (spawned)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ShoulderCapPlug] arm={side} " +
                    $"bone={GetTransformPath(upperBone)} " +
                    $"worldPosition={FormatTraceVector(plug.transform.position)} " +
                    $"diameter={diameter:F3} thickness={thickness:F3}.");
            }
        }

        private void DestroyApiShoulderCapPlugs()
        {
            DestroyApiShoulderCapPlug(ref _leftShoulderCapPlug);
            DestroyApiShoulderCapPlug(ref _rightShoulderCapPlug);
            _shoulderCapPlugUnavailableLogged = false;
        }

        private static void DestroyApiShoulderCapPlug(ref GameObject plug)
        {
            if (plug != null)
                UnityEngine.Object.Destroy(plug);
            plug = null;
        }

        private ManualArmIkChain CaptureManualArmIkChain(
            string side,
            string tipPath,
            Transform target,
            string capturePhase = "pre-swap",
            bool logDetails = true)
        {
            try
            {
                Transform tip = _animator.transform.Find(tipPath);
                Transform mid = tip != null ? tip.parent : null;
                int walked = 0;
                while (mid != null && walked++ < 4 &&
                       mid.name.IndexOf("_lower", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    mid = mid.parent;
                }
                Transform root = mid != null ? mid.parent : null;
                if (root == null || mid == null || tip == null || target == null)
                {
                    if (logDetails)
                    {
                        SurveillanceBootstrap.Log?.LogWarning(
                            $"[LethalCCTV][ManualIK] arm={side} {capturePhase} chain capture failed: " +
                            $"root={GetTransformPath(root)} mid={GetTransformPath(mid)} " +
                            $"tip={GetTransformPath(tip)} target={GetTransformPath(target)}.");
                    }
                    return null;
                }

                float upperLength = Vector3.Distance(root.position, mid.position);
                float lowerLength = Vector3.Distance(mid.position, tip.position);
                if (upperLength <= 0.0001f || lowerLength <= 0.0001f)
                {
                    if (logDetails)
                    {
                        SurveillanceBootstrap.Log?.LogWarning(
                            $"[LethalCCTV][ManualIK] arm={side} {capturePhase} chain has invalid lengths: " +
                            $"upper={upperLength:F4} lower={lowerLength:F4}.");
                    }
                    return null;
                }

                var chain = new ManualArmIkChain
                {
                    Side = side,
                    Root = root,
                    Mid = mid,
                    Tip = tip,
                    Target = target,
                    UpperLength = upperLength,
                    LowerLength = lowerLength,
                    TargetToTipRotation = Quaternion.Inverse(target.rotation) * tip.rotation,
                };

                if (logDetails)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][ManualIK] arm={side} captured {capturePhase}: " +
                        $"root={root.name}@{GetTransformPath(root)} " +
                        $"mid={mid.name}@{GetTransformPath(mid)} " +
                        $"tip={tip.name}@{GetTransformPath(tip)} " +
                        $"target={GetTransformPath(target)} " +
                        $"lengths={upperLength:F4}+{lowerLength:F4} " +
                        $"targetToTipEuler={FormatVector(chain.TargetToTipRotation.eulerAngles)} " +
                        "pole=station-space config when enabled; fallback=current/last-frame elbow plane then down-and-toward-body " +
                        $"configEnabled={Y4NGZPlayerAnimationBridge.UseManualFirstPersonArmIk}.");
                }
                return chain;
            }
            catch (Exception ex)
            {
                if (logDetails)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][ManualIK] arm={side} {capturePhase} chain capture failed: {ex.Message}");
                }
                return null;
            }
        }

        private void ApplyManualFirstPersonArmIk()
        {
            if (!_isLocal || (!_controllerApplied && !_interactionsApiMode) ||
                (!_active && !_windingDown) ||
                !Y4NGZPlayerAnimationBridge.UseManualFirstPersonArmIk ||
                !ShouldSolveFirstPersonArms())
            {
                return;
            }

            if (_interactionsApiMode &&
                !EnsureInteractionsApiManualArmIkChains("frame-recapture"))
            {
                return;
            }

            SolveManualArmIk(_manualLeftArmIk);
            SolveManualArmIk(_manualRightArmIk);
            if (_interactionsApiMode && !_apiManualArmIkFirstSolvedLogged &&
                _manualLeftArmIk != null && _manualRightArmIk != null &&
                _manualLeftArmIk.LastSolvedFrame == Time.frameCount &&
                _manualRightArmIk.LastSolvedFrame == Time.frameCount &&
                _manualLeftArmIk.LastTipTargetDistance >= 0f &&
                _manualRightArmIk.LastTipTargetDistance >= 0f)
            {
                _apiManualArmIkFirstSolvedLogged = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV]{BuildManualIkTrace()} api_first_solved=true.");
            }
        }

        private bool EnsureInteractionsApiManualArmIkChains(string reason)
        {
            if (!_isLocal || !Y4NGZPlayerAnimationBridge.UseManualFirstPersonArmIk)
                return false;

            if (IsManualArmIkChainAvailable(_manualLeftArmIk) &&
                IsManualArmIkChainAvailable(_manualRightArmIk))
            {
                return true;
            }

            if (Time.unscaledTime < _apiManualArmIkNextCaptureAttemptAt)
                return false;
            _apiManualArmIkNextCaptureAttemptAt = Time.unscaledTime + 0.5f;

            _animator = Player != null ? Player.playerBodyAnimator : null;
            _manualLeftArmIk = null;
            _manualRightArmIk = null;
            _firstPersonLeftHandTarget = null;
            _firstPersonRightHandTarget = null;
            bool logCaptureDetails = !_apiManualArmIkChainsUnavailableLogged;
            if (_animator != null)
            {
                // Capture with the right-arm presentation hide temporarily
                // lifted: the hide collapses shoulder.R localScale to ~0, so a
                // capture under it reads degenerate bone lengths and rejects
                // the right chain (Test 32: 'right=missing' => no pin/IK).
                bool rehideRightArm = _rightArmPresentationHidden &&
                    _rightArmPresentationShoulder != null;
                if (rehideRightArm)
                {
                    _rightArmPresentationShoulder.localScale =
                        _rightArmPresentationCachedLocalScale;
                }
                try
                {
                    _manualLeftArmIk = CaptureManualArmIkChain(
                        "L",
                        FirstPersonLeftHandPath,
                        ResolveFirstPersonLeftHandTarget(),
                        "api-session",
                        logCaptureDetails);
                    _manualRightArmIk = CaptureManualArmIkChain(
                        "R",
                        FirstPersonRightHandPath,
                        ResolveFirstPersonRightHandTarget(),
                        "api-session",
                        logCaptureDetails);
                }
                finally
                {
                    if (rehideRightArm)
                    {
                        _rightArmPresentationShoulder.localScale =
                            Vector3.one * PresentationHiddenScale;
                    }
                }
            }

            if (IsManualArmIkChainAvailable(_manualLeftArmIk) &&
                IsManualArmIkChainAvailable(_manualRightArmIk))
            {
                return true;
            }

            if (!_apiManualArmIkChainsUnavailableLogged)
            {
                _apiManualArmIkChainsUnavailableLogged = true;
                string unavailableReason = _animator == null
                    ? "animator-missing"
                    : $"{reason}:left={(_manualLeftArmIk != null ? "available" : "missing")}," +
                      $"right={(_manualRightArmIk != null ? "available" : "missing")}";
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ManualIK] api_chains_unavailable reason={unavailableReason}.");
            }
            return false;
        }

        private static bool IsManualArmIkChainAvailable(ManualArmIkChain chain)
        {
            return chain != null && chain.Root != null && chain.Mid != null &&
                chain.Tip != null && chain.Target != null;
        }

        private void ApplyRightArmPresentationHideAfterManualIk()
        {
            if (!_isLocal || (!_controllerApplied && !_interactionsApiMode) ||
                !_rightArmPresentationScaleCaptured ||
                _rightArmPresentationShoulder == null)
            {
                return;
            }

            bool enterTransitionActive = _active && !MonitorFocus.IsEnterCameraPathComplete;
            // Both backends reveal the right arm only for an accepted action.
            // Keep the existing entry/exit hide, including the exit retract.
            bool sessionOrTransitionActive = _windingDown || enterTransitionActive || !IsRightHandActionPressWindowActive();
            bool shouldHide = Y4NGZPlayerAnimationBridge.HideRightFirstPersonArmDuringEnterAndExit &&
                sessionOrTransitionActive;
            if (shouldHide)
            {
                string reason = _interactionsApiMode
                    ? "api-session"
                    : _windingDown ? "wind-down" : "enter-start";
                Vector3 hiddenScale = Vector3.one * PresentationHiddenScale;
                _rightArmPresentationShoulder.localScale = hiddenScale;
                if (!_rightArmPresentationHidden)
                {
                    _rightArmPresentationHidden = true;
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][RightArmHide] frame={Time.frameCount} reason={reason} " +
                        $"cachedLocalScale={FormatTraceVector(_rightArmPresentationCachedLocalScale)} " +
                        $"appliedLocalScale={FormatTraceVector(_rightArmPresentationShoulder.localScale)}.");
                }
                return;
            }

            if (_rightArmPresentationHidden)
            {
                string reason = Y4NGZPlayerAnimationBridge.HideRightFirstPersonArmDuringEnterAndExit
                    ? "enter-complete"
                    : "restore";
                RestoreRightArmPresentationScale(reason);
            }
        }

        private void ApplyRightArmPresentationHideImmediately(
            string reason = "begin-immediate")
        {
            if (!_isLocal ||
                !Y4NGZPlayerAnimationBridge.HideRightFirstPersonArmDuringEnterAndExit ||
                !_rightArmPresentationScaleCaptured ||
                _rightArmPresentationShoulder == null)
            {
                return;
            }

            try
            {
                _rightArmPresentationShoulder.localScale =
                    Vector3.one * PresentationHiddenScale;
                _rightArmPresentationHidden = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][RightArmHide] frame={Time.frameCount} reason={reason} " +
                    $"cachedLocalScale={FormatTraceVector(_rightArmPresentationCachedLocalScale)} " +
                    $"appliedLocalScale={FormatTraceVector(_rightArmPresentationShoulder.localScale)}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RightArmHide] immediate Begin hide failed: {ex.Message}");
            }
        }

        private void ApplyHeadPresentationHideAfterManualIk()
        {
            if (!_isLocal || !_interactionsApiMode ||
                !_headPresentationScaleCaptured || _headPresentationBone == null)
            {
                return;
            }

            bool shouldHide =
                Y4NGZPlayerAnimationBridge.HideLocalPlayerHeadDuringApiSession &&
                (_active || _windingDown);
            if (shouldHide)
            {
                try
                {
                    _headPresentationBone.localScale =
                        Vector3.one * PresentationHiddenScale;
                    if (!_headPresentationHidden)
                    {
                        _headPresentationHidden = true;
                        SurveillanceBootstrap.Log?.LogInfo(
                            $"[LethalCCTV][HeadHide] frame={Time.frameCount} reason=api-session " +
                            $"cachedLocalScale={FormatTraceVector(_headPresentationCachedLocalScale)} " +
                            $"appliedLocalScale={FormatTraceVector(_headPresentationBone.localScale)}.");
                    }
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][HeadHide] frame reassert failed: {ex.Message}");
                }
                return;
            }

            if (_headPresentationHidden)
                RestoreHeadPresentationScale("config-disabled");
        }

        private void ApplyHeadPresentationHideImmediately(string reason)
        {
            if (!_isLocal || !_interactionsApiMode ||
                !Y4NGZPlayerAnimationBridge.HideLocalPlayerHeadDuringApiSession ||
                !_headPresentationScaleCaptured || _headPresentationBone == null)
            {
                return;
            }

            try
            {
                _headPresentationBone.localScale =
                    Vector3.one * PresentationHiddenScale;
                _headPresentationHidden = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][HeadHide] frame={Time.frameCount} reason={reason} " +
                    $"cachedLocalScale={FormatTraceVector(_headPresentationCachedLocalScale)} " +
                    $"appliedLocalScale={FormatTraceVector(_headPresentationBone.localScale)}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][HeadHide] immediate hide failed reason={reason}: {ex.Message}");
            }
        }

        private void RestoreHeadPresentationScale(string reason)
        {
            if (!_headPresentationScaleCaptured || _headPresentationBone == null)
                return;

            bool wasHidden = _headPresentationHidden;
            try
            {
                _headPresentationBone.localScale = _headPresentationCachedLocalScale;
                if (wasHidden)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][HeadHide] frame={Time.frameCount} reason={reason} " +
                        $"cachedLocalScale={FormatTraceVector(_headPresentationCachedLocalScale)} " +
                        $"appliedLocalScale={FormatTraceVector(_headPresentationBone.localScale)}.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][HeadHide] exact scale restore failed reason={reason}: {ex.Message}");
            }
            finally
            {
                _headPresentationHidden = false;
            }
        }

        private void ClearHeadPresentationState()
        {
            _headPresentationBone = null;
            _headPresentationCachedLocalScale = Vector3.one;
            _headPresentationScaleCaptured = false;
            _headPresentationHidden = false;
        }

        private void RestoreRightArmPresentationScale(string reason)
        {
            if (!_rightArmPresentationScaleCaptured || _rightArmPresentationShoulder == null)
                return;

            bool wasHidden = _rightArmPresentationHidden;
            try
            {
                _rightArmPresentationShoulder.localScale =
                    _rightArmPresentationCachedLocalScale;
                if (wasHidden)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][RightArmHide] frame={Time.frameCount} reason={reason} " +
                        $"cachedLocalScale={FormatTraceVector(_rightArmPresentationCachedLocalScale)} " +
                        $"appliedLocalScale={FormatTraceVector(_rightArmPresentationShoulder.localScale)}.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RightArmHide] exact scale restore failed reason={reason}: {ex.Message}");
            }
            finally
            {
                _rightArmPresentationHidden = false;
            }
        }

        private void SolveManualArmIk(ManualArmIkChain chain)
        {
            if (chain == null || chain.Root == null || chain.Mid == null ||
                chain.Tip == null || chain.Target == null)
            {
                return;
            }

            try
            {
                Vector3 rootPosition = chain.Root.position;
                Vector3 toTarget = chain.Target.position - rootPosition;
                float targetDistance = toTarget.magnitude;
                chain.LastRootPosition = rootPosition;
                chain.LastRootTargetDistance = targetDistance;
                chain.LastReach = chain.UpperLength + chain.LowerLength;
                Vector3 aimDirection = targetDistance > 0.000001f
                    ? toTarget / targetDistance
                    : chain.Root.forward;

                float minReach = Mathf.Abs(chain.UpperLength - chain.LowerLength) + 0.0001f;
                float maxReach = chain.UpperLength + chain.LowerLength - 0.0001f;
                float solveDistance = Mathf.Clamp(targetDistance, minReach, maxReach);
                Vector3 solvedTarget = rootPosition + aimDirection * solveDistance;

                Transform metarig = Player != null ? Player.playerModelArmsMetarig : null;
                Transform poleReference = metarig != null ? metarig : chain.Root.parent;
                Vector3 poleDirection = Vector3.zero;
                string poleHint = "<none>";
                bool useStationPoleHint = Y4NGZPlayerAnimationBridge.UseApiElbowPoleHints;
                if (useStationPoleHint)
                {
                    Transform operatorPose = CCTVOperatorStation.OperatorPoseAnchor;
                    Transform stationRoot = operatorPose != null ? operatorPose.parent : null;
                    if (stationRoot == null)
                    {
                        throw new InvalidOperationException(
                            $"Configured station-space elbow pole cannot resolve the CCTV station root for arm {chain.Side}.");
                    }

                    Vector3 poleTargetStation = string.Equals(
                        chain.Side,
                        "R",
                        StringComparison.OrdinalIgnoreCase)
                        ? Y4NGZPlayerAnimationBridge.ApiRightElbowPoleStation
                        : Y4NGZPlayerAnimationBridge.ApiLeftElbowPoleStation;
                    Vector3 poleTargetWorld = stationRoot.TransformPoint(poleTargetStation);
                    poleDirection = poleTargetWorld - rootPosition;
                    poleDirection -= aimDirection * Vector3.Dot(poleDirection, aimDirection);
                    if (poleDirection.sqrMagnitude <= 0.000001f)
                    {
                        throw new InvalidOperationException(
                            $"Configured station-space elbow pole is collinear with the shoulder-target line for arm {chain.Side}.");
                    }
                    poleHint = $"station-pole-target({chain.Side})";
                }
                else
                {
                    // Preserve the previously solved bend side across the engine's
                    // next-frame FK reset. Store it in metarig space so camera/body
                    // motion carries the pole naturally, then project it onto this
                    // frame's shoulder-target plane. Only reacquire from FK when the
                    // previous plane becomes degenerate.
                    if (chain.HasStablePole)
                    {
                        Vector3 previousPole = chain.StablePoleReference != null
                            ? chain.StablePoleReference.TransformDirection(chain.StablePoleReferenceDirection)
                            : chain.StablePoleReferenceDirection;
                        poleDirection = previousPole -
                            aimDirection * Vector3.Dot(previousPole, aimDirection);
                        if (poleDirection.sqrMagnitude > 0.000001f)
                            poleHint = "last-frame-bend-plane";
                    }

                    if (poleDirection.sqrMagnitude <= 0.000001f)
                    {
                        poleDirection = chain.Mid.position - rootPosition;
                        poleDirection -= aimDirection * Vector3.Dot(poleDirection, aimDirection);
                        if (poleDirection.sqrMagnitude > 0.000001f)
                            poleHint = "current-elbow-plane";
                    }
                    if (poleDirection.sqrMagnitude <= 0.000001f)
                    {
                        // Degenerate straight donor pose: put the elbow below the
                        // shoulder-target line and back toward the operator body.
                        Vector3 towardBody = poleReference != null
                            ? -poleReference.forward
                            : -chain.Root.forward;
                        Vector3 bodyHint = towardBody * 0.75f + Vector3.down;
                        poleDirection = bodyHint -
                            aimDirection * Vector3.Dot(bodyHint, aimDirection);
                        poleHint = "body-fallback(down+toward-body)";
                    }
                    if (poleDirection.sqrMagnitude <= 0.000001f)
                    {
                        Vector3 axisHint = Mathf.Abs(Vector3.Dot(aimDirection, Vector3.up)) < 0.95f
                            ? Vector3.up
                            : Vector3.right;
                        poleDirection = axisHint -
                            aimDirection * Vector3.Dot(axisHint, aimDirection);
                        poleHint = "world-axis-fallback";
                    }
                }
                poleDirection.Normalize();
                if (!useStationPoleHint)
                {
                    chain.HasStablePole = true;
                    chain.StablePoleReference = poleReference;
                    chain.StablePoleReferenceDirection = poleReference != null
                        ? poleReference.InverseTransformDirection(poleDirection)
                        : poleDirection;
                }

                float upperSq = chain.UpperLength * chain.UpperLength;
                float lowerSq = chain.LowerLength * chain.LowerLength;
                float shoulderCos = Mathf.Clamp(
                    (upperSq + solveDistance * solveDistance - lowerSq) /
                    (2f * chain.UpperLength * solveDistance),
                    -1f,
                    1f);
                float elbowAlong = chain.UpperLength * shoulderCos;
                float elbowHeight = Mathf.Sqrt(Mathf.Max(0f, upperSq - elbowAlong * elbowAlong));
                Vector3 desiredMidPosition = rootPosition +
                    aimDirection * elbowAlong + poleDirection * elbowHeight;

                // Keep the elbow behind the wrist on the same reach circle.
                // Reject an impossible fold without changing bone lengths or
                // adding a competing shoulder translation.
                Transform station = CCTVOperatorStation.OperatorPoseAnchor?.parent;
                if (station != null && !CctvControlGeometry.TryKeepElbowBehindWrist(
                    rootPosition + aimDirection * elbowAlong, aimDirection, elbowHeight,
                    desiredMidPosition, solvedTarget, station.right, out desiredMidPosition)) return;

                Vector3 currentUpperDirection = chain.Mid.position - rootPosition;
                Vector3 desiredUpperDirection = desiredMidPosition - rootPosition;
                if (currentUpperDirection.sqrMagnitude > 0.000001f &&
                    desiredUpperDirection.sqrMagnitude > 0.000001f)
                {
                    SetWorldRotationThroughLocal(chain.Root, Quaternion.FromToRotation(
                        currentUpperDirection,
                        desiredUpperDirection) * chain.Root.rotation);
                }

                Vector3 currentLowerDirection = chain.Tip.position - chain.Mid.position;
                Vector3 desiredLowerDirection = solvedTarget - chain.Mid.position;
                if (currentLowerDirection.sqrMagnitude > 0.000001f &&
                    desiredLowerDirection.sqrMagnitude > 0.000001f)
                {
                    SetWorldRotationThroughLocal(chain.Mid, Quaternion.FromToRotation(
                        currentLowerDirection,
                        desiredLowerDirection) * chain.Mid.rotation);
                }

                chain.Tip.rotation = chain.Target.rotation * chain.TargetToTipRotation;
                chain.LastSolvedFrame = Time.frameCount;
                chain.LastTipTargetDistance = Vector3.Distance(
                    chain.Tip.position,
                    chain.Target.position);
                chain.LastPoleHint = poleHint;
            }
            catch (Exception ex)
            {
                chain.LastSolvedFrame = Time.frameCount;
                chain.LastTipTargetDistance = -1f;
                chain.LastPoleHint = "error:" + ex.GetType().Name;
                SurveillanceBootstrap.Log?.LogDebug(
                    $"[LethalCCTV][ManualIK] arm={chain.Side} solve failed: {ex.Message}");
            }
        }

        private static void SetWorldRotationThroughLocal(
            Transform transform,
            Quaternion worldRotation)
        {
            if (transform == null)
                return;
            transform.localRotation = transform.parent != null
                ? Quaternion.Inverse(transform.parent.rotation) * worldRotation
                : worldRotation;
        }

        internal void TickFirstPersonHandsAfterCamera()
        {
            if (!_isLocal || (!_active && !_windingDown) ||
                (_interactionsApiMode && _interactionsApiUsesDedicatedViewmodel)) return;
            ApplyInteractionsApiArmsRootStationPin();
            if (Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive)
            {
                ApplyFirstPersonLeftHandTuning(allowInteractionsApi: true);
                ApplyFirstPersonRightHandTuning(allowInteractionsApi: true);
            }
            ApplyManualFirstPersonArmIk();
            LogApiLeverContactPostSolveSample();
            ApplyRightArmPresentationHideAfterManualIk();
            ApplyHeadPresentationHideAfterManualIk();
            if (Player != null)
                MonitorFocus.LogPendingViewFraming(Player.gameplayCamera, _manualLeftArmIk?.Root, _manualRightArmIk?.Root);
        }

        internal void PrepareFirstPersonHandsForRender(Camera camera)
        {
            if (!_isLocal || Player == null || camera != Player.gameplayCamera ||
                (!_active && !_windingDown) || (!_controllerApplied && !_interactionsApiMode) ||
                (_interactionsApiMode && _interactionsApiUsesDedicatedViewmodel)) return;
            // Interactions/RigBuilder and vanilla lever animation may evaluate
            // after our LateUpdate. Reassert the current absolute pose at the
            // gameplay render boundary without advancing the input/animation clock.
            _operatorPoseFrame = -1;
            TickFirstPersonHandsAfterCamera();
        }
    }
}
