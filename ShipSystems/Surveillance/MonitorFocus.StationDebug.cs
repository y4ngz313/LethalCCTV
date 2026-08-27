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
    internal static partial class MonitorFocus
    {
        /// <summary>
        /// #541 — one switch for every developer hotkey the operator station
        /// owns: F1 station edit menu, F2 camera placement edit and its
        /// F4/F5/F6/Delete actions, F8 first-person arms, and the Alt arms-offset
        /// nudge, plus the three edit-overlay text providers that hijack the
        /// operator tooltip. Gameplay controls are not covered — mouse aim,
        /// arrows, SPACE, wheel, LMB, RMB, V and E/ESC stay live in both modes.
        /// Missing config reads as DISABLED: release defaults ship with the
        /// dev hotkeys off, and a failed bind must not silently re-enable them.
        /// </summary>
        internal static bool IsOperatorDebugToolsEnabled
        {
            get
            {
                var binding = SurveillanceBootstrap.Config?.EnableOperatorDebugTools;
                if (binding == null)
                {
                    // Disabled is the release-safe answer, but a broken bind
                    // must stay distinguishable from the default in the log.
                    if (!_debugToolsBindMissingLogged)
                    {
                        _debugToolsBindMissingLogged = true;
                        SurveillanceBootstrap.Log?.LogWarning(
                            "[LethalCCTV] Enable Operator Debug Tools binding missing; operator debug tools read as disabled.");
                    }
                    return false;
                }
                return binding.Value;
            }
        }

        private static bool _debugToolsBindMissingLogged;

        /// <summary>
        /// Gating only the tick sites would strand whatever was open at the moment
        /// the switch flipped: an edit panel with no key left that closes it, or
        /// the F4 third-person camera still holding the player's view. Runs once
        /// per transition, both directions, and is a single bool compare otherwise.
        /// </summary>
        private static void TickOperatorDebugToolsGate(bool enabled)
        {
            if (enabled == _operatorDebugToolsEnabledLastFrame)
                return;

            _operatorDebugToolsEnabledLastFrame = enabled;
            if (enabled)
                return;

            CloseStationEditMenu("operator-debug-tools-disabled");
            if (_placementEditorOpen)
                CancelPlacementEditor(restore: true);
            CloseCameraReviewMenu();
            _cameraPlacementEditModeOpen = false;
            SetStationThirdPersonDebugPersistent(false);
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Operator debug tools disabled by config; edit modes closed and their hotkeys are now inert.");
        }

        internal static void ToggleStationThirdPersonDebug()
        {
            SetStationThirdPersonDebugPersistent(!_stationThirdPersonDebugPersistent);
        }

        internal static void SetStationThirdPersonDebugPersistent(bool active)
        {
            if (_stationThirdPersonDebugPersistent == active)
                return;

            _stationThirdPersonDebugPersistent = active;
            _stationGlobalThirdPersonFramingLogged = false;

            if (active)
            {
                ResetStationThirdPersonOrbit();
                Y4NGZPlayerAnimationBridge.SetLocalThirdPersonPreviewEnabled(true);
                if (IsFocused)
                    SetStationThirdPersonPreview(true);
                else
                    TickStationGlobalThirdPersonDebugView();
            }
            else
            {
                if (IsFocused)
                    SetStationThirdPersonPreview(false);
                else
                {
                    RestoreStationGlobalThirdPersonDebugView();
                    RestorePreviewForcedLocalRenderers();
                    Y4NGZPlayerAnimationBridge.SetLocalThirdPersonPreviewEnabled(_stationThirdPersonPreviewActive);
                }
            }

            if (IsFocused)
                RefreshLocalOperatorFirstPersonVisibility();

            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Persistent station third-person debug {(active ? "enabled" : "disabled")}.");
        }

        private static void TickStationThirdPersonDebugHotkey()
        {
            if (Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive)
                return;
            if (IsFocused)
                return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.f4Key.wasPressedThisFrame)
                return;

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player != null)
            {
                if (player.inTerminalMenu) return;
                if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen) return;
                if (player.isTypingChat) return;
            }

            ToggleStationThirdPersonDebug();
        }

        private static void TickLocalFirstPersonOperatorAnimationHotkey()
        {
            if (Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive)
                return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.f8Key.wasPressedThisFrame)
                return;

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player != null)
            {
                if (player.inTerminalMenu) return;
                if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen) return;
                if (player.isTypingChat) return;
            }

            bool enabled = Y4NGZPlayerAnimationBridge.UseLocalAuthoredOperatorAnimations;
            bool nextEnabled = !enabled;
            Y4NGZPlayerAnimationBridge.SetLocalAuthoredOperatorAnimationsEnabled(
                nextEnabled,
                nextEnabled ? "first-person-operator-hotkey-enabled" : "first-person-operator-hotkey-disabled");
            RefreshLocalOperatorFirstPersonVisibility();
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] First-person/local authored operator animations {(nextEnabled ? "enabled" : "disabled")} from F8.");
        }

        private static void TickStationGlobalThirdPersonDebugView()
        {
            if (!_stationThirdPersonDebugPersistent || IsFocused)
            {
                if (!_stationThirdPersonDebugPersistent && _stationGlobalThirdPersonCameraActive)
                    RestoreStationGlobalThirdPersonDebugView();
                return;
            }

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null || player.gameplayCamera == null)
                return;

            Camera camera = player.gameplayCamera;
            Transform cameraTransform = camera.transform;
            if (!_stationGlobalThirdPersonCameraActive || _stationGlobalThirdPersonCamera != camera)
            {
                RestoreStationGlobalThirdPersonDebugView();
                _stationGlobalThirdPersonCamera = camera;
                _stationGlobalThirdPersonCameraTransform = cameraTransform;
                _stationGlobalThirdPersonSavedLocalPosition = cameraTransform.localPosition;
                _stationGlobalThirdPersonSavedLocalRotation = cameraTransform.localRotation;
                _stationGlobalThirdPersonSavedFov = camera.fieldOfView;
                _stationGlobalThirdPersonSaved = true;
                _stationGlobalThirdPersonCameraActive = true;
            }

            Transform pose = CCTVOperatorStation.OperatorPoseAnchor;
            if (pose == null)
            {
                // Without the station basis the old fallback pinned the camera
                // to the player's own transform; mouse yaw then swings it in a
                // body-attached orbit through the hull. Drop the debug view
                // instead of ever becoming body-attached.
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] Persistent station third-person debug view dropped: operator pose anchor unavailable.");
                SetStationThirdPersonDebugPersistent(false);
                RestoreStationGlobalThirdPersonDebugView();
                return;
            }

            Renderer previewRenderer = ForceLocalPlayerThirdPersonPreviewRenderers(player);
            bool framedRendererBounds = TryGetPreviewRendererBounds(previewRenderer, out Bounds bounds);
            Transform basis = pose;
            Vector3 target = framedRendererBounds
                ? bounds.center
                : player.transform.position + Vector3.up * 1.05f;
            Vector3 forward = basis != null && basis.forward.sqrMagnitude > 0.0001f ? basis.forward.normalized : player.transform.forward;
            Vector3 right = basis != null && basis.right.sqrMagnitude > 0.0001f ? basis.right.normalized : player.transform.right;
            Vector3 cameraPosition =
                target -
                forward * STATION_GLOBAL_THIRD_PERSON_DISTANCE +
                right * STATION_GLOBAL_THIRD_PERSON_SIDE_OFFSET +
                Vector3.up * STATION_GLOBAL_THIRD_PERSON_HEIGHT_OFFSET;
            Vector3 look = target - cameraPosition;
            if (look.sqrMagnitude < 0.0001f)
                look = forward;

            cameraTransform.SetPositionAndRotation(cameraPosition, Quaternion.LookRotation(look.normalized, Vector3.up));
            camera.fieldOfView = STATION_GLOBAL_THIRD_PERSON_FOV;

            if (!_stationGlobalThirdPersonFramingLogged)
            {
                _stationGlobalThirdPersonFramingLogged = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] Persistent station third-person debug camera active: " +
                    $"target={FormatDebugVector(target)}, camera={FormatDebugVector(cameraPosition)}, " +
                    $"renderer={(previewRenderer != null ? previewRenderer.name : "<none>")}.");
            }
        }

        private static void RestoreStationGlobalThirdPersonDebugView()
        {
            if (_stationGlobalThirdPersonSaved &&
                _stationGlobalThirdPersonCamera != null &&
                _stationGlobalThirdPersonCameraTransform != null &&
                !_physicalFocusViewActive)
            {
                try
                {
                    _stationGlobalThirdPersonCameraTransform.localPosition = _stationGlobalThirdPersonSavedLocalPosition;
                    _stationGlobalThirdPersonCameraTransform.localRotation = _stationGlobalThirdPersonSavedLocalRotation;
                    _stationGlobalThirdPersonCamera.fieldOfView = _stationGlobalThirdPersonSavedFov;
                }
                catch { }
            }

            _stationGlobalThirdPersonCameraActive = false;
            _stationGlobalThirdPersonCamera = null;
            _stationGlobalThirdPersonCameraTransform = null;
            _stationGlobalThirdPersonSavedLocalPosition = Vector3.zero;
            _stationGlobalThirdPersonSavedLocalRotation = Quaternion.identity;
            _stationGlobalThirdPersonSavedFov = 0f;
            _stationGlobalThirdPersonSaved = false;
            _stationGlobalThirdPersonFramingLogged = false;
        }

        private static void TickStationFocusAnchorHotkeys()
        {
            if (_focusViewExitPending)
                return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.f1Key.wasPressedThisFrame)
            {
                if (_stationEditMenuOpen)
                    CloseStationEditMenu("f1");
                else
                    OpenStationEditMenu();
                return;
            }

            if (!_stationEditMenuOpen)
                return;

            if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame)
                SelectStationEditMenuTarget(StationEditMenuTarget.BothHands);
            else if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame)
                SelectStationEditMenuTarget(StationEditMenuTarget.LeftHand);
            else if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame)
                SelectStationEditMenuTarget(StationEditMenuTarget.RightHandCamera);
            else if (keyboard.digit4Key.wasPressedThisFrame || keyboard.numpad4Key.wasPressedThisFrame)
                SelectStationEditMenuTarget(StationEditMenuTarget.RightHandAction);
            else if (keyboard.digit5Key.wasPressedThisFrame || keyboard.numpad5Key.wasPressedThisFrame)
                SelectStationEditMenuTarget(StationEditMenuTarget.FocusView);
            else if (keyboard.digit6Key.wasPressedThisFrame || keyboard.numpad6Key.wasPressedThisFrame)
                SelectStationEditMenuTarget(StationEditMenuTarget.RadarView);
            else if (keyboard.digit7Key.wasPressedThisFrame || keyboard.numpad7Key.wasPressedThisFrame)
                SelectStationEditMenuTarget(StationEditMenuTarget.ThirdPersonDebug);
            else if (keyboard.digit8Key.wasPressedThisFrame || keyboard.numpad8Key.wasPressedThisFrame)
                SelectStationEditMenuTarget(StationEditMenuTarget.ThirdPersonPlayer);
            else if (keyboard.f6Key.wasPressedThisFrame)
                CCTVOperatorStation.DumpControlGeometry("station-edit-menu");

            if (_stationEditMenuTarget == StationEditMenuTarget.BothHands)
                TickStationBothHandsEditInput(keyboard);
        }

        private static bool IsStationEditInputActive()
        {
            return _stationEditMenuOpen ||
                   Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive ||
                   CCTVOperatorStation.IsDebugPlacementActive;
        }

        private static void OpenStationEditMenu()
        {
            if (_stationEditMenuOpen)
                return;

            if (_placementEditorOpen)
                CancelPlacementEditor(restore: true);
            CloseCameraReviewMenu();
            _cameraPlacementEditModeOpen = false;
            _stationEditMenuOpen = true;
            _stationEditMenuTarget = StationEditMenuTarget.None;
            _stationRadarLookBlend = 0f;
            _stationRadarLookSuppressedUntil = Time.unscaledTime + 0.35f;
            _stationFocusAnchorForcedUntil = Time.unscaledTime + 0.35f;
            ShowTimedScanStatus("CCTV EDIT MENU", OVERLAY_ACTIVE_GREEN, 1.0f);
            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Station edit menu opened.");
        }

        internal static void CloseStationEditMenu(string reason)
        {
            bool hadState = _stationEditMenuOpen ||
                            _stationEditMenuTarget != StationEditMenuTarget.None ||
                            Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive ||
                            CCTVOperatorStation.IsDebugPlacementActive ||
                            _stationThirdPersonPreviewActive;

            _stationEditMenuOpen = false;
            _stationEditMenuTarget = StationEditMenuTarget.None;
            Y4NGZPlayerAnimationBridge.ExitFirstPersonHandEditModes(reason);
            CCTVOperatorStation.CancelDebugPlacement();
            SetStationThirdPersonPreview(false, keepPersistentDebug: true);
            FinishFirstPersonArmsOffsetEditing();
            _stationRadarLookBlend = 0f;
            _stationRadarLookSuppressedUntil = Time.unscaledTime + 0.35f;
            _stationFocusAnchorForcedUntil = Time.unscaledTime + 0.35f;

            if (hadState)
            {
                ShowTimedScanStatus("CCTV EDIT CLOSED", OVERLAY_ACTIVE_GREEN, 0.8f);
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Station edit menu closed: reason={reason}.");
            }
        }

        private static void SelectStationEditMenuTarget(StationEditMenuTarget target)
        {
            if (!_stationEditMenuOpen || target == StationEditMenuTarget.None)
                return;

            if (_stationEditMenuTarget == target)
                return;

            FinishFirstPersonArmsOffsetEditing();
            Y4NGZPlayerAnimationBridge.ExitFirstPersonHandEditModes("station-edit-target-changed");
            CCTVOperatorStation.CancelDebugPlacement();
            SetStationThirdPersonPreview(false, keepPersistentDebug: true);

            _stationEditMenuTarget = target;
            _stationRadarLookBlend = 0f;
            _stationRadarLookSuppressedUntil = Time.unscaledTime + 0.35f;
            _stationFocusAnchorForcedUntil = Time.unscaledTime + 0.35f;

            switch (target)
            {
                case StationEditMenuTarget.BothHands:
                    EnsureFirstPersonArmsEnabledForEdit();
                    EnsureFirstPersonArmsOffsetLoaded();
                    _firstPersonArmsOffsetEditingLastFrame = true;
                    ApplyFirstPersonArmsOffsetToForcedRenderers();
                    ShowFirstPersonArmsOffsetStatus(force: true);
                    break;
                case StationEditMenuTarget.LeftHand:
                    Y4NGZPlayerAnimationBridge.StartFirstPersonLeftHandEditMode("station-edit-menu");
                    break;
                case StationEditMenuTarget.RightHandCamera:
                    Y4NGZPlayerAnimationBridge.StartFirstPersonRightHandBlueEditMode("station-edit-menu");
                    break;
                case StationEditMenuTarget.RightHandAction:
                    Y4NGZPlayerAnimationBridge.StartFirstPersonRightHandGreenEditMode("station-edit-menu");
                    break;
                case StationEditMenuTarget.FocusView:
                    CCTVOperatorStation.StartDebugPlacement("focus");
                    break;
                case StationEditMenuTarget.RadarView:
                    CCTVOperatorStation.StartDebugPlacement("radar");
                    break;
                case StationEditMenuTarget.ThirdPersonDebug:
                    SetStationThirdPersonPreview(true);
                    break;
                case StationEditMenuTarget.ThirdPersonPlayer:
                    SetStationThirdPersonPreview(true);
                    CCTVOperatorStation.StartDebugPlacement("player");
                    break;
            }

            ShowTimedScanStatus("EDIT " + GetStationEditMenuTargetLabel(target), OVERLAY_ACTIVE_GREEN, 0.8f);
        }

        private static void EnsureFirstPersonArmsEnabledForEdit()
        {
            if (Y4NGZPlayerAnimationBridge.UseLocalAuthoredOperatorAnimations)
                return;

            Y4NGZPlayerAnimationBridge.SetLocalAuthoredOperatorAnimationsEnabled(
                true,
                "station-edit-menu-enabled-first-person-arms");
            RefreshLocalOperatorFirstPersonVisibility();
        }

        private static void TickStationBothHandsEditInput(Keyboard keyboard)
        {
            if (keyboard == null)
                return;

            if (!CanEditFirstPersonArmsOffset())
                return;

            EnsureFirstPersonArmsOffsetLoaded();
            _firstPersonArmsOffsetEditingLastFrame = true;

            if (keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame)
            {
                _firstPersonArmsOffset = Vector3.zero;
                _firstPersonArmsOffsetDirty = true;
                ApplyFirstPersonArmsOffsetToForcedRenderers();
                FlushFirstPersonArmsOffsetConfig();
                ShowFirstPersonArmsOffsetStatus(force: true);
                return;
            }

            Vector3 delta = Vector3.zero;
            if (keyboard.aKey.isPressed) delta.x -= 1f;
            if (keyboard.dKey.isPressed) delta.x += 1f;
            if (keyboard.fKey.isPressed || keyboard.pageDownKey.isPressed) delta.y -= 1f;
            if (keyboard.rKey.isPressed || keyboard.pageUpKey.isPressed) delta.y += 1f;
            if (keyboard.sKey.isPressed) delta.z -= 1f;
            if (keyboard.wKey.isPressed) delta.z += 1f;

            if (delta.sqrMagnitude <= 0.0001f)
                return;

            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float speed = fast ? FIRST_PERSON_ARMS_OFFSET_FAST_SPEED : FIRST_PERSON_ARMS_OFFSET_SPEED;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
            _firstPersonArmsOffset = ClampFirstPersonArmsOffset(_firstPersonArmsOffset + delta.normalized * speed * dt);
            _firstPersonArmsOffsetDirty = true;
            ApplyFirstPersonArmsOffsetToForcedRenderers();
            ShowFirstPersonArmsOffsetStatus(force: false);
        }

        internal static string GetStationEditMenuOverlayText()
        {
            if (!_stationEditMenuOpen)
                return null;

            string target = _stationEditMenuTarget == StationEditMenuTarget.None
                ? "SELECT TARGET"
                : GetStationEditMenuTargetLabel(_stationEditMenuTarget);
            string details = GetStationEditMenuTargetDetails(_stationEditMenuTarget);
            return
                "CCTV EDIT MENU\n" +
                "1 BOTH HANDS\n" +
                "2 LEFT HAND LEVER\n" +
                "3 RH CAMERA BUTTON\n" +
                "4 RH ACTION BUTTON\n" +
                "5 NORMAL VIEWPOINT\n" +
                "6 RADAR VIEWPOINT\n" +
                "7 THIRD PERSON DEBUG\n" +
                "8 3P PLAYER MODEL\n" +
                "F6 DUMP CONTROLS\n" +
                "TARGET " + target + "\n" +
                details +
                "F1/ESC EXIT EDIT";
        }

        internal static string GetCameraPlacementEditOverlayText()
        {
            if (!IsFocused)
                return null;

            if (_placementEditorOpen)
                return BuildPlacementEditorOverlayText();

            if (_reviewMenuOpen)
                return BuildCameraReviewMenuOverlayText();

            if (!_cameraPlacementEditModeOpen)
                return null;

            return BuildCameraPlacementEditModeOverlayText();
        }

        private static string BuildCameraPlacementEditModeOverlayText()
        {
            CCTVCamera active = GetActiveCamera();
            string activeLabel = active != null ? active.ResolvedLabel : "NO CAMERA";
            int page = Mathf.Max(1, QuadCameraAssignment.CurrentPage + 1);
            int totalPages = Mathf.Max(1, QuadCameraAssignment.TotalPages);
            return
                "CAMERA EDIT\n" +
                "ACTIVE " + activeLabel + "\n" +
                "PAGE " + page + "/" + totalPages + "\n\n" +
                "MOUSE   AIM CAMERA\n" +
                "ARROWS  CAMERA LIST\n" +
                "F5/1    ADJUST CAMERA\n" +
                "F6/2    ADD CAMERA\n" +
                "F4      RATE CAMERA\n" +
                "DEL     DELETE CAMERA\n" +
                "F2      EXIT CAMERA EDIT\n" +
                "E/ESC   EXIT CCTV";
        }

        private static string GetStationEditMenuTargetDetails(StationEditMenuTarget target)
        {
            switch (target)
            {
                case StationEditMenuTarget.BothHands:
                    EnsureFirstPersonArmsOffsetLoaded();
                    return
                        "POS " + FormatStationEditVector(_firstPersonArmsOffset) + "\n" +
                        "WASD X/Z  R/F UP/DOWN\n" +
                        "SHIFT FAST  0 RESET\n";
                case StationEditMenuTarget.LeftHand:
                {
                    Y4NGZPlayerAnimationBridge.FirstPersonRightHandPoseTuning tuning =
                        Y4NGZPlayerAnimationBridge.GetFirstPersonLeftHandPoseTuning();
                    return
                        "POS " + FormatStationEditVector(tuning.Offset) + "\n" +
                        "ROT " + FormatStationEditVector(tuning.Euler) + "\n" +
                        "WASD X/Z  R/F UP/DOWN\n" +
                        "CTRL ROTATE  SHIFT FAST  0 RESET\n";
                }
                case StationEditMenuTarget.RightHandCamera:
                {
                    Y4NGZPlayerAnimationBridge.FirstPersonRightHandPoseTuning tuning =
                        Y4NGZPlayerAnimationBridge.GetFirstPersonRightHandPoseTuning(1);
                    return
                        "POS " + FormatStationEditVector(tuning.Offset) + "\n" +
                        "ROT " + FormatStationEditVector(tuning.Euler) + "\n" +
                        "WASD X/Z  R/F UP/DOWN\n" +
                        "CTRL ROTATE  SHIFT FAST  0 RESET\n";
                }
                case StationEditMenuTarget.RightHandAction:
                {
                    Y4NGZPlayerAnimationBridge.FirstPersonRightHandPoseTuning tuning =
                        Y4NGZPlayerAnimationBridge.GetFirstPersonRightHandPoseTuning(0);
                    return
                        "POS " + FormatStationEditVector(tuning.Offset) + "\n" +
                        "ROT " + FormatStationEditVector(tuning.Euler) + "\n" +
                        "WASD X/Z  R/F UP/DOWN\n" +
                        "CTRL ROTATE  SHIFT FAST  0 RESET\n";
                }
                case StationEditMenuTarget.FocusView:
                {
                    Transform focus = CCTVOperatorStation.FocusViewAnchor;
                    return
                        (focus != null
                            ? "POS " + FormatStationEditVector(focus.localPosition) + "\n" +
                              "ROT " + FormatStationEditVector(focus.localEulerAngles) + "\n"
                            : string.Empty) +
                        "WASD/ARROWS MOVE X/Z\n" +
                        "Q/E UP/DOWN\n" +
                        "F/G PITCH  R/T YAW\n" +
                        "Z/X ROLL  ENTER SAVE\n";
                }
                case StationEditMenuTarget.RadarView:
                {
                    Transform radar = CCTVOperatorStation.RadarViewAnchor;
                    return
                        (radar != null
                            ? "ROT " + FormatStationEditVector(radar.localEulerAngles) + "\n"
                            : string.Empty) +
                        "ROTATION ONLY - POSITION LOCKED\n" +
                        "A/D OR ARROWS PIVOT\n" +
                        "F/G PITCH  R/T YAW\n" +
                        "Z/X ROLL  ENTER SAVE\n";
                }
                case StationEditMenuTarget.ThirdPersonDebug:
                    return
                        "MOUSE ORBIT CAMERA\n" +
                        "CAMERA CONTROL DISABLED\n";
                case StationEditMenuTarget.ThirdPersonPlayer:
                    return
                        "WASD/ARROWS MOVE X/Z\n" +
                        "Q/E UP/DOWN\n" +
                        "F/G PITCH  R/T YAW\n" +
                        "Z/X ROLL  ENTER SAVE\n";
                default:
                    return "PRESS 1-8 TO EDIT\n";
            }
        }

        private static string GetStationEditMenuTargetLabel(StationEditMenuTarget target)
        {
            switch (target)
            {
                case StationEditMenuTarget.BothHands: return "BOTH HANDS";
                case StationEditMenuTarget.LeftHand: return "LEFT HAND LEVER";
                case StationEditMenuTarget.RightHandCamera: return "RH CAMERA BUTTON";
                case StationEditMenuTarget.RightHandAction: return "RH ACTION BUTTON";
                case StationEditMenuTarget.FocusView: return "NORMAL VIEWPOINT";
                case StationEditMenuTarget.RadarView: return "RADAR VIEWPOINT";
                case StationEditMenuTarget.ThirdPersonDebug: return "THIRD PERSON DEBUG";
                case StationEditMenuTarget.ThirdPersonPlayer: return "3P PLAYER MODEL";
                default: return "NONE";
            }
        }

        private static string FormatStationEditVector(Vector3 value)
        {
            return $"{value.x:+0.00;-0.00;0.00},{value.y:+0.00;-0.00;0.00},{value.z:+0.00;-0.00;0.00}";
        }

        private static bool IsStationRadarLookHeld()
        {
            if (SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowRadarView.Value)
                return false;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.spaceKey.isPressed)
                return true;

            return false;
        }

        private static void ResetStationRadarLookView()
        {
            HoldStationFocusAnchorForAction(0.45f);
        }

        private static void HoldStationFocusAnchorForAction(float seconds)
        {
            float hold = Mathf.Max(0.05f, seconds);
            _stationRadarLookBlend = 0f;
            _stationRadarLookSuppressedUntil = Time.unscaledTime + Mathf.Max(0.35f, hold);
            _stationFocusAnchorForcedUntil = Time.unscaledTime + hold;
            if (_radarLookEventState)
            {
                _radarLookEventState = false;
                CCTVStationEvents.RaiseRadarViewChanged(false);
            }

            // Do not force-write the gameplay camera from input callbacks. The
            // regular focus tick will hold the monitor anchor unless SPACE is held.
        }

        private static void ScheduleStationCameraControlActivation()
        {
            _stationCameraControlActive = false;
            _stationCameraControlEnablePending = true;
            _stationCameraControlEnableAt = Time.unscaledTime + STATION_CAMERA_CONTROL_ENABLE_DELAY;
        }

        private static void DisableStationCameraControl()
        {
            _stationCameraControlEnablePending = false;
            _stationCameraControlEnableAt = 0f;
            if (!_stationCameraControlActive)
                return;

            _stationCameraControlActive = false;
            CCTVStationEvents.RaiseCameraControlChanged(false);
        }

        private static void TickStationCameraControlActivation()
        {
            if (!_stationCameraControlEnablePending)
                return;

            if (!IsFocused || _focusViewExitPending)
            {
                _stationCameraControlEnablePending = false;
                return;
            }

            if (Time.unscaledTime < _stationCameraControlEnableAt)
                return;

            _stationCameraControlEnablePending = false;
            if (_stationCameraControlActive)
                return;

            _stationCameraControlActive = true;
            CCTVStationEvents.RaiseCameraControlChanged(true);
        }

    }
}
