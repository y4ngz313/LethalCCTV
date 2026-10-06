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

        private void ApplyInteractionsApiArmsRootStationPin() => ApplyOperatorPose();

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

            bool controlHoldActive = _active && !_windingDown;
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
                // The wider operating frame must accommodate the button hand too.
                // Moving a shared shoulder frame for the left chain alone could
                // leave the visible right wrist short of every button target.
                ManualArmIkChain rightChain = _manualRightArmIk;
                Transform actionTarget = _apiArmsRootStationPinRightTarget;
                if (MonitorFocus.IsEnterCameraPathComplete && IsManualArmIkChainAvailable(rightChain) && actionTarget != null)
                {
                    Vector3 toRight = actionTarget.position - rightChain.Root.position;
                    float rightReach = (rightChain.UpperLength + rightChain.LowerLength) * ApiReachAssistUsableReachFraction;
                    float rightShortfall = Mathf.Min(toRight.magnitude - rightReach, ApiLeverControlReachAssistMaxMeters);
                    if (rightShortfall > reachAssistGoal)
                    {
                        reachAssistGoal = rightShortfall;
                        reachAssistDirection = toRight.normalized;
                    }
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
            // Use the final camera frame: a yaw-only anchor exposes shoulder
            // ends whenever entry starts while looking down at the access button.
            // At the 9-degree operating pitch this is the calibrated station
            // offset (0,-0.80,0.60), without a phase switch at arrival.
            Vector3 cameraTarget = cameraTransform.position +
                cameraTransform.rotation * Y4NGZPlayerAnimationBridge.ApiCameraAnchorOffset;

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
            // Preserve the authored one-based sample timestamps, interpolate between them.
            float sample = CctvIntroTiming.Sample(elapsed, ApiAuthoredTrajectoryFps, table.Length);
            int low = Mathf.FloorToInt(sample);
            int high = Mathf.Min(low + 1, table.Length - 1);
            _apiTrajectoryFrame = sample + 1f;
            pose = new AuthoredLeftHandStationPose(
                Vector3.Lerp(table[low].Position, table[high].Position, sample - low),
                Quaternion.Slerp(table[low].Rotation, table[high].Rotation, sample - low));
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
            float pathClock = _isLocal ? MonitorFocus.EnterPresentationClockSeconds : -1f;
            if (pathClock >= 0f)
                return CctvIntroTiming.ClipSeconds(pathClock, CCTVOperatorInteractionsBridge.EnterClipLengthSeconds);
            return CctvIntroTiming.ClipSeconds(Time.unscaledTime - _interactionsApiStartedAt, CCTVOperatorInteractionsBridge.EnterClipLengthSeconds);
        }

        internal void EvaluateApiEnterAnimatorFromCameraClock(float presentationSeconds, bool complete)
        {
            if (!_interactionsApiMode || !_isLocal || !_apiEnterSpeedOwned || _animator == null || _windingDown) return;
            float seconds = Mathf.Min(CCTVOperatorInteractionsBridge.EnterClipLengthSeconds,
                CctvIntroTiming.ClipSeconds(presentationSeconds, CCTVOperatorInteractionsBridge.EnterClipLengthSeconds));
            float delta = Mathf.Max(0f, seconds - _apiEnterEvaluatedClipSeconds);
            try
            {
                _animator.speed = 1f;
                _animator.Update(delta);
                _apiEnterEvaluatedClipSeconds = seconds;
            }
            catch (Exception ex)
            {
                RestoreApiEnterAnimatorSpeed();
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Entry animator clock failed; restored automatic playback: {ex.Message}");
            }
            finally
            {
                if (_apiEnterSpeedOwned) _animator.speed = 0f;
            }
            if (complete) RestoreApiEnterAnimatorSpeed();
        }

        private Quaternion ResolveSolvedLeftHandRotation(Quaternion target)
            => target * (_manualLeftArmIk != null ? _manualLeftArmIk.TargetToTipRotation : Quaternion.identity);

        private Vector3 ResolveEnterIndexTipOffset()
        {
            Transform hand = _manualLeftArmIk?.Tip;
            if (hand == null) return ApiEnterPressLocalIndexTipOffset;
            if (_apiEnterIndexTip == null)
                _apiEnterIndexTip = FindChildRecursive(hand, "finger2.L.001_end");
            if (_apiEnterIndexTip == null) return ApiEnterPressLocalIndexTipOffset;
            // Rotation-local, including the rig's actual scale. InverseTransformPoint
            // would divide that scale out and miss on scaled player models.
            return Quaternion.Inverse(hand.rotation) * (_apiEnterIndexTip.position - hand.position);
        }

        private Quaternion ResolveEnterPressRotation(Transform station, Quaternion authored)
        {
            if (!Y4NGZPlayerAnimationBridge.TryResolveAccessButtonPressSurface(out _, out Vector3 normal)) return authored;
            normal.Normalize();
            if (!_apiEnterPressRotationResolved)
            {
                Quaternion hand = ResolveSolvedLeftHandRotation(authored);
                Vector3 finger = hand * ResolveEnterIndexTipOffset();
                Vector3 forward = Vector3.ProjectOnPlane(finger, normal).normalized;
                if (forward.sqrMagnitude < 0.5f) return authored;
                // Pitch only, around the across-finger axis. Preserve the authored
                // hand's roll and heading instead of re-aiming all three axes.
                Vector3 axis = Vector3.Cross(normal, forward).normalized;
                Vector3 desired = forward * 0.8f - normal * 0.6f;
                float angle = Vector3.SignedAngle(finger, desired, axis);
                Quaternion press = Quaternion.AngleAxis(angle, axis) * authored;
                _apiEnterPressRotationInStation = Quaternion.Inverse(station.rotation) * press;
                _apiEnterPressRotationResolved = true;
            }
            float release = Smooth01(Mathf.InverseLerp(13f, 21f, _apiTrajectoryFrame));
            return Quaternion.Slerp(station.rotation * _apiEnterPressRotationInStation, authored, release);
        }

        private Vector3 ApplyEnterSurfaceClearance(Vector3 wrist, Quaternion rotation)
        {
            if (!Y4NGZPlayerAnimationBridge.TryResolveAccessButtonPressSurface(out Vector3 cap, out Vector3 normal))
                return wrist;
            normal.Normalize();
            // Cover approach, press and withdrawal. The cap is above the desk:
            // keeping the palm and wrist above this plane also clears its support.
            float envelope = Smooth01(Mathf.Clamp01((_apiTrajectoryFrame - 3f) / 3f)) *
                (1f - Smooth01(Mathf.Clamp01((_apiTrajectoryFrame - 19f) / 4f)));
            Quaternion handRotation = ResolveSolvedLeftHandRotation(rotation);
            Vector3 tip = wrist + handRotation * ResolveEnterIndexTipOffset();
            Vector3 palm = wrist + handRotation * ResolveApiLeverPalmLocalOffset();
            float lift = Mathf.Max(0f, 0.003f - Vector3.Dot(tip - cap, normal));
            lift = Mathf.Max(lift, 0.018f - Vector3.Dot(palm - cap, normal));
            lift = Mathf.Max(lift, 0.035f - Vector3.Dot(wrist - cap, normal));
            return wrist + normal * (lift * envelope);
        }

        private static float ComputeEnterPressContactWeight(float frame)
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
        private static float ComputeEnterLeverContactWeight(float frame)
        {
            return Smooth01(Mathf.Clamp01(
                (frame - (ApiEnterLeverContactFrameStart - ApiEnterLeverContactRampFrames)) /
                ApiEnterLeverContactRampFrames));
        }

        private static float ComputeExitLeverContactWeight(float frame)
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
                liveGrip + CCTVOperatorStation.LeverGripUp * ApiLeverGripPalmOffsetMeters;
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
                liveGrip + CCTVOperatorStation.LeverGripUp * ApiLeverGripPalmOffsetMeters;
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
                _apiTrajectoryFrame < ApiEnterPressTelemetryFrame)
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
            // Measure the fingertip at contact after IK. Wrist distance alone is
            // not a contact test, and frame 15 is already the withdrawal phase.
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
                $"solvedWrist={tipTrace} " +
                $"indexToButton={(_apiEnterIndexTip != null ? Vector3.Distance(_apiEnterIndexTip.position, liveButton).ToString("F3") : "missing")}m.");
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

            _apiArmsRootStationPinLeftTarget = null;
            _apiArmsRootStationPinRightTarget = null;
            _apiArmsRootStationPinActive = false;
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
