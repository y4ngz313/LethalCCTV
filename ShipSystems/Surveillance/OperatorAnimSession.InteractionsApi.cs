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
        private void CaptureInteractionsApiPoseChain()
        {
            _apiPoseChainSnapshots = Array.Empty<ApiPoseTransformSnapshot>();
            if (!_isLocal || !_interactionsApiMode || Player == null)
                return;

            Transform localArms = Player.localArmsTransform;
            if (localArms == null)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV][ApiPose] capture-chain unavailable: localArmsTransform is missing.");
                return;
            }

            Transform leftTarget = FindChildRecursive(localArms, "ArmsLeftArm_target");
            Transform rightTarget = FindChildRecursive(localArms, "ArmsRightArm_target");
            var snapshots = new List<ApiPoseTransformSnapshot>(3);
            CaptureApiPoseTransform(snapshots, "localArmsTransform", localArms);
            CaptureApiPoseTransform(snapshots, "ArmsLeftArm_target", leftTarget);
            CaptureApiPoseTransform(snapshots, "ArmsRightArm_target", rightTarget);
            _apiPoseChainSnapshots = snapshots.ToArray();
        }

        private static void CaptureApiPoseTransform(
            List<ApiPoseTransformSnapshot> snapshots,
            string label,
            Transform transform)
        {
            if (transform == null)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ApiPose] capture-chain transform missing: {label}.");
                return;
            }

            var snapshot = new ApiPoseTransformSnapshot
            {
                Label = label,
                Transform = transform,
                LocalPosition = transform.localPosition,
                LocalRotation = transform.localRotation,
            };
            snapshots.Add(snapshot);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][ApiPose] capture-chain transform={label} " +
                $"path={GetTransformPath(transform)} " +
                $"localPosition={FormatTraceVector(snapshot.LocalPosition)} " +
                $"localEuler={FormatTraceVector(snapshot.LocalRotation.eulerAngles)}.");
        }

        private void RestoreInteractionsApiPoseChain(string reason)
        {
            ApiPoseTransformSnapshot[] snapshots = _apiPoseChainSnapshots;
            _apiPoseChainSnapshots = Array.Empty<ApiPoseTransformSnapshot>();
            for (int i = 0; i < snapshots.Length; i++)
            {
                ApiPoseTransformSnapshot snapshot = snapshots[i];
                if (snapshot == null || snapshot.Transform == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV][ApiPose] restore-chain skipped a destroyed transform " +
                        $"reason='{reason}' label={snapshot?.Label ?? "<unknown>"}.");
                    continue;
                }

                try
                {
                    snapshot.Transform.localPosition = snapshot.LocalPosition;
                    snapshot.Transform.localRotation = snapshot.LocalRotation;
                    float positionResidual = Vector3.Distance(
                        snapshot.Transform.localPosition,
                        snapshot.LocalPosition);
                    float rotationResidual = Quaternion.Angle(
                        snapshot.Transform.localRotation,
                        snapshot.LocalRotation);
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][ApiPose] restore-chain reason='{reason}' " +
                        $"transform={snapshot.Label} path={GetTransformPath(snapshot.Transform)} " +
                        $"postRestoreResidual={positionResidual:F3}m/{rotationResidual:F3}deg.");
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][ApiPose] restore-chain failed reason='{reason}' " +
                        $"transform={snapshot.Label}: {ex.Message}");
                }
            }
        }

        private void ApplyInteractionsApiArmsRootStationPin()
        {
            if (!_isLocal || (!_active && !_windingDown))
                return;

            bool useStationShoulderAnchor =
                Y4NGZPlayerAnimationBridge.UseApiStationShoulderAnchor;
            if (useStationShoulderAnchor &&
                (!IsManualArmIkChainAvailable(_manualLeftArmIk) ||
                 !IsManualArmIkChainAvailable(_manualRightArmIk)))
            {
                EnsureInteractionsApiManualArmIkChains("station-shoulder-anchor");
            }

            Transform localArms = Player != null ? Player.localArmsTransform : null;
            if (localArms == null)
            {
                LogApiArmsRootStationPinUnavailable("localArmsTransform-missing");
                return;
            }

            if (_apiArmsRootStationPinRoot != localArms)
            {
                _apiArmsRootStationPinRoot = localArms;
                _apiArmsRootStationPinLeftTarget = null;
                _apiArmsRootStationPinLeftParent = null;
                _apiArmsRootStationPinRightTarget = null;
            }

            if (_apiArmsRootStationPinLeftTarget == null)
            {
                _apiArmsRootStationPinLeftTarget =
                    FindChildRecursive(localArms, "ArmsLeftArm_target");
                _apiArmsRootStationPinLeftParent = _apiArmsRootStationPinLeftTarget != null
                    ? _apiArmsRootStationPinLeftTarget.parent
                    : null;
            }
            if (_apiArmsRootStationPinRightTarget == null)
                _apiArmsRootStationPinRightTarget =
                    FindChildRecursive(localArms, "ArmsRightArm_target");

            Transform leftTarget = _apiArmsRootStationPinLeftTarget;
            Transform leftParent = _apiArmsRootStationPinLeftParent;
            Transform rightTarget = _apiArmsRootStationPinRightTarget;
            // The round-4 legacy root pin only applied when the station
            // shoulder anchor was off; the anchor is constant-on since the
            // #575 promotion, so the pin (and its config key) is retired.
            const bool useLegacyRootPin = false;
            if ((useStationShoulderAnchor || useLegacyRootPin) && leftParent == null)
            {
                LogApiArmsRootStationPinUnavailable("ArmsLeftArm_target-parent-missing");
                return;
            }

            Transform operatorPose = CCTVOperatorStation.OperatorPoseAnchor;
            Transform stationRoot = operatorPose != null ? operatorPose.parent : null;
            if (stationRoot == null)
            {
                LogApiArmsRootStationPinUnavailable("station-root-missing");
                return;
            }

            try
            {
                _apiArmsRootStationPinUnavailableReason = null;
                Vector3 bakedStationPosition = new Vector3(
                    ApiLeftParentStationPositionX,
                    ApiLeftParentStationPositionY,
                    ApiLeftParentStationPositionZ);
                Vector3 pinStationPosition = bakedStationPosition;
                bool floorRootPinShiftApplied =
                    MonitorFocus.TryGetApiOperatorFloorRootDelta(out float floorRootWorldDeltaY);
                if (floorRootPinShiftApplied)
                    pinStationPosition.y -= floorRootWorldDeltaY;
                Quaternion bakedStationRotation = Quaternion.Euler(
                    ApiLeftParentStationEulerX,
                    ApiLeftParentStationEulerY,
                    ApiLeftParentStationEulerZ);
                Vector3 rightAuthoredWorldPosition =
                    stationRoot.TransformPoint(ApiRightHandRestStationPosition);
                Quaternion rightAuthoredWorldRotation =
                    stationRoot.rotation * ApiRightHandRestStationRotation;
                bool leftAuthoredPoseResolved = TryResolveApiLeftAuthoredStationPose(
                    out AuthoredLeftHandStationPose leftAuthoredStationPose,
                    out string leftAuthoredClip,
                    out int leftAuthoredFrame);
                bool enterClipPlaybackActive = !_windingDown && _active &&
                    !MonitorFocus.IsEnterCameraPathComplete;
                bool authoredTablePlaybackActive = _windingDown || enterClipPlaybackActive;
                Vector3 leftAuthoredWorldPosition = leftAuthoredPoseResolved
                    ? stationRoot.TransformPoint(leftAuthoredStationPose.Position)
                    : Vector3.zero;
                Quaternion leftAuthoredWorldRotation = leftAuthoredPoseResolved
                    ? stationRoot.rotation * leftAuthoredStationPose.Rotation
                    : Quaternion.identity;

                Vector3 prePinLeftParentPosition = leftParent != null
                    ? leftParent.position
                    : Vector3.zero;
                Vector3 shoulderMidWorld = Vector3.zero;
                Vector3 anchorTarget = Vector3.zero;
                Vector3 anchorDelta = Vector3.zero;
                string anchorMode = "<disabled>";
                float anchorStationBlend = 1f;
                bool shoulderAnchorApplied = false;
                // Post-write re-read of the shoulder midpoint. anchorDelta is the
                // PRE-write correction, so it can never show whether a later writer
                // moved the arms root back off the anchor. This is the only field on
                // the pin line that answers "did the pin survive the frame?".
                Vector3 postPinShoulderMid = Vector3.zero;
                bool postPinShoulderMidResolved = false;

                if (useStationShoulderAnchor)
                {
                    if (!IsManualArmIkChainAvailable(_manualLeftArmIk) ||
                        !IsManualArmIkChainAvailable(_manualRightArmIk))
                    {
                        LogApiArmsRootStationPinUnavailable(
                            "station-shoulder-anchor-chains-missing");
                        return;
                    }

                    shoulderMidWorld =
                        (_manualLeftArmIk.Root.position + _manualRightArmIk.Root.position) * 0.5f;
                    anchorTarget = ResolveApiShoulderAnchorTarget(
                        stationRoot,
                        out anchorMode,
                        out anchorStationBlend);
                    anchorDelta = anchorTarget - shoulderMidWorld;

                    // The enter/exit reach assist (Test 31) was retired by the
                    // 2026-07-22 camera-lean redesign: the enter camera path
                    // leans into reach instead of the anchor sliding toward the
                    // contact. Its off-by-default config lever and this branch
                    // were deleted for 1.0 (#575). The separate lever-control
                    // reach assist below is unrelated and still live.

                    // Round 7 root writer: translate only. The legacy leftParent
                    // root pin below is mutually exclusive with this branch.
                    localArms.position += anchorDelta;
                    shoulderAnchorApplied = true;
                    postPinShoulderMid =
                        (_manualLeftArmIk.Root.position + _manualRightArmIk.Root.position) * 0.5f;
                    postPinShoulderMidResolved = true;
                }
                // The round-4 legacy root pin branch that alternated with the
                // shoulder anchor was deleted with its config key (#575); the
                // anchor is the only root writer now.

                string leftTargetWriter = "none";
                // Round 9 direct playback uses the exact v5 station-space pose
                // only while an enter/exit clip is active. During control, the
                // round-7 reconstruction preserves the live lever-driven target.
                // Disabling the A/B gate keeps reconstruction in every phase.
                if (useStationShoulderAnchor && leftTarget != null && leftParent != null)
                {
                    if (Y4NGZPlayerAnimationBridge.UseApiLeftTargetFromAuthoredTable &&
                        authoredTablePlaybackActive &&
                        leftAuthoredPoseResolved)
                    {
                        Vector3 authoredTargetPosition = leftAuthoredWorldPosition;
                        if (enterClipPlaybackActive)
                        {
                            float pressWeight =
                                ComputeEnterPressContactWeight(leftAuthoredFrame);
                            if (pressWeight > 0f)
                            {
                                Vector3? liveButton =
                                    Y4NGZPlayerAnimationBridge.ResolveAccessButtonPressPoint();
                                if (liveButton.HasValue)
                                {
                                    Vector3 authoredContactWorld =
                                        stationRoot.TransformPoint(
                                            ApiLeftEnterAuthoredTable[
                                                ApiEnterPressContactRowIndex].Position);
                                    // Test 40: the designed pointer-finger
                                    // pose plays UNTOUCHED (rotation stays
                                    // authored); contact is purely positional
                                    // — aim the wrist so the index tip, where
                                    // it sits under the AUTHORED quat, lands
                                    // on the cap. Rides pressWeight (in over
                                    // rows 5-8, hold to 16, out by 19) as a
                                    // constant world delta so rows 0-4 and
                                    // the authored dip shape are preserved.
                                    Vector3 wristAtContact = liveButton.Value -
                                        leftAuthoredWorldRotation *
                                            ApiEnterPressLocalIndexTipOffset;
                                    Vector3 correction =
                                        (wristAtContact - authoredContactWorld) *
                                        pressWeight;
                                    authoredTargetPosition += correction;
                                    ArmApiPressContactPostSolveSample(
                                        leftAuthoredFrame,
                                        correction,
                                        liveButton.Value,
                                        authoredContactWorld);
                                }
                            }
                            float rampInWeight = Smooth01(Mathf.Clamp01(
                                leftAuthoredFrame / ApiEnterHandRampInFrames));
                            if (rampInWeight < 1f &&
                                TryResolveCurrentHandRestPoses(
                                    out Vector3 rampRestLeftPos,
                                    out _,
                                    out _,
                                    out _))
                            {
                                // First-frame flash fix: rise in from the
                                // below-frame rest instead of popping onto
                                // the authored desk rows.
                                authoredTargetPosition = Vector3.Lerp(
                                    rampRestLeftPos,
                                    authoredTargetPosition,
                                    rampInWeight);
                            }
                        }
                        // Lever-contact snap (Test 38): land the sweep ON the
                        // live grip and release it during the exit lift-off.
                        float leverContactWeight = 0f;
                        if (enterClipPlaybackActive)
                            leverContactWeight = ComputeEnterLeverContactWeight(leftAuthoredFrame);
                        else if (_windingDown)
                            leverContactWeight = ComputeExitLeverContactWeight(leftAuthoredFrame);
                        if (leverContactWeight > 0f)
                        {
                            if (TryResolveLeverWristTarget(
                                    leftAuthoredWorldRotation,
                                    out Vector3 leverWristTarget,
                                    out _,
                                    out _))
                            {
                                Vector3 authoredLeverRestWorld = stationRoot.TransformPoint(
                                    ApiLeftEnterAuthoredTable[
                                        ApiLeftEnterAuthoredTable.Length - 1].Position);
                                authoredTargetPosition +=
                                    (leverWristTarget - authoredLeverRestWorld) * leverContactWeight;
                            }
                        }
                        leftTarget.SetPositionAndRotation(
                            authoredTargetPosition,
                            leftAuthoredWorldRotation);
                        leftTargetWriter = "authored-table";
                    }
                    else
                    {
                        Vector3 authoredLocalPosition = leftTarget.localPosition;
                        Quaternion authoredLocalRotation = leftTarget.localRotation;
                        Vector3 leftParentScale = leftParent.lossyScale;
                        Vector3 reconstructedStationPosition = bakedStationPosition +
                            bakedStationRotation * Vector3.Scale(
                                leftParentScale,
                                authoredLocalPosition);
                        Vector3 reconstructedWorldPosition =
                            stationRoot.TransformPoint(reconstructedStationPosition);
                        Quaternion reconstructedWorldRotation =
                            stationRoot.rotation * bakedStationRotation * authoredLocalRotation;
                        // Control owns exact positional contact. Reconstructed
                        // clip sway previously survived as an additive delta,
                        // moving a nominally correct wrist target off the knob.
                        // Keep the live rotation/finger pose, but derive the
                        // wrist position from that rotation and the anatomical
                        // palm point so the hand remains planted while the
                        // lever target moves.
                        if (_active && !_windingDown)
                        {
                            if (TryResolveLeverWristTarget(
                                    reconstructedWorldRotation,
                                    out Vector3 leverWristTarget,
                                    out Vector3 liveGrip,
                                    out Vector3 localPalmOffset))
                            {
                                reconstructedWorldPosition = leverWristTarget;
                                ArmApiLeverContactPostSolveSample(
                                    liveGrip, leverWristTarget, localPalmOffset);
                            }
                        }
                        leftTarget.SetPositionAndRotation(
                            reconstructedWorldPosition,
                            reconstructedWorldRotation);
                        leftTargetWriter = "round7-reconstruction";
                    }
                }

                // Round 5 remains the next and final target writer before IK,
                // independent of which (if any) mutually-exclusive root writer ran.
                if (Y4NGZPlayerAnimationBridge.PinRightHandRestToStationDuringApiSession &&
                    rightTarget != null)
                {
                    rightTarget.SetPositionAndRotation(
                        rightAuthoredWorldPosition,
                        rightAuthoredWorldRotation);
                }

                // Exit retract (Test 31): the authored exit table still had the
                // hands over the console when the controller restore snapped
                // them away ("twist upside down then disappear"). Over the
                // final retract window, blend both targets to the below-frame
                // camera-rest poses so the hands drop out of frame smoothly
                // BEFORE the restore swap, making the swap invisible.
                if (_windingDown &&
                    Y4NGZPlayerAnimationBridge.UseApiExitRetractToRest &&
                    _interactionsApiExitStartedAt > 0f &&
                    TryResolveCurrentHandRestPoses(
                        out Vector3 exitRestLeftPos,
                        out Quaternion exitRestLeftRot,
                        out Vector3 exitRestRightPos,
                        out Quaternion exitRestRightRot))
                {
                    float retractSeconds = Mathf.Max(
                        0.05f,
                        Y4NGZPlayerAnimationBridge.ApiExitRetractSeconds);
                    float retractStart = Mathf.Max(
                        0f,
                        CCTVOperatorInteractionsBridge.ExitClipLengthSeconds -
                            retractSeconds);
                    float exitTime = Time.unscaledTime - _interactionsApiExitStartedAt;
                    float retractBlend = Smooth01(Mathf.Clamp01(
                        (exitTime - retractStart) / retractSeconds));
                    if (retractBlend > 0f)
                    {
                        // Position-only (Test 33): the rotation Slerp toward
                        // the camera-rest Euler read as both hands twisting
                        // upside down while still in frame. Keep each hand's
                        // current rotation and just drop them below frame;
                        // orientation only matters while visible.
                        if (leftTarget != null)
                        {
                            leftTarget.position = Vector3.Lerp(
                                leftTarget.position, exitRestLeftPos, retractBlend);
                        }
                        if (rightTarget != null)
                        {
                            rightTarget.position = Vector3.Lerp(
                                rightTarget.position, exitRestRightPos, retractBlend);
                        }
                    }
                }

                bool logTelemetry = !_apiArmsRootStationPinActive ||
                    Time.unscaledTime >= _apiArmsRootStationPinNextTelemetryAt;
                _apiArmsRootStationPinActive = true;
                if (!logTelemetry)
                    return;

                _apiArmsRootStationPinNextTelemetryAt =
                    Time.unscaledTime + ApiArmsRootStationPinTelemetryIntervalSeconds;
                string rootWriter = shoulderAnchorApplied
                    ? "station-shoulder-anchor"
                    : "none";
                string shoulderMidWorldTrace = shoulderAnchorApplied
                    ? FormatTraceVector(shoulderMidWorld)
                    : "<disabled>";
                string anchorTargetTrace = shoulderAnchorApplied
                    ? FormatTraceVector(anchorTarget)
                    : "<disabled>";
                string anchorDeltaTrace = shoulderAnchorApplied
                    ? $"{anchorDelta.magnitude:F3}m"
                    : "<disabled>";
                string preLeftParentWorld = leftParent != null
                    ? FormatTraceVector(prePinLeftParentPosition)
                    : "<missing>";
                string leftTargetWorld = leftTarget != null
                    ? FormatTraceVector(leftTarget.position)
                    : "<missing>";
                string leftAuthoredResidual = leftTarget != null && leftAuthoredPoseResolved
                    ? $"{Vector3.Distance(leftTarget.position, leftAuthoredWorldPosition):F3}m"
                    : leftTarget == null ? "<missing>" : "<authored-pose-unavailable>";
                string rightTargetWorld = rightTarget != null
                    ? FormatTraceVector(rightTarget.position)
                    : "<missing>";
                string rightAuthoredResidual = rightTarget != null
                    ? $"{Vector3.Distance(rightTarget.position, rightAuthoredWorldPosition):F3}m"
                    : "<missing>";
                // Camera frame, resolved offset and frustum term on the SAME row as
                // the anchor. Every prior diagnosis of this pin had to pair an anchor
                // sample with a camera sample from a different frame, which is how the
                // 2026-07-28 handoff derived a 0.227m world delta for an offset whose
                // magnitude is fixed at |cameraFrameOffset| by construction.
                Camera pinCamera = Player != null ? Player.gameplayCamera : null;
                string cameraTrace = "<missing>";
                string anchorCamSpaceTrace = "<unavailable>";
                string frustumTrace = "<unavailable>";
                if (pinCamera != null)
                {
                    Transform pinCameraTransform = pinCamera.transform;
                    cameraTrace =
                        $"cameraWorldPos={FormatTraceVector(pinCameraTransform.position)} " +
                        $"cameraWorldEuler={FormatTraceVector(pinCameraTransform.eulerAngles)} " +
                        $"cameraFov={pinCamera.fieldOfView:0.###} " +
                        $"cameraAspect={pinCamera.aspect:0.###} " +
                        $"cameraNear={pinCamera.nearClipPlane:0.###}";
                    if (shoulderAnchorApplied)
                    {
                        // Measured, not restated from config: what the anchor
                        // ACTUALLY resolved to in camera space this frame.
                        Vector3 anchorCamSpace =
                            Quaternion.Inverse(pinCameraTransform.rotation) *
                            (anchorTarget - pinCameraTransform.position);
                        Vector3 configuredOffset =
                            Y4NGZPlayerAnimationBridge.ApiCameraAnchorOffset;
                        anchorCamSpaceTrace =
                            $"anchorCamSpace={FormatTraceVector(anchorCamSpace)} " +
                            $"cameraFrameOffset={FormatTraceVector(configuredOffset)} " +
                            $"cameraFrame=yaw-flat " +
                            $"offsetResidual={Vector3.Distance(anchorCamSpace, configuredOffset):F4}m";
                        // Signed distance of the shoulder midpoint below the bottom
                        // frustum plane. Negative means below frame. The open shoulder
                        // caps stay hidden only while this is <= -(cap radius); at the
                        // 2026-07-28 defaults it sits at roughly -0.01m, i.e. the caps
                        // clear the plane by a centimetre and render every frame.
                        float halfVertical = pinCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                        float bottomPlaneDistance =
                            (Mathf.Cos(halfVertical) * anchorCamSpace.y) +
                            (Mathf.Sin(halfVertical) * anchorCamSpace.z);
                        frustumTrace =
                            $"shoulderMidBelowBottomPlane={bottomPlaneDistance:F4}m " +
                            $"capRadiusNeeded={Y4NGZPlayerAnimationBridge.ApiShoulderCapPlugDiameter * 0.5f:F4}m";
                    }
                }

                string armUpperTrace = "<unavailable>";
                if (shoulderAnchorApplied && pinCamera != null)
                {
                    Quaternion pinCameraInverse = Quaternion.Inverse(pinCamera.transform.rotation);
                    Vector3 pinCameraPosition = pinCamera.transform.position;
                    string FormatArm(string label, ManualArmIkChain chain)
                    {
                        if (chain == null || chain.Root == null)
                            return $"{label}=<missing>";
                        Vector3 rootWorld = chain.Root.position;
                        Vector3 rootCam = pinCameraInverse * (rootWorld - pinCameraPosition);
                        return $"{label}World={FormatTraceVector(rootWorld)} " +
                               $"{label}Cam={FormatTraceVector(rootCam)} " +
                               $"{label}LossyScale={chain.Root.lossyScale.x:0.###}";
                    }
                    armUpperTrace =
                        FormatArm("armUpperL", _manualLeftArmIk) + " " +
                        FormatArm("armUpperR", _manualRightArmIk);
                }

                string postPinResidual = postPinShoulderMidResolved
                    ? $"{Vector3.Distance(postPinShoulderMid, anchorTarget):F4}m"
                    : "<bypassed>";

                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ApiPose] pin " +
                    $"rootWriter={rootWriter} " +
                    $"{cameraTrace} {anchorCamSpaceTrace} {frustumTrace} " +
                    $"{armUpperTrace} postPinMidToAnchor={postPinResidual} " +
                    $"floorRootDeltaWorldY={(floorRootPinShiftApplied ? floorRootWorldDeltaY.ToString("F3") : "<disabled>")} " +
                    $"pinStationTarget={FormatTraceVector(pinStationPosition)} " +
                    $"shoulderMidWorld={shoulderMidWorldTrace} " +
                    $"anchorTarget={anchorTargetTrace} anchorDelta={anchorDeltaTrace} " +
                    $"anchorMode={anchorMode} stationBlend={anchorStationBlend:F3} " +
                    $"preLeftParentWorld={preLeftParentWorld} " +
                    $"legacyRootPin=removed " +
                    $"leftTargetWriter={leftTargetWriter} authoredClip={leftAuthoredClip} " +
                    $"authoredFrame={leftAuthoredFrame} leftTargetWorld={leftTargetWorld} " +
                    $"leftAuthoredResidual={leftAuthoredResidual} " +
                    $"rightTargetWorld={rightTargetWorld} rightAuthoredResidual={rightAuthoredResidual}.");
            }
            catch (Exception ex)
            {
                LogApiArmsRootStationPinUnavailable(
                    "write-failed:" + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private void ApplyInteractionsApiLeverControlReachAssist()
        {
            Transform localArms = Player != null ? Player.localArmsTransform : null;
            ManualArmIkChain chain = _manualLeftArmIk;
            Transform leftTarget = _apiArmsRootStationPinLeftTarget;
            if (localArms == null || !IsManualArmIkChainAvailable(chain) || leftTarget == null)
            {
                _apiLeverControlReachAssistSmoothed = 0f;
                return;
            }

            bool controlHoldActive = _active &&
                !_windingDown &&
                MonitorFocus.IsEnterCameraPathComplete;
            float reachAssistGoal = 0f;
            Vector3 reachAssistDirection = _apiLeverControlReachAssistLastDirection;
            float targetDistance = 0f;
            float usableReach =
                (chain.UpperLength + chain.LowerLength) *
                ApiReachAssistUsableReachFraction;
            if (controlHoldActive)
            {
                Vector3 toTarget = leftTarget.position - chain.Root.position;
                targetDistance = toTarget.magnitude;
                if (targetDistance > usableReach && targetDistance > 0.0001f)
                {
                    reachAssistGoal = Mathf.Min(
                        targetDistance - usableReach,
                        ApiLeverControlReachAssistMaxMeters);
                    reachAssistDirection = toTarget / targetDistance;
                }
            }

            _apiLeverControlReachAssistSmoothed = Mathf.MoveTowards(
                _apiLeverControlReachAssistSmoothed,
                reachAssistGoal,
                ApiLeverControlReachAssistSlewMetersPerSecond *
                    Mathf.Max(Time.unscaledDeltaTime, 1f / 240f));
            if (_apiLeverControlReachAssistSmoothed <= 0.0001f ||
                reachAssistDirection.sqrMagnitude < 0.0001f)
            {
                return;
            }

            reachAssistDirection.Normalize();
            _apiLeverControlReachAssistLastDirection = reachAssistDirection;
            Vector3 leftTargetPosition = leftTarget.position;
            Quaternion leftTargetRotation = leftTarget.rotation;
            Transform rightTarget = _apiArmsRootStationPinRightTarget;
            Vector3 rightTargetPosition = rightTarget != null
                ? rightTarget.position
                : Vector3.zero;
            Quaternion rightTargetRotation = rightTarget != null
                ? rightTarget.rotation
                : Quaternion.identity;

            // Translate the hidden first-person shoulder frame only. Reassert
            // both targets in world space afterward so the visible contacts do
            // not inherit the root translation; final manual IK then solves the
            // shortened root-to-target distance exactly.
            localArms.position +=
                reachAssistDirection * _apiLeverControlReachAssistSmoothed;
            leftTarget.SetPositionAndRotation(leftTargetPosition, leftTargetRotation);
            if (rightTarget != null)
                rightTarget.SetPositionAndRotation(rightTargetPosition, rightTargetRotation);

            if (controlHoldActive &&
                reachAssistGoal > 0.005f &&
                !_apiLeverControlReachAssistLogged)
            {
                _apiLeverControlReachAssistLogged = true;
                CCTVOperatorStation.TryResolveLeverGripPoint(out _, out string gripSource);
                SurveillanceBootstrap.Log?.LogMessage(
                    $"[LethalCCTV][LeverReach] source={gripSource} " +
                    $"rootToTarget={targetDistance:F3}m usableReach={usableReach:F3}m " +
                    $"goal={reachAssistGoal:F3}m cap={ApiLeverControlReachAssistMaxMeters:F3}m; " +
                    $"cameraPose=unchanged.");
            }
        }

        private Vector3 ResolveApiShoulderAnchorTarget(
            Transform stationRoot,
            out string mode,
            out float stationBlend)
        {
            Vector3 stationTarget = stationRoot.TransformPoint(
                Y4NGZPlayerAnimationBridge.ApiShoulderAnchorStation);
            mode = "station";
            stationBlend = 1f;
            bool keepCameraAnchorDuringControl =
                Y4NGZPlayerAnimationBridge.ApiKeepCameraAnchorDuringControl;
            if (!Y4NGZPlayerAnimationBridge.UseApiCameraRelativeIntroAnchor &&
                !keepCameraAnchorDuringControl)
                return stationTarget;

            bool exitPathActive = _windingDown && _interactionsApiExitStartedAt > 0f;
            bool enterPathActive = !exitPathActive && _active &&
                !MonitorFocus.IsEnterCameraPathComplete;
            if (!enterPathActive && !exitPathActive && !keepCameraAnchorDuringControl)
                return stationTarget;

            Camera camera = Player != null ? Player.gameplayCamera : null;
            if (camera == null)
            {
                mode = "station-camera-missing";
                return stationTarget;
            }

            Transform cameraTransform = camera.transform;
            Vector3 flattenedForward = Vector3.ProjectOnPlane(
                cameraTransform.forward,
                Vector3.up);
            if (flattenedForward.sqrMagnitude < 0.0001f)
            {
                flattenedForward = Vector3.ProjectOnPlane(
                    cameraTransform.up,
                    Vector3.up);
            }
            if (flattenedForward.sqrMagnitude < 0.0001f && Player != null)
            {
                flattenedForward = Vector3.ProjectOnPlane(
                    Player.transform.forward,
                    Vector3.up);
            }
            if (flattenedForward.sqrMagnitude < 0.0001f)
                flattenedForward = Vector3.forward;

            Quaternion yawFlatCameraRotation = Quaternion.LookRotation(
                flattenedForward.normalized,
                Vector3.up);
            // Yaw-flat only: the pitch-relative anchor frame was deleted for 1.0
            // (#575) once its gate promoted to a compile-time false.
            Vector3 cameraTarget = cameraTransform.position +
                yawFlatCameraRotation * Y4NGZPlayerAnimationBridge.ApiCameraAnchorOffset;

            if (keepCameraAnchorDuringControl)
            {
                // Option B anti-float: never blend to the station anchor. The
                // station anchor pushes the shoulders out to desk reach, which
                // exposes the truncated ScavengerModelArmsOnly caps; holding the
                // camera-relative frame keeps them tucked off-screen in every
                // phase, so enter/control/exit share one seamless anchor frame.
                stationBlend = 0f;
                mode = enterPathActive
                    ? "camera-enter-hold"
                    : exitPathActive
                        ? "camera-exit-hold"
                        : "camera-control-hold";
                return cameraTarget;
            }

            if (enterPathActive)
            {
                float rawT = Mathf.Clamp01(
                    (Time.unscaledTime - _interactionsApiStartedAt) /
                    CCTVOperatorInteractionsBridge.EnterClipLengthSeconds);
                stationBlend = Smooth01(Mathf.InverseLerp(
                    ApiCameraAnchorEnterBlendStartRawT,
                    1f,
                    rawT));
                mode = stationBlend <= 0f
                    ? "camera-enter"
                    : stationBlend >= 1f
                        ? "station-enter-settle"
                        : "camera-station-enter-blend";
            }
            else
            {
                float rawT = Mathf.Clamp01(
                    (Time.unscaledTime - _interactionsApiExitStartedAt) /
                    CCTVOperatorInteractionsBridge.ExitClipLengthSeconds);
                stationBlend = 1f - Smooth01(Mathf.InverseLerp(
                    0f,
                    ApiCameraAnchorExitBlendEndRawT,
                    rawT));
                mode = stationBlend >= 1f
                    ? "station-exit-start"
                    : stationBlend <= 0f
                        ? "camera-exit"
                        : "station-camera-exit-blend";
            }

            return Vector3.Lerp(cameraTarget, stationTarget, stationBlend);
        }

        private bool TryResolveApiLeftAuthoredStationPose(
            out AuthoredLeftHandStationPose pose,
            out string clip,
            out int frame)
        {
            bool useExitClip = _windingDown && _interactionsApiExitStartedAt > 0f;
            AuthoredLeftHandStationPose[] table = useExitClip
                ? ApiLeftExitAuthoredTable
                : ApiLeftEnterAuthoredTable;
            clip = useExitClip ? "exit" : "enter";
            frame = -1;
            pose = default;
            if (table == null || table.Length == 0)
                return false;

            float elapsed = useExitClip
                ? Mathf.Max(0f, Time.unscaledTime - _interactionsApiExitStartedAt)
                : ResolveEnterClipPresentationSeconds();
            frame = Mathf.Clamp(
                Mathf.CeilToInt(elapsed * ApiAuthoredTrajectoryFps),
                1,
                table.Length);
            pose = table[frame - 1];
            return true;
        }

        /// <summary>
        /// Enter-phase presentation clock: the camera path's hitch-clamped
        /// elapsed while the enter path animates (Test 34: the wall clock ran
        /// 0.29s ahead of the stalled camera and the press fired while the
        /// camera was still approaching), falling back to wall time once the
        /// path completes or is unavailable.
        /// </summary>
        private float ResolveEnterClipPresentationSeconds()
        {
            float pathClock = MonitorFocus.EnterPresentationClockSeconds;
            if (pathClock >= 0f)
                return pathClock;
            return Mathf.Max(0f, Time.unscaledTime - _interactionsApiStartedAt);
        }

        private static float ComputeEnterPressContactWeight(int frame)
        {
            float rampIn = Mathf.Clamp01(
                (frame - (ApiEnterPressContactFrameStart - ApiEnterPressContactRampFrames)) /
                ApiEnterPressContactRampFrames);
            float rampOut = 1f - Mathf.Clamp01(
                (frame - ApiEnterPressContactFrameEnd) /
                ApiEnterPressContactRampFrames);
            return Smooth01(rampIn) * Smooth01(rampOut);
        }

        // Ramps in over the sweep's final approach and HOLDS at 1 so the
        // enter's landing pose, the control-phase reconstruction, and the
        // exit's first rows all share the full correction (no pop at either
        // handoff).
        private static float ComputeEnterLeverContactWeight(int frame)
        {
            return Smooth01(Mathf.Clamp01(
                (frame - (ApiEnterLeverContactFrameStart - ApiEnterLeverContactRampFrames)) /
                ApiEnterLeverContactRampFrames));
        }

        private static float ComputeExitLeverContactWeight(int frame)
        {
            return 1f - Smooth01(Mathf.Clamp01(
                (frame - ApiExitLeverContactHoldFrames) /
                ApiExitLeverContactFadeFrames));
        }

        private bool TryResolveLeverWristTarget(
            Quaternion targetRotation,
            out Vector3 wristTarget,
            out Vector3 liveGrip,
            out Vector3 localPalmOffset)
        {
            wristTarget = Vector3.zero;
            liveGrip = Vector3.zero;
            localPalmOffset = ResolveApiLeverPalmLocalOffset();

            Vector3? resolvedGrip = Y4NGZPlayerAnimationBridge.ResolveLeverGripPoint();
            if (!resolvedGrip.HasValue)
                return false;

            liveGrip = resolvedGrip.Value;
            Quaternion solvedHandRotation = targetRotation;
            if (_manualLeftArmIk != null)
                solvedHandRotation *= _manualLeftArmIk.TargetToTipRotation;

            Vector3 desiredPalm =
                liveGrip + Vector3.up * ApiLeverGripPalmOffsetMeters;
            wristTarget = desiredPalm - solvedHandRotation * localPalmOffset;
            if (!_apiLeverContactLogged)
            {
                _apiLeverContactLogged = true;
                SurveillanceBootstrap.Log?.LogMessage(
                    $"[LethalCCTV][LeverContact] source={_apiLeverPalmOffsetSource} " +
                    $"localPalm={FormatTraceVector(localPalmOffset)} " +
                    $"liveGrip={FormatTraceVector(liveGrip)} " +
                    $"desiredPalm={FormatTraceVector(desiredPalm)} " +
                    $"wristTarget={FormatTraceVector(wristTarget)}.");
            }
            return true;
        }

        private Vector3 ResolveApiLeverPalmLocalOffset()
        {
            if (_apiLeverPalmOffsetResolved)
                return _apiLeverPalmLocalOffset;

            _apiLeverPalmOffsetResolved = true;
            _apiLeverPalmLocalOffset = ApiLeverFallbackLocalPalmOffset;
            _apiLeverPalmOffsetSource = "fallback";

            Transform hand = _manualLeftArmIk != null ? _manualLeftArmIk.Tip : null;
            if (hand == null)
                return _apiLeverPalmLocalOffset;

            Vector3 knuckleCenter = Vector3.zero;
            int knuckleCount = 0;
            for (int i = 0; i < ApiLeverPalmKnuckleBoneNames.Length; i++)
            {
                Transform knuckle = FindChildRecursive(
                    hand,
                    ApiLeverPalmKnuckleBoneNames[i]);
                if (knuckle == null)
                    continue;
                knuckleCenter += knuckle.position;
                knuckleCount++;
            }

            if (knuckleCount < 2)
                return _apiLeverPalmLocalOffset;

            knuckleCenter /= knuckleCount;
            Vector3 palmCenter = Vector3.Lerp(
                hand.position,
                knuckleCenter,
                ApiLeverPalmKnuckleFraction);
            Vector3 candidate =
                Quaternion.Inverse(hand.rotation) * (palmCenter - hand.position);
            float span = candidate.magnitude;
            if (span < 0.035f || span > 0.14f)
                return _apiLeverPalmLocalOffset;

            _apiLeverPalmLocalOffset = candidate;
            _apiLeverPalmOffsetSource = $"finger-roots:{knuckleCount}";
            return _apiLeverPalmLocalOffset;
        }

        private void ArmApiLeverContactPostSolveSample(
            Vector3 liveGrip,
            Vector3 wristTarget,
            Vector3 localPalmOffset)
        {
            if (_apiLeverContactPostSolveLogged)
                return;

            _apiLeverContactSamplePending = true;
            _apiLeverContactSampleLiveGrip = liveGrip;
            _apiLeverContactSampleDesiredPalm =
                liveGrip + Vector3.up * ApiLeverGripPalmOffsetMeters;
            _apiLeverContactSampleWristTarget = wristTarget;
            _apiLeverContactSampleLocalPalmOffset = localPalmOffset;
        }

        private void ArmApiPressContactPostSolveSample(
            int frame,
            Vector3 correction,
            Vector3 liveButton,
            Vector3 authoredContactWorld)
        {
            // Sample at the deepest press row, not the first full-weight
            // frame (Test 35: the early sample read the hand mid-rise and
            // tipToButton was meaningless).
            if (_apiPressContactSnapLogged || _apiPressContactSamplePending ||
                frame < ApiEnterPressTelemetryFrame)
                return;

            _apiPressContactSamplePending = true;
            _apiPressContactSampleFrame = frame;
            _apiPressContactSampleCorrection = correction;
            _apiPressContactSampleLiveButton = liveButton;
            _apiPressContactSampleAuthoredContact = authoredContactWorld;
        }

        private void LogApiPressContactPostSolveSample()
        {
            if (!_apiPressContactSamplePending)
                return;

            _apiPressContactSamplePending = false;
            _apiPressContactSnapLogged = true;
            Vector3 liveButton = _apiPressContactSampleLiveButton;
            // Post-solve = the pose that renders this frame. The tip is the
            // HAND BONE (wrist): under the Test-40 authored-rotation aim a
            // correct press reads tipToButton ~= |ApiEnterPressLocalIndexTipOffset|
            // (the wrist sits one tip-offset short of the cap) and the finger
            // chain dump shows finger2.L.001_end as the CLOSEST bone at
            // ~0.00-0.02m (the index tip on the cap).
            Transform tip = _manualLeftArmIk != null ? _manualLeftArmIk.Tip : null;
            string tipTrace = tip != null
                ? $"{FormatTraceVector(tip.position)} rotEuler={FormatTraceVector(tip.rotation.eulerAngles)} " +
                  $"tipToButton={Vector3.Distance(tip.position, liveButton):F3}m"
                : "<chain-missing>";
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][PressContact] press-bottom(post-solve) frame={_apiPressContactSampleFrame}: " +
                $"correction={_apiPressContactSampleCorrection.magnitude:F3}m " +
                $"liveButton={FormatTraceVector(liveButton)} " +
                $"authoredContact={FormatTraceVector(_apiPressContactSampleAuthoredContact)} " +
                $"solvedWrist={tipTrace} (expect tipToButton~" +
                $"{ApiEnterPressLocalIndexTipOffset.magnitude:0.00}m, index tip closest at ~0.00-0.02m).");
            if (tip != null)
                LogApiPressContactFingerChain(tip, tip, liveButton);
        }

        private void LogApiLeverContactPostSolveSample()
        {
            if (!_apiLeverContactSamplePending || _apiLeverContactPostSolveLogged)
                return;

            ManualArmIkChain chain = _manualLeftArmIk;
            Transform hand = chain != null ? chain.Tip : null;
            if (hand == null || chain.LastSolvedFrame != Time.frameCount)
                return;

            _apiLeverContactSamplePending = false;
            _apiLeverContactPostSolveLogged = true;
            Vector3 solvedPalm = hand.position +
                hand.rotation * _apiLeverContactSampleLocalPalmOffset;
            SurveillanceBootstrap.Log?.LogMessage(
                $"[LethalCCTV][LeverContact] control(post-solve) " +
                $"liveGrip={FormatTraceVector(_apiLeverContactSampleLiveGrip)} " +
                $"desiredPalm={FormatTraceVector(_apiLeverContactSampleDesiredPalm)} " +
                $"solvedPalm={FormatTraceVector(solvedPalm)} " +
                $"wristTarget={FormatTraceVector(_apiLeverContactSampleWristTarget)} " +
                $"solvedWrist={FormatTraceVector(hand.position)} " +
                $"palmToDesired={Vector3.Distance(solvedPalm, _apiLeverContactSampleDesiredPalm):F3}m " +
                $"palmToGrip={Vector3.Distance(solvedPalm, _apiLeverContactSampleLiveGrip):F3}m " +
                $"wristToTarget={chain.LastTipTargetDistance:F3}m " +
                $"rootToTarget={chain.LastRootTargetDistance:F3}m " +
                $"reach={chain.LastReach:F3}m.");
        }

        /// <summary>
        /// One-shot finger-chain dump at the press bottom (Test 36 Issue A
        /// plan step 1): world position, hand-local offset and distance to
        /// the live cap for every descendant of the solved hand bone. Gives
        /// the true fingertip world position and the hand-local fingertip
        /// offset under the press quat so the wrist can be aimed at
        /// liveButton - handRotation * localFingertipOffset instead of the
        /// guessed scalar backoff.
        /// </summary>
        private void LogApiPressContactFingerChain(
            Transform hand,
            Transform current,
            Vector3 liveButton)
        {
            for (int i = 0; i < current.childCount; i++)
            {
                Transform child = current.GetChild(i);
                if (child == null)
                    continue;
                Vector3 world = child.position;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][PressContact] finger bone='{child.name}' " +
                    $"world={FormatTraceVector(world)} " +
                    $"handLocal={FormatTraceVector(hand.InverseTransformPoint(world))} " +
                    $"toButton={Vector3.Distance(world, liveButton):F3}m " +
                    $"children={child.childCount}.");
                LogApiPressContactFingerChain(hand, child, liveButton);
            }
        }

        private void LogApiArmsRootStationPinUnavailable(string reason)
        {
            if (string.Equals(
                    _apiArmsRootStationPinUnavailableReason,
                    reason,
                    StringComparison.Ordinal))
            {
                return;
            }

            _apiArmsRootStationPinUnavailableReason = reason;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV][ApiPose] pin_unavailable reason='{reason}'.");
        }

        private void ReleaseInteractionsApiArmsRootStationPin(string reason)
        {
            if (_apiArmsRootStationPinActive)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ApiPose] released reason='{reason}'.");
            }

            _apiArmsRootStationPinRoot = null;
            _apiArmsRootStationPinLeftTarget = null;
            _apiArmsRootStationPinLeftParent = null;
            _apiArmsRootStationPinRightTarget = null;
            _apiArmsRootStationPinActive = false;
            _apiArmsRootStationPinNextTelemetryAt = 0f;
            _apiArmsRootStationPinUnavailableReason = null;
            _apiLeverControlReachAssistSmoothed = 0f;
            _apiLeverControlReachAssistLastDirection = Vector3.zero;
        }

        private void FinishFirstPersonArmFrame()
        {
            if (Y4NGZPlayerAnimationBridge.UseManualFirstPersonArmIk)
            {
                // FINAL bone writer for the focused frame. The presentation-only
                // shoulder scale below is deliberately ordered after the solve.
                ApplyManualFirstPersonArmIk();
                ApplyRightArmPresentationHideAfterManualIk();
            }
            else
            {
                // A/B fallback only. The graph path is known not to solve the CCTV
                // chains, but remains available when manual IK is explicitly off.
                EvaluateLegacyFirstPersonArmRig();
                ApplyRightArmPresentationHideAfterManualIk();
            }
            LogApiPressContactPostSolveSample();
            LogApiLeverContactPostSolveSample();

            Transform leftShoulder = _manualLeftArmIk?.Root != null
                ? _manualLeftArmIk.Root.parent
                : null;
            Transform rightShoulder = _manualRightArmIk?.Root != null
                ? _manualRightArmIk.Root.parent
                : _rightArmPresentationShoulder;
            MonitorFocus.LogPendingViewFraming(
                Player != null ? Player.gameplayCamera : null,
                leftShoulder,
                rightShoulder);
        }

        internal void TraceFirstPersonHandsPreCull(Camera camera)
        {
            if (_interactionsApiMode)
                return;

            if (!_handTraceEnabled || _handTraceFrame != Time.frameCount ||
                Player == null || camera == null || camera != Player.gameplayCamera)
                return;

            TraceHandSnapshot("D-gameplay-camera/pre-cull");
            _handTraceFrame = -1;
        }

        private void LogExitArmsFrame(string phase)
        {
            if (!_exitArmsTelemetryEnabled || _exitArmsLastLoggedFrame == Time.frameCount)
                return;

            _exitArmsLastLoggedFrame = Time.frameCount;
            WriteExitArmsLog(
                phase,
                Player,
                _firstPersonArmsRoot,
                _manualLeftArmIk != null ? _manualLeftArmIk.Tip : null,
                _manualLeftArmIk != null ? _manualLeftArmIk.Target : _firstPersonLeftHandTarget,
                _rightArmPresentationShoulder,
                _manualRightArmIk != null ? _manualRightArmIk.Target : _firstPersonRightHandTarget,
                _firstPersonArmsRenderer,
                _presentationRemapValid,
                _shoulderAnchorAppliedFrame == Time.frameCount,
                _firstPersonArmsHiddenForMissingAnchor);
        }

        private void StartPostExitArmsTelemetry()
        {
            if (!_isLocal || Player == null)
                return;

            _postExitArmsTelemetry = new ExitArmsPostExitTelemetry
            {
                Player = Player,
                ArmsRoot = _firstPersonArmsRoot,
                LeftHand = _manualLeftArmIk != null ? _manualLeftArmIk.Tip : null,
                LeftTarget = _manualLeftArmIk != null ? _manualLeftArmIk.Target : _firstPersonLeftHandTarget,
                RightShoulder = _rightArmPresentationShoulder,
                RightTarget = _manualRightArmIk != null ? _manualRightArmIk.Target : _firstPersonRightHandTarget,
                ArmsRenderer = _firstPersonArmsRenderer != null
                    ? _firstPersonArmsRenderer
                    : Player.thisPlayerModelArms,
            };
        }

        internal static void TickPostExitArmsTelemetry()
        {
            ExitArmsPostExitTelemetry telemetry = _postExitArmsTelemetry;
            if (telemetry == null || telemetry.NextIndex > PostExitArmsTelemetryFrames ||
                telemetry.LastLoggedFrame == Time.frameCount)
            {
                return;
            }

            telemetry.LastLoggedFrame = Time.frameCount;
            bool presentationValid = MonitorFocus.TryGetFirstPersonArmsPresentationThisFrame(
                out _, out _, out _, out _, out _);
            WriteExitArmsLog(
                $"post-exit-{telemetry.NextIndex}",
                telemetry.Player,
                telemetry.ArmsRoot,
                telemetry.LeftHand,
                telemetry.LeftTarget,
                telemetry.RightShoulder,
                telemetry.RightTarget,
                telemetry.ArmsRenderer,
                presentationValid,
                shoulderAnchorApplied: false,
                anchorFallbackHidden: false);

            telemetry.NextIndex++;
            if (telemetry.NextIndex > PostExitArmsTelemetryFrames)
                _postExitArmsTelemetry = null;
        }

        internal static void ClearPostExitArmsTelemetry()
        {
            _postExitArmsTelemetry = null;
        }

        private static void WriteExitArmsLog(
            string phase,
            PlayerControllerB player,
            Transform armsRoot,
            Transform leftHand,
            Transform leftTarget,
            Transform rightShoulder,
            Transform rightTarget,
            Renderer armsRenderer,
            bool presentationRemapValid,
            bool shoulderAnchorApplied,
            bool anchorFallbackHidden)
        {
            try
            {
                Camera camera = player != null ? player.gameplayCamera : null;
                Transform visor = player != null ? player.localVisor : null;
                MonitorFocus.GetLocalRendererBookkeeping(
                    armsRenderer,
                    out bool forcedVisible,
                    out bool obstructor);

                string rootWorld = armsRoot != null ? FormatTraceVector(armsRoot.position) : "<missing>";
                string rootLocal = armsRoot != null ? FormatTraceVector(armsRoot.localPosition) : "<missing>";
                string rootEuler = armsRoot != null ? FormatTraceVector(armsRoot.localEulerAngles) : "<missing>";
                string handWorld = leftHand != null ? FormatTraceVector(leftHand.position) : "<missing>";
                string leftTargetWorld = leftTarget != null ? FormatTraceVector(leftTarget.position) : "<missing>";
                string rightTargetWorld = rightTarget != null ? FormatTraceVector(rightTarget.position) : "<missing>";
                string rightTargetViewport = rightTarget != null
                    ? FormatTraceViewport(rightTarget.position, camera)
                    : "<missing>";
                string rightShoulderLossyScaleX = rightShoulder != null
                    ? rightShoulder.lossyScale.x.ToString("0.000000")
                    : "<missing>";
                string handViewport = leftHand != null && camera != null
                    ? FormatTraceVector(camera.WorldToViewportPoint(leftHand.position))
                    : "<missing>";
                string rendererEnabled = armsRenderer != null ? armsRenderer.enabled.ToString() : "<missing>";
                string visorLocal = visor != null ? FormatTraceVector(visor.localPosition) : "<missing>";

                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ExitArms] Time.frameCount={Time.frameCount} phase={phase} " +
                    $"_presentationRemapValid={presentationRemapValid} shoulderAnchorAppliedThisFrame={shoulderAnchorApplied} " +
                    $"ScavengerModelArmsOnly.worldPos={rootWorld} ScavengerModelArmsOnly.localPosition={rootLocal} " +
                    $"ScavengerModelArmsOnly.localEulerAngles={rootEuler} hand.L.worldPos={handWorld} " +
                    $"target.L.worldPos={leftTargetWorld} target.R.worldPos={rightTargetWorld} " +
                    $"target.R.viewport={rightTargetViewport} shoulder.R.lossyScale.x={rightShoulderLossyScaleX} " +
                    $"hand.L.viewport={handViewport} thisPlayerModelArms.enabled={rendererEnabled} " +
                    $"thisPlayerModelArms.forcedVisible={forcedVisible} thisPlayerModelArms.obstructor={obstructor} " +
                    $"anchorFallbackHidden={anchorFallbackHidden} localVisor.localPosition={visorLocal}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ExitArms] Time.frameCount={Time.frameCount} phase={phase} telemetry-failed: {ex.Message}");
            }
        }

        // One-shot ground truth for the authored-clip camera-space mapping:
        // the builder converts camera-frame trajectories to target-parent
        // local curves using calibration constants measured in the tablet
        // pipeline; this logs the ACTUAL parent pose under the CCTV
        // controller so those constants can be corrected.
        private static bool _fpCalibrationLogged;

        private void LogFirstPersonTargetCalibrationOnce()
        {
            if (_fpCalibrationLogged || !_isLocal || !_controllerApplied)
                return;
            Camera camera = Player != null ? Player.gameplayCamera : null;
            Transform left = ResolveFirstPersonLeftHandTarget();
            Transform right = ResolveFirstPersonRightHandTarget();
            if (camera == null || left == null || left.parent == null)
                return;

            _fpCalibrationLogged = true;
            Transform cam = camera.transform;
            LogOne("L", left);
            if (right != null && right.parent != null)
                LogOne("R", right);

            void LogOne(string side, Transform target)
            {
                Transform parent = target.parent;
                Vector3 lp = cam.InverseTransformPoint(parent.position);
                Vector3 le = (Quaternion.Inverse(cam.rotation) * parent.rotation).eulerAngles;
                Vector3 tl = target.localPosition;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][FPCalib] {side} parentCamPos=({lp.x:F4}, {lp.y:F4}, {lp.z:F4}) " +
                    $"parentCamEuler=({le.x:F2}, {le.y:F2}, {le.z:F2}) parentLossy={parent.lossyScale.x:F4} " +
                    $"camLossy={cam.lossyScale.x:F4} targetLocal=({tl.x:F4}, {tl.y:F4}, {tl.z:F4}).");
            }
        }

    }
}
