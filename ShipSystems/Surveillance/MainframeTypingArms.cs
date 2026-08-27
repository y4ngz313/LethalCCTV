using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed class MainframeTypingArms
    {
        private static readonly Vector3 HandHomeLocalOffset = new Vector3(0f, 0.05f, -0.22f);

        // Displace the pinned FP arms rig from its vanilla camera-relative pose so the arms read as
        // outstretched-level instead of raking up from a low root (LC hangs the arms off the low
        // session camera). Lift = world-up; BackShift = toward the player along the mainframe front
        // axis, which straightens the over-bent arm so its elbow rises out of the lower frame.
        private const float ArmRigVerticalLift = 0.25f;
        private const float ArmRigBackShift = 0.20f;

        private const float CameraApproachDuration = 0.52f;
        private const float CameraGlanceDuration = 1.10f;
        private const float CameraFocusBlendDuration = 0.90f;
        private const float CameraGlanceEndSeconds = CameraApproachDuration + CameraGlanceDuration;
        private const float CameraFocusBlendEndSeconds = CameraGlanceEndSeconds + CameraFocusBlendDuration;
        private const int MainframeArmsDiagPhaseNone = -1;
        private const int MainframeArmsDiagPhaseApproach = 0;
        private const int MainframeArmsDiagPhaseGlance = 1;
        private const int MainframeArmsDiagPhaseFocusBlend = 2;
        private const int MainframeArmsDiagPhaseFocus = 3;
        private const float BobGainRampSeconds = 0.35f;
        private const float HandPitchDegrees = 2f;
        private const float HandYawInwardDegrees = 8f;
        private const float HandRollThumbUpDegrees = 6f;
        private const float HandLiftAmplitude = 0.010f;
        private const float HandLiftHz = 0.9f;
        private const float WanderAmplitude = 0.030f;
        private const float WanderHz = 0.5f;
        private const float TapDipDepth = 0.004f;
        private const float HandStrikeDipMin = 0.025f;
        private const float HandStrikeDipMax = 0.035f;
        private const float HandStrikeForwardDepth = 0.018f;
        private const float HandStrikeLateralMax = 0.020f;
        private const float HandDriftRelaxMetersPerSecond = 0.005f;
        private const float KeyFieldHalfWidth = 0.28f;
        private const float MaxReachFraction = 0.92f;
        private const float IntroBurstEndSeconds = CameraApproachDuration + 1.05f;
        private const float FingerBaseCurlDegrees = 22f;
        private const float FingerBaseCurlMidDegrees = 18f;
        private const float FingerLiftDegrees = 14f;
        private const float FingerMidLiftBendDegrees = 10f;
        private const float FingerPressDegrees = 10f;
        private const float ThumbCurlScale = 0.3f;
        private const float KeystrokeBaseDuration = 0.16f;
        private const int FabrikIterations = 15;
        private const float FabrikTolerance = 0.0001f;

        private readonly Transform _mainframeRoot;
        private readonly List<BoneRestPose> _boneRest = new List<BoneRestPose>();
        private readonly List<FingerChain> _leftFingers = new List<FingerChain>();
        private readonly List<FingerChain> _rightFingers = new List<FingerChain>();
        private readonly List<FingerChain> _allFingers = new List<FingerChain>();
        private readonly List<int> _leftThumbFingerIndices = new List<int>();
        private readonly List<int> _rightThumbFingerIndices = new List<int>();
        private readonly List<int> _leftNonThumbFingerIndices = new List<int>();
        private readonly List<int> _rightNonThumbFingerIndices = new List<int>();
        private readonly List<int> _nonThumbFingerIndices = new List<int>();

        private PlayerControllerB _player;
        private Transform _armsRoot;
        private Transform _armsMetarig;
        private bool _armsRigPoseCaptured;
        private bool _armsMetarigPoseCaptured;
        private bool _armsCameraPinningEnabled;
        private Vector3 _armsRigRestLocalPosition;
        private Quaternion _armsRigRestLocalRotation;
        private Vector3 _armsRigCameraRelativePosition;
        private Quaternion _armsRigCameraRelativeRotation;
        private Vector3 _armsMetarigRestLocalPosition;
        private Quaternion _armsMetarigRestLocalRotation;
        private Vector3 _armsMetarigCameraRelativePosition;
        private Quaternion _armsMetarigCameraRelativeRotation;
        private Transform _leftHome;
        private Transform _rightHome;
        private ArmChain _left;
        private ArmChain _right;
        private SkinnedMeshRenderer _armsRenderer;
        private bool _armsRendererStateCaptured;
        private bool _savedArmsRendererEnabled;
        private bool _begun;
        private bool _disabled;
        private bool _missingWarningLogged;
        private bool _runtimeFaulted;
        private bool _runtimeFaultLogged;
        private bool _inputWarningLogged;
        private float _startedAt;
        private float _lastLateSessionTime = -1f;
        private int _lastLoggedMainframeArmsDiagPhase = MainframeArmsDiagPhaseNone;
        private int _nextThumbHand;
        private int _lastStrikeFrame = -1;
        private readonly System.Random _runtimeRng = new System.Random();
        private float _nextIntroBurstTime;
        private int _introBurstTargetCount;
        private int _introBurstCount;
        private int _lastIntroFingerIndex = -1;
        private bool _introBurstComplete;
        private bool _introSpaceStrokePlayed;
        private int _strokeSerial;

        internal MainframeTypingArms(Transform mainframeRoot)
        {
            _mainframeRoot = mainframeRoot;
        }

        internal bool Begin(PlayerControllerB player)
        {
            if (_disabled)
                return false;

            _player = player;
            _startedAt = Time.unscaledTime;
            _lastLateSessionTime = -1f;
            _lastLoggedMainframeArmsDiagPhase = MainframeArmsDiagPhaseNone;
            _nextThumbHand = 0;
            _nextIntroBurstTime = CameraApproachDuration + Range(_runtimeRng, 0.02f, 0.08f);
            _introBurstTargetCount = 8 + _runtimeRng.Next(0, 5);
            _introBurstCount = 0;
            _lastIntroFingerIndex = -1;
            _introBurstComplete = false;
            _introSpaceStrokePlayed = false;
            _strokeSerial = 0;

            try
            {
                List<string> missing = new List<string>();
                if (player == null)
                    AddMissing(missing, "player");

                Transform searchRoot = ResolveArmsSearchRoot(player);
                if (searchRoot == null)
                    AddMissing(missing, "localArmsTransform/gameplayCamera.root");

                if (_mainframeRoot == null)
                    AddMissing(missing, "mainframe root");

                Transform leftUpper = Require(searchRoot, "arm.L_upper", missing);
                Transform leftLower = Require(searchRoot, "arm.L_lower", missing);
                Transform leftHand = Require(searchRoot, "hand.L", missing);
                Transform leftTarget = Require(searchRoot, "ArmsLeftArm_target", missing);
                Transform rightUpper = Require(searchRoot, "arm.R_upper", missing);
                Transform rightLower = Require(searchRoot, "arm.R_lower", missing);
                Transform rightHand = Require(searchRoot, "hand.R", missing);
                Transform rightTarget = Require(searchRoot, "ArmsRightArm_target", missing);
                Transform leftHome = Require(_mainframeRoot, "MainframeLeftHandHome", missing);
                Transform rightHome = Require(_mainframeRoot, "MainframeRightHandHome", missing);

                if (leftTarget != null && leftTarget.parent == null)
                    AddMissing(missing, "ArmsLeftArm_target.parent");
                if (rightTarget != null && rightTarget.parent == null)
                    AddMissing(missing, "ArmsRightArm_target.parent");

                if (missing.Count > 0)
                {
                    DisableWithMissing(missing);
                    return false;
                }

                _armsRoot = searchRoot;
                _leftHome = leftHome;
                _rightHome = rightHome;
                _left = new ArmChain
                {
                    Upper = leftUpper,
                    Lower = leftLower,
                    Hand = leftHand,
                    Target = leftTarget,
                };
                _right = new ArmChain
                {
                    Upper = rightUpper,
                    Lower = rightLower,
                    Hand = rightHand,
                    Target = rightTarget,
                };

                CaptureCameraRelativeArmsPose();
                CaptureRendererState(player);
                CaptureTargetRest(_left);
                CaptureTargetRest(_right);
                CaptureBoneRest(_left.Upper);
                CaptureBoneRest(_right.Upper);
                CaptureChainLength(_left);
                CaptureChainLength(_right);

                // Forearm roll-carrier constants (wrist candy-wrap fix, same idea as the tablet
                // baker's arm-FK carrier): reproduce the rest hand-in-forearm relationship at the
                // target rotation so the hand's roll lands in the forearm sleeve, not the wrist.
                CaptureForearmRollCarrier(_left);
                CaptureForearmRollCarrier(_right);
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeArms] Chain lengths: left={_left.ChainLength:0.000}m, right={_right.ChainLength:0.000}m, clamp={MaxReachFraction:0.00}.");
                TryLogBeginDiagnostics();

                CollectFingers(_left.Hand, 1000, _leftFingers);
                CollectFingers(_right.Hand, 2000, _rightFingers);
                RegisterFingerIndices();

                RestoreBoneRestSnapshot();
                DeriveFingerCurlAxes(_left.Hand, _leftFingers);
                DeriveFingerCurlAxes(_right.Hand, _rightFingers);

                _begun = true;
                return true;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Begin failed; procedural typing arms disabled: {ex.Message}");
                RestoreAfterBeginFailure();
                ClearState();
                _disabled = true;
                return false;
            }
        }

        internal void Tick()
        {
            if (!_begun || _disabled || _runtimeFaulted)
                return;

            float sessionTime = Time.unscaledTime - _startedAt;

            try
            {
                DriveIntroBurst(sessionTime);

                if (sessionTime <= CameraApproachDuration)
                    return;

                Keyboard keyboard = Keyboard.current;
                if (keyboard == null || !keyboard.anyKey.wasPressedThisFrame)
                    return;

                for (int i = 0; i < keyboard.allKeys.Count; i++)
                {
                    KeyControl key = keyboard.allKeys[i];
                    if (key == null || !key.wasPressedThisFrame)
                        continue;

                    if (key.keyCode == Key.Escape)
                        continue;

                    if (IsEnterKey(key))
                    {
                        PlayRightThumbKeystroke(1.3f, 0.22f, StrikeStyle.Enter);
                        return;
                    }

                    if (IsSpaceKey(key))
                    {
                        PlayAlternatingThumbKeystroke(1.05f, RandomKeystrokeDuration(), StrikeStyle.Thumb);
                        return;
                    }

                    if (IsArrowKey(key))
                    {
                        PlayArrowKeystroke(key);
                        return;
                    }

                    if (_nonThumbFingerIndices.Count <= 0)
                        return;

                    int hash = (int)key.keyCode;
                    int nonThumbIndex = (hash & int.MaxValue) % _nonThumbFingerIndices.Count;
                    PlayDeliberateKeystroke(_nonThumbFingerIndices[nonThumbIndex], Range(_runtimeRng, 0.90f, 1.15f));
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!_inputWarningLogged)
                {
                    _inputWarningLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Keyboard keystroke probe failed: {ex.Message}");
                }
            }
        }

        internal void LateTick(float sessionTime)
        {
            if (!_begun || _disabled || _runtimeFaulted)
                return;

            float deltaTime = 0f;
            if (_lastLateSessionTime >= 0f)
                deltaTime = Mathf.Max(0f, sessionTime - _lastLateSessionTime);
            _lastLateSessionTime = sessionTime;

            try
            {
                PinArmsToGameplayCamera();
                RestoreBoneRestSnapshot();

                AdvanceHandMotion(_left, deltaTime);
                AdvanceHandMotion(_right, deltaTime);

                float extend = Smooth(Mathf.Clamp01(sessionTime / CameraApproachDuration));
                float typingTime = sessionTime - CameraApproachDuration;
                float bobGain = Mathf.Clamp01(typingTime / BobGainRampSeconds) * (typingTime > 0f ? 1f : 0f);

                PoseTarget(_left, _leftHome, ComputeHandGoalRotation(_leftHome, thumbLocalX: -1f),
                    extend, typingTime, bobGain, 0f, GetHandTapDip(_leftFingers));
                PoseTarget(_right, _rightHome, ComputeHandGoalRotation(_rightHome, thumbLocalX: +1f),
                    extend, typingTime, bobGain, 2.3f, GetHandTapDip(_rightFingers));

                SolveChainIk(_left);
                SolveChainIk(_right);
                TryLogLatePhaseDiagnostics(sessionTime);

                ApplyFingerTyping(_leftFingers, extend);
                ApplyFingerTyping(_rightFingers, extend);
                AdvanceKeystrokes(_leftFingers, deltaTime);
                AdvanceKeystrokes(_rightFingers, deltaTime);
            }
            catch (Exception ex)
            {
                _runtimeFaulted = true;
                if (!_runtimeFaultLogged)
                {
                    _runtimeFaultLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] LateTick failed; procedural typing arms disabled for this session: {ex.Message}");
                }
            }
        }

        internal void Stop(string reason)
        {
            if (!_begun && !_armsRendererStateCaptured && _boneRest.Count <= 0 && !_armsRigPoseCaptured && !_armsMetarigPoseCaptured)
                return;

            try
            {
                RestoreBoneRestSnapshot();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Failed to restore arm bone rest on stop '{reason ?? "unspecified"}': {ex.Message}");
            }

            try
            {
                RestoreTargetPose(_left);
                RestoreTargetPose(_right);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Failed to restore IK targets on stop '{reason ?? "unspecified"}': {ex.Message}");
            }

            try
            {
                RestorePinnedArmsLocalPose();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Failed to restore local arms rig pose on stop '{reason ?? "unspecified"}': {ex.Message}");
            }

            try
            {
                RestoreRendererState();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Failed to restore local arms renderer on stop '{reason ?? "unspecified"}': {ex.Message}");
            }

            ClearState();
        }

        /// <summary>Semantic keystroke from the screen controller (overlay-accepted inputs) routes
        /// onto the same visible strike machinery as raw keyboard input.</summary>
        internal void PlaySemanticKeystroke(int fingerIndex, float strength)
        {
            if (!_begun || _disabled || _runtimeFaulted || _nonThumbFingerIndices.Count <= 0)
                return;

            if (strength >= 1.25f)
            {
                PlayRightThumbKeystroke(1.3f, 0.22f, StrikeStyle.Enter);
                return;
            }

            int index = ((fingerIndex % _nonThumbFingerIndices.Count) + _nonThumbFingerIndices.Count)
                % _nonThumbFingerIndices.Count;
            PlayDeliberateKeystroke(_nonThumbFingerIndices[index], strength);
        }

        private void DriveIntroBurst(float sessionTime)
        {
            if (_introBurstComplete || sessionTime < CameraApproachDuration)
                return;

            if (sessionTime > IntroBurstEndSeconds && _introBurstCount >= _introBurstTargetCount)
            {
                _introBurstComplete = true;
                return;
            }

            if (sessionTime < _nextIntroBurstTime)
                return;

            bool shouldSpace = !_introSpaceStrokePlayed &&
                (_introBurstCount >= Mathf.Max(1, _introBurstTargetCount - 2) || sessionTime >= IntroBurstEndSeconds - 0.18f);

            if (shouldSpace)
            {
                _introSpaceStrokePlayed = true;
                PlayAlternatingThumbKeystroke(Range(_runtimeRng, 0.95f, 1.20f), Range(_runtimeRng, 0.15f, 0.20f), StrikeStyle.Thumb);
            }
            else if (_nonThumbFingerIndices.Count > 0)
            {
                int selected = PickIntroNonThumbFinger();
                PlayDeliberateKeystroke(selected, Range(_runtimeRng, 0.85f, 1.25f), RandomKeystrokeDuration(), StrikeStyle.Normal, Range(_runtimeRng, -HandStrikeLateralMax, HandStrikeLateralMax));
            }

            _introBurstCount++;
            if (_introBurstCount >= _introBurstTargetCount && _introSpaceStrokePlayed)
                _introBurstComplete = true;

            _nextIntroBurstTime = sessionTime + Range(_runtimeRng, 0.06f, 0.14f);
        }

        private int PickIntroNonThumbFinger()
        {
            if (_nonThumbFingerIndices.Count <= 0)
                return -1;

            int selected = _nonThumbFingerIndices[_runtimeRng.Next(0, _nonThumbFingerIndices.Count)];
            for (int i = 0; i < 8 && selected == _lastIntroFingerIndex && _nonThumbFingerIndices.Count > 1; i++)
                selected = _nonThumbFingerIndices[_runtimeRng.Next(0, _nonThumbFingerIndices.Count)];

            _lastIntroFingerIndex = selected;
            return selected;
        }

        private bool PlayDeliberateKeystroke(int fingerIndex, float strength)
        {
            return PlayDeliberateKeystroke(fingerIndex, strength, RandomKeystrokeDuration(), StrikeStyle.Normal, Range(_runtimeRng, -HandStrikeLateralMax, HandStrikeLateralMax));
        }

        private bool PlayDeliberateKeystroke(int fingerIndex, float strength, float duration, StrikeStyle style, float lateralDrift)
        {
            if (fingerIndex < 0 || fingerIndex >= _allFingers.Count)
                return false;

            // One strike per frame: the keyboard poll and the screen controller's semantic
            // hook can both observe the same key press.
            if (_lastStrikeFrame == Time.frameCount)
                return false;

            FingerChain finger = _allFingers[fingerIndex];
            if (finger == null)
                return false;

            _lastStrikeFrame = Time.frameCount;
            finger.KeystrokeElapsed = 0f;
            finger.KeystrokeStrength = Mathf.Max(0f, strength);
            finger.KeystrokeDuration = Mathf.Max(0.08f, duration);
            BeginHandStrike(finger, strength, finger.KeystrokeDuration, style, lateralDrift);

            // Audible keystroke click for every registered strike — covers both the load-in
            // ambient typing and live semantic/arrow input. Never affects arm behavior.
            MainframeAudio.Current?.PlayTyping();
            return true;
        }

        private void BeginHandStrike(FingerChain finger, float strength, float duration, StrikeStyle style, float lateralDrift)
        {
            ArmChain arm = finger != null ? finger.Arm : null;
            Transform home = ResolveHomeForArm(arm);
            if (arm == null || home == null || _mainframeRoot == null)
                return;

            float homeLocalX = _mainframeRoot.InverseTransformPoint(home.position).x;
            float side = Mathf.Sign(homeLocalX);
            if (Mathf.Abs(side) < 0.5f)
                side = ReferenceEquals(arm, _left) ? 1f : -1f;

            float desiredLocalX = homeLocalX + arm.KeyDriftX + lateralDrift;
            if (style == StrikeStyle.Arrow)
                desiredLocalX = side * KeyFieldHalfWidth * 0.88f;

            desiredLocalX = Mathf.Clamp(desiredLocalX, -KeyFieldHalfWidth, KeyFieldHalfWidth);
            if (side > 0f)
                desiredLocalX = Mathf.Max(0.035f, desiredLocalX);
            else
                desiredLocalX = Mathf.Min(-0.035f, desiredLocalX);

            arm.KeyDriftX = desiredLocalX - homeLocalX;
            arm.StrikeElapsed = 0f;
            arm.StrikeDuration = Mathf.Max(0.08f, duration);
            arm.StrikeStrength = Mathf.Max(0f, strength);
            arm.StrikeDip = Range(_runtimeRng, HandStrikeDipMin, HandStrikeDipMax) * arm.StrikeStrength;
            arm.StrikeForward = HandStrikeForwardDepth * Mathf.Clamp(arm.StrikeStrength, 0.6f, 1.4f);

            if (style == StrikeStyle.Enter)
            {
                arm.StrikeDip *= 1.25f;
                arm.StrikeForward *= 1.2f;
            }
            else if (style == StrikeStyle.Arrow)
            {
                arm.StrikeDip *= 0.72f;
                arm.StrikeForward *= 0.7f;
            }

            _strokeSerial++;
            arm.WristRollAccentDegrees = (_strokeSerial % 4 == 0)
                ? Range(_runtimeRng, 2.0f, 3.0f) * (side > 0f ? 1f : -1f)
                : 0f;
        }

        private void PlayAlternatingThumbKeystroke(float strength, float duration, StrikeStyle style)
        {
            int selected = -1;
            if ((_nextThumbHand & 1) == 0)
                selected = FirstIndexOrFallback(_leftThumbFingerIndices, _rightThumbFingerIndices);
            else
                selected = FirstIndexOrFallback(_rightThumbFingerIndices, _leftThumbFingerIndices);

            _nextThumbHand++;
            if (selected >= 0)
                PlayDeliberateKeystroke(selected, strength, duration, style, Range(_runtimeRng, -0.010f, 0.010f));
        }

        private void PlayRightThumbKeystroke(float strength, float duration, StrikeStyle style)
        {
            int selected = FirstIndexOrFallback(_rightThumbFingerIndices, _rightNonThumbFingerIndices);
            if (selected < 0)
                selected = FirstIndexOrFallback(_leftThumbFingerIndices, _nonThumbFingerIndices);

            if (selected >= 0)
                PlayDeliberateKeystroke(selected, strength, duration, style, -HandStrikeLateralMax * 0.35f);
        }

        private void PlayArrowKeystroke(KeyControl key)
        {
            List<int> pool = _rightNonThumbFingerIndices.Count > 0 ? _rightNonThumbFingerIndices : _rightThumbFingerIndices;
            if (pool == null || pool.Count <= 0)
                pool = _nonThumbFingerIndices;

            if (pool == null || pool.Count <= 0)
                return;

            int hash = key != null ? (int)key.keyCode : _runtimeRng.Next();
            int selected = pool[(hash & int.MaxValue) % pool.Count];
            PlayDeliberateKeystroke(selected, 0.78f, 0.12f, StrikeStyle.Arrow, -HandStrikeLateralMax);
        }

        private float RandomKeystrokeDuration()
        {
            return Range(_runtimeRng, 0.13f, 0.20f);
        }

        private static int FirstIndexOrFallback(List<int> preferred, List<int> fallback)
        {
            if (preferred != null && preferred.Count > 0)
                return preferred[0];
            if (fallback != null && fallback.Count > 0)
                return fallback[0];
            return -1;
        }

        private static bool IsSpaceKey(KeyControl key)
        {
            return key != null && key.keyCode == Key.Space;
        }

        private static bool IsEnterKey(KeyControl key)
        {
            return key != null && (key.keyCode == Key.Enter || key.keyCode == Key.NumpadEnter);
        }

        private static bool IsArrowKey(KeyControl key)
        {
            if (key == null)
                return false;

            return key.keyCode == Key.UpArrow ||
                key.keyCode == Key.DownArrow ||
                key.keyCode == Key.LeftArrow ||
                key.keyCode == Key.RightArrow;
        }

        private Transform ResolveHomeForArm(ArmChain arm)
        {
            if (ReferenceEquals(arm, _left))
                return _leftHome;
            if (ReferenceEquals(arm, _right))
                return _rightHome;
            return null;
        }

        private enum StrikeStyle
        {
            Normal,
            Thumb,
            Enter,
            Arrow
        }

        private void PoseTarget(
            ArmChain arm,
            Transform home,
            Quaternion goalRot,
            float extend,
            float typingTime,
            float bobGain,
            float phase,
            float tapDip)
        {
            if (arm == null || arm.Target == null || arm.Target.parent == null || home == null)
                return;

            Transform targetParent = arm.Target.parent;
            Vector3 restWorld = targetParent.TransformPoint(arm.RestTargetLocalPosition);
            Quaternion restRotWorld = targetParent.rotation * arm.RestTargetLocalRotation;

            Vector3 bob = Vector3.zero;
            if (bobGain > 0f)
            {
                // Two incommensurate sines per axis so the drift never reads as a metronome.
                float t = typingTime;
                float lift = HandLiftAmplitude *
                    (0.6f * Mathf.Sin(2f * Mathf.PI * HandLiftHz * t + phase)
                   + 0.4f * Mathf.Sin(2f * Mathf.PI * HandLiftHz * 1.73f * t + phase * 2.1f));
                float wander = WanderAmplitude *
                    (0.6f * Mathf.Sin(2f * Mathf.PI * WanderHz * t + phase * 1.7f)
                   + 0.4f * Mathf.Sin(2f * Mathf.PI * WanderHz * 1.31f * t + phase * 0.6f));
                float dip = TapDipDepth * Mathf.Clamp01(tapDip);
                bob += Vector3.up * (lift * bobGain) + Vector3.down * (dip * dip / Mathf.Max(0.0001f, TapDipDepth) * bobGain);
                bob += _mainframeRoot.right * (wander * bobGain);
            }

            bob += GetHandStrikeOffset(arm, extend);
            Vector3 unclampedGoalPos = home.position + _mainframeRoot.rotation * HandHomeLocalOffset + bob;
            Vector3 goalPos = ClampGoalToReach(arm, unclampedGoalPos);
            arm.LastUnclampedGoal = unclampedGoalPos;
            arm.LastClampedGoal = goalPos;

            // Quadratic Bezier approach: pull the control point down to typing height early so
            // the hands travel IN toward the keyboard, not down onto it.
            Vector3 mid = Vector3.Lerp(restWorld, goalPos, 0.5f);
            mid.y = Mathf.Min(restWorld.y, goalPos.y) - 0.02f;
            Vector3 a = Vector3.Lerp(restWorld, mid, extend);
            Vector3 b = Vector3.Lerp(mid, goalPos, extend);
            arm.Target.position = Vector3.Lerp(a, b, extend);

            Quaternion finalGoalRot = goalRot;
            float strikeK = GetHandStrikeEnvelope(arm);
            if (strikeK > 0f && Mathf.Abs(arm.WristRollAccentDegrees) > 0.001f)
                finalGoalRot = Quaternion.AngleAxis(arm.WristRollAccentDegrees * strikeK, _mainframeRoot.forward) * finalGoalRot;

            arm.Target.rotation = Quaternion.Slerp(restRotWorld, finalGoalRot, extend);
        }

        private Vector3 GetHandStrikeOffset(ArmChain arm, float extend)
        {
            if (arm == null || _mainframeRoot == null)
                return Vector3.zero;

            Vector3 offset = _mainframeRoot.right * arm.KeyDriftX;
            float strikeK = GetHandStrikeEnvelope(arm);
            if (strikeK > 0f)
            {
                offset += Vector3.down * (arm.StrikeDip * strikeK);
                offset += _mainframeRoot.forward * (arm.StrikeForward * strikeK);
            }

            return offset * Mathf.Clamp01(extend);
        }

        private static Vector3 ClampGoalToReach(ArmChain arm, Vector3 goalPos)
        {
            if (arm == null || arm.Upper == null || arm.ChainLength <= 0.05f)
                return goalPos;

            Vector3 root = arm.Upper.position;
            Vector3 offset = goalPos - root;
            float distance = offset.magnitude;
            float maxDistance = arm.ChainLength * MaxReachFraction;
            if (distance <= maxDistance || distance <= 0.0001f)
                return goalPos;

            float dy = goalPos.y - root.y;
            if (Mathf.Abs(dy) >= maxDistance * 0.98f)
                return root + offset * (maxDistance / distance);

            Vector3 horizontal = offset;
            horizontal.y = 0f;
            float horizontalDistance = horizontal.magnitude;
            if (horizontalDistance <= 0.0001f)
                return root + offset * (maxDistance / distance);

            float horizontalMax = Mathf.Sqrt(Mathf.Max(0f, maxDistance * maxDistance - dy * dy));
            return root + horizontal * (horizontalMax / horizontalDistance) + Vector3.up * dy;
        }

        private static float GetHandStrikeEnvelope(ArmChain arm)
        {
            if (arm == null || arm.StrikeElapsed < 0f || arm.StrikeDuration <= 0f)
                return 0f;

            float u = Mathf.Clamp01(arm.StrikeElapsed / arm.StrikeDuration);
            if (u < 0.35f)
                return Smooth(u / 0.35f);

            return 1f - Smooth((u - 0.35f) / 0.65f);
        }

        private static void AdvanceHandMotion(ArmChain arm, float deltaTime)
        {
            if (arm == null || deltaTime <= 0f)
                return;

            if (arm.StrikeElapsed >= 0f)
            {
                arm.StrikeElapsed += deltaTime;
                if (arm.StrikeElapsed >= arm.StrikeDuration)
                {
                    arm.StrikeElapsed = -1f;
                    arm.StrikeStrength = 0f;
                    arm.StrikeDip = 0f;
                    arm.StrikeForward = 0f;
                    arm.WristRollAccentDegrees = 0f;
                }

                return;
            }

            arm.KeyDriftX = Mathf.MoveTowards(arm.KeyDriftX, 0f, HandDriftRelaxMetersPerSecond * deltaTime);
        }

        /// <summary>
        /// Desired world rotation for a hand bone over the keys, from the measured axis mapping:
        /// local +Y = wrist->knuckles, local -Z = palm normal, thumb side = local X * thumbLocalX.
        /// Basis: knuckle line toward the rack, yawed toward keyboard center, pitched by
        /// handPitchDegrees; palm down; then rolled about the knuckle line so the thumb side
        /// rises by handRollThumbUpDegrees (palm arch).
        /// </summary>
        private Quaternion ComputeHandGoalRotation(Transform home, float thumbLocalX)
        {
            Vector3 up = _mainframeRoot.up;
            // Inward = toward the keyboard center plane (x=0 in root space).
            float side = Mathf.Sign(_mainframeRoot.InverseTransformPoint(home.position).x);
            Vector3 fingersDir = Quaternion.AngleAxis(-side * HandYawInwardDegrees, up) * _mainframeRoot.forward;
            fingersDir = Quaternion.AngleAxis(HandPitchDegrees, Vector3.Cross(fingersDir, up).normalized * -1f)
                         * fingersDir;   // pitch down around the hand's across-axis
            Vector3 backOfHand = up;
            backOfHand = (backOfHand - Vector3.Dot(backOfHand, fingersDir) * fingersDir).normalized;
            Quaternion rot = Quaternion.LookRotation(backOfHand, fingersDir); // +Z = back of hand, +Y = knuckles

            // Roll about the knuckle line, sign chosen so the thumb edge rises.
            Quaternion rolled = Quaternion.AngleAxis(HandRollThumbUpDegrees, fingersDir) * rot;
            if ((rolled * new Vector3(thumbLocalX, 0f, 0f)).y < (rot * new Vector3(thumbLocalX, 0f, 0f)).y)
                rolled = Quaternion.AngleAxis(-HandRollThumbUpDegrees, fingersDir) * rot;
            return rolled;
        }

        private static void ApplyFingerTyping(List<FingerChain> fingers, float baseGain)
        {
            if (fingers == null || fingers.Count <= 0)
                return;

            for (int i = 0; i < fingers.Count; i++)
            {
                FingerChain f = fingers[i];
                if (f == null || f.Root == null)
                    continue;

                float thumbScale = f.IsThumb ? ThumbCurlScale : 1f;
                float liftK = 0f;
                float pressK = 0f;
                float strength = f.KeystrokeStrength;

                if (f.KeystrokeElapsed >= 0f && f.KeystrokeDuration > 0f)
                {
                    float u = f.KeystrokeElapsed / f.KeystrokeDuration;
                    if (u < 1f)
                        KeystrokeEnvelope(u, out liftK, out pressK);
                    else
                        f.KeystrokeElapsed = -1f;
                }

                float baseRoot = FingerBaseCurlDegrees * baseGain * thumbScale;
                float baseMid = FingerBaseCurlMidDegrees * baseGain * thumbScale;
                float lift = FingerLiftDegrees * f.LiftScale * thumbScale * strength;
                float midLift = FingerMidLiftBendDegrees * f.LiftScale * thumbScale * strength;
                float press = FingerPressDegrees * f.PressScale * thumbScale * strength;

                // Root knuckle raises the whole finger; mid joint KEEPS its bend (and adds a
                // little) so the middle knuckle is the apex and the tip stays lowest.
                float rootDeg = Mathf.Lerp(baseRoot, -lift, liftK) + press * pressK;
                float midDeg = baseMid + midLift * liftK + press * 0.6f * pressK;
                f.Root.localRotation *= Quaternion.AngleAxis(rootDeg, f.RootCurlAxis);
                if (f.Mid != null)
                    f.Mid.localRotation *= Quaternion.AngleAxis(midDeg, f.MidCurlAxis);
            }
        }

        private static void AdvanceKeystrokes(List<FingerChain> fingers, float deltaTime)
        {
            if (fingers == null || deltaTime <= 0f)
                return;

            for (int i = 0; i < fingers.Count; i++)
            {
                FingerChain finger = fingers[i];
                if (finger == null || finger.KeystrokeElapsed < 0f)
                    continue;

                finger.KeystrokeElapsed += deltaTime;
                if (finger.KeystrokeElapsed >= finger.KeystrokeDuration)
                {
                    finger.KeystrokeElapsed = -1f;
                    finger.KeystrokeStrength = 0f;
                }
            }
        }

        private static float GetHandTapDip(List<FingerChain> fingers)
        {
            if (fingers == null || fingers.Count <= 0)
                return 0f;

            float tap = 0f;
            for (int i = 0; i < fingers.Count; i++)
            {
                FingerChain finger = fingers[i];
                if (finger == null || finger.KeystrokeElapsed < 0f || finger.KeystrokeDuration <= 0f)
                    continue;

                float u = finger.KeystrokeElapsed / finger.KeystrokeDuration;
                if (u >= 1f)
                    continue;

                KeystrokeEnvelope(u, out _, out float pressK);
                tap = Mathf.Max(tap, pressK * finger.PressScale * finger.KeystrokeStrength);
            }

            return Mathf.Clamp01(tap);
        }

        /// <summary>Lift/press envelopes over one keystroke cycle u in [0,1): lift ramps 0->1
        /// (finger raises + extends), hands off to press 1->0/0->1 (strike down through the base
        /// pose), press releases, rest until the next cycle.</summary>
        private static void KeystrokeEnvelope(float u, out float liftK, out float pressK)
        {
            if (u < 0.18f) { liftK = Smooth(u / 0.18f); pressK = 0f; }
            else if (u < 0.35f) { float s = Smooth((u - 0.18f) / 0.17f); liftK = 1f - s; pressK = s; }
            else if (u < 0.70f) { liftK = 0f; pressK = 1f - Smooth((u - 0.35f) / 0.35f); }
            else { liftK = 0f; pressK = 0f; }
        }

        private static float Smooth(float x) => x * x * (3f - 2f * x);

        private void CaptureRendererState(PlayerControllerB player)
        {
            if (player == null)
                return;

            _armsRenderer = player.thisPlayerModelArms;
            if (_armsRenderer == null)
                return;

            _savedArmsRendererEnabled = _armsRenderer.enabled;
            _armsRendererStateCaptured = true;
            _armsRenderer.enabled = true;
        }

        private void RestoreRendererState()
        {
            if (!_armsRendererStateCaptured || _armsRenderer == null)
                return;

            _armsRenderer.enabled = _savedArmsRendererEnabled;
        }

        private void CaptureTargetRest(ArmChain arm)
        {
            if (arm == null || arm.Target == null || arm.Target.parent == null)
                return;

            Transform parent = arm.Target.parent;
            arm.RestTargetLocalPosition = parent.InverseTransformPoint(arm.Target.position);
            arm.RestTargetLocalRotation = Quaternion.Inverse(parent.rotation) * arm.Target.rotation;
        }

        private static void RestoreTargetPose(ArmChain arm)
        {
            if (arm == null || arm.Target == null)
                return;

            if (arm.Target.parent != null)
            {
                arm.Target.localPosition = arm.RestTargetLocalPosition;
                arm.Target.localRotation = arm.RestTargetLocalRotation;
            }
            else
            {
                arm.Target.position = arm.RestTargetLocalPosition;
                arm.Target.rotation = arm.RestTargetLocalRotation;
            }
        }

        private void CaptureBoneRest(Transform armRoot)
        {
            if (armRoot == null)
                return;

            Transform[] transforms = armRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                _boneRest.Add(new BoneRestPose(t, t.localPosition, t.localRotation));
            }
        }

        private void RestoreBoneRestSnapshot()
        {
            for (int i = 0; i < _boneRest.Count; i++)
            {
                BoneRestPose pose = _boneRest[i];
                if (pose.Transform == null)
                    continue;

                pose.Transform.localPosition = pose.LocalPosition;
                pose.Transform.localRotation = pose.LocalRotation;
            }
        }

        private static void CaptureChainLength(ArmChain arm)
        {
            if (arm == null || arm.Upper == null || arm.Lower == null || arm.Hand == null)
                return;

            float upperLength = Vector3.Distance(arm.Upper.position, arm.Lower.position);
            float lowerLength = Vector3.Distance(arm.Lower.position, arm.Hand.position);
            arm.ChainLength = upperLength + lowerLength;
        }

        private static void CaptureForearmRollCarrier(ArmChain arm)
        {
            if (arm == null || arm.Lower == null || arm.Hand == null)
                return;

            arm.RestHandInLower = Quaternion.Inverse(arm.Lower.rotation) * arm.Hand.rotation;
            arm.LowerBoneAxisLocal = Quaternion.Inverse(arm.Lower.rotation)
                * (arm.Hand.position - arm.Lower.position).normalized;
        }

        /// <summary>Derive each finger bone's curl axis (in its OWN local frame) so a positive
        /// curl rotates the finger tip toward the palm in the plane spanned by the bone axis and
        /// the palm normal - pure up/down motion over the keys regardless of how the bone's
        /// local frame is twisted. Sign is verified empirically per bone (rotate, measure, undo)
        /// on the rest pose.</summary>
        private static void DeriveFingerCurlAxes(Transform hand, List<FingerChain> fingers)
        {
            if (hand == null || fingers == null)
                return;

            Vector3 palm = hand.rotation * Vector3.back; // palm normal = hand local -Z
            foreach (FingerChain f in fingers)
            {
                Vector3 rootDir = (f.Mid != null
                    ? (f.Mid.position - f.Root.position)
                    : f.Root.rotation * Vector3.up).normalized;
                f.RootCurlAxis = SignedCurlAxis(f.Root, rootDir, palm,
                    f.Mid != null ? f.Mid : f.Root);
                if (f.Mid != null)
                {
                    Vector3 midDir = (f.Mid.rotation * Vector3.up).normalized; // bones run along +Y
                    f.MidCurlAxis = SignedCurlAxis(f.Mid, midDir, palm, f.Mid);
                }
            }
        }

        private static Vector3 SignedCurlAxis(Transform bone, Vector3 boneDirWorld, Vector3 palmWorld,
            Transform tipRef)
        {
            Vector3 axisWorld = Vector3.Cross(boneDirWorld, palmWorld).normalized;
            Vector3 axisLocal = Quaternion.Inverse(bone.rotation) * axisWorld;
            // Empirical sign check: +20 deg must move the tip toward the palm.
            Vector3 tipBefore = tipRef.TransformPoint(0f, 0.05f, 0f);
            Quaternion saved = bone.localRotation;
            bone.localRotation = saved * Quaternion.AngleAxis(20f, axisLocal);
            Vector3 tipAfter = tipRef.TransformPoint(0f, 0.05f, 0f);
            bone.localRotation = saved;
            if (Vector3.Dot(tipAfter - tipBefore, palmWorld) < 0f)
                axisLocal = -axisLocal;
            return axisLocal;
        }

        private static void CollectFingers(Transform hand, int seed, List<FingerChain> fingers)
        {
            if (hand == null || fingers == null)
                return;

            // Deterministic per-finger variation: same seed -> same animation every run, but
            // every finger lifts a different height and presses at a different depth.
            System.Random rng = new System.Random(seed);

            foreach (Transform child in hand)
            {
                if (!child.name.StartsWith("finger", StringComparison.Ordinal))
                    continue;

                Transform mid = null;
                foreach (Transform grandChild in child)
                {
                    if (grandChild.name.StartsWith("finger", StringComparison.Ordinal))
                        mid = grandChild;
                }

                bool isThumb = child.name.StartsWith("finger1", StringComparison.Ordinal);
                fingers.Add(new FingerChain
                {
                    Root = child,
                    Mid = mid,
                    IsThumb = isThumb,
                    SpeedScale = isThumb ? Range(rng, 0.30f, 0.45f) : Range(rng, 0.75f, 1.25f),
                    LiftScale = isThumb ? 0.25f : Range(rng, 0.55f, 1.35f),
                    PressScale = isThumb ? 0.5f : Range(rng, 0.75f, 1.25f),
                    Phase = (float)rng.NextDouble(),
                    KeystrokeElapsed = -1f,
                });
            }
        }

        private static float Range(System.Random rng, float lo, float hi)
        {
            return lo + (float)rng.NextDouble() * (hi - lo);
        }

        private void RegisterFingerIndices()
        {
            _allFingers.Clear();
            _leftThumbFingerIndices.Clear();
            _rightThumbFingerIndices.Clear();
            _leftNonThumbFingerIndices.Clear();
            _rightNonThumbFingerIndices.Clear();
            _nonThumbFingerIndices.Clear();

            RegisterFingerIndices(_leftFingers, _leftThumbFingerIndices, _leftNonThumbFingerIndices, _left);
            RegisterFingerIndices(_rightFingers, _rightThumbFingerIndices, _rightNonThumbFingerIndices, _right);
        }

        private void RegisterFingerIndices(List<FingerChain> fingers, List<int> thumbIndices, List<int> handNonThumbIndices, ArmChain arm)
        {
            if (fingers == null)
                return;

            for (int i = 0; i < fingers.Count; i++)
            {
                FingerChain finger = fingers[i];
                if (finger == null)
                    continue;

                finger.Index = _allFingers.Count;
                finger.Arm = arm;
                _allFingers.Add(finger);
                if (finger.IsThumb)
                {
                    thumbIndices.Add(finger.Index);
                }
                else
                {
                    handNonThumbIndices.Add(finger.Index);
                    _nonThumbFingerIndices.Add(finger.Index);
                }
            }
        }

        private static Transform ResolveArmsSearchRoot(PlayerControllerB player)
        {
            if (player == null)
                return null;

            if (player.localArmsTransform != null)
                return player.localArmsTransform;

            Camera camera = player.gameplayCamera;
            Transform cameraTransform = camera != null ? camera.transform : null;
            return cameraTransform != null ? cameraTransform.root : null;
        }

        private static Transform Require(Transform root, string name, List<string> missing)
        {
            Transform result = FindDeepChild(root, name);
            if (result == null)
                AddMissing(missing, name);
            return result;
        }

        private static void AddMissing(List<string> missing, string name)
        {
            if (missing == null || string.IsNullOrEmpty(name))
                return;

            for (int i = 0; i < missing.Count; i++)
            {
                if (string.Equals(missing[i], name, StringComparison.Ordinal))
                    return;
            }

            missing.Add(name);
        }

        private void DisableWithMissing(List<string> missing)
        {
            _disabled = true;
            if (_missingWarningLogged)
                return;

            _missingWarningLogged = true;
            string missingText = missing != null && missing.Count > 0
                ? string.Join(", ", missing.ToArray())
                : "<unknown>";
            SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][MainframeArms] Missing required transform(s): " +
                missingText + "; procedural typing arms disabled.");
        }

        private void CaptureCameraRelativeArmsPose()
        {
            _armsRigPoseCaptured = false;
            _armsMetarigPoseCaptured = false;
            _armsCameraPinningEnabled = false;
            _armsMetarig = null;

            if (_armsRoot == null)
                return;

            _armsRigRestLocalPosition = _armsRoot.localPosition;
            _armsRigRestLocalRotation = _armsRoot.localRotation;
            _armsRigPoseCaptured = true;

            _armsMetarig = FindDirectChild(_armsRoot, "metarig");
            if (_armsMetarig != null)
            {
                _armsMetarigRestLocalPosition = _armsMetarig.localPosition;
                _armsMetarigRestLocalRotation = _armsMetarig.localRotation;
                _armsMetarigPoseCaptured = true;
            }
            else
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][MainframeArms] Missing direct 'metarig' child under local arms rig; metarig camera pinning skipped for this session.");
            }

            Transform cameraTransform = GetGameplayCameraTransform();
            if (cameraTransform == null)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][MainframeArms] Gameplay camera unavailable at Begin; local arms camera pinning skipped for this session.");
                return;
            }

            Quaternion inverseCameraRotation = Quaternion.Inverse(cameraTransform.rotation);
            _armsRigCameraRelativePosition = inverseCameraRotation * (_armsRoot.position - cameraTransform.position);
            _armsRigCameraRelativeRotation = inverseCameraRotation * _armsRoot.rotation;

            if (_armsMetarigPoseCaptured && _armsMetarig != null)
            {
                _armsMetarigCameraRelativePosition = inverseCameraRotation * (_armsMetarig.position - cameraTransform.position);
                _armsMetarigCameraRelativeRotation = inverseCameraRotation * _armsMetarig.rotation;
            }

            _armsCameraPinningEnabled = true;
        }

        private void PinArmsToGameplayCamera()
        {
            if (!_armsCameraPinningEnabled || !_armsRigPoseCaptured || _armsRoot == null)
                return;

            Transform cameraTransform = GetGameplayCameraTransform();
            if (cameraTransform == null)
                return;

            Quaternion cameraRotation = cameraTransform.rotation;
            Vector3 lift = Vector3.up * ArmRigVerticalLift;
            if (_mainframeRoot != null)
                lift -= _mainframeRoot.forward * ArmRigBackShift;
            _armsRoot.SetPositionAndRotation(
                cameraTransform.position + cameraRotation * _armsRigCameraRelativePosition + lift,
                cameraRotation * _armsRigCameraRelativeRotation);

            if (_armsMetarigPoseCaptured && _armsMetarig != null)
            {
                _armsMetarig.SetPositionAndRotation(
                    cameraTransform.position + cameraRotation * _armsMetarigCameraRelativePosition + lift,
                    cameraRotation * _armsMetarigCameraRelativeRotation);
            }
        }

        private void RestorePinnedArmsLocalPose()
        {
            if (_armsRigPoseCaptured && _armsRoot != null)
            {
                _armsRoot.localPosition = _armsRigRestLocalPosition;
                _armsRoot.localRotation = _armsRigRestLocalRotation;
            }

            if (_armsMetarigPoseCaptured && _armsMetarig != null)
            {
                _armsMetarig.localPosition = _armsMetarigRestLocalPosition;
                _armsMetarig.localRotation = _armsMetarigRestLocalRotation;
            }
        }

        private static Transform FindDirectChild(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child != null && string.Equals(child.name, name, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }

        private void RestoreAfterBeginFailure()
        {
            try
            {
                RestoreBoneRestSnapshot();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Begin-failure bone restore failed: {ex.Message}");
            }

            try
            {
                RestoreTargetPose(_left);
                RestoreTargetPose(_right);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Begin-failure target restore failed: {ex.Message}");
            }

            try
            {
                RestorePinnedArmsLocalPose();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Begin-failure local arms rig restore failed: {ex.Message}");
            }

            try
            {
                RestoreRendererState();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeArms] Begin-failure renderer restore failed: {ex.Message}");
            }
        }

        private void TryLogBeginDiagnostics()
        {
            try
            {
                Transform cameraTransform = GetGameplayCameraTransform();
                int leftHomeCount = CountNamedChildren(_mainframeRoot, "MainframeLeftHandHome");
                int rightHomeCount = CountNamedChildren(_mainframeRoot, "MainframeRightHandHome");

                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][MainframeArmsDiag] Begin diagnostics\n" +
                    $"mainframeRoot name={TransformName(_mainframeRoot)} pos={FormatTransformPosition(_mainframeRoot)} forward={FormatTransformForward(_mainframeRoot)} right={FormatTransformRight(_mainframeRoot)} up={FormatTransformUp(_mainframeRoot)}\n" +
                    $"homeL path={FullPath(_leftHome)} pos={FormatTransformPosition(_leftHome)} count(MainframeLeftHandHome)={leftHomeCount}\n" +
                    $"homeR path={FullPath(_rightHome)} pos={FormatTransformPosition(_rightHome)} count(MainframeRightHandHome)={rightHomeCount}\n" +
                    BuildBeginArmDiagnostics("L", _left) + "\n" +
                    BuildBeginArmDiagnostics("R", _right) + "\n" +
                    $"gameplayCamera pos={FormatTransformPosition(cameraTransform)}");

                LogMainframeConsoleBounds();
            }
            catch
            {
            }
        }

        /// <summary>One-time dump of the world-space AABBs of renderers near the hand homes
        /// (the console/keyboard/tray) so the wrist targets can be placed to clear the front
        /// lip instead of guessing. Diagnostic only.</summary>
        private void LogMainframeConsoleBounds()
        {
            if (_mainframeRoot == null || _leftHome == null)
                return;

            Vector3 reference = _leftHome.position;
            Renderer[] renderers = _mainframeRoot.GetComponentsInChildren<Renderer>(true);
            string text = "[LethalCCTV][MainframeArmsDiag] Console-area renderer bounds (world AABB, within 1.0m of left home):";
            int count = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Bounds bounds = renderer.bounds;
                if (Vector3.Distance(bounds.ClosestPoint(reference), reference) > 1.0f)
                    continue;

                text += "\n  " + renderer.transform.name + " min=" + FormatVector(bounds.min) + " max=" + FormatVector(bounds.max);
                count++;
            }

            if (count == 0)
                text += "\n  <none within range>";

            SurveillanceBootstrap.Log?.LogInfo(text);
        }

        private void TryLogLatePhaseDiagnostics(float sessionTime)
        {
            try
            {
                int phase = GetMainframeArmsDiagPhase(sessionTime);
                if (phase == _lastLoggedMainframeArmsDiagPhase)
                    return;

                _lastLoggedMainframeArmsDiagPhase = phase;
                string phaseName = GetMainframeArmsDiagPhaseName(phase);
                string cameraPosition = FormatTransformPosition(GetGameplayCameraTransform());
                LogLatePhaseArmDiagnostics("L", phaseName, sessionTime, _leftHome, _left, cameraPosition);
                LogLatePhaseArmDiagnostics("R", phaseName, sessionTime, _rightHome, _right, cameraPosition);
            }
            catch
            {
            }
        }

        private Transform GetGameplayCameraTransform()
        {
            Camera camera = _player != null ? _player.gameplayCamera : null;
            return camera != null ? camera.transform : null;
        }

        private static int GetMainframeArmsDiagPhase(float sessionTime)
        {
            if (sessionTime < CameraApproachDuration)
                return MainframeArmsDiagPhaseApproach;
            if (sessionTime < CameraGlanceEndSeconds)
                return MainframeArmsDiagPhaseGlance;
            if (sessionTime < CameraFocusBlendEndSeconds)
                return MainframeArmsDiagPhaseFocusBlend;
            return MainframeArmsDiagPhaseFocus;
        }

        private static string GetMainframeArmsDiagPhaseName(int phase)
        {
            switch (phase)
            {
                case MainframeArmsDiagPhaseApproach:
                    return "approach";
                case MainframeArmsDiagPhaseGlance:
                    return "glance";
                case MainframeArmsDiagPhaseFocusBlend:
                    return "focusblend";
                case MainframeArmsDiagPhaseFocus:
                    return "focus";
                default:
                    return "unknown";
            }
        }

        private static void LogLatePhaseArmDiagnostics(string tag, string phaseName, float sessionTime, Transform home, ArmChain arm, string cameraPosition)
        {
            if (arm == null)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeArmsDiag] LateTick phase={phaseName} t={sessionTime:F3} arm={tag} arm=<null> camera={cameraPosition}");
                return;
            }

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][MainframeArmsDiag] LateTick phase={phaseName} t={sessionTime:F3} arm={tag} home={FormatTransformPosition(home)} unclampedGoal={FormatVector(arm.LastUnclampedGoal)} clampedGoal={FormatVector(arm.LastClampedGoal)} upper={FormatTransformPosition(arm.Upper)} target={FormatTransformPosition(arm.Target)} hand={FormatTransformPosition(arm.Hand)} camera={cameraPosition}");
        }

        private static string BuildBeginArmDiagnostics(string tag, ArmChain arm)
        {
            Transform upper = arm != null ? arm.Upper : null;
            Transform hand = arm != null ? arm.Hand : null;
            Transform target = arm != null ? arm.Target : null;
            Transform targetParent = target != null ? target.parent : null;
            return $"{tag} upperPath={FullPath(upper)} upperPos={FormatTransformPosition(upper)} handPos={FormatTransformPosition(hand)} targetPath={FullPath(target)} targetParent={TransformName(targetParent)} targetParentPos={FormatTransformPosition(targetParent)}";
        }

        private static int CountNamedChildren(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return 0;

            int count = 0;
            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                    count++;
            }

            return count;
        }

        private static string FullPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            string path = transform.name;
            Transform parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static string TransformName(Transform transform)
        {
            return transform != null ? transform.name : "<null>";
        }

        private static string FormatTransformPosition(Transform transform)
        {
            return transform != null ? FormatVector(transform.position) : "<null>";
        }

        private static string FormatTransformForward(Transform transform)
        {
            return transform != null ? FormatVector(transform.forward) : "<null>";
        }

        private static string FormatTransformRight(Transform transform)
        {
            return transform != null ? FormatVector(transform.right) : "<null>";
        }

        private static string FormatTransformUp(Transform transform)
        {
            return transform != null ? FormatVector(transform.up) : "<null>";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3}, {value.y:F3}, {value.z:F3})";
        }

        private void ClearState()
        {
            _player = null;
            _armsRoot = null;
            _armsMetarig = null;
            _armsRigPoseCaptured = false;
            _armsMetarigPoseCaptured = false;
            _armsCameraPinningEnabled = false;
            _armsRigRestLocalPosition = Vector3.zero;
            _armsRigRestLocalRotation = Quaternion.identity;
            _armsRigCameraRelativePosition = Vector3.zero;
            _armsRigCameraRelativeRotation = Quaternion.identity;
            _armsMetarigRestLocalPosition = Vector3.zero;
            _armsMetarigRestLocalRotation = Quaternion.identity;
            _armsMetarigCameraRelativePosition = Vector3.zero;
            _armsMetarigCameraRelativeRotation = Quaternion.identity;
            _leftHome = null;
            _rightHome = null;
            _left = null;
            _right = null;
            _armsRenderer = null;
            _armsRendererStateCaptured = false;
            _savedArmsRendererEnabled = false;
            _begun = false;
            _runtimeFaulted = false;
            _runtimeFaultLogged = false;
            _inputWarningLogged = false;
            _lastLateSessionTime = -1f;
            _lastLoggedMainframeArmsDiagPhase = MainframeArmsDiagPhaseNone;
            _lastStrikeFrame = -1;
            _nextIntroBurstTime = 0f;
            _introBurstTargetCount = 0;
            _introBurstCount = 0;
            _lastIntroFingerIndex = -1;
            _introBurstComplete = false;
            _introSpaceStrokePlayed = false;
            _strokeSerial = 0;
            _boneRest.Clear();
            _leftFingers.Clear();
            _rightFingers.Clear();
            _allFingers.Clear();
            _leftThumbFingerIndices.Clear();
            _rightThumbFingerIndices.Clear();
            _leftNonThumbFingerIndices.Clear();
            _rightNonThumbFingerIndices.Clear();
            _nonThumbFingerIndices.Clear();
        }

        private static Transform FindDeepChild(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }

        // ---- LC ChainIKConstraint emulation (identical to Y4NGZLcArmsIkPreviewRenderer) ----

        private sealed class ArmChain
        {
            public Transform Upper, Lower, Hand, Target;
            public Vector3 RestTargetLocalPosition;
            public Quaternion RestTargetLocalRotation;
            // Rest constants for the forearm roll carrier (captured in Initialize).
            public Quaternion RestHandInLower;
            public Vector3 LowerBoneAxisLocal;
            public Vector3 LastUnclampedGoal;
            public Vector3 LastClampedGoal;
            public float ChainLength;
            public float KeyDriftX;
            public float StrikeElapsed = -1f;
            public float StrikeDuration = KeystrokeBaseDuration;
            public float StrikeStrength;
            public float StrikeDip;
            public float StrikeForward;
            public float WristRollAccentDegrees;
        }

        private sealed class BoneRestPose
        {
            public readonly Transform Transform;
            public readonly Vector3 LocalPosition;
            public readonly Quaternion LocalRotation;

            public BoneRestPose(Transform transform, Vector3 localPosition, Quaternion localRotation)
            {
                Transform = transform;
                LocalPosition = localPosition;
                LocalRotation = localRotation;
            }
        }

        private sealed class FingerChain
        {
            public int Index;
            public Transform Root;
            public Transform Mid;
            public bool IsThumb;
            public float SpeedScale;
            public float LiftScale;
            public float PressScale;
            public float Phase;
            public Vector3 RootCurlAxis = Vector3.right;
            public Vector3 MidCurlAxis = Vector3.right;
            public float KeystrokeElapsed = -1f;
            public float KeystrokeStrength;
            public float KeystrokeDuration = KeystrokeBaseDuration;
            public ArmChain Arm;
        }

        private static void SolveChainIk(ArmChain arm)
        {
            Transform root = arm.Upper;
            Transform mid = arm.Lower;
            Transform tip = arm.Hand;
            Vector3 targetPos = arm.Target.position;

            Vector3 p0 = root.position;
            Vector3 p1 = mid.position;
            Vector3 p2 = tip.position;
            float d0 = Vector3.Distance(p0, p1);
            float d1 = Vector3.Distance(p1, p2);

            if (Vector3.Distance(p0, targetPos) >= d0 + d1)
            {
                Vector3 dir = (targetPos - p0).normalized;
                p1 = p0 + dir * d0;
                p2 = p1 + dir * d1;
            }
            else
            {
                for (int i = 0; i < FabrikIterations && Vector3.Distance(p2, targetPos) > FabrikTolerance; i++)
                {
                    p2 = targetPos;
                    p1 = p2 + (p1 - p2).normalized * d1;
                    p1 = p0 + (p1 - p0).normalized * d0;
                    p2 = p1 + (p2 - p1).normalized * d1;
                }
            }

            Vector3 oldDir0 = mid.position - root.position;
            root.rotation = Quaternion.FromToRotation(oldDir0, p1 - p0) * root.rotation;

            // Forearm = roll carrier (wrist candy-wrap fix): reproduce the rest hand-in-forearm
            // relationship at the target rotation, then swing minimally so the bone still aims
            // at the solved hand position. The hand's roll lands in the forearm sleeve where the
            // skinning can absorb it; without this the whole roll delta pinches at the wrist.
            Quaternion carrier = arm.Target.rotation * Quaternion.Inverse(arm.RestHandInLower);
            Vector3 dirNow = (p2 - p1).normalized;
            mid.rotation = Quaternion.FromToRotation(carrier * arm.LowerBoneAxisLocal, dirNow) * carrier;

            tip.rotation = arm.Target.rotation;
        }
    }
}
