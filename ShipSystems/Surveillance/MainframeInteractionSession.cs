using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZCompany.Bootstrap;

using Y4NGZCompany.Facility.Mainframe;
namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed class MainframeInteractionSession
    {
        private const float PlayerBlendDuration = 0.52f;
        private const float CameraApproachDuration = 0.52f;
        private const float KeyboardGlanceHoldSeconds = 1.10f;
        private const float CameraFocusBlendDuration = 0.90f;
        private const float TeleportFailsafeDistance = 5.25f;
        private const string DefaultStopTrigger = "SA_stopAnimation";

        // Lethal Company renders at a very low internal resolution and the mainframe screen is a
        // world-space canvas drawn by the gameplay camera, so the only way to give the screen text
        // more actual pixels is to make the screen cover more of the frame. The focus pose is fixed
        // by the prefab anchor, so the remaining lever is a modest lens zoom held for as long as
        // the player is reading the screen. Restored with the rest of the camera state on Stop.
        private const float FocusFovFactor = 0.72f;
        private const float MinFocusFieldOfView = 36f;

        private readonly MonoBehaviour _owner;
        private readonly Transform _root;
        private readonly MainframeSupport _mainframe;
        private readonly MainframeScreenController _screen;

        private MainframeTypingArms _arms;

        private Transform _playerRootAnchor;
        private Transform _focusAnchor;
        private Transform _glanceAnchor;
        private bool _missingAnchorLogged;

        private PlayerControllerB _player;
        private Camera _camera;
        private Transform _cameraTransform;
        private Transform _savedCameraParent;
        private Vector3 _savedCameraLocalPosition;
        private Quaternion _savedCameraLocalRotation;
        private float _savedCameraFieldOfView;
        private float _savedCameraNearClipPlane;
        private bool _cameraStateCaptured;
        private Vector3 _introStartPosition;
        private Quaternion _introStartRotation;

        private Vector3 _playerStartPosition;
        private Quaternion _playerStartRotation;
        private bool _savedInSpecialInteractAnimation;
        private bool _savedEnteringSpecialAnimation;
        private bool _savedDisableLookInput;
        private bool _savedDisableMoveInput;
        private bool _savedDisableInteract;
        private bool _savedControllerEnabled;
        private bool _controllerStateCaptured;

        private GrabbableObject _hiddenHeldItem;
        private Renderer[] _visorRenderers;
        private bool[] _visorRendererStates;
        private readonly List<SuppressedHudEntry> _suppressedHudEntries = new List<SuppressedHudEntry>();

        private string _enterTrigger;
        private string _stopTrigger;
        private int _startingHealth = -1;
        private int _lastHealth = -1;
        private float _startedAt;
        private float _playerBlendStartedAt;
        private short _sessionYaw;
        private bool _active;
        private bool _playerBlendComplete;

        private static MainframeInteractionSession _activeLocalSession;
        private static int _lastActiveFrame = -1;

        internal bool IsActive => _active;

        internal static bool IsAnyLocalSessionActive =>
            _activeLocalSession != null && _activeLocalSession.IsActive;

        internal static bool WasActiveThisFrame => _lastActiveFrame == Time.frameCount;

        /// <summary>Screen-controller semantic keystrokes (overlay-accepted inputs) route into
        /// the active local session's typing arms; no-op when no session or arms are active.</summary>
        internal static void NotifyScreenKeystroke(int fingerIndex, float strength)
        {
            MainframeInteractionSession session = _activeLocalSession;
            if (session == null || !session.IsActive)
                return;

            session._arms?.PlaySemanticKeystroke(fingerIndex, strength);
        }

        internal MainframeInteractionSession(
            MonoBehaviour owner,
            Transform root,
            MainframeSupport mainframe,
            MainframeScreenController screen)
        {
            _owner = owner;
            _root = root;
            _mainframe = mainframe;
            _screen = screen;
        }

        internal bool Begin(PlayerControllerB player)
        {
            if (_active)
                Stop("reenter", returnScreenToIdle: true);

            if (player == null || !IsLocalPlayer(player))
                return false;

            if (_mainframe == null || _screen == null || !EnsureAnchors())
                return false;

            if (_mainframe.IsLockedOut)
                return false;

            _player = player;
            _camera = player.gameplayCamera;
            _cameraTransform = _camera != null ? _camera.transform : null;
            _playerStartPosition = player.transform.position;
            _playerStartRotation = player.transform.rotation;
            _sessionYaw = ResolveAnchorYaw();
            _startedAt = Time.unscaledTime;
            _playerBlendStartedAt = _startedAt;
            _playerBlendComplete = false;
            _startingHealth = ReadPlayerHealth(player);
            _lastHealth = _startingHealth;
            _enterTrigger = null;
            _stopTrigger = DefaultStopTrigger;
            _active = true;
            _activeLocalSession = this;
            MarkActiveThisFrame();

            CaptureCameraState();
            if (_cameraTransform != null)
            {
                _introStartPosition = _cameraTransform.position;
                _introStartRotation = _cameraTransform.rotation;
            }
            CapturePlayerControlState();
            HideLocalViewObstructions();
            SuppressHudForSession();
            ResolveVanillaTerminalAnimation(out _enterTrigger, out _stopTrigger);
            EnterSpecialAnimation();
            CCTVOperatorAnimSync.SendMainframeEnter(player, _sessionYaw);
            _arms = new MainframeTypingArms(_root);
            if (!_arms.Begin(player))
                _arms = null;

            if (_mainframe.IsHacked)
                _screen.OpenHackedMainframe();
            else
                _screen.BeginHackingIntro();

            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][MainframeInteract] session_started " +
                $"player=#{player.playerClientId} yaw={_sessionYaw} hacked={_mainframe.IsHacked} " +
                $"health={_startingHealth} root='{(_root != null ? _root.name : "<null>")}'.");

            return true;
        }

        internal void Tick()
        {
            if (!_active)
                return;

            MarkActiveThisFrame();

            string interruptReason = GetInterruptReason();
            if (!string.IsNullOrEmpty(interruptReason))
            {
                Stop(interruptReason, returnScreenToIdle: true);
                return;
            }

            if (IsEscapePressed())
            {
                Stop("escape", returnScreenToIdle: true);
                return;
            }

            DrivePlayerAnchorBlend();
            _arms?.Tick();
        }

        internal void LateTick()
        {
            if (!_active)
                return;

            MarkActiveThisFrame();

            if (_playerBlendComplete)
                SetPlayerRootToAnchor();

            DriveCameraLate();
            _arms?.LateTick(Time.unscaledTime - _startedAt);
        }

        internal void Stop(string reason, bool returnScreenToIdle)
        {
            if (!_active && _player == null)
                return;

            MarkActiveThisFrame();

            PlayerControllerB player = _player;
            if (player != null)
            {
                TryTriggerPlayerAnimator(player, _stopTrigger, "exit");
                RestoreSpecialAnimation(player);
                RestorePlayerControl(player);
                CCTVOperatorAnimSync.SendMainframeExit(player);
            }

            _arms?.Stop(reason);
            _arms = null;
            RestoreLocalViewObstructions(player);
            RestoreHudForSession();
            RestoreCameraState();

            if (returnScreenToIdle && _screen != null)
                _screen.ReturnToIdleFromInteraction(reason ?? "session-ended");

            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][MainframeInteract] session_stopped " +
                $"reason='{reason ?? "unspecified"}' player=#{(player != null ? player.playerClientId.ToString() : "<none>")}.");

            if (ReferenceEquals(_activeLocalSession, this))
                _activeLocalSession = null;

            _player = null;
            _camera = null;
            _cameraTransform = null;
            _savedCameraParent = null;
            _cameraStateCaptured = false;
            _introStartPosition = Vector3.zero;
            _introStartRotation = Quaternion.identity;
            _controllerStateCaptured = false;
            _enterTrigger = null;
            _stopTrigger = null;
            _startingHealth = -1;
            _lastHealth = -1;
            _sessionYaw = 0;
            _active = false;
            _playerBlendComplete = false;
        }

        private bool EnsureAnchors()
        {
            if (_playerRootAnchor == null)
                _playerRootAnchor = FindDeepChild(_root, "MainframePlayerRootAnchor");
            if (_focusAnchor == null)
                _focusAnchor = FindDeepChild(_root, "MainframeFocusViewAnchor");
            if (_glanceAnchor == null)
                _glanceAnchor = FindDeepChild(_root, "MainframeKeyboardGlanceAnchor");

            bool ready = _playerRootAnchor != null && _focusAnchor != null && _glanceAnchor != null;
            if (!ready && !_missingAnchorLogged)
            {
                _missingAnchorLogged = true;
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][MainframeInteract] Missing MainframePlayerRootAnchor, MainframeKeyboardGlanceAnchor, or MainframeFocusViewAnchor; mainframe session disabled.");
            }

            return ready;
        }

        private void CaptureCameraState()
        {
            if (_camera == null || _cameraTransform == null)
                return;

            _savedCameraParent = _cameraTransform.parent;
            _savedCameraLocalPosition = _cameraTransform.localPosition;
            _savedCameraLocalRotation = _cameraTransform.localRotation;
            _savedCameraFieldOfView = _camera.fieldOfView;
            _savedCameraNearClipPlane = _camera.nearClipPlane;
            _cameraStateCaptured = true;
        }

        private void RestoreCameraState()
        {
            if (!_cameraStateCaptured || _camera == null || _cameraTransform == null)
                return;

            try
            {
                if (_cameraTransform.parent == _savedCameraParent)
                {
                    _cameraTransform.localPosition = _savedCameraLocalPosition;
                    _cameraTransform.localRotation = _savedCameraLocalRotation;
                }

                _camera.fieldOfView = _savedCameraFieldOfView;
                _camera.nearClipPlane = _savedCameraNearClipPlane;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to restore camera state: {ex.Message}");
            }
        }

        /// <summary>2026-07-05 playtest: the held item (parented to the right hand the typing
        /// pose drives) and the helmet visor rim both cover the screen during the close-up.
        /// Hide both locally for the session; remote clients are unaffected.</summary>
        private void HideLocalViewObstructions()
        {
            if (_player == null)
                return;

            try
            {
                GrabbableObject held = _player.currentlyHeldObjectServer;
                if (held != null)
                {
                    held.EnableItemMeshes(false);
                    _hiddenHeldItem = held;
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to hide held item meshes: {ex.Message}");
            }

            try
            {
                if (_player.localVisor != null)
                {
                    _visorRenderers = _player.localVisor.GetComponentsInChildren<Renderer>(true);
                    _visorRendererStates = new bool[_visorRenderers.Length];
                    for (int i = 0; i < _visorRenderers.Length; i++)
                    {
                        _visorRendererStates[i] = _visorRenderers[i] != null && _visorRenderers[i].enabled;
                        if (_visorRenderers[i] != null)
                            _visorRenderers[i].enabled = false;
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to hide local visor: {ex.Message}");
            }
        }

        private void RestoreLocalViewObstructions(PlayerControllerB player)
        {
            try
            {
                // Only re-enable if it is still the held object; drops/pocketing during an
                // interrupt are managed by vanilla and must not be overridden.
                if (_hiddenHeldItem != null && player != null &&
                    ReferenceEquals(player.currentlyHeldObjectServer, _hiddenHeldItem))
                {
                    _hiddenHeldItem.EnableItemMeshes(true);
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to restore held item meshes: {ex.Message}");
            }

            try
            {
                if (_visorRenderers != null && _visorRendererStates != null)
                {
                    for (int i = 0; i < _visorRenderers.Length && i < _visorRendererStates.Length; i++)
                    {
                        if (_visorRenderers[i] != null)
                            _visorRenderers[i].enabled = _visorRendererStates[i];
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to restore local visor: {ex.Message}");
            }

            _hiddenHeldItem = null;
            _visorRenderers = null;
            _visorRendererStates = null;
        }

        private void SuppressHudForSession()
        {
            try
            {
                HUDManager hud = HUDManager.Instance;
                if (hud == null)
                    return;

                SuppressHudElement(hud.Inventory);
                SuppressHudElement(hud.Tooltips);
                SuppressHudElement(hud.Chat);
                SuppressHudElement(hud.PlayerInfo);
                SuppressHudElement(hud.Clock);
                SuppressHudElement(hud.Compass);
                SuppressHudElement(hud.InstabilityCounter);
                SuppressCanvasGroup(hud.holdInteractionCanvasGroup);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to suppress HUD for session: {ex.Message}");
            }
        }

        private void RestoreHudForSession()
        {
            try
            {
                for (int i = _suppressedHudEntries.Count - 1; i >= 0; i--)
                {
                    SuppressedHudEntry entry = _suppressedHudEntries[i];
                    if (entry.Element != null)
                        entry.Element.targetAlpha = entry.OriginalTargetAlpha;

                    if (entry.Group != null)
                    {
                        entry.Group.alpha = entry.OriginalAlpha;
                        entry.Group.blocksRaycasts = entry.OriginalBlocksRaycasts;
                        entry.Group.interactable = entry.OriginalInteractable;
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to restore HUD after session: {ex.Message}");
            }
            finally
            {
                _suppressedHudEntries.Clear();
            }
        }

        private void SuppressHudElement(HUDElement element)
        {
            if (element == null || element.canvasGroup == null)
                return;

            SuppressCanvasGroup(element.canvasGroup, element);
        }

        private void SuppressCanvasGroup(CanvasGroup group, HUDElement element = null)
        {
            if (group == null)
                return;

            for (int i = 0; i < _suppressedHudEntries.Count; i++)
            {
                if (_suppressedHudEntries[i].Group == group)
                {
                    if (element != null)
                        element.targetAlpha = 0f;

                    group.alpha = 0f;
                    group.blocksRaycasts = false;
                    group.interactable = false;
                    return;
                }
            }

            _suppressedHudEntries.Add(new SuppressedHudEntry(element, group));
            if (element != null)
                element.targetAlpha = 0f;

            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
        }

        private readonly struct SuppressedHudEntry
        {
            internal readonly HUDElement Element;
            internal readonly CanvasGroup Group;
            internal readonly float OriginalTargetAlpha;
            internal readonly float OriginalAlpha;
            internal readonly bool OriginalBlocksRaycasts;
            internal readonly bool OriginalInteractable;

            internal SuppressedHudEntry(HUDElement element, CanvasGroup group)
            {
                Element = element;
                Group = group;
                OriginalTargetAlpha = element != null ? element.targetAlpha : 0f;
                OriginalAlpha = group != null ? group.alpha : 0f;
                OriginalBlocksRaycasts = group != null && group.blocksRaycasts;
                OriginalInteractable = group != null && group.interactable;
            }
        }

        private void CapturePlayerControlState()
        {
            if (_player == null)
                return;

            _savedInSpecialInteractAnimation = _player.inSpecialInteractAnimation;
            _savedEnteringSpecialAnimation = _player.enteringSpecialAnimation;
            _savedDisableLookInput = _player.disableLookInput;
            _savedDisableMoveInput = _player.disableMoveInput;
            _savedDisableInteract = _player.disableInteract;

            if (_player.thisController != null)
            {
                _savedControllerEnabled = _player.thisController.enabled;
                _controllerStateCaptured = true;
            }
        }

        private void EnterSpecialAnimation()
        {
            if (_player == null)
                return;

            try
            {
                _player.Crouch(false);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Crouch(false) failed: {ex.Message}");
            }

            try
            {
                _player.disableLookInput = true;
                _player.disableMoveInput = true;
                _player.disableInteract = true;
                _player.UpdateSpecialAnimationValue(true, _sessionYaw);
                _player.inSpecialInteractAnimation = true;
                _player.enteringSpecialAnimation = false;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to enter special-animation stance: {ex.Message}");
            }

            TryTriggerPlayerAnimator(_player, _enterTrigger, "enter");
        }

        private void RestoreSpecialAnimation(PlayerControllerB player)
        {
            if (player == null)
                return;

            try
            {
                player.UpdateSpecialAnimationValue(false, 0);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] UpdateSpecialAnimationValue(false) failed: {ex.Message}");
            }

            try
            {
                player.inSpecialInteractAnimation = _savedInSpecialInteractAnimation;
                player.enteringSpecialAnimation = _savedEnteringSpecialAnimation;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to restore special-animation flags: {ex.Message}");
            }
        }

        private void RestorePlayerControl(PlayerControllerB player)
        {
            if (player == null)
                return;

            try
            {
                player.disableLookInput = _savedDisableLookInput;
                player.disableMoveInput = _savedDisableMoveInput;
                player.disableInteract = _savedDisableInteract;

                if (_controllerStateCaptured && player.thisController != null)
                    player.thisController.enabled = _savedControllerEnabled;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Failed to restore player controls: {ex.Message}");
            }
        }

        private void DrivePlayerAnchorBlend()
        {
            if (_player == null || _playerRootAnchor == null)
                return;

            float t = Mathf.Clamp01((Time.unscaledTime - _playerBlendStartedAt) / Mathf.Max(0.01f, PlayerBlendDuration));
            float k = Smooth(t);

            Vector3 targetPosition = _playerRootAnchor.position;
            Quaternion targetRotation = Quaternion.Euler(0f, _playerRootAnchor.eulerAngles.y, 0f);
            Vector3 position = Vector3.Lerp(_playerStartPosition, targetPosition, k);
            Quaternion rotation = Quaternion.Slerp(_playerStartRotation, targetRotation, k);

            try
            {
                _player.transform.SetPositionAndRotation(position, rotation);
                if (t >= 1f)
                {
                    _playerBlendComplete = true;
                    SetPlayerRootToAnchor();
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Player anchor blend failed: {ex.Message}");
            }
        }

        private void SetPlayerRootToAnchor()
        {
            if (_player == null || _playerRootAnchor == null)
                return;

            try
            {
                _player.transform.SetPositionAndRotation(
                    _playerRootAnchor.position,
                    Quaternion.Euler(0f, _playerRootAnchor.eulerAngles.y, 0f));
            }
            catch
            {
            }
        }

        private void DriveCameraLate()
        {
            if (_cameraTransform == null || _playerRootAnchor == null || _glanceAnchor == null || _focusAnchor == null)
                return;

            float time = Time.unscaledTime - _startedAt;
            // Blend from the camera's actual captured pose — LC's real standing eye height is
            // taller than the preview's 1.62 guess (proven by the 2026-07-05 crosshair playtest).
            Vector3 standPos = _introStartPosition;
            Quaternion standRot = _introStartRotation;

            float lookUpStart = CameraApproachDuration + KeyboardGlanceHoldSeconds;
            Vector3 camPos;
            Quaternion camRot;
            float focusBlend;

            if (time < CameraApproachDuration)
            {
                float k = Smooth(time / Mathf.Max(0.01f, CameraApproachDuration));
                camPos = Vector3.Lerp(standPos, _glanceAnchor.position, k);
                camRot = Quaternion.Slerp(standRot, _glanceAnchor.rotation, k);
                focusBlend = 0f;
            }
            else if (time < lookUpStart)
            {
                camPos = _glanceAnchor.position;
                camRot = _glanceAnchor.rotation;
                focusBlend = 0f;
            }
            else
            {
                float k = Smooth(Mathf.Clamp01((time - lookUpStart) / Mathf.Max(0.01f, CameraFocusBlendDuration)));
                camPos = Vector3.Lerp(_glanceAnchor.position, _focusAnchor.position, k);
                camRot = Quaternion.Slerp(_glanceAnchor.rotation, _focusAnchor.rotation, k);
                focusBlend = k;
            }

            try
            {
                _cameraTransform.SetPositionAndRotation(camPos, camRot);
                ApplyFocusZoom(focusBlend);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Camera drive failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Narrows the lens as the camera settles on the screen, so the screen fills more of the
        /// frame and its text lands on more of the game's (very few) internal pixels. The zoom
        /// tracks the same blend as the pose, so it never pops, and it is driven every late frame
        /// from the captured base FOV, so a vanilla FOV write earlier in the frame cannot fight it.
        /// </summary>
        private void ApplyFocusZoom(float focusBlend)
        {
            if (!_cameraStateCaptured || _camera == null)
                return;

            float baseFov = Mathf.Max(1f, _savedCameraFieldOfView);
            float zoomedFov = Mathf.Max(MinFocusFieldOfView, baseFov * FocusFovFactor);
            _camera.fieldOfView = Mathf.Lerp(baseFov, Mathf.Min(baseFov, zoomedFov), Mathf.Clamp01(focusBlend));
        }

        private string GetInterruptReason()
        {
            if (_player == null || !_player.gameObject.activeInHierarchy)
                return "player-missing";

            if (!IsLocalPlayer(_player))
                return "not-local-player";

            if (_mainframe == null)
                return "mainframe-missing";

            if (_mainframe.IsLockedOut)
                return "lockout";

            try
            {
                if (_player.isPlayerDead)
                    return "death";
                if (!_player.isPlayerControlled)
                    return "player-not-controlled";
                if (_player.isClimbingLadder)
                    return "ladder";
                if (_player.inTerminalMenu)
                    return "terminal-menu";
                if (_player.isTypingChat)
                    return "chat";
                if (_player.enteringSpecialAnimation)
                    return "other-special-entering";
                if (!_player.inSpecialInteractAnimation)
                    return "special-animation-lost";
            }
            catch
            {
                return "player-state-error";
            }

            int health = ReadPlayerHealth(_player);
            if (_lastHealth >= 0 && health >= 0 && health < _lastHealth)
                return "damaged";
            if (health >= 0)
                _lastHealth = health;

            if (_playerRootAnchor != null)
            {
                Vector3 offset = _player.transform.position - _playerRootAnchor.position;
                offset.y = 0f;
                if (offset.sqrMagnitude > TeleportFailsafeDistance * TeleportFailsafeDistance)
                    return "distance-failsafe";
            }

            return null;
        }

        private short ResolveAnchorYaw()
        {
            float yaw = _playerRootAnchor != null
                ? _playerRootAnchor.eulerAngles.y
                : (_root != null ? _root.eulerAngles.y : 0f);
            return (short)Mathf.RoundToInt(Mathf.Repeat(yaw, 360f));
        }

        private static bool IsEscapePressed()
        {
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            PlayerControllerB localPlayer = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            return player != null && localPlayer != null && ReferenceEquals(player, localPlayer);
        }

        private static void MarkActiveThisFrame()
        {
            _lastActiveFrame = Time.frameCount;
        }

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        private static int ReadPlayerHealth(PlayerControllerB player)
        {
            if (player == null)
                return -1;

            try
            {
                Type type = player.GetType();
                FieldInfo field = type.GetField("health", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    object value = field.GetValue(player);
                    if (value is int i)
                        return i;
                    if (value is short s)
                        return s;
                    if (value is byte b)
                        return b;
                }

                PropertyInfo property = type.GetProperty("health", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                {
                    object value = property.GetValue(player, null);
                    if (value is int i)
                        return i;
                    if (value is short s)
                        return s;
                    if (value is byte b)
                        return b;
                }
            }
            catch
            {
            }

            return -1;
        }

        private static void ResolveVanillaTerminalAnimation(out string enterTrigger, out string stopTrigger)
        {
            enterTrigger = null;
            stopTrigger = DefaultStopTrigger;

            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                InteractTrigger terminalTrigger = terminal != null ? terminal.GetComponent<InteractTrigger>() : null;
                if (terminalTrigger == null)
                    return;

                if (!string.IsNullOrWhiteSpace(terminalTrigger.animationString))
                    enterTrigger = terminalTrigger.animationString;
                if (!string.IsNullOrWhiteSpace(terminalTrigger.stopAnimationString))
                    stopTrigger = terminalTrigger.stopAnimationString;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Vanilla terminal animation probe failed: {ex.Message}");
            }
        }

        private static void TryTriggerPlayerAnimator(PlayerControllerB player, string trigger, string phase)
        {
            if (player == null || player.playerBodyAnimator == null || string.IsNullOrWhiteSpace(trigger))
                return;

            try
            {
                if (!AnimatorHasTrigger(player.playerBodyAnimator, trigger))
                    return;

                player.playerBodyAnimator.ResetTrigger(trigger);
                player.playerBodyAnimator.SetTrigger(trigger);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Animator {phase} trigger '{trigger}' failed: {ex.Message}");
            }
        }

        private static bool AnimatorHasTrigger(Animator animator, string trigger)
        {
            if (animator == null || string.IsNullOrWhiteSpace(trigger))
                return false;

            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].type == AnimatorControllerParameterType.Trigger &&
                        string.Equals(parameters[i].name, trigger, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static Transform FindDeepChild(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }
    }
}
