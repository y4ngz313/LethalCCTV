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
    /// <summary>
    /// Operator-station focus state + LcInputActions exit handling. Entered from
    /// the vanilla monitor station's access button; drives the seated camera pose,
    /// the CCTV feed on the vanilla monitors, and the docked interactive overlays.
    /// </summary>
    internal static partial class MonitorFocus
    {
        // Physical-focus overlay sized to match the terminal CRT while
        // giving the 2x2 CCTV grid most of the screen.
        //
        // Legacy standalone terminal overlay. The vanilla station now renders CCTV
        // on the left monitor, radar on the right monitor, and controls in HUD space.
        private const float OVERLAY_WIDTH = 960f;
        private const float OVERLAY_HEIGHT = 720f;
        private const float OVERLAY_TOP_SECTION_HEIGHT = 480f;
        private const float OVERLAY_BOTTOM_SECTION_HEIGHT = 240f;
        private const float OVERLAY_QUAD_WIDTH = 420f;
        private const float OVERLAY_QUAD_HEIGHT = 212f;
        private const float OVERLAY_GAP = 14f;
        private const float OVERLAY_LEFT_COLUMN_WIDTH = 520f;
        private const float OVERLAY_RIGHT_COLUMN_WIDTH = 392f;
        private const float OVERLAY_BOTTOM_MARGIN = 10f;
        private const float OVERLAY_BOTTOM_DOCK_HEIGHT = OVERLAY_BOTTOM_SECTION_HEIGHT - OVERLAY_BOTTOM_MARGIN * 2f;
        private const float OVERLAY_RIGHT_TOP_PANEL_HEIGHT = 92f;
        private const float OVERLAY_RIGHT_DOCK_GAP = 8f;
        private const float OVERLAY_RIGHT_BOTTOM_RADAR_HEIGHT = OVERLAY_BOTTOM_DOCK_HEIGHT - OVERLAY_RIGHT_TOP_PANEL_HEIGHT - OVERLAY_RIGHT_DOCK_GAP;
        private const float OVERLAY_OUTLINE_BORDER_PX = 3f;
        private const float OVERLAY_LABEL_WIDTH = 92f;
        private const float OVERLAY_BODYCAM_LABEL_WIDTH = 200f;
        // OpenBodyCams reserves the upper-right corner for CAM/date/time telemetry.
        // Keep the player badge right-aligned but below that block.
        private const float OVERLAY_BODYCAM_LABEL_TOP_INSET = 78f;
        private const float OVERLAY_LABEL_HEIGHT = 22f;
        private const float OVERLAY_LABEL_INSET = 8f;
        private const float OVERLAY_STATUS_HEIGHT = 30f;
        private const float OVERLAY_BACKPLATE_MARGIN = 12f;
        private const float OVERLAY_BACKPLATE_RULE_PX = 3f;
        private const float OVERLAY_FRAME_CORNER_PX = 46f;
        private const float OVERLAY_FRAME_THICKNESS_PX = 2f;
        private const int OVERLAY_SCAN_LABEL_COUNT = 6;
        private const float OVERLAY_SCAN_LABEL_WIDTH = 190f;
        private const float OVERLAY_SCAN_LABEL_HEIGHT = 48f;
        private const float OVERLAY_SCAN_TRACER_THICKNESS = 3f;
        private const float OVERLAY_CONTEXT_DURATION = 8f;
        private const float CONTEXT_RAY_DISTANCE = 85f;
        private const float CONTEXT_SNAP_VIEWPORT_RADIUS = 0.095f;
        private const float ZOOM_MIN_FOV = 22f;
        // Terminal-like seat transition: long enough to read as a deliberate camera
        // move (the old 0.20s read as a teleport), short enough not to feel sluggish.
        private const float STATION_FOCUS_EXIT_SECONDS = 0.60f;
        private const float STATION_POSE_SETTLE_RATE = 20f;
        private const float STATION_POSE_SETTLE_TIMEOUT_SECONDS = 1.5f;
        private const float STATION_POSE_SETTLE_POSITION_TOLERANCE_M = 0.01f;
        private const float STATION_POSE_SETTLE_YAW_TOLERANCE_DEG = 0.5f;
        private const float STATION_POSE_SETTLE_CAMERA_TOLERANCE_DEG = 0.5f;
        private const float API_ROOT_WORLD_TELEMETRY_INTERVAL_SECONDS = 1f;
        // Round-1 Interactions API port: the camera begins immediately and is
        // settled on the button before the authored clip's frame-11 contact.
        private const float STATION_FOCUS_ENTER_BUTTON_PRESS_DELAY = 0f;
        // Timing derives from v5's 33 frames at 30 fps, not the retired runtime
        // HandEnter* drive: hold through contact, pan over frames 15-27, then
        // reach the clamped monitor view at the 1.10-second clip end.
        private const float STATION_FOCUS_ENTER_TOTAL_SECONDS = 1.10f;
        private const float STATION_INTRO_HAND_FOLLOW_SMOOTHING_RATE = 14f;
        private const float STATION_INTRO_HAND_FOLLOW_BLEND_IN_SECONDS = 0.15f;
        private const float STATION_INTRO_HAND_FOLLOW_BLEND_OUT_START_T = 0.73f;
        private const float STATION_INTRO_HAND_FOLLOW_MIN_DIRECTION_SQR_M = 0.0025f;
        private const string STATION_INTRO_HAND_FOLLOW_TARGET_NAME = "ArmsLeftArm_target";
        private const string STATION_INTRO_HAND_FOLLOW_TARGET_PATH =
            "ScavengerModelArmsOnly/metarig/spine.003/RigArms/LeftArm/ArmsLeftArm_target";
        private const float STATION_FOCUS_ENTER_KNOT_BUTTON = 0.218f;      // 0.24s: settled before 0.367s contact
        private const float STATION_FOCUS_ENTER_KNOT_BUTTON_HOLD = 0.455f; // 0.50s: begin frame-15-to-27 pan
        private const float STATION_FOCUS_ENTER_KNOT_LEVER = 0.818f;       // 0.90s: lever view at frame 27
        // Presentation-only widening at the two close-up beats. The eye path
        // remains unchanged so hand reach, contact timing, and camera motion
        // keep the authored choreography; the extra FOV eases back to zero at
        // the final knot so steady-state uses the player's preserved FOV.
        private const float STATION_FOCUS_ENTER_BUTTON_FOV_WIDEN_DEG = 6f;
        private const float STATION_FOCUS_ENTER_LEVER_FOV_WIDEN_DEG = 4f;
        private const float STATION_FOCUS_ENTER_BUTTON_LOOK_BLEND_END_T = 0.12f;
        // Camera-lean redesign (2026-07-22): the press eye is derived from the
        // button contact so the camera travels INTO arm reach (the shoulder
        // anchor rides the camera; nothing moves the arms camera-relative).
        // Baseline stance before the lean solve pulls it toward the button.
        private const float STATION_FOCUS_ENTER_BUTTON_EYE_FRACTION = 0.40f;
        // Max allowed anchor->contact distance at the press beat: usable left
        // reach 0.755 (0.778 chain x0.97) minus margin for the left shoulder's
        // offset from the anchor midpoint (Test 33 logs put the roots ~0.2m
        // either side of it; 0.70 left the finger hovering).
        // Two caveats, both 2026-07-28:
        //  - "yaw-flat" no longer describes the anchor unconditionally. Since
        //    b72d60f `Api Camera Anchor Pitch Relative` defaults true, so the
        //    anchor frame is the FULL pitched camera rotation unless a profile
        //    turns it off. ResolveApiShoulderAnchorTarget owns that choice.
        //  - the x0.97 usable-reach fraction is ApiReachAssistUsableReachFraction,
        //    which only applies inside the reach-assist block; that block is
        //    gated on `Api Reach Assist` (retired, default false), so at runtime
        //    the effective clamp is the full chain span, not 0.755.
        private const float STATION_FOCUS_ENTER_PRESS_REACH_BOUND_M = 0.60f;
        // Settle/lever bound is looser: pulling the authored settle eye is a
        // visible framing change, and the lever only needs the hand planted.
        // Test 39: 0.70 ate every attempt to move the settle eye back (pull
        // log 0.199m each enter). Usable left reach is (0.4037+0.3748)x0.97
        // = 0.755m, so 0.75 is the honest ceiling — beyond it the planted
        // hand physically cannot stay on the grip.
        private const float STATION_FOCUS_ENTER_LEVER_REACH_BOUND_M = 0.75f;
        // Test 39 (user-directed): the settle framing showed neither the
        // monitor top nor the throttle/hand — the operating viewpoint sits
        // too close/low. Offset the F1 focus placement back along its look
        // and slightly up before the lever-reach pull; the pull still clamps
        // whatever reach cannot afford. Applied only to the station FOCUS
        // anchor (never the radar glance) so every consumer — enter target,
        // path build, steady-state hold — shares one offset eye.
        // Test 41: still short — the monitor top edge was cut at frame top
        // and the throttle sat below the frame during control. More back +
        // notably more up (a higher eye compresses the vertical span the
        // desk-to-monitor sweep needs), plus a small look-up so the full
        // display fits. The 0.75 lever bound still clamps position; raising
        // the placement tilts the pull ray so the clamped eye lands higher.
        private const float STATION_FOCUS_EYE_PULLBACK_M = 0.22f;
        private const float STATION_FOCUS_EYE_RAISE_M = 0.12f;
        // Look-up applied after the pitch clamp (the clamp only caps
        // downward pitch, so this cannot fight it).
        private const float STATION_FOCUS_EYE_PITCH_UP_DEG = 3.5f;
        // Never lean the eye closer to the button than this, even if the
        // reach solve would allow it (keeps the near plane off the desk).
        private const float STATION_FOCUS_ENTER_MIN_CONTACT_EYE_DISTANCE_M = 0.35f;
        // Fallback only: the enter hand driver fires the flip at actual finger
        // contact (CompleteStationFeedFlipAtPressContact). This timer catches
        // configs where the local authored arms are disabled.
        private const float STATION_FOCUS_ENTER_FEED_FLIP_DELAY = 0.60f;
        private const float STATION_FOCUS_EXIT_HAND_RELEASE_DELAY = 0.15f;
        // Keep a short input margin after the 1.10-second enter/camera path.
        private const float STATION_CAMERA_CONTROL_ENABLE_DELAY = 1.35f;
        private const int INTRO_CAMERA_FOCUS_RENDER_TELEMETRY_BUDGET = 16;
        // The vanilla operator station uses a locked monitor focus. Holding SPACE
        // eases the seated camera toward the right radar screen with a small
        // head-tilt roll, then returns to the CCTV monitor when released.
        private const float STATION_RADAR_LOOK_YAW_DEG = 28f;
        private const float STATION_RADAR_LOOK_PITCH_DEG = -1.5f;
        private const float STATION_RADAR_LOOK_ROLL_DEG = -3.5f;
        // Fixed glance duration with smoothstep easing. An exponential blend moves
        // fastest on its first frame, which read as a jerk on both press and release.
        private const float STATION_RADAR_GLANCE_SECONDS = 0.46f;
        private const float STATION_THIRD_PERSON_PREVIEW_DISTANCE = 1.45f;
        private const float STATION_THIRD_PERSON_PREVIEW_SIDE_OFFSET = 0.82f;
        private const float STATION_THIRD_PERSON_PREVIEW_HEIGHT_OFFSET = 0.42f;
        private const float STATION_THIRD_PERSON_PREVIEW_TARGET_HEIGHT = 0.42f;
        private const float STATION_GLOBAL_THIRD_PERSON_DISTANCE = 2.10f;
        private const float STATION_GLOBAL_THIRD_PERSON_SIDE_OFFSET = 0.92f;
        private const float STATION_GLOBAL_THIRD_PERSON_HEIGHT_OFFSET = 0.34f;
        private const float STATION_GLOBAL_THIRD_PERSON_FOV = 68f;
        private const float STATION_THIRD_PERSON_ORBIT_YAW_MIN = -160f;
        private const float STATION_THIRD_PERSON_ORBIT_YAW_MAX = 160f;
        private const float STATION_THIRD_PERSON_ORBIT_PITCH_MIN = -48f;
        private const float STATION_THIRD_PERSON_ORBIT_PITCH_MAX = 58f;
        private const float FIRST_PERSON_ARMS_OFFSET_SPEED = 0.22f;
        private const float FIRST_PERSON_ARMS_OFFSET_FAST_SPEED = 1.20f;
        private const float FIRST_PERSON_ARMS_OFFSET_MAX = 2.50f;
        // The legacy-value migration guard retired with the arms-offset config
        // keys (#575): the offset no longer persists, so nothing stale can be
        // carried into the corrected reference frame.
        private const float FIRST_PERSON_ARMS_OFFSET_STATUS_INTERVAL = 0.25f;
        private const float RADAR_INSET_REFRESH_INTERVAL = 0.20f;
        private const int PHYSICAL_FOCUS_RT_WIDTH = 1920;
        private const int PHYSICAL_FOCUS_RT_HEIGHT = 1440;
        private const int PHYSICAL_FOCUS_UI_LAYER = 29;
        private const float FOCUS_PLAYER_HUD_ALPHA = 0.01f;
        private const float FOCUS_PLAYER_HUD_REFRESH_INTERVAL = 8.0f;
        private const float STATION_FOCUS_OVERLAY_REFRESH_INTERVAL = 0.20f;
        private const float STATION_CAMERA_RENDER_BOOST_SECONDS = 0.20f;
        private const float STATION_FOCUS_GAMEPLAY_FAR_CLIP_M = 55f;
        private const float AUDIO_PROXY_VOICE_REFRESH_INTERVAL = 0.35f;
        private const float FOCUS_MACHINE_LOOP_VOLUME = 0.22f;
        private const string FOCUS_AUDIO_DIR = "Audio";
        private const string FOCUS_CLICK_2_FILE = "ui_click_mx_2.ogg";
        private const string FOCUS_CLICK_3_FILE = "ui_click_mx_3.ogg";
        private const string FOCUS_MACHINE_LOOP_FILE = "old_machine_mx_2_loop.ogg";
        private const string FOCUS_PAGE_FILE = "ui_submit_mx_7.ogg";
        private const string FOCUS_PING_FILE = "retro_computer_beep_mx_1.ogg";
        private const string FOCUS_INTERACT_FILE = "retro_computer_beep_mx_2.ogg";
        private const string FOCUS_BOOT_FILE = "analog_computer_boot.ogg";
        private static readonly Color OVERLAY_ACTIVE_GREEN = new Color(0.20f, 1f, 0.40f, 1f);
        private static readonly Color OVERLAY_AMBER = new Color(1.0f, 0.72f, 0.18f, 1f);
        private static readonly Color OVERLAY_REC_RED = new Color(1f, 0.28f, 0.24f, 1f);
        private const float OVERLAY_REC_BLINK_PERIOD = 1.0f;
        private const float OVERLAY_REC_BLINK_ON_FRACTION = 0.55f;
        private static readonly Color OVERLAY_SCAN_BLUE = new Color(0.18f, 0.76f, 1f, 1f);
        private static readonly Color OVERLAY_SCAN_RED = new Color(1f, 0.25f, 0.22f, 1f);
        private static readonly string[] FocusHudNameTokens =
        {
            "health",
            "sprint",
            "stamina",
            "weight",
            "battery",
            "hotbar",
            "inventory",
            "itemslot",
            "item slot",
            "slot",
            "tooltip",
            "controltip",
            "control tip",
            "cursor",
            "scan",
        };

        private static GameObject _overlayRoot;
        private static GameObject _physicalFocusRenderRigRoot;
        private static RenderTexture _physicalFocusRenderTexture;
        private static Camera _physicalFocusUiCamera;
        private static Texture2D _crtScanlineTexture;
        private static Sprite _crtScanlineSprite;
        private static Texture2D _terminalBackdropTexture;
        private static Sprite _terminalBackdropSprite;
        private static RawImage[] _overlayRawImages;
        private static RectTransform[] _overlayPaneRects;
        private static RectTransform[] _overlayOutlineRects;
        private static PaneFrame[] _overlayPaneFrames;
        // T8 — per-pane outline Image behind each RawImage. PaneHighlight
        // toggles the color between transparent (inactive) and active-tint.
        // Lives on the overlay Canvas, sized larger than the RawImage so the
        // border peeks out around the camera feed edges.
        internal static Image[] OverlayOutlines { get; private set; }
        private static TextMeshProUGUI[] _overlayCameraLabels;
        private static Image[] _overlayCameraLabelBackgrounds;
        private static RectTransform[] _overlayCameraLabelRects;
        private static TextMeshProUGUI _overlayHeaderText;
        private static TextMeshProUGUI _overlayPageText;
        // Screen furniture in the header bar, mirroring the live monitor's clock and
        // REC light. Both read from CctvScreenClock / the same 1 Hz blink cycle so the
        // two surfaces cannot drift apart.
        private static TextMeshProUGUI _overlayClockText;
        private static TextMeshProUGUI _overlayRecText;
        private static Image _overlayRecDot;
        private static bool _overlayRecOn;
        private static float _nextOverlayHeaderRefreshTime;
        private static TextMeshProUGUI _overlayActiveText;
        private static RawImage _radarInsetImage;
        private static RectTransform _radarInsetRect;
        private static RectTransform _radarImageRect;
        private static TextMeshProUGUI _radarInsetStatus;
        private static RectTransform _operatorStatsRect;
        private static TextMeshProUGUI _operatorStatsText;
        private static RectTransform _signalLogRect;
        private static TextMeshProUGUI _signalLogTitleText;
        private static TextMeshProUGUI _signalLogText;
        private static TextMeshProUGUI _scanStatusText;
        private static GameObject _reviewMenuRoot;
        private static TextMeshProUGUI _reviewMenuText;
        private static GameObject _placementEditorRoot;
        private static TextMeshProUGUI _placementEditorText;
        private static Image _centerReticle;
        private static ScanLabel[] _scanLabels;
        private static LethalCCTVInputActions _inputActions;
        private static PlayerControllerB _audioListenerPlayer;
        private static GameObject _cameraAudioProxyRoot;
        private static AudioListener _cameraAudioProxyListener;
        private static AudioClip _switchSfx;
        private static AudioClip _scanSfx;
        private static AudioClip _pingSfx;
        private static AudioClip _rotateSfx;
        private static AudioClip[] _switchSfxPool;
        private static AudioClip _pageSfx;
        private static AudioClip _interactSfx;
        private static AudioClip _bootSfx;
        private static AudioClip _machineLoopSfx;
        private static GameObject _focusMachineLoopRoot;
        private static AudioSource _focusMachineLoopSource;
        private static bool _providedFocusAudioLoadStarted;
        private static float _nextRotateSfxTime;
        private static float _nextStatsRefreshTime;
        private static float _nextRadarInsetRefreshTime;
        private static float _nextSignalLogRefreshTime;
        private static float _scanVisibleUntil;
        private static bool _turretPageActive;
        private static float _turretHitmarkerUntil;
        private static bool _turretHitmarkerEnemy;
        private static bool _lastHackDockState;
        private static bool _lastDockTurretState;
        private static bool _focusDockLayoutInitialized;
        private static readonly Dictionary<CCTVCamera, float> _zoomByCamera = new Dictionary<CCTVCamera, float>();
        private static readonly List<FocusHudDimTarget> _focusHudDimTargets = new List<FocusHudDimTarget>(64);
        private static readonly HashSet<int> _focusHudDimTargetIds = new HashSet<int>();
        private static readonly List<FocusHudGraphicTarget> _focusHudGraphicTargets = new List<FocusHudGraphicTarget>(96);
        private static readonly HashSet<int> _focusHudGraphicTargetIds = new HashSet<int>();
        private static bool _stationCameraControlActive;
        private static bool _stationCameraControlEnablePending;
        private static float _stationCameraControlEnableAt;
        private static ContextTarget _contextTarget;
        private static float _nextFocusHudDimRefreshAt;
        private static bool _focusHudDimActive;
        private static bool _focusHudSceneScanCompleted;
        private static float _nextStationFocusOverlayRefreshAt;
        private static float _stationCameraRenderBoostUntil;
        private static CCTVCamera _audioProxyCamera;
        private static GameObject _audioProxyTarget;
        private static bool _audioProxyFlip;
        private static float _nextAudioProxyVoiceRefreshAt;

        internal static bool IsFocused { get; private set; }
        internal static bool IsEntryPoseSettleActive => _stationPoseSettleActive;
        internal static bool IsEnterCameraPathComplete =>
            IsFocused &&
            !_focusViewExitPending &&
            (!_physicalFocusViewActive || !_focusViewAnimating);
        /// <summary>
        /// Hitch-clamped elapsed seconds of the enter camera path, or -1 when
        /// the enter path is not animating. The authored hand table and the
        /// press feed-flip must ride THIS clock, not wall time: session-begin
        /// stalls pause the camera path (Test 34), and a wall clock would
        /// fire the press while the camera is still approaching.
        /// </summary>
        internal static float EnterPresentationClockSeconds =>
            _focusViewAnimating && !_focusViewExitPending && _focusViewEnterPath != null
                ? _focusViewAnimationElapsedSeconds
                : -1f;
        internal static bool IsStationCameraFeedInteractionActive =>
            IsFocused &&
            Time.unscaledTime < _stationCameraRenderBoostUntil;
        internal static bool IsStationRadarLookActive =>
            IsFocused &&
            (_radarLookEventState ||
             _stationRadarLookBlend > 0.001f ||
             IsStationRadarLookHeld() ||
             CCTVOperatorStation.IsEditingDebugPlacement("radar"));

        internal static bool TryGetApiOperatorFloorRootDelta(out float worldDeltaY)
        {
            worldDeltaY = _stationPlayerPoseFloorDeltaWorldY;
            return _stationPlayerPoseLockActive && _stationPlayerPoseUsesApiFloorRoot;
        }

        private static void BoostStationCameraRenderCadence(float seconds = STATION_CAMERA_RENDER_BOOST_SECONDS)
        {
            if (!IsFocused)
                return;

            float until = Time.unscaledTime + Mathf.Max(0f, seconds);
            if (until > _stationCameraRenderBoostUntil)
                _stationCameraRenderBoostUntil = until;
        }
        private static bool _isExitingFocus;

        // One-shot enter/exit diagnostics (issues #19/#20). EnterPerf logs the
        // per-phase cost of the EnterFocus frame; ExitSeam samples the camera and
        // body pose through the exit wind-down so the lerp-target vs restored-pose
        // seam is visible in LogOutput.log.
        private static bool _enterPerfLogPending;
        private static int _enterPerfFrame;
        private static float _exitSeamStartedAt;
        private static float _exitSeamLogUntil;
        private static float _exitSeamNextLogAt;
        private static Quaternion _exitSeamReferenceRotation = Quaternion.identity;
        private static bool _settleViewFramingLogPending;
        private static bool _arrivalViewFramingLogPending;
        private static bool _exitHandbackAnimationCompletePoseCaptured;
        private static Vector3 _exitHandbackAnimationCompletePosition;
        private static Quaternion _exitHandbackAnimationCompleteRotation = Quaternion.identity;
        private static bool _exitPostureLogPending;
        private static PlayerControllerB _exitPosturePlayer;
        private static float _exitPostureRestoredAt;
        private static float _exitPostureLogAt;
        private static int _exitPostureSampleIndex;
        private static PlayerControllerB _exitCameraInvariantPlayer;
        private static int _exitCameraInvariantScheduledFrame = -1;
        internal static bool IsStationCameraControlActive => _stationCameraControlActive;
        internal static bool IsStationThirdPersonDebugViewActive =>
            IsFocused &&
            (_stationThirdPersonPreviewActive || _stationThirdPersonDebugPersistent);
        internal static bool IsStationEditMenuOpen => _stationEditMenuOpen;
        internal static bool IsStationEditThirdPersonDebugActive =>
            _stationEditMenuOpen && _stationEditMenuTarget == StationEditMenuTarget.ThirdPersonDebug;
        internal static bool IsTurretPageActive => _turretPageActive && ShipTurretController.IsUnlocked;
        private static bool IsHackingOverlayOpen => _hackingOverlay != null && _hackingOverlay.IsOpen;

        // Phase 1.5a Round 2 — same-frame race survival for ESC interception. LcInputActions
        // ESC handler and vanilla PlayerControllerB.OpenMenu_performed both fire on the same
        // physical key press in undefined order; if our handler clears IsFocused first, the
        // Harmony prefix on OpenQuickMenu would read IsFocused=false and let the pause menu
        // open. This flag latches "focus was active when ESC was pressed" and survives the
        // entire frame so the prefix can suppress regardless of handler ordering.
        internal static bool WasActiveThisFrame { get; private set; }
        private static int _wasActiveSetFrame = -1;

        private sealed class ScanLabel
        {
            public RectTransform Rect;
            public Image Tracer;
            public Image Background;
            public Image Top;
            public Image Bottom;
            public Image Left;
            public Image Right;
            public Image Center;
            public TextMeshProUGUI Header;
            public TextMeshProUGUI SubText;
        }

        private sealed class PaneFrame
        {
            public Image[] Segments;
        }

        private sealed class ContextTarget
        {
            public Component Component;
            public string DisplayName;
            public string CommandHint;
            public Vector3 Position;
            public float Radius;
            public bool Commandable;
            public Color Color;
            public float ExpiresAt;
        }

        private sealed class FocusHudDimTarget
        {
            public CanvasGroup Group;
            public float OriginalAlpha;
            public int InstanceId;
            public bool AddedByUs;
        }

        private sealed class FocusHudGraphicTarget
        {
            public Graphic Graphic;
            public float OriginalAlpha;
            public int InstanceId;
        }

        // The single-feed station always renders through pane slot 0. ActiveSlot remains
        // as the animation/network compatibility value while _activeFeedKind and the
        // player slot identify the selected entry in the unified feed roster.
        internal static int ActiveSlot { get; private set; }
        private static SurveillanceFeedKind _activeFeedKind = SurveillanceFeedKind.Facility;
        private static int _activeBodycamPlayerSlot = -1;

        internal static bool IsBodycamFeedActive =>
            !_turretPageActive && _activeFeedKind == SurveillanceFeedKind.Bodycam;
        internal static bool IsFacilityFeedActive =>
            !_turretPageActive && _activeFeedKind == SurveillanceFeedKind.Facility;
        internal static int ActiveBodycamFeedIndex => IsBodycamFeedActive
            ? OpenBodyCamsCompat.IndexOfPlayerSlot(_activeBodycamPlayerSlot)
            : -1;

        // Look-suppression stash/restore. _priorDisableLook captures the
        // PRE-focus value so ExitFocus restores faithfully (not "unconditional
        // false" — vanilla may own a true via inSpecialAnimation, and SPEC
        // Decision 3 explicitly rejects clobbering it). _focusedPlayer holds
        // the player reference the stash was taken from; it is nulled after
        // restore so a second ExitFocus call cannot write a stale reference.
        private static bool _priorDisableLook;
        private static bool _priorDisableMove;
        private static bool _priorDisableInteract;
        private static bool _cameraPlacementEditModeOpen;
        private static bool _reviewMenuOpen;
        private static int _reviewMenuSelectedIndex;
        private static readonly List<string> _reviewSelectedTags = new List<string>();
        private static bool _reviewMenuTagMode;
        private static bool _placementEditorOpen;
        private static PlacementEditSession _placementEditSession;
        private static PlayerControllerB _focusedPlayer;
        private static Camera _physicalFocusCamera;
        private static Transform _physicalFocusCameraTransform;
        private static Vector3 _priorFocusCameraLocalPosition;
        private static Quaternion _priorFocusCameraLocalRotation;
        private static float _priorFocusCameraFov;
        private static bool _focusRenderBudgetActive;
        private static Camera _focusRenderBudgetCamera;
        private static float _focusRenderBudgetSavedFarClip;
        // World pose captured before focus camera control begins. Station focus
        // pins the player root separately so the real first-person arms inherit
        // one deterministic operator pose instead of the entry position.
        private static Vector3 _priorFocusCameraWorldPosition;
        private static Quaternion _priorFocusCameraWorldRotation;
        private static Vector3 _focusViewAnimationStartPosition;
        private static Quaternion _focusViewAnimationStartRotation;
        private static float _focusViewAnimationStartFov;
        private static Vector3 _focusViewAnimationTargetPosition;
        private static Quaternion _focusViewAnimationTargetRotation;
        private static float _focusViewAnimationTargetFov;
        private static float _focusViewAnimationStartedAt;
        private static float _focusViewAnimationDelayUntil;
        private static float _focusViewAnimationDuration;
        // Hitch-proof choreography clock (Test 34): session-begin work stalled
        // the first path frames 100-290ms and the wall clock skipped the whole
        // walk-in glide (second rendered frame already at rawT=0.28). The path
        // advances by at most one nominal step per rendered frame, so heavy
        // frames pause the animation instead of skipping it.
        private const float FOCUS_ANIMATION_MAX_FRAME_STEP_SECONDS = 0.05f;
        private static float _focusViewAnimationElapsedSeconds;
        private static int _focusViewAnimationLastAdvanceFrame = -1;
        private static bool _focusViewAnimating;
        private static bool _focusViewExitPending;
        private static int _introCameraRenderedFramesLogged;
        private static int _introCameraLastRenderedFrame = -1;
        // Choreographed enter path (2026-07-12 session 3): piecewise knots the
        // enter camera walks through (button focus -> lever look -> monitors).
        // Each segment eases independently; equal adjacent poses hold the view.
        private struct FocusPathKnot
        {
            internal float T;
            internal Vector3 Position;
            internal Quaternion Rotation;
        }
        private static FocusPathKnot[] _focusViewEnterPath;
        private static Transform _stationIntroHandFollowTarget;
        private static Quaternion _stationIntroHandFollowSmoothedRotation = Quaternion.identity;
        private static bool _stationIntroHandFollowRotationInitialized;
        private static bool _stationIntroHandFollowResolutionAttempted;
        private static bool _stationIntroHandFollowTargetWasResolved;
        private static bool _stationIntroHandFollowActivated;
        private static bool _stationIntroHandFollowActiveLogged;
        private static bool _stationIntroHandFollowFallbackLogged;
        private static bool _stationIntroHandFollowSettleLogged;
        // Enter sweep waypoint (2026-07-12): the enter camera path passes a
        // mid rotation aimed at the ship throttle/lever (button -> throttle ->
        // monitor, user design). Quadratic de-Casteljau blend through it.
        private static bool _focusViewAnimationHasWaypoint;
        private static Vector3 _focusViewWaypointPosition;
        private static Quaternion _focusViewWaypointRotation;
        private static bool _physicalFocusViewActive;
        private static bool _stationThirdPersonPreviewActive;
        private static bool _stationThirdPersonDebugPersistent;
        private static bool _stationGlobalThirdPersonCameraActive;
        private static Camera _stationGlobalThirdPersonCamera;
        private static Transform _stationGlobalThirdPersonCameraTransform;
        private static Vector3 _stationGlobalThirdPersonSavedLocalPosition;
        private static Quaternion _stationGlobalThirdPersonSavedLocalRotation;
        private static float _stationGlobalThirdPersonSavedFov;
        private static bool _stationGlobalThirdPersonSaved;
        private static bool _stationGlobalThirdPersonFramingLogged;
        private static float _stationThirdPersonOrbitYawDeg;
        private static float _stationThirdPersonOrbitPitchDeg;
        private enum StationEditMenuTarget
        {
            None,
            BothHands,
            LeftHand,
            RightHandCamera,
            RightHandAction,
            FocusView,
            RadarView,
            ThirdPersonDebug,
            ThirdPersonPlayer,
        }

        private static bool _stationEditMenuOpen;
        // #541 - seeded true so a config that starts false closes on the first
        // tick instead of leaving stale editor state that nothing can reach.
        private static bool _operatorDebugToolsEnabledLastFrame = true;
        private static StationEditMenuTarget _stationEditMenuTarget;
        private static bool _stationPoseTrackingLogged;
        private struct StationPlayerPoseSnapshot
        {
            internal bool Valid;
            internal PlayerControllerB Player;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Vector3 ServerPosition;
            internal bool SnapToServerPosition;
            internal bool DisableSyncInAnimation;
            internal bool DisableLookInput;
            internal bool FreeRotationInInteractAnimation;
            internal bool ClampLooking;
            internal float MinVerticalClamp;
            internal float MaxVerticalClamp;
            internal float HorizontalClamp;
            internal bool InSpecialInteractAnimation;
            internal bool EnteringSpecialAnimation;
            internal bool HadController;
            internal bool ControllerDetectCollisions;
            internal bool ControllerEnabled;
            internal bool WasCrouching;
            internal bool CameraBaselineCaptured;
            internal Vector3 CameraPlayerLocalPosition;
        }

        private static bool _stationPoseSettleActive;
        private static bool _stationPoseSettleTransferToFocus;
        private static PlayerControllerB _stationPoseSettlePlayer;
        private static Transform _stationPoseSettleAnchor;
        private static Vector3 _stationPoseSettleTargetPosition;
        private static Quaternion _stationPoseSettleTargetRotation;
        private static float _stationPoseSettleTargetCameraUp;
        private static bool _stationPoseSettleEntryCameraPoseCaptured;
        private static Vector3 _stationPoseSettleEntryCameraWorldPosition;
        private static Quaternion _stationPoseSettleEntryCameraWorldRotation = Quaternion.identity;
        // Render-only camera pin during the settle (Test 34): the settle rides
        // the camera to the desk in ~2 heavy frames, then the enter path snaps
        // it back to the entry pose - a visible back-and-forth teleport. The
        // pin holds the RENDERED camera position at the entry capture and is
        // undone at the next tick before any logic or capture reads it.
        private static bool _stationPoseSettleCameraRenderPinActive;
        private static Vector3 _stationPoseSettleCameraRenderPinSavedPosition;
        private static StationPlayerPoseSnapshot _stationPoseSettleSnapshot;
        private static float _stationPoseSettleStartedAt;
        private static int _stationPoseSettleFrames;
        private static int _stationPoseSettleHardSetFrame = -1;
        private static bool _stationPlayerPoseLockActive;
        private static PlayerControllerB _stationPlayerPosePlayer;
        private static Vector3 _stationPlayerPoseTargetPosition;
        private static Quaternion _stationPlayerPoseTargetRotation = Quaternion.identity;
        private static string _stationPlayerPoseTargetAnchorKind;
        private static bool _stationPlayerPoseApiModeConfigured;
        private static bool _stationPlayerPoseUsesApiFloorRoot;
        private static float _stationPlayerPoseFloorDeltaWorldY;
        private static float _stationPlayerPoseNextRootTelemetryAt;
        private static Vector3 _stationPlayerPoseSavedPosition;
        private static Quaternion _stationPlayerPoseSavedRotation;
        private static Quaternion _stationPlayerPoseHandbackRotation = Quaternion.identity;
        private static float _stationPlayerPoseHandbackCameraPitch;
        private static Vector3 _stationPlayerPoseSavedServerPosition;
        private static bool _stationPlayerPoseSavedSnapToServerPosition;
        private static bool _stationPlayerPoseSavedDisableSyncInAnimation;
        private static bool _stationPlayerPoseSavedFreeRotationInInteractAnimation;
        private static bool _stationPlayerPoseSavedInSpecialInteractAnimation;
        private static bool _stationPlayerPoseSavedEnteringSpecialAnimation;
        private static bool _stationPlayerPoseHadController;
        private static bool _stationPlayerPoseSavedControllerDetectCollisions;
        private static bool _stationPlayerPoseSavedControllerEnabled;
        private static bool _stationPlayerPoseSavedWasCrouching;
        private static bool _stationPlayerCameraBaselineCaptured;
        private static Vector3 _stationPlayerCameraPlayerLocalPosition;
        private static CCTVLocalCameraPositionStabilizer _stationPlayerCameraPositionStabilizer;
        // First-person/local renderers and visor can sit directly on the seated
        // camera transition path. Hide only for the local focus view and restore
        // from saved state on every cleanup path.
        private sealed class LocalRendererState
        {
            public Renderer Renderer;
            public bool WasEnabled;
            public Material[] SharedMaterials;
            public ShadowCastingMode ShadowCastingMode;
            public bool ReceiveShadows;
            public bool ForceRenderingOff;
            public bool AllowOcclusionWhenDynamic;
            public bool HasUpdateWhenOffscreen;
            public bool UpdateWhenOffscreen;
            public Transform Transform;
            public Vector3 LocalPosition;
            public bool ApplyFirstPersonArmsOffset;
            public Transform OffsetTransform;
            public Vector3 OffsetLocalPosition;
            public Quaternion OffsetLocalRotation;
        }

        private sealed class LocalLodGroupState
        {
            public LODGroup Group;
            public bool WasEnabled;
        }

        private static readonly List<LocalRendererState> _focusHiddenLocalRenderers = new List<LocalRendererState>();
        private static readonly List<LocalRendererState> _focusForcedLocalRenderers = new List<LocalRendererState>();
        // EndPhysicalFocusView restores and clears the live focus bookkeeping
        // before the authored controller finishes its wind-down. Retain the
        // original focus-start states separately so controller teardown can
        // re-apply the exact vanilla renderer/visor contract at the real exit
        // seam, after the CCTV controller has stopped writing the arms rig.
        private static readonly List<LocalRendererState> _controllerRestoreLocalRenderers = new List<LocalRendererState>();
        private static PlayerControllerB _controllerRestorePlayer;
        private static Transform _controllerRestoreLocalVisor;
        private static Vector3 _controllerRestoreLocalVisorLocalPosition;
        private static Quaternion _controllerRestoreLocalVisorLocalRotation = Quaternion.identity;
        private static Vector3 _controllerRestoreLocalVisorLocalScale = Vector3.one;
        private static readonly List<LocalRendererState> _previewForcedLocalRenderers = new List<LocalRendererState>();
        private static readonly List<LocalLodGroupState> _previewForcedLodGroups = new List<LocalLodGroupState>();
        private static bool _previewRendererFailureLogged;
        private static bool _previewCameraFramingLogged;
        private static bool _localFirstPersonArmsActiveLogged;
        private static bool _localFirstPersonArmsMissingLogged;
        private static bool _firstPersonArmsOffsetLoaded;
        private static Vector3 _firstPersonArmsOffset;
        private static bool _firstPersonArmsOffsetDirty;
        private static bool _firstPersonArmsOffsetEditingLastFrame;
        private static float _nextFirstPersonArmsOffsetStatusAt;
        private static bool _firstPersonArmsCameraPoseCaptured;
        private static Transform _firstPersonArmsCameraPoseRoot;
        private static bool _firstPersonArmsCameraFollowLogged;
        // Per-frame presentation stash consumed by the post-camera hand hook:
        // the animator-owned root pose read before the glue write, and the
        // camera-glued pose actually written. RigBuilder.Evaluate resets the
        // root to the animator pose, so the hook needs both to remap its
        // world-space targets and re-assert the glued pose afterwards.
        private static int _firstPersonArmsPresentationFrame = -1;
        private static Transform _firstPersonArmsPresentationRoot;
        private static Vector3 _firstPersonArmsPresentationAnimPosition;
        private static Quaternion _firstPersonArmsPresentationAnimRotation = Quaternion.identity;
        private static Vector3 _firstPersonArmsPresentationFinalPosition;
        private static Quaternion _firstPersonArmsPresentationFinalRotation = Quaternion.identity;
        private static Camera _previewForcedCamera;
        private static int _previewSavedCameraCullingMask;
        private static bool _previewCameraCullingMaskSaved;
        private static Transform _focusHiddenLocalVisor;
        private static Vector3 _focusHiddenLocalVisorLocalPosition;
        private static Quaternion _focusHiddenLocalVisorLocalRotation;
        private static Vector3 _focusHiddenLocalVisorLocalScale;
        private static Vector3 _focusHiddenLocalVisorHiddenWorldPosition;
        private static bool _holdLocalObstructorsForInteractionsApiRestore;
        private static readonly HashSet<string> _localObstructorCallLogs =
            new HashSet<string>(StringComparer.Ordinal);
        // #452: one boundary-recovery line per (operation:reason), cleared with the
        // obstructor log set so a fresh focus session logs its boundaries again.
        private static readonly HashSet<string> _boundaryInputRecoveryLogs =
            new HashSet<string>(StringComparer.Ordinal);
        private static float _stationRadarLookBlend;
        // The body-yaw and cameraUp the station actually renders with SPACE up.
        // Sampled on every idle glance frame; the SPACE glance blends out of
        // these rather than out of the focus anchor, which is a different pose.
        private static float _stationGlanceRestYawDeg;
        private static float _stationGlanceRestPitchDeg;
        // Press-contact feed flip (see STATION_FOCUS_ENTER_FEED_FLIP_DELAY).
        private static bool _stationFeedFlipPending;
        private static float _stationFeedFlipAt;
        private static float _stationRadarLookSuppressedUntil;
        private static float _stationFocusAnchorForcedUntil;
        private static bool _radarLookEventState;
        private static bool _chairEventActive;
        private sealed class PlacementEditSession
        {
            public Transform Transform;
            public CCTVCamera Camera;
            public Tile Tile;
            public string ObjectKind;
            public string ObjectId;
            public string Label;
            public Vector3 StartPosition;
            public Quaternion StartRotation;
            public bool CreatedDuringEdit;
            public int PreviousPage;
            public int PreviousSlot;
            // PLACE MAINFRAME mode: the mainframe instance is moved directly as the live preview
            // and a temporary framing camera (PreviewCamera) is bound to the active pane so the
            // operator sees it in third-person without moving the player body.
        }

        private static readonly CameraPlacementReviewRating[] ReviewMenuRatings =
        {
            CameraPlacementReviewRating.Perfect,
            CameraPlacementReviewRating.TooLow,
            CameraPlacementReviewRating.Floating,
            CameraPlacementReviewRating.TooHigh,
            CameraPlacementReviewRating.PoorCoverage,
            CameraPlacementReviewRating.BlockedView,
            CameraPlacementReviewRating.LookingAtWall,
            CameraPlacementReviewRating.SkyboxVisible,
            CameraPlacementReviewRating.Duplicate,
            CameraPlacementReviewRating.ImportantRoom,
        };

        private static readonly string[] ReviewMenuLabels =
        {
            "PERFECT",
            "TOO LOW / FLOOR",
            "FLOATING",
            "TOO HIGH",
            "POOR COVERAGE",
            "BLOCKED VIEW",
            "LOOKING AT WALL",
            "SKYBOX VISIBLE",
            "DUPLICATE",
            "IMPORTANT ROOM",
        };

        private static readonly string[] ReviewTagValues =
        {
            CameraPlacementReviewTags.DoorwayVisible,
            CameraPlacementReviewTags.DoorwayNotVisible,
            CameraPlacementReviewTags.FramesRoomWell,
            CameraPlacementReviewTags.DoesNotFrameRoom,
            CameraPlacementReviewTags.StuckInFurniture,
            CameraPlacementReviewTags.PipeObstructed,
            CameraPlacementReviewTags.CoversMainframe,
            CameraPlacementReviewTags.GoodHeight,
            CameraPlacementReviewTags.GoodCorner,
            CameraPlacementReviewTags.MainframeRoom,
            CameraPlacementReviewTags.BadMainframeRoom,
            CameraPlacementReviewTags.CompanyStashRoom,
            CameraPlacementReviewTags.BadCompanyStashRoom,
        };

        private static readonly string[] ReviewTagLabels =
        {
            "DOORWAY VISIBLE",
            "DOORWAY NOT VISIBLE",
            "FRAMES ROOM WELL",
            "DOES NOT FRAME ROOM",
            "STUCK IN FURNITURE/FIXTURE",
            "PIPE/OBSTRUCTION BLOCKING VIEW",
            "COVERS MAINFRAME",
            "GOOD HEIGHT",
            "GOOD CORNER",
            "MAINFRAME ROOM",
            "BAD MAINFRAME ROOM",
            "COMPANY STASH ROOM",
            "BAD COMPANY STASH ROOM",
        };

        internal static bool IsReviewMenuOpen => _reviewMenuOpen;
    }
}
