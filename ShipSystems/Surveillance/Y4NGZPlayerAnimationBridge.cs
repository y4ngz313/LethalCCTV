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
    /// Drives CCTV operator player animations (Scanvan-style controller swap).
    /// The authored controller ships in y4ngz-cctv-playeranimations.lethalbundle;
    /// when it is absent the bridge stays quiet and the legacy runtime pose/IK
    /// fallback provides a rough stance instead.
    ///
    /// The local player's session is fed by CCTVStationEvents; remote players'
    /// sessions are created by CCTVOperatorAnimSync from network messages so
    /// every client sees the operator lean in and work the console.
    /// </summary>
    internal static class Y4NGZPlayerAnimationBridge
    {
        private const string PrimaryBundleFile = "y4ngz-cctv-playeranimations.lethalbundle";
        private const string SecondaryBundleFile = "y4ngz-cctv-playeranimations.bundle";

        private const string PrimaryControllerAsset = "Y4NGZ_CCTV_PlayerMetarig.controller";
        private const string SecondaryControllerAsset = "Y4NGZ_CCTV_PlayerMetarig";

        internal const string SeatedBool = "Y4NGZ_CCTV_Seated";
        internal const string CameraControlBool = "Y4NGZ_CCTV_CameraControl";
        internal const string RadarLookBool = "Y4NGZ_CCTV_RadarLook";
        internal const string JoystickXFloat = "Y4NGZ_CCTV_JoystickX";
        internal const string JoystickYFloat = "Y4NGZ_CCTV_JoystickY";
        internal const string ActiveSlotInt = "Y4NGZ_CCTV_ActiveSlot";
        internal const string ActionButtonInt = "Y4NGZ_CCTV_ActionButton";
        internal const string EnterTrigger = "Y4NGZ_CCTV_Enter";
        internal const string ExitTrigger = "Y4NGZ_CCTV_Exit";
        internal const string SelectCameraTrigger = "Y4NGZ_CCTV_SelectCamera";
        internal const string JoystickGrabTrigger = "Y4NGZ_CCTV_JoystickGrab";
        internal const string JoystickReleaseTrigger = "Y4NGZ_CCTV_JoystickRelease";
        internal const string ButtonPressTrigger = "Y4NGZ_CCTV_ButtonPress";

        private const float FirstPersonRightHandTuneMoveSpeed = 0.35f;
        private const float FirstPersonRightHandTuneMoveFastSpeed = 1.25f;
        private const float FirstPersonRightHandTuneRotationSpeed = 42f;
        private const float FirstPersonRightHandTuneRotationFastSpeed = 120f;
        private const float FirstPersonRightHandTuneOffsetMax = 2.0f;
        private const float FirstPersonRightHandTuneEulerMax = 180f;
        private const float FirstPersonRightHandTuneStatusInterval = 0.25f;

        private static readonly RequiredAnimatorParameter[] RequiredParameters =
        {
            new RequiredAnimatorParameter(SeatedBool, AnimatorControllerParameterType.Bool),
            new RequiredAnimatorParameter(CameraControlBool, AnimatorControllerParameterType.Bool),
            new RequiredAnimatorParameter(RadarLookBool, AnimatorControllerParameterType.Bool),
            new RequiredAnimatorParameter(JoystickXFloat, AnimatorControllerParameterType.Float),
            new RequiredAnimatorParameter(JoystickYFloat, AnimatorControllerParameterType.Float),
            new RequiredAnimatorParameter(ActiveSlotInt, AnimatorControllerParameterType.Int),
            new RequiredAnimatorParameter(ActionButtonInt, AnimatorControllerParameterType.Int),
            new RequiredAnimatorParameter(EnterTrigger, AnimatorControllerParameterType.Trigger),
            new RequiredAnimatorParameter(ExitTrigger, AnimatorControllerParameterType.Trigger),
            new RequiredAnimatorParameter(SelectCameraTrigger, AnimatorControllerParameterType.Trigger),
            new RequiredAnimatorParameter(JoystickGrabTrigger, AnimatorControllerParameterType.Trigger),
            new RequiredAnimatorParameter(JoystickReleaseTrigger, AnimatorControllerParameterType.Trigger),
            new RequiredAnimatorParameter(ButtonPressTrigger, AnimatorControllerParameterType.Trigger),
        };

        private static readonly string[] ExpectedAdditiveLayerNames =
        {
            "CCTVFirstPersonArms",
            "CCTVLeftHand",
            "CCTVGlance",
        };

        private static readonly string[] RequiredVanillaLayerNames =
        {
            "UpperBodyEmotes",
            "EmotesNoArms",
            "HoldingItemsRightHand",
            "HoldingItemsBothHands",
            "SpecialAnimations",
        };

        private static bool _initialized;
        private static OperatorAnimSession _localSession;

        private static AssetBundle _controllerBundle;
        private static RuntimeAnimatorController _cctvController;
        private static bool _controllerLoadAttempted;
        private static bool _missingControllerLogged;
        private static bool _controllerDiagnosticsLogged;
        private static bool _controllerContractLogged;
        private static bool _controllerLayerWarningLogged;
        private static bool _controllerRejected;
        private static bool _controllerRejectedLogged;
        private static string _rejectedControllerName;
        private static string _controllerRejectReason;
        private static bool _localAuthoredSuppressedLogged;
        private static FirstPersonRightHandTuneTarget _firstPersonRightHandTuneTarget;
        private static bool _firstPersonRightHandEditModeActive;
        private static bool _firstPersonRightHandTuneEditingLastFrame;
        private static float _nextFirstPersonRightHandTuneStatusAt;
        private static bool _firstPersonLeftHandEditModeActive;
        // #575: the 18 debug-nudge hand offset/euler keys left the config file.
        // They only ever existed to persist an in-session developer nudge, and
        // every one of them shipped at zero, so the tuning now lives in memory
        // for the length of the session. The F10 / Alt+1 / Alt+2 edit modes are
        // unchanged; a dialled-in pose is transcribed into the authored clip or
        // into the rest-frame constants above, not left in a user's cfg.
        private static FirstPersonRightHandPoseTuning _blueRightHandPoseTuning;
        private static FirstPersonRightHandPoseTuning _greenRightHandPoseTuning;
        private static FirstPersonRightHandPoseTuning _leftHandPoseTuning;
        private static bool _firstPersonLeftHandTuneEditingLastFrame;
        private static float _nextFirstPersonLeftHandTuneStatusAt;
        private static bool _localAuthoredOperatorAnimationsEnabled;

        internal static bool IsFirstPersonRightHandTuningHotkeyActive { get; private set; }
        internal static bool IsFirstPersonLeftHandTuningHotkeyActive { get; private set; }
        internal static bool IsFirstPersonHandTuningHotkeyActive =>
            IsFirstPersonRightHandTuningHotkeyActive || IsFirstPersonLeftHandTuningHotkeyActive;
        internal static bool IsFirstPersonHandEditModeActive =>
            _firstPersonRightHandEditModeActive || _firstPersonLeftHandEditModeActive;

        internal static bool UseLocalAuthoredOperatorAnimations =>
            _localAuthoredOperatorAnimationsEnabled;

        internal static bool UseInteractionsApiOperatorSession =>
            CCTVOperatorInteractionsBridge.ConfigEnabled;

        /// <summary>
        /// #593/#638: the legacy path is the API's automatic runtime fallback.
        /// A missing Interactions install or rejected pack must still get the
        /// authored controller, first-person arms layer, and manual arm solve.
        /// The old player-facing API/local-authored switches are retired; the
        /// local flag is now only a temporary developer hand-tuning override.
        /// </summary>
        internal static bool UseAuthoredLegacyOperatorPresentation =>
            UseLocalAuthoredOperatorAnimations || UseInteractionsApiOperatorSession;

        // #575 config-surface reduction: the operator-presentation family below
        // is calibration, not preference. Every one of these already sat at its
        // code default even in the tuned Gale "Current" profile, so promoting
        // them to constants is value-preserving and removes a fresh-install
        // drift hazard. Re-tuning means editing these constants.
        internal static bool UseLiveArmsForLocalOperator => true;

        internal static bool IsLocalInteractionsApiSessionActive =>
            _localSession != null && _localSession.UsesInteractionsApi &&
            (_localSession.IsActive || _localSession.IsWindingDown);

        internal static bool ShouldUseLegacyFirstPersonArmsPresentation =>
            !UseInteractionsApiOperatorSession ||
            (_localSession != null && !_localSession.UsesInteractionsApi);

        // #593: a live session that is not driving a dedicated viewmodel is
        // rendering the world body, so its first-person arms belong on screen
        // whichever path owns the animator. The old extra
        // `UsesInteractionsApi || UseLocalAuthoredOperatorAnimations` term left
        // `thisPlayerModelArms` in MonitorFocus's hidden-obstructor set for the
        // whole API-fallback session, which is the armless enter intro.
        internal static bool ShouldShowAuthoredFirstPersonArms =>
            _localSession != null
                ? !_localSession.UsesDedicatedLocalViewmodel
                : UseAuthoredLegacyOperatorPresentation;

        internal static bool UseManualFirstPersonArmIk => true;

        internal static bool UseFirstPersonShoulderAnchor => true;

        internal static bool UseApiOperatorRootAtFloorLevel => true;

        // The Api shoulder/camera anchor family is calibration, not preference:
        // the values below are the verified Gale "Current"-profile tuning,
        // promoted to constants for 1.0 (#575 config-surface reduction). Fresh
        // installs previously regenerated stale defaults and put the shoulder
        // caps in frame (round-6 Test 3). Re-tuning means editing these
        // constants; no runtime dial exists for this family.
        internal static bool UseApiStationShoulderAnchor => true;

        internal static Vector3 ApiShoulderAnchorStation =>
            new Vector3(0.06f, 1.62f, 0.60f);

        internal static bool UseApiCameraRelativeIntroAnchor => true;

        internal static bool ApiKeepCameraAnchorDuringControl => true;

        // The reach assist was retired by the 2026-07-22 camera-lean redesign
        // (the assist moved the anchor toward contacts, which the redesign
        // forbids; reach comes from the enter camera path leaning in). Its two
        // config keys and its runtime branch were deleted for 1.0 (#575).

        internal static bool UseApiExitRetractToRest => true;

        internal static float ApiExitRetractSeconds => 0.30f;

        // Yaw-flat camera-frame anchor, the verified Current-profile tuning:
        // Z=-0.32 (was 0.2) with the yaw-flat frame keeps the shoulder mid
        // below the bottom frustum plane so the truncated arm caps stay
        // off-screen (#575; round-6 regression evidence).
        internal static Vector3 ApiCameraAnchorOffset =>
            new Vector3(0f, -0.25f, -0.32f);

        // The pitch-relative anchor frame lost its last consumer for 1.0 (#575):
        // the gate promoted to a compile-time false, so the yaw-flat frame above
        // is the only camera-hold frame. Its switch and view offset were deleted
        // with the dead branches rather than left as unreachable constants.

        internal static bool UseApiShoulderCapPlugs => true;

        internal static float ApiShoulderCapPlugDiameter => 0.16f;

        internal static float ApiShoulderCapPlugThickness => 0.10f;

        internal static bool UseApiLeftTargetFromAuthoredTable => true;

        internal static bool UseApiElbowPoleHints => true;

        internal static Vector3 ApiLeftElbowPoleStation =>
            new Vector3(0.20f, 0.90f, 0.90f);

        internal static Vector3 ApiRightElbowPoleStation =>
            new Vector3(0.22f, 0.90f, 0.20f);

        internal static bool PinRightHandRestToStationDuringApiSession => true;

        internal static bool ShoulderAnchorIgnoresCameraPitch => true;

        internal static Vector3 FirstPersonShoulderAnchorCameraOffset =>
            new Vector3(0f, -0.25f, -0.12f);

        internal static float FirstPersonLeverFollowMotionScale => 1.6f;

        internal static Vector3 FirstPersonLeverLeftShoulderOffset =>
            new Vector3(0f, -0.25f, -0.10f);

        internal static bool HideRightFirstPersonArmDuringEnterAndExit => true;

        // A/B experiment that never shipped on: scaling the local third-person
        // head bone does not hide the visible helmet, so the whole head-hide
        // path stays inert. The config key left the file for 1.0 (#575); the
        // gate below keeps the (unused) plumbing compiled and off until the
        // subsystem itself is retired.
        internal static bool HideLocalPlayerHeadDuringApiSession => false;

        internal static Vector3 FirstPersonRightHandRestOffset =>
            new Vector3(0.24f, -0.70f, 0.05f);

        internal static Vector3 FirstPersonRightHandRestEuler => Vector3.zero;

        internal static Vector3 FirstPersonLeftHandRestOffset =>
            new Vector3(-0.24f, -0.70f, 0.05f);

        internal static Vector3 FirstPersonLeftHandRestEuler => Vector3.zero;

        private static bool _localThirdPersonPreviewEnabled;

        // #593: resolving the authored controller when the API session is
        // merely REQUESTED costs nothing on the healthy path - the station
        // warm-up already cached it, and OperatorAnimSession.Begin ignores the
        // controller entirely once the API session starts. It is only consumed
        // on the fallback branch, which is precisely where the frozen,
        // controller-less legacy session came from.
        private static bool ShouldApplyLocalAuthoredController =>
            UseAuthoredLegacyOperatorPresentation || _localThirdPersonPreviewEnabled;

        internal static void SetLocalThirdPersonPreviewEnabled(bool enabled)
        {
            if (_localThirdPersonPreviewEnabled == enabled)
                return;

            _localThirdPersonPreviewEnabled = enabled;
            RefreshLocalSessionForPreview(enabled ? "third-person-preview-enabled" : "third-person-preview-disabled");
        }

        internal static void StartFirstPersonRightHandBlueEditMode(string reason)
        {
            if (!EnsureFirstPersonHandEditAvailable("right-hand-blue-edit"))
                return;

            EnterFirstPersonRightHandEditMode(FirstPersonRightHandTuneTarget.Blue, reason);
        }

        internal static void StartFirstPersonRightHandGreenEditMode(string reason)
        {
            if (!EnsureFirstPersonHandEditAvailable("right-hand-green-edit"))
                return;

            EnterFirstPersonRightHandEditMode(FirstPersonRightHandTuneTarget.Green, reason);
        }

        internal static void StartFirstPersonLeftHandEditMode(string reason)
        {
            if (!EnsureFirstPersonHandEditAvailable("left-hand-edit"))
                return;

            EnterFirstPersonLeftHandEditMode(reason);
        }

        internal static void ExitFirstPersonHandEditModes(string reason)
        {
            ExitFirstPersonRightHandEditMode(reason);
            ExitFirstPersonLeftHandEditMode(reason);
        }

        private static bool EnsureFirstPersonHandEditAvailable(string label)
        {
            if (!MonitorFocus.IsFocused || !MonitorFocus.IsStationEditMenuOpen)
                return false;

            if (MonitorFocus.IsStationThirdPersonDebugViewActive)
            {
                MonitorFocus.ShowOperatorTuningStatus("HAND EDIT UNAVAILABLE IN 3P");
                return false;
            }

            bool interactionsApiSessionActive =
                string.Equals(label, "left-hand-edit", StringComparison.Ordinal) &&
                IsLocalInteractionsApiSessionActive;
            if (!interactionsApiSessionActive && !UseLocalAuthoredOperatorAnimations)
            {
                _localAuthoredOperatorAnimationsEnabled = true;
                RefreshLocalAuthoredOperatorAnimationSetting(label + "-enabled-first-person-arms");
                MonitorFocus.RefreshLocalOperatorFirstPersonVisibility();
            }

            if ((!interactionsApiSessionActive && !UseLocalAuthoredOperatorAnimations) ||
                _localSession == null ||
                !_localSession.IsActive)
            {
                MonitorFocus.ShowOperatorTuningStatus("HAND EDIT UNAVAILABLE");
                return false;
            }

            return true;
        }

        internal static void RefreshLocalAuthoredOperatorAnimationSetting(string reason)
        {
            RefreshLocalSessionForPreview(string.IsNullOrWhiteSpace(reason)
                ? "local-authored-setting-changed"
                : reason);
        }

        internal static void SetLocalAuthoredOperatorAnimationsEnabled(bool enabled, string reason)
        {
            if (_localAuthoredOperatorAnimationsEnabled == enabled)
                return;

            _localAuthoredOperatorAnimationsEnabled = enabled;
            RefreshLocalAuthoredOperatorAnimationSetting(reason);
        }

        internal static bool IsLocalSessionActive(PlayerControllerB player)
        {
            // Wind-down counts: the custom controller is still applied while the
            // exit lean plays, so vanilla animation sync must stay suppressed.
            return _localSession != null &&
                   (_localSession.IsActive || _localSession.IsWindingDown) &&
                   _localSession.Player == player;
        }

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            CCTVOperatorInteractionsBridge.Initialize();

            CCTVStationEvents.ChairEntered += OnChairEntered;
            CCTVStationEvents.ChairExited += OnChairExited;
            CCTVStationEvents.CameraSelected += OnCameraSelected;
            CCTVStationEvents.CameraControlChanged += OnCameraControlChanged;
            CCTVStationEvents.JoystickMoved += OnJoystickMoved;
            CCTVStationEvents.ActionButtonPressed += OnActionButtonPressed;
            CCTVStationEvents.RadarViewChanged += OnRadarViewChanged;
            // HDRP never invokes Camera.onPreCull; the SRP begin-camera event
            // is the one that actually fires in game. Both stay registered so
            // the trace works in either pipeline.
            Camera.onPreCull += OnCameraPreCull;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            CCTVOperatorAnimSync.Initialize();
        }

        internal static void CaptureInteractionBeginPresentation(PlayerControllerB player)
        {
            OperatorAnimSession.CaptureInteractionBeginPresentation(player);
        }

        internal static void AbortInteractionBeginPresentation(
            PlayerControllerB player,
            string reason)
        {
            OperatorAnimSession.AbortInteractionBeginPresentation(player, reason);
        }

        internal static void Shutdown()
        {
            if (!_initialized) return;

            CCTVStationEvents.ChairEntered -= OnChairEntered;
            CCTVStationEvents.ChairExited -= OnChairExited;
            CCTVStationEvents.CameraSelected -= OnCameraSelected;
            CCTVStationEvents.CameraControlChanged -= OnCameraControlChanged;
            CCTVStationEvents.JoystickMoved -= OnJoystickMoved;
            CCTVStationEvents.ActionButtonPressed -= OnActionButtonPressed;
            CCTVStationEvents.RadarViewChanged -= OnRadarViewChanged;
            Camera.onPreCull -= OnCameraPreCull;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

            OperatorAnimSession.TickDeferredVanillaRigRebuild(
                force: true,
                forcedReason: "bridge-shutdown");
            OperatorAnimSession.AbortInteractionBeginPresentation(null, "shutdown");
            _localSession?.EndImmediate("shutdown");
            _localSession = null;
            OperatorAnimSession.ClearPostExitArmsTelemetry();
            CCTVOperatorAnimSync.Shutdown();
            CCTVOperatorInteractionsBridge.Shutdown();

            if (_controllerBundle != null)
            {
                _controllerBundle.Unload(unloadAllLoadedObjects: false);
                _controllerBundle = null;
            }
            _cctvController = null;
            _controllerLoadAttempted = false;
            _controllerRejected = false;
            _controllerRejectedLogged = false;
            _rejectedControllerName = null;
            _controllerRejectReason = null;
            _localThirdPersonPreviewEnabled = false;
            _initialized = false;
        }

        internal static void Tick()
        {
            CCTVOperatorInteractionsBridge.Tick();
            TickFirstPersonRightHandTuningHotkeys();
            TickFirstPersonLeftHandTuningHotkeys();

            if (_localSession != null)
            {
                _localSession.Tick();
                if (!_localSession.IsActive && !_localSession.IsWindingDown)
                    _localSession = null;
                else
                    CCTVOperatorAnimSync.TickLocalJoystickSend(_localSession);
            }
            OperatorAnimSession.TickDeferredVanillaRigRebuild();
            CCTVOperatorAnimSync.Tick();
        }

        /// <summary>
        /// Runs after MonitorFocus has applied the gameplay camera pose and moved
        /// the arms-only root into its captured camera-relative viewmodel pose.
        /// World-space hand targets and the rig graph must evaluate after that
        /// root movement or the visible wrist will lag in the old reference frame.
        /// </summary>
        internal static void TickLocalFirstPersonHandsAfterCamera()
        {
            _localSession?.TickFirstPersonHandsAfterCamera();
            OperatorAnimSession.TickPostExitArmsTelemetry();
        }

        private static void OnCameraPreCull(Camera camera)
        {
            _localSession?.TraceFirstPersonHandsPreCull(camera);
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            _localSession?.TraceFirstPersonHandsPreCull(camera);
        }

        private enum FirstPersonRightHandTuneTarget
        {
            None,
            Blue,
            Green,
        }

        internal struct FirstPersonRightHandPoseTuning
        {
            internal Vector3 Offset;
            internal Vector3 Euler;
        }

        private static void TickFirstPersonRightHandTuningHotkeys()
        {
            IsFirstPersonRightHandTuningHotkeyActive = false;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || _localSession == null || !_localSession.IsActive ||
                !MonitorFocus.IsFocused ||
                MonitorFocus.IsStationThirdPersonDebugViewActive)
            {
                ExitFirstPersonRightHandEditMode("unavailable");
                return;
            }

            if (!MonitorFocus.IsStationEditMenuOpen)
            {
                ExitFirstPersonRightHandEditMode("station-edit-menu-closed");
                return;
            }

            if (!UseLocalAuthoredOperatorAnimations)
            {
                ExitFirstPersonRightHandEditMode("first-person-arms-disabled");
                return;
            }

            if (!_firstPersonRightHandEditModeActive)
            {
                FinishFirstPersonRightHandTuneEditing();
                return;
            }

            if (_firstPersonRightHandTuneTarget == FirstPersonRightHandTuneTarget.None)
                SetFirstPersonRightHandTuneTarget(FirstPersonRightHandTuneTarget.Blue);

            IsFirstPersonRightHandTuningHotkeyActive = true;
            _firstPersonRightHandTuneEditingLastFrame = true;
            _localSession.PreviewFirstPersonRightHandTune(GetActionButtonForTuneTarget(_firstPersonRightHandTuneTarget));

            if (keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame)
            {
                SetFirstPersonRightHandPoseTuning(_firstPersonRightHandTuneTarget, new FirstPersonRightHandPoseTuning());
                ShowFirstPersonRightHandTuneStatus(force: true);
                return;
            }

            bool rotate = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);

            Vector3 moveDelta = Vector3.zero;
            Vector3 eulerDelta = Vector3.zero;
            if (rotate)
            {
                if (keyboard.wKey.isPressed) eulerDelta.x += 1f;
                if (keyboard.sKey.isPressed) eulerDelta.x -= 1f;
                if (keyboard.aKey.isPressed) eulerDelta.y -= 1f;
                if (keyboard.dKey.isPressed) eulerDelta.y += 1f;
                if (keyboard.rKey.isPressed || keyboard.pageUpKey.isPressed) eulerDelta.z += 1f;
                if (keyboard.fKey.isPressed || keyboard.pageDownKey.isPressed) eulerDelta.z -= 1f;
            }
            else
            {
                if (keyboard.aKey.isPressed) moveDelta.x -= 1f;
                if (keyboard.dKey.isPressed) moveDelta.x += 1f;
                if (keyboard.fKey.isPressed || keyboard.pageDownKey.isPressed) moveDelta.y -= 1f;
                if (keyboard.rKey.isPressed || keyboard.pageUpKey.isPressed) moveDelta.y += 1f;
                if (keyboard.sKey.isPressed) moveDelta.z -= 1f;
                if (keyboard.wKey.isPressed) moveDelta.z += 1f;
            }

            if (moveDelta.sqrMagnitude <= 0.0001f && eulerDelta.sqrMagnitude <= 0.0001f)
                return;

            FirstPersonRightHandPoseTuning tuning = GetFirstPersonRightHandPoseTuning(_firstPersonRightHandTuneTarget);
            if (moveDelta.sqrMagnitude > 0.0001f)
            {
                float speed = fast ? FirstPersonRightHandTuneMoveFastSpeed : FirstPersonRightHandTuneMoveSpeed;
                tuning.Offset = ClampFirstPersonRightHandTuneOffset(tuning.Offset + moveDelta.normalized * speed * dt);
            }
            if (eulerDelta.sqrMagnitude > 0.0001f)
            {
                float speed = fast ? FirstPersonRightHandTuneRotationFastSpeed : FirstPersonRightHandTuneRotationSpeed;
                tuning.Euler = ClampFirstPersonRightHandTuneEuler(tuning.Euler + eulerDelta.normalized * speed * dt);
            }

            SetFirstPersonRightHandPoseTuning(_firstPersonRightHandTuneTarget, tuning);
            ShowFirstPersonRightHandTuneStatus(force: false);
        }

        private static void TickFirstPersonLeftHandTuningHotkeys()
        {
            IsFirstPersonLeftHandTuningHotkeyActive = false;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || _localSession == null || !_localSession.IsActive ||
                !MonitorFocus.IsFocused ||
                MonitorFocus.IsStationThirdPersonDebugViewActive)
            {
                ExitFirstPersonLeftHandEditMode("unavailable");
                return;
            }

            if (!MonitorFocus.IsStationEditMenuOpen)
            {
                ExitFirstPersonLeftHandEditMode("station-edit-menu-closed");
                return;
            }

            if (!UseLocalAuthoredOperatorAnimations &&
                !IsLocalInteractionsApiSessionActive)
            {
                ExitFirstPersonLeftHandEditMode("first-person-arms-disabled");
                return;
            }

            if (!_firstPersonLeftHandEditModeActive)
            {
                FinishFirstPersonLeftHandTuneEditing();
                return;
            }

            IsFirstPersonLeftHandTuningHotkeyActive = true;
            _firstPersonLeftHandTuneEditingLastFrame = true;

            if (keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame)
            {
                SetFirstPersonLeftHandPoseTuning(new FirstPersonRightHandPoseTuning());
                ShowFirstPersonLeftHandTuneStatus(force: true);
                return;
            }

            bool rotate = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);

            Vector3 moveDelta = Vector3.zero;
            Vector3 eulerDelta = Vector3.zero;
            if (rotate)
            {
                if (keyboard.wKey.isPressed) eulerDelta.x += 1f;
                if (keyboard.sKey.isPressed) eulerDelta.x -= 1f;
                if (keyboard.aKey.isPressed) eulerDelta.y -= 1f;
                if (keyboard.dKey.isPressed) eulerDelta.y += 1f;
                if (keyboard.rKey.isPressed || keyboard.pageUpKey.isPressed) eulerDelta.z += 1f;
                if (keyboard.fKey.isPressed || keyboard.pageDownKey.isPressed) eulerDelta.z -= 1f;
            }
            else
            {
                if (keyboard.aKey.isPressed) moveDelta.x -= 1f;
                if (keyboard.dKey.isPressed) moveDelta.x += 1f;
                if (keyboard.fKey.isPressed || keyboard.pageDownKey.isPressed) moveDelta.y -= 1f;
                if (keyboard.rKey.isPressed || keyboard.pageUpKey.isPressed) moveDelta.y += 1f;
                if (keyboard.sKey.isPressed) moveDelta.z -= 1f;
                if (keyboard.wKey.isPressed) moveDelta.z += 1f;
            }

            if (moveDelta.sqrMagnitude <= 0.0001f && eulerDelta.sqrMagnitude <= 0.0001f)
                return;

            FirstPersonRightHandPoseTuning tuning = GetFirstPersonLeftHandPoseTuning();
            if (moveDelta.sqrMagnitude > 0.0001f)
            {
                float speed = fast ? FirstPersonRightHandTuneMoveFastSpeed : FirstPersonRightHandTuneMoveSpeed;
                tuning.Offset = ClampFirstPersonRightHandTuneOffset(tuning.Offset + moveDelta.normalized * speed * dt);
            }
            if (eulerDelta.sqrMagnitude > 0.0001f)
            {
                float speed = fast ? FirstPersonRightHandTuneRotationFastSpeed : FirstPersonRightHandTuneRotationSpeed;
                tuning.Euler = ClampFirstPersonRightHandTuneEuler(tuning.Euler + eulerDelta.normalized * speed * dt);
            }

            SetFirstPersonLeftHandPoseTuning(tuning);
            ShowFirstPersonLeftHandTuneStatus(force: false);
        }

        private static void FinishFirstPersonRightHandTuneEditing()
        {
            if (!_firstPersonRightHandTuneEditingLastFrame)
                return;

            _firstPersonRightHandTuneEditingLastFrame = false;
            ShowFirstPersonRightHandTuneStatus(force: true);
        }

        private static void EnterFirstPersonRightHandEditMode(FirstPersonRightHandTuneTarget target, string reason)
        {
            if (target == FirstPersonRightHandTuneTarget.None)
                target = FirstPersonRightHandTuneTarget.Blue;

            ExitFirstPersonLeftHandEditMode("right-hand-edit-entered");
            _firstPersonRightHandEditModeActive = true;
            _firstPersonRightHandTuneEditingLastFrame = true;
            SetFirstPersonRightHandTuneTarget(target);
            try { _localSession?.SetCameraControl(false); } catch { }
            ShowFirstPersonRightHandTuneStatus(force: true);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] First-person right-hand edit mode entered: target={target}, reason={reason}.");
        }

        internal static void ExitFirstPersonRightHandEditMode(string reason)
        {
            if (!_firstPersonRightHandEditModeActive && !_firstPersonRightHandTuneEditingLastFrame)
                return;

            FirstPersonRightHandTuneTarget previous = _firstPersonRightHandTuneTarget;
            _firstPersonRightHandEditModeActive = false;
            _firstPersonRightHandTuneTarget = FirstPersonRightHandTuneTarget.None;
            FinishFirstPersonRightHandTuneEditing();
            MonitorFocus.ShowOperatorTuningStatus("RH EDIT OFF");
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] First-person right-hand edit mode exited: previous={previous}, reason={reason}.");
        }

        private static void FinishFirstPersonLeftHandTuneEditing()
        {
            if (!_firstPersonLeftHandTuneEditingLastFrame)
                return;

            _firstPersonLeftHandTuneEditingLastFrame = false;
            ShowFirstPersonLeftHandTuneStatus(force: true);
        }

        private static void EnterFirstPersonLeftHandEditMode(string reason)
        {
            ExitFirstPersonRightHandEditMode("left-hand-edit-entered");
            _firstPersonLeftHandEditModeActive = true;
            _firstPersonLeftHandTuneEditingLastFrame = true;
            try { _localSession?.SetCameraControl(false); } catch { }
            ShowFirstPersonLeftHandTuneStatus(force: true);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] First-person left-hand edit mode entered: reason={reason}.");
        }

        internal static void ExitFirstPersonLeftHandEditMode(string reason)
        {
            if (!_firstPersonLeftHandEditModeActive && !_firstPersonLeftHandTuneEditingLastFrame)
                return;

            _firstPersonLeftHandEditModeActive = false;
            FinishFirstPersonLeftHandTuneEditing();
            MonitorFocus.ShowOperatorTuningStatus("LH EDIT OFF");
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] First-person left-hand edit mode exited: reason={reason}.");
        }

        private static void SetFirstPersonRightHandTuneTarget(FirstPersonRightHandTuneTarget target)
        {
            if (_firstPersonRightHandTuneTarget == target)
                return;

            _firstPersonRightHandTuneTarget = target;
            ShowFirstPersonRightHandTuneStatus(force: true);
        }

        private static int GetActionButtonForTuneTarget(FirstPersonRightHandTuneTarget target)
        {
            return target == FirstPersonRightHandTuneTarget.Blue ? 1 : 0;
        }

        internal static bool IsBlueRightHandAction(int actionButton)
        {
            return actionButton == 1 || actionButton == 4;
        }

        internal static bool IsGreenRightHandAction(int actionButton)
        {
            return actionButton == 0 || actionButton == 2;
        }

        internal static FirstPersonRightHandPoseTuning GetFirstPersonRightHandPoseTuning(int actionButton)
        {
            if (IsBlueRightHandAction(actionButton))
                return GetFirstPersonRightHandPoseTuning(FirstPersonRightHandTuneTarget.Blue);
            if (IsGreenRightHandAction(actionButton))
                return GetFirstPersonRightHandPoseTuning(FirstPersonRightHandTuneTarget.Green);
            return default;
        }

        private static FirstPersonRightHandPoseTuning GetFirstPersonRightHandPoseTuning(FirstPersonRightHandTuneTarget target)
        {
            if (target == FirstPersonRightHandTuneTarget.Blue)
                return _blueRightHandPoseTuning;
            if (target == FirstPersonRightHandTuneTarget.Green)
                return _greenRightHandPoseTuning;
            return default;
        }

        internal static FirstPersonRightHandPoseTuning GetFirstPersonLeftHandPoseTuning()
        {
            return _leftHandPoseTuning;
        }

        private static void SetFirstPersonRightHandPoseTuning(FirstPersonRightHandTuneTarget target, FirstPersonRightHandPoseTuning tuning)
        {
            tuning.Offset = ClampFirstPersonRightHandTuneOffset(tuning.Offset);
            tuning.Euler = ClampFirstPersonRightHandTuneEuler(tuning.Euler);
            if (target == FirstPersonRightHandTuneTarget.Blue)
                _blueRightHandPoseTuning = tuning;
            else if (target == FirstPersonRightHandTuneTarget.Green)
                _greenRightHandPoseTuning = tuning;
        }

        private static void SetFirstPersonLeftHandPoseTuning(FirstPersonRightHandPoseTuning tuning)
        {
            tuning.Offset = ClampFirstPersonRightHandTuneOffset(tuning.Offset);
            tuning.Euler = ClampFirstPersonRightHandTuneEuler(tuning.Euler);
            _leftHandPoseTuning = tuning;
        }

        private static Vector3 ClampFirstPersonRightHandTuneOffset(Vector3 value)
        {
            return new Vector3(
                Mathf.Clamp(value.x, -FirstPersonRightHandTuneOffsetMax, FirstPersonRightHandTuneOffsetMax),
                Mathf.Clamp(value.y, -FirstPersonRightHandTuneOffsetMax, FirstPersonRightHandTuneOffsetMax),
                Mathf.Clamp(value.z, -FirstPersonRightHandTuneOffsetMax, FirstPersonRightHandTuneOffsetMax));
        }

        private static Vector3 ClampFirstPersonRightHandTuneEuler(Vector3 value)
        {
            return new Vector3(
                Mathf.Clamp(value.x, -FirstPersonRightHandTuneEulerMax, FirstPersonRightHandTuneEulerMax),
                Mathf.Clamp(value.y, -FirstPersonRightHandTuneEulerMax, FirstPersonRightHandTuneEulerMax),
                Mathf.Clamp(value.z, -FirstPersonRightHandTuneEulerMax, FirstPersonRightHandTuneEulerMax));
        }

        private static void ShowFirstPersonRightHandTuneStatus(bool force)
        {
            if (!force && Time.unscaledTime < _nextFirstPersonRightHandTuneStatusAt)
                return;

            _nextFirstPersonRightHandTuneStatusAt = Time.unscaledTime + FirstPersonRightHandTuneStatusInterval;
            if (_firstPersonRightHandTuneTarget == FirstPersonRightHandTuneTarget.None)
            {
                MonitorFocus.ShowOperatorTuningStatus("RH TUNE OFF");
                return;
            }

            FirstPersonRightHandPoseTuning tuning = GetFirstPersonRightHandPoseTuning(_firstPersonRightHandTuneTarget);
            string label = _firstPersonRightHandTuneTarget == FirstPersonRightHandTuneTarget.Blue ? "RH BLUE" : "RH GREEN";
            MonitorFocus.ShowOperatorTuningStatus(
                $"{label} P {FormatTuningVector(tuning.Offset)} R {FormatTuningVector(tuning.Euler)}");
        }

        private static void ShowFirstPersonLeftHandTuneStatus(bool force)
        {
            if (!force && Time.unscaledTime < _nextFirstPersonLeftHandTuneStatusAt)
                return;

            _nextFirstPersonLeftHandTuneStatusAt = Time.unscaledTime + FirstPersonRightHandTuneStatusInterval;
            if (!_firstPersonLeftHandEditModeActive)
            {
                MonitorFocus.ShowOperatorTuningStatus("LH TUNE OFF");
                return;
            }

            FirstPersonRightHandPoseTuning tuning = GetFirstPersonLeftHandPoseTuning();
            MonitorFocus.ShowOperatorTuningStatus(
                $"LH LEVER P {FormatTuningVector(tuning.Offset)} R {FormatTuningVector(tuning.Euler)}");
        }

        private static string FormatTuningVector(Vector3 value)
        {
            return $"{value.x:+0.00;-0.00;0.00},{value.y:+0.00;-0.00;0.00},{value.z:+0.00;-0.00;0.00}";
        }

        internal static string GetFirstPersonHandEditOverlayText()
        {
            string right = GetFirstPersonRightHandEditOverlayText();
            if (!string.IsNullOrWhiteSpace(right))
                return right;
            return GetFirstPersonLeftHandEditOverlayText();
        }

        internal static string GetFirstPersonRightHandEditOverlayText()
        {
            if (!_firstPersonRightHandEditModeActive)
                return null;

            string label = _firstPersonRightHandTuneTarget == FirstPersonRightHandTuneTarget.Green
                ? "GREEN ACTION"
                : "BLUE CAMERA";
            FirstPersonRightHandPoseTuning tuning = GetFirstPersonRightHandPoseTuning(_firstPersonRightHandTuneTarget);
            return
                "RIGHT HAND EDIT\n" +
                $"TARGET  {label}\n" +
                $"POS     {FormatTuningVector(tuning.Offset)}\n" +
                $"ROT     {FormatTuningVector(tuning.Euler)}\n" +
                "1/2     SELECT TARGET\n" +
                "WASD    X/Z MOVE\n" +
                "R/F     UP/DOWN\n" +
                "CTRL    ROTATE MODE\n" +
                "SHIFT   FAST\n" +
                "0       RESET TARGET\n" +
                "F1/ESC EXIT EDIT";
        }

        internal static string GetFirstPersonLeftHandEditOverlayText()
        {
            if (!_firstPersonLeftHandEditModeActive)
                return null;

            FirstPersonRightHandPoseTuning tuning = GetFirstPersonLeftHandPoseTuning();
            return
                "LEFT HAND EDIT\n" +
                "TARGET  BRAKE LEVER\n" +
                $"POS     {FormatTuningVector(tuning.Offset)}\n" +
                $"ROT     {FormatTuningVector(tuning.Euler)}\n" +
                "WASD    X/Z MOVE\n" +
                "R/F     UP/DOWN\n" +
                "CTRL    ROTATE MODE\n" +
                "SHIFT   FAST\n" +
                "0       RESET TARGET\n" +
                "F1/ESC EXIT EDIT";
        }

        private static void OnChairEntered(PlayerControllerB player)
        {
            if (player == null)
                return;

            // A new controller swap must not overtake an exit rebuild that was
            // deliberately waiting for the prior camera seam to settle.
            OperatorAnimSession.TickDeferredVanillaRigRebuild(
                force: true,
                forcedReason: "new-chair-session");
            bool useLocalAuthoredController = ShouldApplyLocalAuthoredController;
            long perfT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RuntimeAnimatorController controller = useLocalAuthoredController ? ResolveControllerShared() : null;
            long perfT1 = System.Diagnostics.Stopwatch.GetTimestamp();

            _localSession?.EndImmediate("replaced");
            _localSession = new OperatorAnimSession(player, isLocal: true);
            // The enter flourish (button press -> settle onto joystick) only makes
            // sense at the vanilla monitor station, where entry comes from the red
            // bezel button and MonitorFocus holds the camera during the press.
            bool enterFlourish = UseInteractionsApiOperatorSession || useLocalAuthoredController;
            bool began = _localSession.Begin(controller, enterFlourish);
            long perfT2 = System.Diagnostics.Stopwatch.GetTimestamp();
            double ticksToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][EnterPerf] chair-entered: controller-load={(perfT1 - perfT0) * ticksToMs:F1}ms " +
                $"session-begin={(perfT2 - perfT1) * ticksToMs:F1}ms began={began}.");
            if (!began)
            {
                OperatorAnimSession.AbortInteractionBeginPresentation(
                    player,
                    "session-begin-failed");
                _localSession = null;
                return;
            }

            MonitorFocus.RefreshLocalOperatorFirstPersonVisibility();
            if (_localSession.UsesInteractionsApi)
            {
                // The API owns controller diagnostics and restore telemetry.
            }
            else if (useLocalAuthoredController)
            {
                LogMissingControllerOnce();
            }
            else
            {
                LogRuntimeControllerDiagnosticsOnce(ResolveControllerShared());
                LogLocalAuthoredSuppressedOnce();
            }
            CCTVOperatorAnimSync.SendEnter(player, MonitorFocus.ActiveSlot);
        }

        private static void RefreshLocalSessionForPreview(string reason)
        {
            if (_localSession == null || !_localSession.IsActive || _localSession.IsWindingDown)
                return;

            if (_localSession.UsesInteractionsApi)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ApiPort] legacy_preview_change_ignored reason='{reason}' " +
                    "while the Interactions API session owns the animator.");
                return;
            }

            PlayerControllerB player = _localSession.Player;
            if (player == null)
                return;

            bool cameraControlActive = _localSession.CameraControlActive;
            Vector2 joystick = _localSession.JoystickSmoothed;
            RuntimeAnimatorController controller = ShouldApplyLocalAuthoredController
                ? ResolveControllerShared()
                : null;

            _localSession.EndImmediate(reason + "-restart");
            var replacement = new OperatorAnimSession(player, isLocal: true);
            if (!replacement.Begin(controller))
            {
                _localSession = null;
                return;
            }

            replacement.SetActiveSlotQuiet(MonitorFocus.ActiveSlot);
            replacement.SetCameraControl(cameraControlActive);
            if (cameraControlActive)
                replacement.SetJoystickTargetAbsolute(joystick, immediate: true);

            _localSession = replacement;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Local operator anim session refreshed for {reason}; " +
                $"thirdPersonPreview={_localThirdPersonPreviewEnabled}, " +
                $"localAuthoredOverride={UseLocalAuthoredOperatorAnimations}, " +
                $"controllerRequested={(controller != null ? controller.name : "<none>")}.");
        }

        private static void OnChairExited(PlayerControllerB player)
        {
            if (_localSession == null)
                return;
            if (player != null && _localSession.Player != null && _localSession.Player != player)
                return;
            if (_localSession.IsWindingDown)
                return;

            // Capture before BeginGracefulEnd: a degraded (immediate) end nulls Player.
            PlayerControllerB sessionPlayer = _localSession.Player;
            _localSession.BeginGracefulEnd("chair-exited");
            CCTVOperatorAnimSync.SendExit(sessionPlayer);
        }

        private static void OnCameraSelected(int slot)
        {
            if (_localSession == null || !_localSession.IsActive) return;
            if (_firstPersonRightHandEditModeActive) return;
            if (ShouldSuppressCameraUnsafeRightHandAction("camera-select"))
            {
                _localSession.SetActiveSlotQuiet(slot);
                return;
            }

            _localSession.SelectCamera(slot);
            CCTVOperatorAnimSync.SendSelectCamera(_localSession.Player, slot);
        }

        private static void OnCameraControlChanged(bool controllingCamera)
        {
            if (_localSession == null || !_localSession.IsActive) return;
            if (_firstPersonRightHandEditModeActive) return;
            _localSession.SetCameraControl(controllingCamera);
            CCTVOperatorAnimSync.SendCameraControl(_localSession.Player, controllingCamera);
        }

        private static void OnJoystickMoved(Vector2 appliedDeltaDeg)
        {
            if (_localSession == null || !_localSession.IsActive) return;
            if (_firstPersonRightHandEditModeActive) return;
            _localSession.OnJoystickMoved(appliedDeltaDeg);
        }

        private static void OnActionButtonPressed(string actionId)
        {
            if (_localSession == null || !_localSession.IsActive) return;
            if (_firstPersonRightHandEditModeActive) return;
            if (ShouldSuppressCameraUnsafeRightHandAction(actionId))
                return;

            int buttonId = ResolveActionButtonId(actionId);
            PlayerControllerB sessionPlayer = _localSession.Player;
            _localSession.PressButton(buttonId, isStandUp: buttonId == 3);
            if (buttonId == 3)
                CCTVOperatorAnimSync.SendExit(sessionPlayer);
            else
                CCTVOperatorAnimSync.SendButtonPress(sessionPlayer, buttonId);
        }

        private static void OnRadarViewChanged(bool lookingAtRadar)
        {
            if (_localSession == null || !_localSession.IsActive) return;
            if (_firstPersonRightHandEditModeActive) return;
            _localSession.SetRadarLook(lookingAtRadar);
            CCTVOperatorAnimSync.SendRadarLook(_localSession.Player, lookingAtRadar);
        }

        private static bool ShouldSuppressCameraUnsafeRightHandAction(string actionId)
        {
            return false;
        }

        internal static int ResolveActionButtonId(string actionId)
        {
            if (string.Equals(actionId, "camera-select", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(actionId, "monitor-mode-toggle", StringComparison.OrdinalIgnoreCase))
                return 2;
            if (string.Equals(actionId, "stand-up", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (string.Equals(actionId, "camera-page", StringComparison.OrdinalIgnoreCase))
                return 4;

            return 0;
        }

        /// <summary>
        /// Loads and preflights the authored controller off the enter frame
        /// (called at station spawn). ResolveControllerShared caches, so the
        /// chair-enter path becomes a dictionary hit.
        /// </summary>
        internal static void WarmUpAuthoredController()
        {
            ResolveControllerShared();
        }

        internal static RuntimeAnimatorController ResolveControllerShared()
        {
            if (_cctvController != null)
                return _cctvController;
            if (_controllerLoadAttempted)
            {
                LogMissingControllerOnce();
                return null;
            }

            _controllerLoadAttempted = true;
            string pluginDir = GetPluginDirectory();
            if (string.IsNullOrEmpty(pluginDir))
            {
                LogMissingControllerOnce();
                return null;
            }

            // LethalLevelLoader auto-loads every *.lethalbundle in the plugin folder long before
            // this runs, so AssetBundle.LoadFromFile on our own file makes Unity log
            // "can't be loaded because another AssetBundle with the same files is already loaded"
            // as an error before the recovery scan further down succeeds. Scan the already-loaded
            // bundles first so the normal path never touches LoadFromFile at all.
            RuntimeAnimatorController preloaded = FindControllerInLoadedBundles();
            if (TryAcceptLoadedController(preloaded, "loaded-bundle-scan"))
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] CCTV player animator controller resolved from an already-loaded bundle (LethalLevelLoader auto-load).");
                return _cctvController;
            }

            string[] bundleFiles =
            {
                PrimaryBundleFile,
                SecondaryBundleFile
            };

            for (int i = 0; i < bundleFiles.Length; i++)
            {
                string path = Path.Combine(pluginDir, bundleFiles[i]);
                if (!File.Exists(path))
                    continue;

                try
                {
                    _controllerBundle = AssetBundle.LoadFromFile(path);
                    if (_controllerBundle != null)
                    {
                        RuntimeAnimatorController loaded = LoadControllerFromBundle(_controllerBundle);
                        if (TryAcceptLoadedController(loaded, Path.GetFileName(path)))
                        {
                            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Loaded CCTV player animator controller '{_cctvController.name}' from {Path.GetFileName(path)}.");
                            return _cctvController;
                        }

                        if (loaded == null)
                            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV player animation bundle contains no expected controller: {path}");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV player animation bundle load failed '{path}': {ex.Message}");
                }

                // LethalLevelLoader auto-loads *.lethalbundle files before we get
                // here, which makes our LoadFromFile return null/throw. Recover by
                // scanning the already-loaded bundles for our controller.
                RuntimeAnimatorController recovered = FindControllerInLoadedBundles();
                if (TryAcceptLoadedController(recovered, "loaded-bundle-scan"))
                {
                    SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] CCTV player animator controller recovered from an already-loaded bundle (LethalLevelLoader auto-load).");
                    return _cctvController;
                }
            }

            LogMissingControllerOnce();
            return null;
        }

        private static bool TryAcceptLoadedController(RuntimeAnimatorController controller, string sourceLabel)
        {
            if (controller == null)
                return false;

            if (!PreflightControllerContract(controller, sourceLabel))
            {
                if (_controllerBundle != null)
                {
                    try { _controllerBundle.Unload(unloadAllLoadedObjects: false); } catch { }
                    _controllerBundle = null;
                }
                return false;
            }

            _cctvController = controller;
            _controllerRejected = false;
            _rejectedControllerName = null;
            _controllerRejectReason = null;
            return true;
        }

        private static bool PreflightControllerContract(RuntimeAnimatorController controller, string sourceLabel)
        {
            GameObject probe = null;
            try
            {
                probe = new GameObject("LethalCCTV_CCTVAnimatorPreflight")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Animator animator = probe.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                LogAnimatorControllerDiagnosticsOnce(animator, controller);
                bool valid = ValidateAnimatorControllerContract(animator, controller, "bundle-preflight:" + sourceLabel);
                if (!valid)
                {
                    RejectLoadedController(
                        controller,
                        "controller contract invalid during bundle preflight; preserve vanilla player animator layers [" +
                        string.Join(", ", RequiredVanillaLayerNames) +
                        "] by cloning/overriding the vanilla metarig controller instead of shipping a standalone CCTV-only controller");
                }
                return valid;
            }
            catch (Exception ex)
            {
                RejectLoadedController(controller, "controller preflight exception: " + ex.Message);
                return false;
            }
            finally
            {
                if (probe != null)
                    UnityEngine.Object.Destroy(probe);
            }
        }

        private static void RejectLoadedController(RuntimeAnimatorController controller, string reason)
        {
            _controllerRejected = true;
            _rejectedControllerName = controller != null ? controller.name : "<null>";
            _controllerRejectReason = reason;

            if (_controllerRejectedLogged)
                return;

            _controllerRejectedLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                "[LethalCCTV] Authored CCTV animator controller rejected before live player apply: " +
                $"controller='{_rejectedControllerName}', reason='{_controllerRejectReason}'. " +
                "Runtime pose fallback will remain active; no live player controller swap was attempted.");
        }

        private static RuntimeAnimatorController FindControllerInLoadedBundles()
        {
            // Probing every loaded bundle with LoadAsset/LoadAllAssets measured
            // ~910ms at station spawn in a large pack (#280): LoadAllAssets
            // deserializes assets in every bundle that misses the named lookups.
            // Pass 1 filters on each bundle's name table / bundle name, which is
            // where our own auto-loaded bundle is found; the full probe survives
            // as pass 2 for bundles whose name table does not answer.
            try
            {
                foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (loaded == null || !BundleLikelyContainsController(loaded)) continue;
                    RuntimeAnimatorController controller = LoadControllerFromBundle(loaded);
                    if (controller != null)
                        return controller;
                }

                foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (loaded == null || BundleLikelyContainsController(loaded)) continue;
                    RuntimeAnimatorController controller = LoadControllerFromBundle(loaded);
                    if (controller != null)
                        return controller;
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Loaded-bundle scan for CCTV controller failed: {ex.Message}");
            }
            return null;
        }

        private static bool BundleLikelyContainsController(AssetBundle bundle)
        {
            try
            {
                string bundleName = bundle.name;
                if (!string.IsNullOrEmpty(bundleName) &&
                    bundleName.IndexOf("y4ngz-cctv-playeranimations", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                return bundle.Contains(PrimaryControllerAsset) || bundle.Contains(SecondaryControllerAsset);
            }
            catch
            {
                return false;
            }
        }

        private static RuntimeAnimatorController LoadControllerFromBundle(AssetBundle bundle)
        {
            if (bundle == null)
                return null;

            RuntimeAnimatorController controller;
            try
            {
                controller = bundle.LoadAsset<RuntimeAnimatorController>(PrimaryControllerAsset);
                if (controller != null)
                    return controller;

                controller = bundle.LoadAsset<RuntimeAnimatorController>(SecondaryControllerAsset);
                if (controller != null)
                    return controller;

                RuntimeAnimatorController[] controllers = bundle.LoadAllAssets<RuntimeAnimatorController>();
                for (int i = 0; i < controllers.Length; i++)
                {
                    RuntimeAnimatorController candidate = controllers[i];
                    if (candidate == null)
                        continue;

                    if (string.Equals(candidate.name, PrimaryControllerAsset, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidate.name, SecondaryControllerAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string GetPluginDirectory()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }

        private static void LogMissingControllerOnce()
        {
            if (_cctvController != null || _missingControllerLogged)
                return;

            _missingControllerLogged = true;
            if (_controllerRejected)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] CCTV player animation controller unavailable because the bundled controller was rejected: " +
                    $"controller='{(_rejectedControllerName ?? "<unknown>")}', reason='{(_controllerRejectReason ?? "unknown")}'. " +
                    "Remote and local third-person preview animation will use the station body-pose fallback.");
                return;
            }

            string pluginDir = GetPluginDirectory() ?? "<unknown>";
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] CCTV player animation controller not loaded. " +
                $"Checked '{PrimaryBundleFile}' and '{SecondaryBundleFile}' beside the plugin in '{pluginDir}'. " +
                "Remote third-person operator animation will use the station body-pose fallback only.");
        }

        private static void LogLocalAuthoredSuppressedOnce()
        {
            if (_localAuthoredSuppressedLogged)
                return;

            _localAuthoredSuppressedLogged = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Local authored CCTV operator animations are disabled by default; " +
                "first-person arms are hidden in station focus. Remote operator animation sync remains enabled.");
        }

        internal static void LogAnimatorControllerDiagnosticsOnce(Animator animator, RuntimeAnimatorController controller)
        {
            if (_controllerDiagnosticsLogged || animator == null || controller == null)
                return;

            _controllerDiagnosticsLogged = true;
            try
            {
                var layerNames = new List<string>();
                int layerCount = Mathf.Max(0, animator.layerCount);
                for (int i = 0; i < layerCount; i++)
                    layerNames.Add(i.ToString() + ":" + animator.GetLayerName(i));

                var parameterNames = new List<string>();
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    parameterNames.Add(parameter.name + ":" + parameter.type);
                }

                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] CCTV animator controller diagnostics: " +
                    $"controller='{controller.name}', layerCount={layerCount}, " +
                    $"layers=[{string.Join(", ", layerNames.ToArray())}], " +
                    $"parameters=[{string.Join(", ", parameterNames.ToArray())}].");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV animator controller diagnostics failed: {ex.Message}");
            }
        }

        internal static bool ValidateAnimatorControllerContract(Animator animator, RuntimeAnimatorController controller, string context)
        {
            if (animator == null || controller == null)
                return false;

            bool valid = true;
            var missing = new List<string>();
            var parameterTypes = new Dictionary<string, AnimatorControllerParameterType>();
            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    parameterTypes[parameter.name] = parameter.type;
                }

                for (int i = 0; i < RequiredParameters.Length; i++)
                {
                    RequiredAnimatorParameter required = RequiredParameters[i];
                    if (!parameterTypes.TryGetValue(required.Name, out AnimatorControllerParameterType actual))
                    {
                        missing.Add(required.Name + ":missing");
                        valid = false;
                        continue;
                    }

                    if (actual != required.Type)
                    {
                        missing.Add(required.Name + ":expected " + required.Type + " got " + actual);
                        valid = false;
                    }
                }

                if (animator.layerCount <= 0)
                {
                    missing.Add("layerCount:expected >=1 got " + animator.layerCount);
                    valid = false;
                }

                for (int i = 0; i < RequiredVanillaLayerNames.Length; i++)
                {
                    string requiredLayer = RequiredVanillaLayerNames[i];
                    bool found = false;
                    for (int layer = 0; layer < animator.layerCount; layer++)
                    {
                        if (string.Equals(animator.GetLayerName(layer), requiredLayer, StringComparison.Ordinal))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        missing.Add("layer:" + requiredLayer + ":missing");
                        valid = false;
                    }
                }

                if (!_controllerLayerWarningLogged)
                {
                    var missingLayers = new List<string>();
                    for (int i = 0; i < ExpectedAdditiveLayerNames.Length; i++)
                    {
                        string expected = ExpectedAdditiveLayerNames[i];
                        bool found = false;
                        for (int layer = 0; layer < animator.layerCount; layer++)
                        {
                            if (string.Equals(animator.GetLayerName(layer), expected, StringComparison.Ordinal))
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                            missingLayers.Add(expected);
                    }

                    if (missingLayers.Count > 0)
                    {
                        _controllerLayerWarningLogged = true;
                        SurveillanceBootstrap.Log?.LogWarning(
                            "[LethalCCTV] CCTV animator controller is missing optional action layers " +
                            $"[{string.Join(", ", missingLayers.ToArray())}] in {context}; base pose may still work, " +
                            "but button/radar overlays will not.");
                    }
                }

                if (!_controllerContractLogged || !valid)
                {
                    _controllerContractLogged = true;
                    string level = valid ? "OK" : "INVALID";
                    string details = missing.Count == 0 ? "all required parameters present" : string.Join(", ", missing.ToArray());
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] CCTV animator controller contract {level}: " +
                        $"controller='{controller.name}', context='{context}', {details}.");
                }
            }
            catch (Exception ex)
            {
                valid = false;
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] CCTV animator controller contract validation failed in {context}: {ex.Message}");
            }

            return valid;
        }

        private static void LogRuntimeControllerDiagnosticsOnce(RuntimeAnimatorController controller)
        {
            if (_controllerDiagnosticsLogged || controller == null)
                return;

            GameObject probe = null;
            try
            {
                probe = new GameObject("LethalCCTV_CCTVAnimatorDiagnostics")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Animator animator = probe.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                LogAnimatorControllerDiagnosticsOnce(animator, controller);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV animator controller diagnostics probe failed: {ex.Message}");
            }
            finally
            {
                if (probe != null)
                    UnityEngine.Object.Destroy(probe);
            }
        }

        private struct RequiredAnimatorParameter
        {
            internal readonly string Name;
            internal readonly AnimatorControllerParameterType Type;

            internal RequiredAnimatorParameter(string name, AnimatorControllerParameterType type)
            {
                Name = name;
                Type = type;
            }
        }

        /// <summary>
        /// World-space point the enter press should land on: the live access
        /// button cap's top surface (bezel red button as legacy fallback).
        /// Shared by the enter hand driver and enter camera choreography.
        /// </summary>
        internal static Vector3? ResolveAccessButtonPressPoint()
        {
            return TryResolveAccessButtonPressSurface(out Vector3 point, out _) ? point : (Vector3?)null;
        }

        /// <summary>
        /// Live lever/throttle grip point resolved from the visible handle or
        /// knob bounds when possible. The station-owned target is parented to
        /// the animated lever pivot, so the planted hand follows lever tilt.
        /// </summary>
        internal static Vector3? ResolveLeverGripPoint()
        {
            return CCTVOperatorStation.TryResolveLeverGripPoint(
                out Vector3 point,
                out _)
                    ? point
                    : (Vector3?)null;
        }

        internal static bool TryResolveAccessButtonPressSurface(out Vector3 point, out Vector3 normal)
        {
            if (CCTVAccessButton.TryGetPressSurface(out point, out normal))
                return true;

            Transform button = CCTVVanillaMonitorButtons.RedButtonTransform;
            if (button == null)
            {
                point = Vector3.zero;
                normal = Vector3.up;
                return false;
            }

            Renderer renderer = button.GetComponentInChildren<Renderer>();
            Bounds bounds = renderer != null ? renderer.bounds : new Bounds(button.position, Vector3.zero);
            point = bounds.center;
            normal = Vector3.up;

            Camera camera = GameNetworkManager.Instance != null &&
                            GameNetworkManager.Instance.localPlayerController != null
                ? GameNetworkManager.Instance.localPlayerController.gameplayCamera
                : null;
            if (camera != null)
            {
                Vector3 toCamera = camera.transform.position - point;
                if (toCamera.sqrMagnitude > 0.0001f)
                    normal = toCamera.normalized;
            }

            Vector3 extents = bounds.extents;
            float projectedExtent =
                Mathf.Abs(normal.x) * extents.x +
                Mathf.Abs(normal.y) * extents.y +
                Mathf.Abs(normal.z) * extents.z;
            point += normal * Mathf.Max(projectedExtent, 0.015f);
            return true;
        }
    }
}
