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
    /// <summary>
    /// One operator's animation state: animator controller swap with full
    /// save/restore, animator parameter driving, joystick smoothing, and the
    /// pose component that leans the visible body to the station anchor.
    /// Used for the local operator (event-fed) and for remote operators
    /// (network-fed) alike.
    /// </summary>
    internal sealed partial class OperatorAnimSession
    {
        private const float JoystickIdleAfterSeconds = 0.16f;
        private const float ButtonPressLayerSeconds = 1.15f;
        private const int DeferredVanillaRigRebuildFrames = 3;
        // Exit is two-phase: first ease any joystick deflection back to neutral,
        // then play the authored hand-release clip before restoring vanilla.
        // Neutralize trimmed 2026-07-02 so the hand lets go of the joystick
        // while the (now faster) exit camera move is still in flight.
        private const float GracefulExitNeutralizeSeconds = 0.18f;
        // 1.10 -> 0.85 (2026-07-11): ExitSeam sampling showed the vanilla
        // controller returning ~0.45s AFTER the exit camera lerp finished
        // (0.15s delay + 0.60s), with the visible pose snap landing exactly on
        // that late swap. 0.85 restores right as the camera settles — the
        // FirstPersonExit hands reach their hidden pose at ~0.76s
        // (neutralize 0.18 + FirstPersonExitRecoverSeconds 0.58), so only the
        // motionless hold tail is trimmed.
        private const float GracefulRestoreSeconds = 0.85f;
        // Baked LeftArm station-space pose measured by cctv-v5-build*.log.
        private const float ApiLeftParentStationPositionX = -0.373f;
        private const float ApiLeftParentStationPositionY = 0.669f;
        private const float ApiLeftParentStationPositionZ = 2.192f;
        private const float ApiLeftParentStationEulerX = 354.90f;
        private const float ApiLeftParentStationEulerY = 106.55f;
        private const float ApiLeftParentStationEulerZ = 359.12f;
        private const float ApiArmsRootStationPinTelemetryIntervalSeconds = 1f;
        private const float ApiAuthoredTrajectoryFps = 30f;
        private const float ApiCameraAnchorEnterBlendStartRawT = 0.85f;
        private const float ApiCameraAnchorExitBlendEndRawT = 0.15f;
        // Exact v5 lp/lq samples from cctv-fp-hand-trajectories-v5-candidate.json.
        // Keep these station-space values aligned with the shipped authored clips.
        private static readonly AuthoredLeftHandStationPose[] ApiLeftEnterAuthoredTable =
        {
            // Rotations for the pre-reveal arc hold the press orientation from
            // frame 0: the hand is below the frame until ~frame 8, and the
            // authored rest->press wrist swing read as "hand facing down then
            // swinging up" at reveal (Test 31 user report, 2026-07-22).
            new AuthoredLeftHandStationPose(0.47f, 1.25f, 0.8f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.49309f, 1.25499f, 0.81357f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.55309f, 1.26784f, 0.84857f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.63609f, 1.28542f, 0.89643f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.59819f, 1.30458f, 0.94857f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.55548f, 1.32216f, 0.99643f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.49405f, 1.33501f, 1.03143f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.4f, 1.34f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.41156f, 1.32891f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.4175f, 1.30375f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.41969f, 1.27672f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.42f, 1.26f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.42f, 1.25422f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.42f, 1.25125f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.42f, 1.25016f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.42f, 1.25f, 1.045f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.44318f, 1.325f, 0.9967f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.48f, 1.4f, 0.9f, -0.1941f, -0.75013f, -0.21893f, 0.59304f),
            new AuthoredLeftHandStationPose(0.50464f, 1.39982f, 0.81172f, -0.18978f, -0.74789f, -0.21951f, 0.59705f),
            new AuthoredLeftHandStationPose(0.52441f, 1.39929f, 0.73944f, -0.17788f, -0.74155f, -0.22108f, 0.60793f),
            new AuthoredLeftHandStationPose(0.5399f, 1.39842f, 0.68156f, -0.1599f, -0.73163f, -0.22331f, 0.62393f),
            new AuthoredLeftHandStationPose(0.55168f, 1.39721f, 0.63648f, -0.13742f, -0.7186f, -0.22586f, 0.64321f),
            new AuthoredLeftHandStationPose(0.56036f, 1.39567f, 0.60261f, -0.1121f, -0.70313f, -0.22844f, 0.66398f),
            new AuthoredLeftHandStationPose(0.56652f, 1.39381f, 0.57834f, -0.08571f, -0.68612f, -0.23079f, 0.68456f),
            new AuthoredLeftHandStationPose(0.57074f, 1.39164f, 0.56207f, -0.06012f, -0.66878f, -0.23275f, 0.70352f),
            new AuthoredLeftHandStationPose(0.57361f, 1.38917f, 0.5522f, -0.03718f, -0.65254f, -0.23424f, 0.71968f),
            new AuthoredLeftHandStationPose(0.57572f, 1.3864f, 0.54713f, -0.01869f, -0.63897f, -0.23527f, 0.73214f),
            new AuthoredLeftHandStationPose(0.57765f, 1.38334f, 0.54527f, -0.00636f, -0.62968f, -0.23586f, 0.74016f),
            new AuthoredLeftHandStationPose(0.58f, 1.38f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.58311f, 1.37648f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.58637f, 1.37326f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.58895f, 1.37091f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.59f, 1.37f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
        };
        private static readonly AuthoredLeftHandStationPose[] ApiLeftExitAuthoredTable =
        {
            new AuthoredLeftHandStationPose(0.59f, 1.37f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.58749f, 1.37592f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.57914f, 1.39163f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.56373f, 1.41402f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.54f, 1.44f, 0.545f, -0.00188f, -0.62626f, -0.23606f, 0.74302f),
            new AuthoredLeftHandStationPose(0.50732f, 1.46672f, 0.54693f, -0.02576f, -0.62108f, -0.22535f, 0.7502f),
            new AuthoredLeftHandStationPose(0.46736f, 1.4923f, 0.5524f, -0.09074f, -0.60386f, -0.19504f, 0.76752f),
            new AuthoredLeftHandStationPose(0.42238f, 1.51506f, 0.56092f, -0.18612f, -0.57036f, -0.14744f, 0.78633f),
            new AuthoredLeftHandStationPose(0.37466f, 1.53337f, 0.57201f, -0.2978f, -0.5176f, -0.08658f, 0.79744f),
            new AuthoredLeftHandStationPose(0.32644f, 1.54557f, 0.5852f, -0.40891f, -0.44726f, -0.01929f, 0.79523f),
            new AuthoredLeftHandStationPose(0.28f, 1.55f, 0.6f, -0.50478f, -0.36579f, 0.04662f, 0.78052f),
            new AuthoredLeftHandStationPose(0.23717f, 1.54279f, 0.61596f, -0.58374f, -0.28338f, 0.1067f, 0.75336f),
            new AuthoredLeftHandStationPose(0.19811f, 1.52325f, 0.63277f, -0.65153f, -0.2043f, 0.16142f, 0.71254f),
            new AuthoredLeftHandStationPose(0.16255f, 1.4945f, 0.65016f, -0.7077f, -0.12963f, 0.21023f, 0.66194f),
            new AuthoredLeftHandStationPose(0.13023f, 1.45968f, 0.66786f, -0.7511f, -0.06268f, 0.25143f, 0.60721f),
            new AuthoredLeftHandStationPose(0.10087f, 1.4219f, 0.68558f, -0.78124f, -0.00824f, 0.28305f, 0.5563f),
            new AuthoredLeftHandStationPose(0.07422f, 1.3843f, 0.70305f, -0.79882f, 0.02836f, 0.30333f, 0.51872f),
            new AuthoredLeftHandStationPose(0.115f, 1.35f, 0.72f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
            new AuthoredLeftHandStationPose(0.15803f, 1.3215f, 0.73614f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
            new AuthoredLeftHandStationPose(0.2034f, 1.29882f, 0.75117f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
            new AuthoredLeftHandStationPose(0.2513f, 1.28132f, 0.76476f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
            new AuthoredLeftHandStationPose(0.30191f, 1.26839f, 0.7766f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
            new AuthoredLeftHandStationPose(0.35541f, 1.25942f, 0.78636f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
            new AuthoredLeftHandStationPose(0.41197f, 1.25377f, 0.79372f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
            new AuthoredLeftHandStationPose(0.47177f, 1.25084f, 0.79838f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
            new AuthoredLeftHandStationPose(0.47f, 1.25f, 0.8f, -0.80473f, 0.04175f, 0.31056f, 0.5042f),
        };
        // Authored constant rest from cctv-fp-hand-trajectories-v5-candidate.json.
        private static readonly Vector3 ApiRightHandRestStationPosition =
            new Vector3(0.47f, 1.25f, 0.40f);
        private static readonly Quaternion ApiRightHandRestStationRotation =
            new Quaternion(-0.48309f, 0.40693f, 0.70802f, 0.3158f);
        // Runtime hand trajectory driver (2026-07-12 session 3). Two sessions
        // of offline station->parent-local clip mapping failed in different
        // ways (round 3: arms hidden by config; round 4: collapsed motion,
        // press off-screen), so the left-hand IK target is now driven directly
        // in WORLD space from the live button/lever transforms while the enter
        // and exit clips play underneath. The clip keeps ownership of
        // rotation, fingers, and spine lean; the driver owns both FP IK targets
        // during entry. The right hand stays in a designed yaw-flat camera-frame
        // rest pose while the left follows the live controls. The
        // left target remains live-owned on the lever during operation, so the
        // failed clip mapping can never pull it back toward the throttle base.
        // Enter timeline (seconds from session begin; the enter camera path
        // knots in MonitorFocus.STATION_FOCUS_ENTER_* must track these):
        // rise to a hover over the button, press with contact at 0.56s (cap
        // depress + SFX + feed flip fire on that exact frame), release, sweep
        // on a lifted arc to the lever grip, then keep tracking the live lever.
        private const float HandEnterHoverAtSeconds = 0.40f;
        private const float HandEnterContactAtSeconds = 0.56f;
        private const float HandEnterPressHoldEndSeconds = 0.66f;
        private const float HandEnterReleaseEndSeconds = 0.80f;
        private const float HandEnterSweepEndSeconds = 1.38f;
        private const float HandEnterSettleEndSeconds = 1.70f;
        // The IK target is the wrist, not the fingertip. Blender's authored
        // press used a 17cm wrist standoff plus a small station-left offset so
        // the extended index finger, rather than the palm, meets the cap.
        private static readonly Vector3 HandEnterPressWristStationOffset =
            new Vector3(-0.07f, 0f, 0.015f);
        private const float HandEnterPressWristHeightMeters = 0.17f;
        private const float HandEnterHoverLiftMeters = 0.10f;
        private const float HandEnterPressDepthMeters = 0.012f;
        private const float HandEnterReleaseLiftMeters = 0.08f;
        private const float HandEnterSweepLiftMeters = 0.13f;
        private const float HandEnterSweepPullMeters = 0.05f;
        private static readonly Quaternion HandEnterPressStationRotation =
            new Quaternion(-0.19410f, -0.75013f, -0.21893f, 0.59304f);
        private static readonly Quaternion HandEnterLeverStationRotation =
            new Quaternion(-0.00188f, -0.62626f, -0.23606f, 0.74302f);
        // Exit: lift off the lever and return to the designed left-hand rest
        // pose. Full ownership avoids the
        // exit clip fighting the path or pulling the wrist through the lever.
        private const float HandExitLiftEndSeconds = 0.24f;
        private const float HandExitReturnEndSeconds = 0.62f;
        private const float HandExitLiftMeters = 0.14f;
        private const float HandExitPullMeters = 0.10f;
        private static readonly float[] HandTraceMilestonesSeconds =
        {
            0f, 0.34f, 0.56f, 0.80f, 0.94f, 1.38f, 1.70f,
        };
        private const string FirstPersonLeftHandPath =
            "ScavengerModelArmsOnly/metarig/spine.003/shoulder.L/arm.L_upper/arm.L_lower/hand.L";
        private const string FirstPersonRightHandPath =
            "ScavengerModelArmsOnly/metarig/spine.003/shoulder.R/arm.R_upper/arm.R_lower/hand.R";
        private const string FirstPersonLeftUpperArmPath =
            "ScavengerModelArmsOnly/metarig/spine.003/shoulder.L/arm.L_upper";
        private const string FirstPersonRightUpperArmPath =
            "ScavengerModelArmsOnly/metarig/spine.003/shoulder.R/arm.R_upper";
        private const float PresentationHiddenScale = 0.0001f;
        private const float PresentationScaleSanityEpsilon = 0.001f;
        private static readonly string[] LocalThirdPersonHeadSpineChain =
        {
            "metarig",
            "spine",
            "spine.001",
            "spine.002",
            "spine.003",
            "spine.004",
        };

        private static readonly int SeatedHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.SeatedBool);
        private static readonly int CameraControlHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.CameraControlBool);
        private static readonly int RadarLookHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.RadarLookBool);
        private static readonly int JoystickXHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.JoystickXFloat);
        private static readonly int JoystickYHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.JoystickYFloat);
        private static readonly int ActiveSlotHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.ActiveSlotInt);
        private static readonly int ActionButtonHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.ActionButtonInt);
        private static readonly int EnterHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.EnterTrigger);
        private static readonly int ExitHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.ExitTrigger);
        private static readonly int SelectCameraHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.SelectCameraTrigger);
        private static readonly int JoystickGrabHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.JoystickGrabTrigger);
        private static readonly int JoystickReleaseHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.JoystickReleaseTrigger);
        private static readonly int ButtonPressHash = Animator.StringToHash(Y4NGZPlayerAnimationBridge.ButtonPressTrigger);
        private static readonly int OperatorIdleStateHash = Animator.StringToHash("CCTVOperatorFullBody.OperatorIdle");
        private static readonly int RealArmsIdleStateHash = Animator.StringToHash("CCTVFirstPersonArms.RealArmsIdle");
        private static readonly int OperatorIdleShortStateHash = Animator.StringToHash("OperatorIdle");
        private static readonly int RealArmsIdleShortStateHash = Animator.StringToHash("RealArmsIdle");
        private static readonly int WalkingHash = Animator.StringToHash("Walking");
        private static readonly int SprintingHash = Animator.StringToHash("Sprinting");
        private static readonly int SidewaysHash = Animator.StringToHash("Sideways");
        private static readonly int CrouchingHash = Animator.StringToHash("crouching");
        private static readonly int JumpingHash = Animator.StringToHash("Jumping");
        private static readonly int FallNoJumpHash = Animator.StringToHash("FallNoJump");
        private static readonly int ClimbingLadderHash = Animator.StringToHash("ClimbingLadder");
        private static readonly int AnimationSpeedHash = Animator.StringToHash("animationSpeed");
        private static readonly int PressBlueCameraSwitchStateHash = Animator.StringToHash("PressBlueCameraSwitch");
        private static readonly int PressGreenActionStateHash = Animator.StringToHash("PressGreenAction");

        // The local exit rebuild is queued until the camera seam and its
        // two-LateUpdate baseline guard have released. Building immediately
        // after restoring the controller can capture constraint offsets from
        // the transient exit pose and leave a persistent post-exit lean.
        private static Animator _deferredVanillaRigRebuildAnimator;
        private static PlayerControllerB _deferredVanillaRigRebuildPlayer;
        private static Component[] _deferredVanillaRigRebuildBuilders = Array.Empty<Component>();
        private static int _deferredVanillaRigRebuildQueuedFrame = -1;
        private static int _deferredVanillaRigRebuildEarliestFrame = -1;
        private static InteractionBeginPresentationState _interactionBeginPresentation;
        private static Transform _lastKnownHeadPresentationBone;
        private static Vector3 _lastKnownHeadPresentationLocalScale = Vector3.one;
        private static bool _lastKnownHeadPresentationScaleValid;

        private sealed class InteractionBeginPresentationState
        {
            internal PlayerControllerB Player;
            internal Transform RightShoulder;
            internal Vector3 RightShoulderScale = Vector3.one;
            internal bool RightShoulderCaptured;
            internal bool RightShoulderHidden;
            internal Transform HeadBone;
            internal Vector3 HeadScale = Vector3.one;
            internal bool HeadCaptured;
            internal bool HeadHidden;
        }

        private sealed class ApiPoseTransformSnapshot
        {
            internal string Label;
            internal Transform Transform;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation = Quaternion.identity;
        }

        private readonly bool _isLocal;
        private Animator _animator;
        private RuntimeAnimatorController _savedController;
        private bool _controllerApplied;
        private bool _interactionsApiMode;
        private bool _interactionsApiUsesDedicatedViewmodel;
        private object _interactionsApiHandle;
        private float _interactionsApiStartedAt;
        private float _interactionsApiExitStartedAt;
        private bool _interactionsApiFeedFlipFired;
        private SavedAnimatorParameter[] _savedParameters = Array.Empty<SavedAnimatorParameter>();
        private SavedAnimatorState[] _savedStates = Array.Empty<SavedAnimatorState>();
        private readonly Dictionary<int, AnimatorControllerParameterType> _parameterTypes =
            new Dictionary<int, AnimatorControllerParameterType>();

        private bool _active;
        private bool _cameraControlActive;
        private Vector2 _joystickSmoothed;
        private readonly CCTVJoystickPhaseDriver _joystickPhase = new CCTVJoystickPhaseDriver();
        private const float ApiReachAssistUsableReachFraction = 0.97f;
        private const float ApiLeverControlReachAssistMaxMeters = 0.24f;
        private const float ApiLeverControlReachAssistSlewMetersPerSecond = 2.0f;
        private float _apiLeverControlReachAssistSmoothed;
        private Vector3 _apiLeverControlReachAssistLastDirection = Vector3.zero;
        private bool _apiLeverControlReachAssistLogged;
        // Press-contact snap (Test 33): the authored enter table presses at
        // station (0.42, 1.25, 1.045) but the live resolved button cap sat
        // ~0.09m away, so the fingertip hovered above it. During the press
        // window the authored left-hand position is blended by the world
        // delta to the LIVE ResolveAccessButtonPressPoint. Frames are the
        // 1-based 30fps indices from TryResolveApiLeftAuthoredStationPose:
        // fingertip on the cap frames 8-16, deepest press at frame 16.
        private const int ApiEnterPressContactRowIndex = 15;
        private const float ApiEnterPressContactFrameStart = 8f;
        private const float ApiEnterPressContactFrameEnd = 16f;
        private const float ApiEnterPressContactRampFrames = 3f;
        // Test 40: the Test-39 FromToRotation re-aim made contact but
        // REPLACED the designed pointer-finger press pose with a solved
        // orientation — on video the hand/arm visibly twisted around to
        // reach the cap (user-rejected). The AUTHORED rotation is law now:
        // the designed animation plays untouched, and contact is achieved
        // purely positionally by aiming the WRIST at
        // liveButton - R_authored * localTipOffset so the index tip (where
        // it actually sits under the authored quat) lands on the cap. The
        // arm may approach from a different angle than authored — accepted.
        // Offset = finger2.L.001_end hand-local under the press quat,
        // measured post-solve at frame 15 (Test 39, enter #1).
        private static readonly Vector3 ApiEnterPressLocalIndexTipOffset =
            new Vector3(-0.045f, 0.178f, -0.076f);
        // The deepest authored press row (1-based frame 16); the one-shot
        // telemetry samples here so it reads the actual press, not mid-rise.
        private const int ApiEnterPressTelemetryFrame = 15;
        // Lever-contact snap (Test 38): the authored enter table ends the
        // sweep at station (0.59,1.37,0.545), but the LIVE lever grip
        // resolved ~0.23m below/behind that (world 9.88,1.90 vs grip
        // 9.86,1.67) — the hand hovered over the throttle recess instead of
        // sitting on the grip. Same defect class as the button press
        // (authored contact vs live control), same fix: during the sweep's
        // final approach blend the authored position by the world delta to
        // the live grip, hold the full correction through control
        // (reconstruction writer), and fade it out over the exit's first
        // rows as the authored lift-off takes the hand away.
        private const float ApiEnterLeverContactFrameStart = 26f;
        private const float ApiEnterLeverContactRampFrames = 3f;
        private const float ApiExitLeverContactHoldFrames = 2f;
        private const float ApiExitLeverContactFadeFrames = 4f;
        // The lever contact point belongs to the rendered palm, not to the
        // wrist target. Resolve the palm center from the live finger-root
        // geometry in hand-local orientation space, then aim the wrist so
        // that anatomical point lands on top of the live grip. The fallback
        // matches the roughly 8 cm wrist-to-palm span of the FP rig.
        private const float ApiLeverGripPalmOffsetMeters = 0.025f;
        private const float ApiLeverPalmKnuckleFraction = 0.58f;
        private static readonly Vector3 ApiLeverFallbackLocalPalmOffset =
            new Vector3(0f, 0.08f, 0f);
        private static readonly string[] ApiLeverPalmKnuckleBoneNames =
            { "finger2.L", "finger3.L", "finger4.L", "finger5.L" };
        private bool _apiLeverContactLogged;
        private bool _apiLeverPalmOffsetResolved;
        private Vector3 _apiLeverPalmLocalOffset;
        private string _apiLeverPalmOffsetSource;
        private bool _apiLeverContactSamplePending;
        private bool _apiLeverContactPostSolveLogged;
        private Vector3 _apiLeverContactSampleLiveGrip;
        private Vector3 _apiLeverContactSampleDesiredPalm;
        private Vector3 _apiLeverContactSampleWristTarget;
        private Vector3 _apiLeverContactSampleLocalPalmOffset;
        // Test 35 first-frame flash: authored rows 0-6 sit ON the desk and
        // the steeper walk-up framing puts the desk in frame at t=0, so the
        // hand popped visible for ~2 frames at session begin. Blend the left
        // target in from the below-frame camera rest pose over the first
        // rows (fully authored by frame 7, before the press rise).
        private const float ApiEnterHandRampInFrames = 6f;
        private bool _apiPressContactSnapLogged;
        // Test-36 telemetry trap: the press-bottom sample used to read the IK
        // tip during the pin phase, where the animator had already FK-stomped
        // the bones and the manual IK had not re-solved yet (tipToButton read
        // 0.54-0.84m while the video showed the hand on the button). The pin
        // now only ARMS the sample; FinishFirstPersonArmFrame logs it after
        // ApplyManualFirstPersonArmIk = the pose that actually renders.
        private bool _apiPressContactSamplePending;
        private int _apiPressContactSampleFrame;
        private Vector3 _apiPressContactSampleCorrection;
        private Vector3 _apiPressContactSampleLiveButton;
        private Vector3 _apiPressContactSampleAuthoredContact;
        private float _lastJoystickMoveAt;
        private float _restoreAt;
        private float _exitReleaseAt;
        private bool _windingDown;
        private bool _exitTriggered;
        private CCTVOperatorRuntimePose _runtimePose;
        private string _savedControllerName;
        private int _operatorFullBodyLayer = -1;
        private int _firstPersonArmsLayer = -1;
        private int _buttonPressLayer = -1;
        private int _glanceLayer = -1;
        private float _buttonPressLayerUntil;
        private int _buttonPressActionId;
        private int _rightHandEditPreviewActionId = -1;
        private Transform _firstPersonRightHandTarget;
        private Transform _firstPersonLeftHandTarget;
        private PoseTuningApplication _firstPersonRightHandTuningApplication;
        private PoseTuningApplication _firstPersonLeftHandTuningApplication;
        private bool _firstPersonRightHandTargetMissingLogged;
        private bool _firstPersonLeftHandTargetMissingLogged;
        private Component[] _firstPersonRightHandRigBuilders = Array.Empty<Component>();
        private bool _firstPersonRightHandRigBuildersResolved;
        private bool _firstPersonRightHandRigBuilderMissingLogged;
        private TransformPoseSnapshot _scopedFirstPersonPoseSnapshot;
        private bool _radarLookActive;
        private bool _handDriveEnterActive;
        private float _handDriveEnterStartedAt;
        private bool _handDriveContactFired;
        private bool _handDriveEnterLogged;
        private bool _handDriveLeverGripCaptureAttempted;
        private bool _handDriveLeverGripCaptured;
        private Transform _handDriveMovingLever;
        private Vector3 _handDriveLeverGripLocalPosition;
        private Quaternion _handDriveLeverGripLocalRotation = Quaternion.identity;
        private Transform _handDriveLeverNeutralGripFrame;
        private Vector3 _handDriveLeverNeutralGripFramePosition;
        private Quaternion _handDriveLeverNeutralGripFrameRotation = Quaternion.identity;
        private Vector3 _handDriveLeverHandoffPosition;
        private Quaternion _handDriveLeverHandoffRotation = Quaternion.identity;
        private bool _handDriveLeverHandoffPoseCaptured;
        private bool _handDriveLeverFirstFollowLogged;
        private bool _handDriveExitActive;
        private Vector3 _handDriveExitFromPosition;
        private Quaternion _handDriveExitFromRotation;
        private bool _windDownTargetHoldCaptured;
        private Vector3 _windDownLeftTargetHoldPosition;
        private Quaternion _windDownLeftTargetHoldRotation = Quaternion.identity;
        private Vector3 _windDownRightTargetHoldPosition;
        private Quaternion _windDownRightTargetHoldRotation = Quaternion.identity;
        // Diagnostic-only snapshot of the parked vanilla target transforms.
        // Round 21 removed every visual consumer of these values because the
        // vanilla FP arm targets do not represent the rendered lowered hands.
        private Vector3 _handDriveStartLeftPosition;
        private Quaternion _handDriveStartLeftRotation;
        private Vector3 _handDriveStartRightPosition;
        private Quaternion _handDriveStartRightRotation;
        private bool _handRestFirstPinLogged;
        private float _exitTriggeredAt;
        private bool _handTraceEnabled;
        private int _handTraceNextMilestoneIndex;
        private int _handTraceFrame = -1;
        private float _handTraceMilestoneSeconds;
        private int _handTraceDriverEntryCount;
        private int _handTraceDriveApplicationCount;
        private int _handTraceRigEvaluateCount;
        private bool _handTraceBaselineCaptured;
        private Vector3 _handTraceBaselineTargetPosition;
        private Vector3 _handTraceBaselineWristPosition;
        private Vector3 _handTraceBaselineTipPosition;
        private ManualArmIkChain _manualLeftArmIk;
        private ManualArmIkChain _manualRightArmIk;
        private bool _apiManualArmIkChainsUnavailableLogged;
        private bool _apiManualArmIkFirstSolvedLogged;
        private float _apiManualArmIkNextCaptureAttemptAt;
        private Transform _firstPersonArmsRoot;
        private Vector3 _savedFirstPersonArmsRootLocalPosition;
        private Quaternion _savedFirstPersonArmsRootLocalRotation = Quaternion.identity;
        private bool _firstPersonArmsRootPoseCaptured;
        private Transform _apiArmsRootStationPinRoot;
        private Transform _apiArmsRootStationPinLeftTarget;
        private Transform _apiArmsRootStationPinLeftParent;
        private Transform _apiArmsRootStationPinRightTarget;
        private bool _apiArmsRootStationPinActive;
        private float _apiArmsRootStationPinNextTelemetryAt;
        private string _apiArmsRootStationPinUnavailableReason;
        private Renderer _firstPersonArmsRenderer;
        private bool _firstPersonArmsHiddenForMissingAnchor;
        private bool _firstPersonArmsEnabledBeforeMissingAnchor;
        private Transform _rightArmPresentationShoulder;
        private Vector3 _rightArmPresentationCachedLocalScale = Vector3.one;
        private bool _rightArmPresentationScaleCaptured;
        private bool _rightArmPresentationHidden;
        private Transform _headPresentationBone;
        private Vector3 _headPresentationCachedLocalScale = Vector3.one;
        private bool _headPresentationScaleCaptured;
        private bool _headPresentationHidden;
        private ApiPoseTransformSnapshot[] _apiPoseChainSnapshots =
            Array.Empty<ApiPoseTransformSnapshot>();
        private GameObject _leftShoulderCapPlug;
        private GameObject _rightShoulderCapPlug;
        private bool _shoulderCapPlugUnavailableLogged;
        private int _shoulderAnchorAppliedFrame = -1;
        private float _leverLeftShoulderBlend;
        private bool _leverLeftShoulderFullOffsetLogged;
        private bool _exitArmsTelemetryEnabled;
        private int _exitArmsLastLoggedFrame = -1;

        private const int PostExitArmsTelemetryFrames = 15;
        private static ExitArmsPostExitTelemetry _postExitArmsTelemetry;

        private sealed class ExitArmsPostExitTelemetry
        {
            internal PlayerControllerB Player;
            internal Transform ArmsRoot;
            internal Transform LeftHand;
            internal Transform LeftTarget;
            internal Transform RightShoulder;
            internal Transform RightTarget;
            internal Renderer ArmsRenderer;
            internal int NextIndex = 1;
            internal int LastLoggedFrame = -1;
        }

        private sealed class ManualArmIkChain
        {
            internal string Side;
            internal Transform Root;
            internal Transform Mid;
            internal Transform Tip;
            internal Transform Target;
            internal float UpperLength;
            internal float LowerLength;
            internal Quaternion TargetToTipRotation = Quaternion.identity;
            internal int LastSolvedFrame = -1;
            internal float LastTipTargetDistance = -1f;
            internal Vector3 LastRootPosition;
            internal float LastRootTargetDistance = -1f;
            internal float LastReach = -1f;
            internal string LastPoleHint = "<not-solved>";
            internal bool HasStablePole;
            internal Transform StablePoleReference;
            internal Vector3 StablePoleReferenceDirection;
        }

        private readonly struct AuthoredLeftHandStationPose
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;

            internal AuthoredLeftHandStationPose(
                float positionX,
                float positionY,
                float positionZ,
                float rotationX,
                float rotationY,
                float rotationZ,
                float rotationW)
            {
                Position = new Vector3(positionX, positionY, positionZ);
                Rotation = new Quaternion(rotationX, rotationY, rotationZ, rotationW);
            }
        }

        private struct PoseTuningApplication
        {
            internal Transform Target;
            internal Vector3 BaseLocalPosition;
            internal Quaternion BaseLocalRotation;
            internal Vector3 AppliedLocalPosition;
            internal Quaternion AppliedLocalRotation;
            internal bool Active;
        }

        internal PlayerControllerB Player { get; private set; }
        internal bool IsActive => _active;
        internal bool IsWindingDown => _windingDown;
        internal bool IsLocal => _isLocal;
        internal bool UsesInteractionsApi => _interactionsApiMode;
        internal bool UsesDedicatedLocalViewmodel =>
            _interactionsApiMode && _interactionsApiUsesDedicatedViewmodel;
        internal bool CameraControlActive => _cameraControlActive;
        internal Vector2 JoystickSmoothed => _joystickSmoothed;
        internal Vector2 JoystickCommand => _joystickPhase.Requested;

        internal OperatorAnimSession(PlayerControllerB player, bool isLocal)
        {
            Player = player;
            _isLocal = isLocal;
        }

        // Ported from Y4NGZInteractions LiveBodyAnimatorPresenter's
        // TransformPoseSnapshot. Keep this scoped to the first-person metarig
        // so controller teardown cannot strand any descendant in the CCTV pose.
        private sealed class TransformPoseSnapshot
        {
            private readonly TransformPose[] _poses;

            internal int Count => _poses.Length;

            private TransformPoseSnapshot(TransformPose[] poses)
            {
                _poses = poses ?? Array.Empty<TransformPose>();
            }

            internal static TransformPoseSnapshot CaptureDescendants(Transform root)
            {
                if (root == null)
                    return new TransformPoseSnapshot(Array.Empty<TransformPose>());

                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                var captured = new List<TransformPose>(Math.Max(0, transforms.Length - 1));
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform transform = transforms[i];
                    if (transform == null || transform == root)
                        continue;
                    captured.Add(new TransformPose(transform));
                }

                return new TransformPoseSnapshot(captured.ToArray());
            }

            internal int Restore()
            {
                int restored = 0;
                for (int i = 0; i < _poses.Length; i++)
                {
                    if (_poses[i].Restore())
                        restored++;
                }
                return restored;
            }
        }

        private readonly struct TransformPose
        {
            private readonly Transform _transform;
            private readonly Vector3 _localPosition;
            private readonly Quaternion _localRotation;
            private readonly Vector3 _localScale;

            internal TransformPose(Transform transform)
            {
                _transform = transform;
                _localPosition = transform.localPosition;
                _localRotation = transform.localRotation;
                _localScale = transform.localScale;
            }

            internal bool Restore()
            {
                if (_transform == null)
                    return false;
                _transform.localPosition = _localPosition;
                _transform.localRotation = _localRotation;
                _transform.localScale = _localScale;
                return true;
            }
        }

        private struct SavedAnimatorParameter
        {
            private readonly string _name;
            private readonly int _hash;
            private readonly AnimatorControllerParameterType _type;
            private readonly float _floatValue;
            private readonly int _intValue;
            private readonly bool _boolValue;

            internal SavedAnimatorParameter(Animator animator, AnimatorControllerParameter parameter)
            {
                _name = parameter.name;
                _hash = parameter.nameHash;
                _type = parameter.type;
                _floatValue = 0f;
                _intValue = 0;
                _boolValue = false;

                switch (_type)
                {
                    case AnimatorControllerParameterType.Float:
                        _floatValue = animator.GetFloat(_hash);
                        break;
                    case AnimatorControllerParameterType.Int:
                        _intValue = animator.GetInteger(_hash);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        _boolValue = animator.GetBool(_hash);
                        break;
                }
            }

            internal void Restore(Animator animator, Dictionary<int, AnimatorControllerParameterType> parameterTypes)
            {
                if (animator == null || parameterTypes == null)
                    return;
                if (!parameterTypes.TryGetValue(_hash, out AnimatorControllerParameterType currentType) ||
                    currentType != _type)
                {
                    return;
                }

                switch (_type)
                {
                    case AnimatorControllerParameterType.Float:
                        animator.SetFloat(_hash, _floatValue);
                        break;
                    case AnimatorControllerParameterType.Int:
                        animator.SetInteger(_hash, _intValue);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        animator.SetBool(_hash, _boolValue);
                        break;
                    case AnimatorControllerParameterType.Trigger:
                        animator.ResetTrigger(_hash);
                        break;
                }
            }

            public override string ToString()
            {
                return _name;
            }
        }

        private struct SavedAnimatorState
        {
            internal readonly int StateHash;
            internal readonly float NormalizedTime;

            internal SavedAnimatorState(int stateHash, float normalizedTime)
            {
                StateHash = stateHash;
                NormalizedTime = normalizedTime;
            }
        }
    }
}
