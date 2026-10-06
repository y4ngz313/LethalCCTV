using System;
using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZCompany.Facility.Cameras;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>A local keyboard-owned command line. Enter always dismisses; the
    /// selected device and feed stay latched until validation/submission.</summary>
    internal static class CctvDeviceCommandLine
    {
        private static TerminalAccessibleObject _target;
        private static CCTVCamera _camera, _pendingCamera;
        private static string _code, _label, _text = "", _pending;
        private static Keyboard _keyboard;
        private static RectTransform _root;
        private static TextMeshProUGUI _readout;
        private static float _statusUntil, _nextHoverAt;
        private static int _closedFrame = -1;
        internal static bool IsOpen => _keyboard != null;
        internal static bool ConsumesInput => IsOpen || _closedFrame == Time.frameCount;

        internal static bool TryOpen(CCTVCamera camera)
        {
            if (IsOpen) return true;
            if (camera?.Cam == null || camera.IsSecurityBroken || !MonitorFocus.IsStationCameraControlActive) return false;
            TerminalAccessibleObject target = CctvDeviceCommands.Hover(camera.Cam);
            if (!CctvDeviceCommands.TryDescribe(target, out string label)) return false;
            if (!CCTVShipSystemsBridge.CanUseRemoteOperations(out string reason)) { ShowStatus(reason); return true; }
            if (Keyboard.current == null) { ShowStatus("KEYBOARD UNAVAILABLE"); return true; }
            EnsureUi();
            if (_root == null) return true;
            _target = target;
            _camera = camera;
            _code = target.objectCode;
            _label = label;
            _text = "";
            _pending = null;
            _keyboard = Keyboard.current;
            _keyboard.onTextInput += OnText;
            CCTVWalkieTalkieBridge.SetLocalSpeaking(false);
            UpdateReadout();
            CCTVStationEvents.RaiseActionButtonPressed("device-command-open");
            return true;
        }

        private static void OnText(char character)
        {
            if (IsOpen && !char.IsControl(character) && _text.Length < 24)
            {
                _text += character;
                UpdateReadout();
            }
        }

        internal static void Tick()
        {
            PlayerControllerB player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (!MonitorFocus.IsFocused || player == null || player.isPlayerDead ||
                !MonitorFocus.IsFacilityFeedActive || !CCTVShipSystemsBridge.IsShipPowerOnline())
            { Close(true); return; }
            CCTVCamera current = QuadCameraAssignment.GetBoundCamera(MonitorFocus.ActiveSlot);
            if (current?.Cam == null || current.IsSecurityBroken) { Close(true); return; }
            if (_pending != null && current != _pendingCamera) { Close(true); return; }
            if (IsOpen)
            {
                bool needsReadout = _root == null;
                EnsureUi();
                if (_target == null || current != _camera || _target.objectCode != _code) { Close(true); return; }
                if (needsReadout) UpdateReadout();
                if (_keyboard.escapeKey.wasPressedThisFrame) { Close(); return; }
                if (_keyboard.enterKey.wasPressedThisFrame || _keyboard.numpadEnterKey.wasPressedThisFrame) { Submit(); return; }
                if (_keyboard.backspaceKey.wasPressedThisFrame && _text.Length > 0)
                { _text = _text.Substring(0, _text.Length - 1); UpdateReadout(); }
                return;
            }
            if (_pending != null && Time.unscaledTime >= _statusUntil)
            { _pending = null; ShowStatus("NO RESPONSE FROM HOST"); }
            if (Time.unscaledTime < _statusUntil) return;
            if (Time.unscaledTime < _nextHoverAt) return;
            _nextHoverAt = Time.unscaledTime + 0.1f;
            TerminalAccessibleObject hover = CctvDeviceCommands.Hover(current.Cam);
            if (CctvDeviceCommands.TryDescribe(hover, out string label))
            {
                EnsureUi();
                SetText(label + "   /   LMB COMMAND");
            }
            else if (_root != null) _root.gameObject.SetActive(false);
        }

        private static void Submit()
        {
            TerminalAccessibleObject target = _target;
            CCTVCamera camera = _camera;
            string command = _text;
            bool valid = target != null && target.objectCode == _code && camera?.Cam != null &&
                camera == QuadCameraAssignment.GetBoundCamera(MonitorFocus.ActiveSlot) &&
                !camera.IsSecurityBroken && CctvDeviceCommands.Hover(camera.Cam) == target;
            Close(); // Dismiss on every Enter, including invalid text / lost targets.
            if (!valid) { ShowStatus("TARGET LOST"); return; }
            string normalized = command.Trim().ToLowerInvariant();
            if (target.isBigDoor ? normalized != "unlock" && normalized != "open" : normalized != "disable")
            { ShowStatus(target.isBigDoor ? "USE UNLOCK" : "USE DISABLE"); return; }
            try
            {
                if (CctvDeviceCommands.Submit(target, camera, normalized) == null) ShowStatus("HOST COMMAND CHANNEL UNAVAILABLE");
            }
            catch { _pending = null; ShowStatus("COMMAND COULD NOT BE SENT"); }
        }

        internal static void SetPending(string requestId, CCTVCamera camera)
        {
            _pending = requestId;
            _pendingCamera = camera;
            ShowStatus("COMMAND PENDING", 4f);
        }

        internal static void ReceiveResult(string requestId, string status)
        {
            if (_pending != requestId || !MonitorFocus.IsFocused) return;
            _pending = null;
            ShowStatus(status);
        }

        internal static void Close(bool clearStatus = false)
        {
            if (_keyboard != null)
            {
                _keyboard.onTextInput -= OnText;
                _keyboard = null;
                _closedFrame = Time.frameCount;
            }
            _target = null;
            _camera = null;
            _text = "";
            if (clearStatus) { _pending = null; _pendingCamera = null; _statusUntil = 0f; }
            if (_root != null && _root.gameObject.activeSelf)
            {
                _root.gameObject.SetActive(false);
                CCTVVanillaMonitorDisplay.InvalidateMachineVisionUi();
            }
        }

        internal static void Shutdown()
        {
            Close(true);
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            _root = null;
            _readout = null;
        }

        private static void ShowStatus(string status, float seconds = 2f)
        {
            _statusUntil = Time.unscaledTime + seconds;
            EnsureUi();
            SetText(status);
        }

        private static void UpdateReadout()
        {
            string hint = _target != null && _target.isBigDoor ? "unlock" : "disable";
            SetText(_label + "  >  " + _text + "_\n" + hint + "   /   ENTER SEND   /   ESC CANCEL");
        }

        private static void SetText(string text)
        {
            if (_root == null || _readout == null) return;
            _root.gameObject.SetActive(true);
            _readout.text = text ?? "";
            CCTVVanillaMonitorDisplay.InvalidateMachineVisionUi();
        }

        private static void EnsureUi()
        {
            if (_root != null) return;
            _root = new GameObject("CCTV_DeviceCommand", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            if (!CCTVVanillaMonitorDisplay.TryAttachToLowerLeftOverlay(_root.gameObject))
            { UnityEngine.Object.Destroy(_root.gameObject); _root = null; return; }
            _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0f);
            _root.pivot = new Vector2(0.5f, 0f);
            _root.anchoredPosition = new Vector2(0f, 48f);
            _root.sizeDelta = new Vector2(510f, 48f);
            Image backing = _root.GetComponent<Image>();
            backing.color = new Color(0.015f, 0.035f, 0.045f, 0.90f);
            backing.raycastTarget = false;
            _readout = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            _readout.transform.SetParent(_root, false);
            _readout.gameObject.layer = _root.gameObject.layer;
            _readout.rectTransform.anchorMin = Vector2.zero;
            _readout.rectTransform.anchorMax = Vector2.one;
            _readout.rectTransform.offsetMin = new Vector2(12f, 4f);
            _readout.rectTransform.offsetMax = new Vector2(-12f, -4f);
            _readout.fontSize = 13f;
            _readout.richText = false;
            _readout.color = new Color(0.7f, 0.94f, 0.96f);
            _readout.alignment = TextAlignmentOptions.MidlineLeft;
            _readout.raycastTarget = false;
        }
    }
}
