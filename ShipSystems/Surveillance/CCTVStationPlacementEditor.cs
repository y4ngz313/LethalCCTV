using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed class CCTVStationPlacementEditor
    {
        // Orbit-view constants mirror the mainframe/company-stash interior
        // placement editor in Y4NGZDebugTools (2026-07-11: Lawson asked the
        // station editor to use the same scheme — the old fixed camera made
        // small-target rotation edits like the access button unworkable).
        private const float ViewOrbitSensitivity = 0.18f;
        private const float ViewMinPitch = -35f;
        private const float ViewMaxPitch = 55f;

        private Transform _target;
        private string _targetName;
        private Vector3 _startLocalPosition;
        private Quaternion _startLocalRotation;
        private Vector3 _startLocalScale;
        private bool _active;
        private string _status = "ready";
        private float _statusVisibleUntil;
        private float _viewYaw;
        private float _viewPitch;
        private bool _rotationInputLogged;
        private float _nextRotationInputLogAt;

        private PlayerControllerB _player;
        private Camera _camera;
        private bool _capturedInputState;
        private CursorLockMode _savedCursorLock;
        private bool _savedCursorVisible;
        private bool _savedDisableLook;
        private bool _savedDisableMove;
        private bool _capturedCameraTransform;
        private Vector3 _savedCameraLocalPosition;
        private Quaternion _savedCameraLocalRotation;
        private bool _liveFocusCameraMode;

        internal bool IsActive => _active;

        internal void Start(string targetName)
        {
            if (!CCTVOperatorStation.TryGetDebugPlacementTarget(targetName, out Transform target, out string normalized))
            {
                ShowStatus($"CCTV placement target '{targetName ?? "chair"}' unavailable", 3f, hudTip: true);
                return;
            }

            Cancel();

            _target = target;
            _targetName = normalized;
            _startLocalPosition = target.localPosition;
            _startLocalRotation = target.localRotation;
            _startLocalScale = target.localScale;
            _liveFocusCameraMode = MonitorFocus.IsFocused &&
                                   (normalized == "focus" || normalized == "radar" || normalized == "player");
            _viewYaw = 0f;
            _viewPitch = 0f;
            _rotationInputLogged = false;
            _nextRotationInputLogAt = 0f;
            _active = true;
            BeginInputCapture();
            if (!_liveFocusCameraMode)
            {
                EnsurePlacementCamera();
                UpdatePlacementCameraPose();
            }
            ShowStatus($"editing CCTV {_targetName}", 2f, hudTip: true);
            SurveillanceBootstrap.Log?.LogMessage($"[LethalCCTV][ViewEdit] started target={_targetName} pos={target.localPosition} euler={target.localEulerAngles} scale={target.localScale}.");
        }

        internal void Tick()
        {
            if (!_active || _target == null)
                return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                Save();
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame || keyboard.deleteKey.wasPressedThisFrame)
            {
                Cancel();
                return;
            }

            // Mirrors the mainframe/company-stash interior editor: middle-mouse
            // orbits the view around the target, and the camera re-frames the
            // target's bounds every tick so rotation edits stay visible.
            if (!_liveFocusCameraMode)
            {
                UpdateOrbitInput();
                EnsurePlacementCamera();
                UpdatePlacementCameraPose();
            }

            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float moveSpeed = fast ? 4.0f : 0.85f;
            float verticalSpeed = fast ? 1.4f : 0.35f;
            float rotationSpeed = fast ? 120f : 42f;
            float scaleSpeed = fast ? 1.2f : 0.35f;
            bool radarRotationOnly = string.Equals(
                _targetName,
                "radar",
                StringComparison.OrdinalIgnoreCase);

            Vector3 move = Vector3.zero;
            if (!radarRotationOnly)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move += GetEditForward();
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move -= GetEditForward();
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move += GetEditRight();
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move -= GetEditRight();
            }

            // Vertical: space/ctrl (interior-editor layout); Q/E and
            // PageUp/PageDown stay as aliases for muscle memory.
            float vertical = 0f;
            if (!radarRotationOnly)
            {
                if (keyboard.spaceKey.isPressed || keyboard.eKey.isPressed || keyboard.pageUpKey.isPressed) vertical += verticalSpeed * dt;
                if (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed || keyboard.qKey.isPressed || keyboard.pageDownKey.isPressed) vertical -= verticalSpeed * dt;
            }

            // Keep the compact F1-menu layout functional while retaining the
            // interior editor aliases: F/G or I/K pitch, R/T or J/L yaw,
            // Z/X or U/O roll. In the rotation-only radar editor, A/D and the
            // horizontal arrows are also intuitive left/right pivot aliases.
            Vector3 eulerDelta = Vector3.zero;
            if (keyboard.iKey.isPressed || keyboard.fKey.isPressed) eulerDelta.x -= rotationSpeed * dt;
            if (keyboard.kKey.isPressed || keyboard.gKey.isPressed) eulerDelta.x += rotationSpeed * dt;
            if (keyboard.jKey.isPressed || keyboard.rKey.isPressed) eulerDelta.y -= rotationSpeed * dt;
            if (keyboard.lKey.isPressed || keyboard.tKey.isPressed) eulerDelta.y += rotationSpeed * dt;
            if (keyboard.uKey.isPressed || keyboard.zKey.isPressed) eulerDelta.z -= rotationSpeed * dt;
            if (keyboard.oKey.isPressed || keyboard.xKey.isPressed) eulerDelta.z += rotationSpeed * dt;
            if (radarRotationOnly)
            {
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                    eulerDelta.y -= rotationSpeed * dt;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                    eulerDelta.y += rotationSpeed * dt;
            }

            float scaleDelta = 0f;
            if (!radarRotationOnly)
            {
                if (keyboard.cKey.isPressed) scaleDelta -= scaleSpeed * dt;
                if (keyboard.vKey.isPressed) scaleDelta += scaleSpeed * dt;
            }

            Vector3 worldDelta = move.sqrMagnitude > 0.0001f ? move.normalized * moveSpeed * dt : Vector3.zero;
            if (Mathf.Abs(vertical) > 0.0001f)
                worldDelta += Vector3.up * vertical;

            if (worldDelta.sqrMagnitude > 0f || eulerDelta.sqrMagnitude > 0.000001f || Mathf.Abs(scaleDelta) > 0.0001f)
                Nudge(worldDelta, eulerDelta, scaleDelta);
        }

        internal bool IsEditingTarget(string targetName)
        {
            return _active && string.Equals(_targetName, NormalizeTargetForCompare(targetName), StringComparison.OrdinalIgnoreCase);
        }

        internal string GetStatus()
        {
            string state = _active
                ? $"editing {_targetName} {FormatSummary()}"
                : Time.unscaledTime <= _statusVisibleUntil ? _status : "ready";
            string status = $"CCTV station placement: {state}";
            if (_active)
            {
                bool radar = string.Equals(_targetName, "radar", StringComparison.OrdinalIgnoreCase);
                bool focus = string.Equals(_targetName, "focus", StringComparison.OrdinalIgnoreCase);
                status += radar
                    ? "\nControls ROTATION ONLY: A/D or arrows pivot, F/G pitch, R/T yaw, Z/X roll (I/K, J/L, U/O aliases), Enter save, Esc cancel"
                    : focus
                        ? "\nControls WASD move, SPACE/CTRL height, F/G pitch, R/T yaw, Z/X roll (I/K, J/L, U/O aliases), Enter save, Esc cancel"
                        : "\nControls WASD move, SPACE/CTRL height, F/G pitch, R/T yaw, Z/X roll (I/K, J/L, U/O aliases), C/V scale, MIDDLE-MOUSE orbit view, Enter save, Esc cancel";
            }
            return status;
        }

        internal void Cancel()
        {
            if (!_active)
                return;

            if (_target != null)
            {
                _target.localPosition = _startLocalPosition;
                _target.localRotation = _startLocalRotation;
                _target.localScale = _startLocalScale;
                // The throttle group's secondaries (lever handle, casing) were moved
                // along during the edit; restore them to the same captured group pose.
                if (string.Equals(_targetName, "throttle", StringComparison.OrdinalIgnoreCase))
                    CCTVOperatorStation.SyncThrottleSecondaryTargetsToPrimary(_target);
            }

            _active = false;
            _liveFocusCameraMode = false;
            RestoreInputCapture();
            ShowStatus("CCTV placement cancelled", 1.5f, hudTip: true);
        }

        // hudTip is reserved for explicit one-shot user actions (start/save/cancel/
        // preview). Per-frame paths (Nudge, scale ticks) must pass false — the status
        // string is surfaced by DebugTools' GUI via GetStatus, and a DisplayTip per
        // frame floods the vanilla HUD notification queue.
        internal void ShowStatus(string message, float seconds, bool hudTip = false)
        {
            _status = message ?? string.Empty;
            _statusVisibleUntil = Time.unscaledTime + Mathf.Max(0.1f, seconds);
            if (hudTip)
                HUDManager.Instance?.DisplayTip("CCTV PLACEMENT", _status, isWarning: false);
        }

        private void Save()
        {
            if (_target == null)
                return;

            if (string.Equals(_targetName, "radar", StringComparison.OrdinalIgnoreCase) &&
                CCTVOperatorStation.FocusViewAnchor != null)
            {
                // Radar is a rotation-only glance from the normal CCTV eye.
                // Normalize stale profile translations when the rotation is saved.
                _target.position = CCTVOperatorStation.FocusViewAnchor.position;
            }
            CCTVOperatorStation.SaveDebugPlacement(_targetName, _target);
            SurveillanceBootstrap.Log?.LogMessage(
                $"[LethalCCTV][ViewEdit] saved target={_targetName} {FormatSummary()}.");
            _active = false;
            _liveFocusCameraMode = false;
            RestoreInputCapture();
            ShowStatus("CCTV placement saved", 2f, hudTip: true);
        }

        private void Nudge(Vector3 worldDelta, Vector3 localEulerDelta, float scaleDelta)
        {
            if (_target == null)
                return;

            _target.position += worldDelta;

            if (localEulerDelta.sqrMagnitude > 0.000001f)
            {
                bool stationViewpoint =
                    string.Equals(_targetName, "focus", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_targetName, "radar", StringComparison.OrdinalIgnoreCase);
                if (stationViewpoint)
                {
                    // Viewpoints are cameras, not prop transforms. Yaw must be
                    // a pivot around world-up; adding local Euler Y under the
                    // rotated station hierarchy does not reliably turn the
                    // rendered view left/right. Pitch and roll use the live
                    // camera's right/forward axes for the same reason.
                    Quaternion worldRotation = _target.rotation;
                    if (Mathf.Abs(localEulerDelta.y) > 0.000001f)
                        worldRotation = Quaternion.AngleAxis(
                            localEulerDelta.y,
                            Vector3.up) * worldRotation;
                    if (Mathf.Abs(localEulerDelta.x) > 0.000001f)
                    {
                        Vector3 pitchAxis = worldRotation * Vector3.right;
                        worldRotation = Quaternion.AngleAxis(
                            localEulerDelta.x,
                            pitchAxis) * worldRotation;
                    }
                    if (Mathf.Abs(localEulerDelta.z) > 0.000001f)
                    {
                        Vector3 rollAxis = worldRotation * Vector3.forward;
                        worldRotation = Quaternion.AngleAxis(
                            localEulerDelta.z,
                            rollAxis) * worldRotation;
                    }
                    _target.rotation = worldRotation;
                }
                else
                {
                    _target.localRotation = Quaternion.Euler(
                        _target.localEulerAngles + localEulerDelta);
                }
                // Sampled on the same 0.5s cadence as [RadarEditDiag] and from the
                // earlier bootstrap LateUpdate step, so the pair brackets one frame:
                // worldEuler here is what the editor wrote, anchorEuler there is what
                // the camera read. A divergence between them localises the defect to
                // the anchor; agreement moves it downstream of the anchor entirely.
                if (!_rotationInputLogged || Time.unscaledTime >= _nextRotationInputLogAt)
                {
                    _rotationInputLogged = true;
                    _nextRotationInputLogAt = Time.unscaledTime + 0.5f;
                    SurveillanceBootstrap.Log?.LogMessage(
                        $"[LethalCCTV][ViewEdit] rotation input target={_targetName} delta={localEulerDelta} " +
                        $"localEuler={_target.localEulerAngles} worldEuler={_target.rotation.eulerAngles}.");
                }
            }

            if (Mathf.Abs(scaleDelta) > 0.0001f)
            {
                float multiplier = Mathf.Max(0.01f, 1f + scaleDelta);
                _target.localScale = NormalizeScale(_target.localScale * multiplier);
            }

            if (string.Equals(_targetName, "throttle", StringComparison.OrdinalIgnoreCase))
                CCTVOperatorStation.SyncThrottleSecondaryTargetsToPrimary(_target);

            if (!_liveFocusCameraMode)
                UpdatePlacementCameraPose();
            ShowStatus($"editing CCTV {_targetName} {FormatSummary()}", 0.35f);
        }

        private Vector3 GetEditForward()
        {
            Transform basis = _camera != null ? _camera.transform : _target;
            Vector3 forward = basis != null ? basis.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            return forward.normalized;
        }

        private Vector3 GetEditRight()
        {
            Transform basis = _camera != null ? _camera.transform : _target;
            Vector3 right = basis != null ? basis.right : Vector3.right;
            right.y = 0f;
            if (right.sqrMagnitude < 0.0001f)
                right = Vector3.right;
            return right.normalized;
        }

        private void BeginInputCapture()
        {
            PlayerControllerB player = ResolvePlayer();
            if (player == null)
                return;

            RestoreInputCapture();

            _player = player;
            _savedCursorLock = Cursor.lockState;
            _savedCursorVisible = Cursor.visible;
            _savedDisableLook = player.disableLookInput;
            _savedDisableMove = player.disableMoveInput;
            player.disableLookInput = true;
            player.disableMoveInput = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _capturedInputState = true;
        }

        private void RestoreInputCapture()
        {
            RestoreCameraTransform();

            if (_capturedInputState)
            {
                if (_player != null)
                {
                    _player.disableLookInput = _savedDisableLook;
                    _player.disableMoveInput = _savedDisableMove;
                }

                Cursor.lockState = _savedCursorLock;
                Cursor.visible = _savedCursorVisible;
            }

            _capturedInputState = false;
            _player = null;
            _camera = null;
        }

        private void EnsurePlacementCamera()
        {
            Camera source = ResolveCamera();
            if (source == null)
                return;

            if (!_capturedCameraTransform)
            {
                _savedCameraLocalPosition = source.transform.localPosition;
                _savedCameraLocalRotation = source.transform.localRotation;
                _capturedCameraTransform = true;
            }

            _camera = source;
            _camera.enabled = true;
            _camera.cullingMask |= 1 << 0;
        }

        private Camera ResolveCamera()
        {
            PlayerControllerB player = _player ?? ResolvePlayer();
            if (player?.gameplayCamera != null)
                return player.gameplayCamera;

            if (Camera.main != null)
                return Camera.main;

            Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].enabled)
                    return cameras[i];
            }

            return null;
        }

        private static PlayerControllerB ResolvePlayer()
        {
            if (GameNetworkManager.Instance != null && GameNetworkManager.Instance.localPlayerController != null)
                return GameNetworkManager.Instance.localPlayerController;
            return StartOfRound.Instance != null ? StartOfRound.Instance.localPlayerController : null;
        }

        private void UpdateOrbitInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.middleButton.isPressed)
                return;

            Vector2 delta = mouse.delta.ReadValue();
            if (delta.sqrMagnitude <= 0.0001f)
                return;

            _viewYaw += delta.x * ViewOrbitSensitivity;
            _viewPitch = Mathf.Clamp(_viewPitch - delta.y * ViewOrbitSensitivity, ViewMinPitch, ViewMaxPitch);
        }

        private void UpdatePlacementCameraPose()
        {
            if (_camera == null || _target == null)
                return;

            if (!TryGetRendererBounds(_target, out Bounds bounds))
                bounds = new Bounds(_target.position + Vector3.up * 0.5f, new Vector3(1.2f, 1.2f, 1.2f));

            Vector3 focus = bounds.center;
            Vector3 front = _target.forward;
            front.y = 0f;
            if (front.sqrMagnitude < 0.0001f)
                front = Vector3.forward;
            front.Normalize();

            // Bounds-scaled orbit framing (interior-editor formula, tightened
            // for the cramped ship: small targets like the access button pull
            // the camera in close instead of clipping through the hull).
            float horizontalSpan = Mathf.Max(0.4f, Mathf.Max(bounds.size.x, bounds.size.z));
            float verticalSpan = Mathf.Max(0.25f, bounds.size.y);
            float distance = Mathf.Clamp(horizontalSpan * 0.95f + verticalSpan * 0.35f + 0.9f, 1.1f, 8.0f);
            float heightOffset = Mathf.Clamp(verticalSpan * 0.45f + horizontalSpan * 0.08f, 0.25f, 3.0f);

            Vector3 orbitForward = Quaternion.AngleAxis(_viewYaw, Vector3.up) * front;
            float pitchRadians = Mathf.Clamp(_viewPitch, ViewMinPitch, ViewMaxPitch) * Mathf.Deg2Rad;
            float horizontalDistance = Mathf.Max(0.2f, distance * Mathf.Cos(pitchRadians));
            float verticalOffset = heightOffset + distance * Mathf.Sin(pitchRadians);
            Vector3 cameraPosition = focus + orbitForward * horizontalDistance + Vector3.up * verticalOffset;

            Vector3 lookDirection = focus - cameraPosition;
            if (lookDirection.sqrMagnitude < 0.0001f)
                lookDirection = -front;

            _camera.transform.SetPositionAndRotation(cameraPosition, Quaternion.LookRotation(lookDirection.normalized, Vector3.up));
        }

        private void RestoreCameraTransform()
        {
            if (!_capturedCameraTransform)
                return;

            if (_camera != null)
            {
                _camera.transform.localPosition = _savedCameraLocalPosition;
                _camera.transform.localRotation = _savedCameraLocalRotation;
            }

            _capturedCameraTransform = false;
            _savedCameraLocalPosition = Vector3.zero;
            _savedCameraLocalRotation = Quaternion.identity;
        }

        private static bool TryGetRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
                return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            bool initialized = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                if (!initialized)
                {
                    bounds = renderer.bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return initialized;
        }

        private string FormatSummary()
        {
            if (_target == null)
                return string.Empty;

            Vector3 pos = _target.localPosition;
            Vector3 euler = _target.localEulerAngles;
            Vector3 scale = _target.localScale;
            return $"pos=({pos.x:0.00},{pos.y:0.00},{pos.z:0.00}) rot=({euler.x:0},{euler.y:0},{euler.z:0}) scale=({scale.x:0.00},{scale.y:0.00},{scale.z:0.00})";
        }

        private static Vector3 NormalizeScale(Vector3 scale)
        {
            return new Vector3(
                Mathf.Clamp(scale.x, 0.05f, 5f),
                Mathf.Clamp(scale.y, 0.05f, 5f),
                Mathf.Clamp(scale.z, 0.05f, 5f));
        }

        private static string NormalizeTargetForCompare(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return "focus";

            string lower = targetName.Trim().ToLowerInvariant();
            if (lower == "seat" || lower == "operator" || lower == "operatorchair")
                return "chair";
            if (lower == "stick" || lower == "joy" || lower == "joystick" || lower == "control")
                return "throttle";
            if (lower == "lever" || lower == "startlever" || lower == "shiplever" || lower == "startmatchlever" ||
                lower == "brake" || lower == "brakelever" || lower == "throttlelever" || lower == "gearstick" ||
                lower == "gear" || lower == "sticklever")
                return "throttle";
            if (lower == "view" || lower == "camera" || lower == "focusview" || lower == "focusanchor")
                return "focus";
            if (lower == "radar" || lower == "radarview" || lower == "radaranchor" ||
                lower == "radarfocus" || lower == "spaceview" || lower == "spacelook")
                return "radar";
            if (lower == "player" || lower == "playerroot" || lower == "playerrootanchor" ||
                lower == "thirdperson" || lower == "thirdpersonplayer" || lower == "thirdpersonmodel" ||
                lower == "playermodel" || lower == "model" || lower == "root")
                return "player";
            if (lower == "operatorpose" || lower == "poseanchor" || lower == "bodypose")
                return "pose";
            if (lower == "button" || lower == "accessbutton" || lower == "cctvbutton" ||
                lower == "cctvaccessbutton" || lower == "interactbutton" || lower == "entrybutton")
                return "button";
            return lower;
        }
    }
}
