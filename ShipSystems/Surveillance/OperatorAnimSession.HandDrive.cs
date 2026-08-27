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
        private bool IsRightHandActionPressWindowActive()
        {
            return Time.unscaledTime <= _buttonPressLayerUntil &&
                (Y4NGZPlayerAnimationBridge.IsBlueRightHandAction(_buttonPressActionId) ||
                 Y4NGZPlayerAnimationBridge.IsGreenRightHandAction(_buttonPressActionId));
        }

        private void ApplyFirstPersonRightHandTuning()
        {
            if (!_isLocal || !_controllerApplied || !ShouldShowFirstPersonArmsLayer())
                return;
            if (Time.unscaledTime > _buttonPressLayerUntil)
                return;
            if (!Y4NGZPlayerAnimationBridge.IsBlueRightHandAction(_buttonPressActionId) &&
                !Y4NGZPlayerAnimationBridge.IsGreenRightHandAction(_buttonPressActionId))
                return;

            Y4NGZPlayerAnimationBridge.FirstPersonRightHandPoseTuning tuning =
                Y4NGZPlayerAnimationBridge.GetFirstPersonRightHandPoseTuning(_buttonPressActionId);
            Transform target = ResolveFirstPersonRightHandTarget();
            if (target == null)
                return;

            try
            {
                ApplyPoseTuning(target, tuning, ref _firstPersonRightHandTuningApplication);
            }
            catch { }
        }

        private void ApplyFirstPersonLeftHandTuning(bool allowInteractionsApi = false)
        {
            if (!_isLocal || (!_controllerApplied && !allowInteractionsApi))
                return;
            if (!allowInteractionsApi && !ShouldShowFirstPersonArmsLayer())
                return;

            Y4NGZPlayerAnimationBridge.FirstPersonRightHandPoseTuning tuning =
                Y4NGZPlayerAnimationBridge.GetFirstPersonLeftHandPoseTuning();
            Transform target = ResolveFirstPersonLeftHandTarget();
            if (target == null)
                return;

            try
            {
                ApplyPoseTuning(target, tuning, ref _firstPersonLeftHandTuningApplication);
            }
            catch { }
        }

        private static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static Vector3 EvaluateCubicBezier(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return u * u * u * p0 +
                   3f * u * u * t * p1 +
                   3f * u * t * t * p2 +
                   t * t * t * p3;
        }

        /// <summary>
        /// Enter hand choreography for the left target. The right target's
        /// one-writer ownership is selected by TickFirstPersonHandsAfterCamera.
        /// The left wrist starts at its designed rest, reaches over the live
        /// cap, presses downward, and settles onto the live lever.
        /// </summary>
        private void ApplyEnterHandDrive()
        {
            if (!_handDriveEnterActive)
                return;
            if (!_isLocal || !_controllerApplied)
            {
                _handDriveEnterActive = false;
                return;
            }
            if (!ShouldShowFirstPersonArmsLayer())
                return;

            // Transient gates (3P debug view, config toggle): skip the frame
            // rather than cancel — the old correction cancelled permanently
            // here and silently lost the whole press.
            Transform target = ResolveFirstPersonLeftHandTarget();
            if (target == null)
                return;

            float t = Mathf.Max(0f, Time.unscaledTime - _handDriveEnterStartedAt);
            if (!TryResolveCurrentHandRestPoses(
                    out Vector3 loweredLeftPosition,
                    out Quaternion loweredLeftRotation,
                    out _,
                    out _))
            {
                return;
            }

            if (!Y4NGZPlayerAnimationBridge.TryResolveAccessButtonPressSurface(
                    out Vector3 buttonPoint,
                    out Vector3 surfaceNormal))
            {
                target.SetPositionAndRotation(loweredLeftPosition, loweredLeftRotation);
                return;
            }

            Camera camera = Player != null ? Player.gameplayCamera : null;
            if (surfaceNormal.sqrMagnitude < 0.0001f)
                surfaceNormal = Vector3.up;
            else
                surfaceNormal.Normalize();

            Transform stationFrame = CCTVAccessButton.Root != null
                ? CCTVAccessButton.Root.parent
                : CCTVOperatorStation.OperatorPoseAnchor != null
                    ? CCTVOperatorStation.OperatorPoseAnchor.parent
                    : null;
            Quaternion stationRotation = stationFrame != null ? stationFrame.rotation : Quaternion.identity;
            Vector3 wristContact = buttonPoint +
                surfaceNormal * HandEnterPressWristHeightMeters +
                stationRotation * HandEnterPressWristStationOffset;
            Vector3 hover = wristContact + surfaceNormal * HandEnterHoverLiftMeters;
            Vector3 releaseHover = wristContact + surfaceNormal * HandEnterReleaseLiftMeters;
            Vector3 pressAxis = -surfaceNormal;

            Vector3 toBody = Vector3.zero;
            Transform poseAnchor = CCTVOperatorStation.OperatorPoseAnchor;
            if (poseAnchor != null)
            {
                toBody = poseAnchor.position - buttonPoint;
                toBody -= surfaceNormal * Vector3.Dot(toBody, surfaceNormal);
                if (toBody.sqrMagnitude > 0.0001f)
                    toBody.Normalize();
            }
            if (toBody.sqrMagnitude < 0.0001f && camera != null)
            {
                toBody = camera.transform.position - buttonPoint;
                toBody -= surfaceNormal * Vector3.Dot(toBody, surfaceNormal);
                if (toBody.sqrMagnitude > 0.0001f)
                    toBody.Normalize();
            }

            Transform leverGrip = CCTVOperatorStation.RightHandTarget != null
                ? CCTVOperatorStation.RightHandTarget
                : CCTVOperatorStation.JoystickTiltPivot;
            Quaternion pressRotation = stationRotation * HandEnterPressStationRotation;
            Quaternion leverRotation = stationRotation * HandEnterLeverStationRotation;

            if (!_handDriveEnterLogged)
            {
                _handDriveEnterLogged = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][EnterAnim] hand driver active: capSurface={FormatVector(buttonPoint)} " +
                    $"normal={FormatVector(surfaceNormal)} wristContact={FormatVector(wristContact)} " +
                    $"lever={(leverGrip != null ? FormatVector(leverGrip.position) : "<missing>")} " +
                    $"designedRest=true contactAt={HandEnterContactAtSeconds:0.00}s " +
                    $"settleEnd={HandEnterSettleEndSeconds:0.00}s.");
            }

            Vector3 path;
            Quaternion rotation;
            if (t < HandEnterHoverAtSeconds)
            {
                float s = Smooth01(t / HandEnterHoverAtSeconds);
                Vector3 planarReach = hover - loweredLeftPosition;
                planarReach -= surfaceNormal * Vector3.Dot(planarReach, surfaceNormal);
                if (planarReach.sqrMagnitude > 0.0001f)
                    planarReach.Normalize();
                Vector3 liftFromSide = loweredLeftPosition +
                    surfaceNormal * 0.10f + planarReach * 0.10f;
                Vector3 approachFromBody = hover + toBody * 0.12f + surfaceNormal * 0.02f;
                path = EvaluateCubicBezier(
                    loweredLeftPosition,
                    liftFromSide,
                    approachFromBody,
                    hover,
                    s);
                rotation = Quaternion.Slerp(loweredLeftRotation, pressRotation, s);
            }
            else if (t < HandEnterContactAtSeconds)
            {
                float s = Smooth01((t - HandEnterHoverAtSeconds) / (HandEnterContactAtSeconds - HandEnterHoverAtSeconds));
                path = Vector3.Lerp(hover, wristContact, s);
                rotation = pressRotation;
            }
            else if (t < HandEnterPressHoldEndSeconds)
            {
                float s = Mathf.Clamp01((t - HandEnterContactAtSeconds) / (HandEnterPressHoldEndSeconds - HandEnterContactAtSeconds));
                path = wristContact + pressAxis * (HandEnterPressDepthMeters * Mathf.Sin(Mathf.PI * s));
                rotation = pressRotation;
            }
            else if (t < HandEnterReleaseEndSeconds)
            {
                float s = Smooth01((t - HandEnterPressHoldEndSeconds) / (HandEnterReleaseEndSeconds - HandEnterPressHoldEndSeconds));
                path = Vector3.Lerp(wristContact, releaseHover, s);
                rotation = pressRotation;
            }
            else if (leverGrip != null && t < HandEnterSweepEndSeconds)
            {
                float s = Smooth01((t - HandEnterReleaseEndSeconds) / (HandEnterSweepEndSeconds - HandEnterReleaseEndSeconds));
                Vector3 sweepEnd = leverGrip.position;
                Vector3 mid = Vector3.Lerp(releaseHover, sweepEnd, 0.5f) +
                    surfaceNormal * HandEnterSweepLiftMeters + toBody * HandEnterSweepPullMeters;
                Vector3 a = Vector3.Lerp(releaseHover, mid, s);
                Vector3 b = Vector3.Lerp(mid, sweepEnd, s);
                path = Vector3.Lerp(a, b, s);
                rotation = Quaternion.Slerp(pressRotation, leverRotation, s);
            }
            else if (leverGrip != null)
            {
                if (_handDriveLeverGripCaptured && _handDriveMovingLever != null)
                {
                    float followScaleBlend = Smooth01(
                        (t - HandEnterSweepEndSeconds) /
                        (HandEnterSettleEndSeconds - HandEnterSweepEndSeconds));
                    float followScale = Mathf.Lerp(
                        1f,
                        Y4NGZPlayerAnimationBridge.FirstPersonLeverFollowMotionScale,
                        followScaleBlend);
                    if (!TryResolveMovingLeverFollowPose(followScale, out path, out rotation))
                    {
                        path = leverGrip.position;
                        rotation = leverRotation;
                    }

                    if (!_handDriveLeverFirstFollowLogged)
                    {
                        _handDriveLeverFirstFollowLogged = true;
                        float positionDelta = _handDriveLeverHandoffPoseCaptured
                            ? Vector3.Distance(_handDriveLeverHandoffPosition, path)
                            : -1f;
                        float rotationDelta = _handDriveLeverHandoffPoseCaptured
                            ? Quaternion.Angle(_handDriveLeverHandoffRotation, rotation)
                            : -1f;
                        SurveillanceBootstrap.Log?.LogInfo(
                            "[LethalCCTV][LeverHandoff] first followed frame: " +
                            $"elapsed={t:F4}s appliedScale={followScale:F4} " +
                            $"handoffDelta={positionDelta:F5}m/{rotationDelta:F3}deg " +
                            $"targetScale={Y4NGZPlayerAnimationBridge.FirstPersonLeverFollowMotionScale:F2} " +
                            $"easeEnd={HandEnterSettleEndSeconds:0.00}s.");
                    }
                }
                else
                {
                    path = leverGrip.position;
                    rotation = leverRotation;
                }
            }
            else
            {
                path = releaseHover;
                rotation = pressRotation;
            }

            if (!_handDriveContactFired && t >= HandEnterContactAtSeconds)
            {
                _handDriveContactFired = true;
                MonitorFocus.CompleteStationFeedFlipAtPressContact();
            }

            _handTraceDriveApplicationCount++;
            try
            {
                target.SetPositionAndRotation(path, rotation);
                if (t >= HandEnterSweepEndSeconds && !_handDriveLeverGripCaptureAttempted)
                    CaptureMovingLeverGrip(target);
            }
            catch (Exception ex)
            {
                _handDriveEnterActive = false;
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Enter hand drive failed: {ex.Message}");
            }
        }

        private void CaptureMovingLeverGrip(Transform handTarget)
        {
            _handDriveLeverGripCaptureAttempted = true;
            Transform movingLever = CCTVOperatorStation.JoystickTiltPivot;
            if (movingLever == null || handTarget == null)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][EnterAnim] moving-lever grip follow unavailable; " +
                    "using the static lever grip hold.");
                return;
            }

            _handDriveMovingLever = movingLever;
            _handDriveLeverGripLocalPosition = movingLever.InverseTransformPoint(handTarget.position);
            _handDriveLeverGripLocalRotation =
                Quaternion.Inverse(movingLever.rotation) * handTarget.rotation;
            _handDriveLeverNeutralGripFrame = movingLever.parent;
            if (_handDriveLeverNeutralGripFrame != null)
            {
                _handDriveLeverNeutralGripFramePosition =
                    _handDriveLeverNeutralGripFrame.InverseTransformPoint(handTarget.position);
                _handDriveLeverNeutralGripFrameRotation =
                    Quaternion.Inverse(_handDriveLeverNeutralGripFrame.rotation) * handTarget.rotation;
            }
            else
            {
                _handDriveLeverNeutralGripFramePosition = handTarget.position;
                _handDriveLeverNeutralGripFrameRotation = handTarget.rotation;
            }
            _handDriveLeverGripCaptured = true;
            _handDriveLeverHandoffPosition = handTarget.position;
            _handDriveLeverHandoffRotation = handTarget.rotation;
            _handDriveLeverHandoffPoseCaptured = true;
            if (TryResolveMovingLeverFollowPose(
                    1f,
                    out Vector3 firstFollowPosition,
                    out Quaternion firstFollowRotation))
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][LeverHandoff] boundary pose: " +
                    $"sweepToFollow={Vector3.Distance(handTarget.position, firstFollowPosition):F6}m/" +
                    $"{Quaternion.Angle(handTarget.rotation, firstFollowRotation):F4}deg " +
                    $"firstScale=1.00 targetScale={Y4NGZPlayerAnimationBridge.FirstPersonLeverFollowMotionScale:F2} " +
                    $"ease={HandEnterSweepEndSeconds:0.00}-{HandEnterSettleEndSeconds:0.00}s.");
            }
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][EnterAnim] moving-lever grip follow captured: " +
                $"lever='{movingLever.name}' localPos={FormatVector(_handDriveLeverGripLocalPosition)} " +
                $"localEuler={FormatVector(_handDriveLeverGripLocalRotation.eulerAngles)} " +
                $"motionScale={Y4NGZPlayerAnimationBridge.FirstPersonLeverFollowMotionScale:F2}.");
        }

        private bool TryResolveMovingLeverFollowPose(
            float followScale,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!_handDriveLeverGripCaptured || _handDriveMovingLever == null)
                return false;

            Vector3 followedPosition =
                _handDriveMovingLever.TransformPoint(_handDriveLeverGripLocalPosition);
            Quaternion followedRotation =
                _handDriveMovingLever.rotation * _handDriveLeverGripLocalRotation;
            ResolveNeutralLeverGripPose(
                out Vector3 neutralGripPosition,
                out Quaternion neutralGripRotation);
            position = neutralGripPosition +
                (followedPosition - neutralGripPosition) * followScale;
            Quaternion neutralToFollowRotation =
                followedRotation * Quaternion.Inverse(neutralGripRotation);
            rotation = Quaternion.SlerpUnclamped(
                Quaternion.identity,
                neutralToFollowRotation,
                followScale) * neutralGripRotation;
            return true;
        }

        private void ResolveNeutralLeverGripPose(
            out Vector3 position,
            out Quaternion rotation)
        {
            if (_handDriveLeverNeutralGripFrame != null)
            {
                position = _handDriveLeverNeutralGripFrame.TransformPoint(
                    _handDriveLeverNeutralGripFramePosition);
                rotation = _handDriveLeverNeutralGripFrame.rotation *
                    _handDriveLeverNeutralGripFrameRotation;
                return;
            }

            position = _handDriveLeverNeutralGripFramePosition;
            rotation = _handDriveLeverNeutralGripFrameRotation;
        }

        /// <summary>
        /// Exit hand choreography: lift clear of the lever, then return to the
        /// designed left-hand rest. When no higher-precedence Blue/Green press
        /// owns the right target, this driver also pins the right-hand rest
        /// until the vanilla controller is restored.
        /// </summary>
        private void ApplyExitHandDrive(bool pinRightHandToRest)
        {
            if (!_exitTriggered)
                return;
            if (!_isLocal || !_controllerApplied)
            {
                _handDriveExitActive = false;
                return;
            }

            Transform target = ResolveFirstPersonLeftHandTarget();
            Transform rightTarget = ResolveFirstPersonRightHandTarget();
            if (!_handDriveExitActive || target == null)
            {
                _handDriveExitActive = false;
                if (pinRightHandToRest)
                    PinRightHandToRestPose(rightTarget);
                return;
            }

            float t = Mathf.Max(0f, Time.unscaledTime - _exitTriggeredAt);
            if (!TryResolveCurrentHandRestPoses(
                    out Vector3 loweredLeftPosition,
                    out Quaternion loweredLeftRotation,
                    out _,
                    out _))
            {
                if (pinRightHandToRest)
                    PinRightHandToRestPose(rightTarget);
                return;
            }
            Vector3 toBody = loweredLeftPosition - _handDriveExitFromPosition;
            Vector3 planarPull = new Vector3(toBody.x, 0f, toBody.z);
            if (planarPull.sqrMagnitude > 0.0001f)
                planarPull = planarPull.normalized * HandExitPullMeters;
            Vector3 liftPoint = _handDriveExitFromPosition + Vector3.up * HandExitLiftMeters + planarPull;
            Vector3 path;
            Quaternion rotation;
            if (t < HandExitLiftEndSeconds)
            {
                float s = Smooth01(t / HandExitLiftEndSeconds);
                path = Vector3.Lerp(_handDriveExitFromPosition, liftPoint, s);
                rotation = Quaternion.Slerp(_handDriveExitFromRotation, loweredLeftRotation, s * 0.45f);
            }
            else if (t < HandExitReturnEndSeconds)
            {
                float s = Smooth01((t - HandExitLiftEndSeconds) / (HandExitReturnEndSeconds - HandExitLiftEndSeconds));
                Vector3 hiddenApproach = loweredLeftPosition + Vector3.up * 0.08f;
                path = EvaluateCubicBezier(liftPoint, liftPoint + planarPull, hiddenApproach, loweredLeftPosition, s);
                rotation = Quaternion.Slerp(_handDriveExitFromRotation, loweredLeftRotation, s);
            }
            else
            {
                path = loweredLeftPosition;
                rotation = loweredLeftRotation;
            }

            try
            {
                target.SetPositionAndRotation(path, rotation);
            }
            catch (Exception ex)
            {
                _handDriveExitActive = false;
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Exit hand drive failed: {ex.Message}");
            }

            if (pinRightHandToRest)
                PinRightHandToRestPose(rightTarget);
        }

        private bool TryResolveCurrentHandRestPoses(
            out Vector3 leftPosition,
            out Quaternion leftRotation,
            out Vector3 rightPosition,
            out Quaternion rightRotation)
        {
            leftPosition = Vector3.zero;
            leftRotation = Quaternion.identity;
            rightPosition = Vector3.zero;
            rightRotation = Quaternion.identity;
            Camera camera = Player != null ? Player.gameplayCamera : null;
            if (camera == null)
                return false;

            // Read-only reuse of ApplyFirstPersonShoulderAnchor's frame contract:
            // flatten the gameplay-camera forward vector to world up, use the
            // same fallback chain, and respect Shoulder Anchor Ignore Camera Pitch.
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
                flattenedForward = Player.transform.forward;
            if (flattenedForward.sqrMagnitude < 0.0001f)
                flattenedForward = Vector3.forward;

            Quaternion yawFlattenedCameraRotation = Quaternion.LookRotation(
                flattenedForward.normalized,
                Vector3.up);
            Quaternion restFrameRotation =
                Y4NGZPlayerAnimationBridge.ShoulderAnchorIgnoresCameraPitch
                    ? yawFlattenedCameraRotation
                    : cameraTransform.rotation;
            leftPosition = cameraTransform.position + restFrameRotation *
                Y4NGZPlayerAnimationBridge.FirstPersonLeftHandRestOffset;
            leftRotation = restFrameRotation * Quaternion.Euler(
                Y4NGZPlayerAnimationBridge.FirstPersonLeftHandRestEuler);
            rightPosition = cameraTransform.position + restFrameRotation *
                Y4NGZPlayerAnimationBridge.FirstPersonRightHandRestOffset;
            rightRotation = restFrameRotation * Quaternion.Euler(
                Y4NGZPlayerAnimationBridge.FirstPersonRightHandRestEuler);
            return true;
        }

        private void PinRightHandToRestPose(Transform rightTarget)
        {
            if (rightTarget == null || !TryResolveCurrentHandRestPoses(
                    out Vector3 leftPosition,
                    out _,
                    out Vector3 rightPosition,
                    out Quaternion rightRotation))
            {
                return;
            }

            try
            {
                rightTarget.SetPositionAndRotation(rightPosition, rightRotation);
                _firstPersonRightHandTuningApplication = default;
                _handTraceDriveApplicationCount++;
                if (!_handRestFirstPinLogged)
                {
                    _handRestFirstPinLogged = true;
                    Camera camera = Player != null ? Player.gameplayCamera : null;
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV][EnterAnim] designed hand rests first pin: " +
                        $"left.worldPos={FormatTraceVector(leftPosition)} " +
                        $"left.viewport={FormatTraceViewport(leftPosition, camera)} " +
                        $"right.worldPos={FormatTraceVector(rightPosition)} " +
                        $"right.viewport={FormatTraceViewport(rightPosition, camera)}.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug(
                    $"[LethalCCTV] Right-hand rest pin failed: {ex.Message}");
            }
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
        }

    }
}
