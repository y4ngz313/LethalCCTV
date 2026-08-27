using System;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZCompany.Bootstrap;

using Y4NGZCompany.Facility.Mainframe;
namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed class MainframeInteractionController : MonoBehaviour
    {
        private const float InteractRange = 4.0f;
        private const float StandRange = 2.45f;
        private const float AimDot = 0.76f;
        private const string HackPrompt = "[E] Hack Mainframe";
        private const string UsePrompt = "[E] Use Mainframe";
        private const string LockoutPrompt = "MAINFRAME LOCKOUT";

        private MainframeSupport _mainframe;
        private MainframeScreenController _screen;
        private Transform _screenCenter;
        private Transform _playerRootAnchor;
        private InputAction _interactAction;
        private bool _interactActionResolved;
        private bool _tipActive;
        private bool _cursorActive;
        private bool _canUseThisFrame;
        private string _capturedTipText;
        private string _activePrompt;
        private PlayerControllerB _lastPlayer;
        private MainframeInteractionSession _session;
        private bool _missingAnchorLogged;

        private void OnEnable()
        {
            ResolveInteractAction();
            EnsureInitialized();
        }

        private void Update()
        {
            if (_session != null && _session.IsActive)
            {
                ClearPromptState();
                _session.Tick();
                _canUseThisFrame = false;
                return;
            }

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null)
            {
                ClearPromptState();
                return;
            }

            _lastPlayer = player;
            bool canUse = EnsureInitialized() && IsLookingAtMainframe(player);
            _canUseThisFrame = canUse;

            if (canUse)
            {
                string prompt = CurrentPrompt();
                ShowHoverTip(prompt);
                if (IsInteractPressed())
                {
                    HideHoverTip();
                    HideHoverCursor(player);
                    Activate(player);
                    _canUseThisFrame = false;
                }
            }
            else
            {
                ClearPromptState();
            }
        }

        private void LateUpdate()
        {
            if (_session != null && _session.IsActive)
            {
                _session.LateTick();
                _canUseThisFrame = false;
                return;
            }

            if (_canUseThisFrame && _lastPlayer != null)
                ShowHoverCursor(_lastPlayer, CurrentPrompt());
            _canUseThisFrame = false;
        }

        private bool EnsureInitialized()
        {
            if (_mainframe == null)
                _mainframe = GetComponent<MainframeSupport>() ?? GetComponentInParent<MainframeSupport>();
            if (_screen == null)
                _screen = GetComponent<MainframeScreenController>() ?? GetComponentInChildren<MainframeScreenController>(true);
            if (_screenCenter == null)
                _screenCenter = FindDeepChild(transform, "MainframeScreenCenter");
            if (_playerRootAnchor == null)
                _playerRootAnchor = FindDeepChild(transform, "MainframePlayerRootAnchor");

            if (_session == null && _mainframe != null && _screen != null)
                _session = new MainframeInteractionSession(this, transform, _mainframe, _screen);

            bool ready = _mainframe != null && _screen != null && _screenCenter != null && _playerRootAnchor != null;
            if (!ready && !_missingAnchorLogged)
            {
                _missingAnchorLogged = true;
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][MainframeInteract] Missing MainframeSupport, MainframeScreenController, MainframeScreenCenter, or MainframePlayerRootAnchor; physical mainframe interaction disabled.");
            }

            return ready;
        }

        private bool IsLookingAtMainframe(PlayerControllerB player)
        {
            Camera camera = player != null ? player.gameplayCamera : null;
            if (camera == null || _screenCenter == null || _playerRootAnchor == null)
                return false;

            if (!IsPlayerOnInteractionSide(player))
                return false;

            Vector3 toStand = player.transform.position - _playerRootAnchor.position;
            toStand.y = 0f;
            if (toStand.sqrMagnitude > StandRange * StandRange)
                return false;

            Vector3 toScreen = _screenCenter.position - camera.transform.position;
            float distance = toScreen.magnitude;
            if (distance <= 0.05f || distance > InteractRange)
                return false;

            return Vector3.Dot(camera.transform.forward, toScreen / distance) >= AimDot;
        }

        private bool IsPlayerOnInteractionSide(PlayerControllerB player)
        {
            if (player == null)
                return false;

            // FixtureFootprint.Mainframe front is root-local -Z; the double-sided canvas made back-side interaction possible.
            Vector3 toPlayer = player.transform.position - transform.position;
            toPlayer.y = 0f;
            Vector3 front = -transform.forward;
            front.y = 0f;
            if (front.sqrMagnitude <= 0.0001f)
                return true;

            return Vector3.Dot(toPlayer, front.normalized) >= -0.15f;
        }

        private string CurrentPrompt()
        {
            if (_mainframe != null && _mainframe.IsLockedOut)
                return LockoutPrompt;
            return _mainframe != null && _mainframe.IsHacked ? UsePrompt : HackPrompt;
        }

        private void Activate(PlayerControllerB player)
        {
            if (_mainframe == null || _screen == null)
                return;

            if (!IsPlayerOnInteractionSide(player))
                return;

            LogInteractionDiagnosticBurst(player, "session-start");
            if (_mainframe.IsLockedOut)
            {
                HUDManager.Instance?.DisplayTip("MAINFRAME", $"Trace lockout {_mainframe.LockoutRemaining:0}s.", isWarning: true);
                MainframeAudio.Current?.PlayLockedOut();
                return;
            }

            if (_session != null && _session.IsActive)
                return;

            if (!EnsureInitialized() || _session == null)
                return;

            if (!_session.Begin(player))
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][MainframeInteract] Mainframe interaction session refused to start.");
        }

        private void ResolveInteractAction()
        {
            if (_interactActionResolved)
                return;

            _interactActionResolved = true;
            try
            {
                _interactAction = InputSystem.actions != null
                    ? InputSystem.actions.FindAction("Interact")
                    : null;
            }
            catch
            {
                _interactAction = null;
            }

            if (_interactAction == null)
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][MainframeInteract] Vanilla 'Interact' InputAction not resolvable; falling back to Keyboard.current.eKey.");
        }

        private bool IsInteractPressed()
        {
            bool actionPressed = false;
            if (_interactAction != null)
            {
                try
                {
                    actionPressed = _interactAction.WasPressedThisFrame();
                }
                catch
                {
                    actionPressed = false;
                }
            }

            return actionPressed || (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame);
        }

        private void StopActiveSession(string reason)
        {
            if (_session != null && _session.IsActive)
                _session.Stop(reason, returnScreenToIdle: true);
        }

        private short ResolveMainframeYaw()
        {
            float yaw = _playerRootAnchor != null
                ? _playerRootAnchor.eulerAngles.y
                : transform.eulerAngles.y;
            return (short)Mathf.RoundToInt(Mathf.Repeat(yaw, 360f));
        }

        private void LogInteractionDiagnosticBurst(PlayerControllerB player, string phase)
        {
            Camera camera = player != null ? player.gameplayCamera : null;
            Transform cameraTransform = camera != null ? camera.transform : null;
            Vector3 toStand = Vector3.zero;
            Vector3 toScreen = Vector3.zero;
            float standDistance = -1f;
            float screenDistance = -1f;
            float aimDot = -1f;

            if (player != null && _playerRootAnchor != null)
            {
                toStand = player.transform.position - _playerRootAnchor.position;
                toStand.y = 0f;
                standDistance = toStand.magnitude;
            }

            if (cameraTransform != null && _screenCenter != null)
            {
                toScreen = _screenCenter.position - cameraTransform.position;
                screenDistance = toScreen.magnitude;
                if (screenDistance > 0.05f)
                    aimDot = Vector3.Dot(cameraTransform.forward, toScreen / screenDistance);
            }

            short resolvedYaw = ResolveMainframeYaw();
            SurveillanceBootstrap.Log?.LogInfo(
                $"[MainframeDiag.Interact] phase={phase} root='{GetHierarchyPath(transform)}' prompt='{CurrentPrompt()}' " +
                $"hacked={(_mainframe != null && _mainframe.IsHacked)} lockedOut={(_mainframe != null && _mainframe.IsLockedOut)} " +
                $"resolvedYaw={resolvedYaw} rootYaw={transform.eulerAngles.y:0.0} anchorYaw={(_playerRootAnchor != null ? _playerRootAnchor.eulerAngles.y : -1f):0.0} " +
                $"playerYaw={(player != null ? player.transform.eulerAngles.y : -1f):0.0} cameraYaw={(cameraTransform != null ? cameraTransform.eulerAngles.y : -1f):0.0} " +
                $"standDistance={standDistance:0.00} screenDistance={screenDistance:0.00} aimDot={aimDot:0.000} canUseThisFrame={_canUseThisFrame}.");
            SurveillanceBootstrap.Log?.LogInfo(
                $"[MainframeDiag.Interact] anchors phase={phase} screen={FormatTransform(_screenCenter)} playerRoot={FormatTransform(_playerRootAnchor)} " +
                $"camera={FormatTransform(cameraTransform)} player={FormatTransform(player != null ? player.transform : null)}.");
            SurveillanceBootstrap.Log?.LogInfo(
                $"[MainframeDiag.Interact] vectors phase={phase} rootForward={FormatVector(transform.forward)} anchorForward={FormatVector(_playerRootAnchor != null ? _playerRootAnchor.forward : Vector3.zero)} " +
                $"cameraForward={FormatVector(cameraTransform != null ? cameraTransform.forward : Vector3.zero)} toStand={FormatVector(toStand)} toScreen={FormatVector(toScreen)}.");
        }

        private static string FormatTransform(Transform target)
        {
            if (target == null)
                return "<null>";

            return $"'{GetHierarchyPath(target)}' pos={FormatVector(target.position)} rot={FormatVector(target.eulerAngles)} forward={FormatVector(target.forward)}";
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"({vector.x:0.###},{vector.y:0.###},{vector.z:0.###})";
        }

        private void ShowHoverTip(string prompt)
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null || hud.controlTipLines == null || hud.controlTipLines.Length == 0 || hud.controlTipLines[0] == null)
                return;

            if (!_tipActive)
            {
                _capturedTipText = hud.controlTipLines[0].text;
                _tipActive = true;
            }

            hud.controlTipLines[0].text = prompt;
            _activePrompt = prompt;
        }

        private void HideHoverTip()
        {
            HUDManager hud = HUDManager.Instance;
            if (hud != null && hud.controlTipLines != null && hud.controlTipLines.Length > 0 && hud.controlTipLines[0] != null)
                hud.controlTipLines[0].text = _capturedTipText ?? string.Empty;

            _capturedTipText = null;
            _activePrompt = null;
            _tipActive = false;
        }

        private void ShowHoverCursor(PlayerControllerB player, string prompt)
        {
            if (player == null)
                return;

            if (player.cursorIcon != null && player.grabItemIcon != null)
            {
                player.cursorIcon.sprite = player.grabItemIcon;
                player.cursorIcon.enabled = true;
            }
            if (player.cursorTip != null)
                player.cursorTip.text = prompt;
            _cursorActive = true;
            _activePrompt = prompt;
        }

        private void HideHoverCursor(PlayerControllerB player)
        {
            if (!_cursorActive)
                return;

            if (player != null)
            {
                if (player.cursorTip != null && string.Equals(player.cursorTip.text, _activePrompt, StringComparison.Ordinal))
                    player.cursorTip.text = string.Empty;
                if (player.cursorIcon != null && player.cursorIcon.sprite == player.grabItemIcon)
                    player.cursorIcon.enabled = false;
            }

            _cursorActive = false;
        }

        private void ClearPromptState()
        {
            if (_tipActive)
                HideHoverTip();
            if (_cursorActive)
                HideHoverCursor(_lastPlayer);
        }

        private void OnDisable()
        {
            ClearPromptState();
            StopActiveSession("controller-disabled");
        }

        private void OnDestroy()
        {
            ClearPromptState();
            StopActiveSession("controller-destroyed");
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

        private static string GetHierarchyPath(Transform target)
        {
            if (target == null)
                return "<null>";

            var names = new System.Collections.Generic.Stack<string>();
            Transform current = target;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names.ToArray());
        }
    }
}
