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
        private void TickWindingDown()
        {
            if (_animator == null || Player == null)
            {
                EndImmediate("wind-down-invalid");
                return;
            }

            if (Player.isPlayerDead || !Player.isPlayerControlled)
            {
                EndImmediate("wind-down-player-invalid");
                return;
            }

            float now = Time.unscaledTime;
            if (now >= _restoreAt)
            {
                EndImmediate("wind-down-complete");
                return;
            }

            _cameraControlActive = false;
            _radarLookActive = false;
            _joystickPhase.SetRequested(Vector2.zero);
            TickJoystickPose(controllingCamera: false);
            SetBool(CameraControlHash, false);
            SetBool(RadarLookHash, false);

            if (!_exitTriggered &&
                (now >= _exitReleaseAt || _joystickSmoothed.sqrMagnitude <= 0.0025f))
            {
                _joystickPhase.SetImmediate(Vector2.zero);
                _joystickSmoothed = Vector2.zero;
                SetFloat(JoystickXHash, 0f);
                SetFloat(JoystickYHash, 0f);
                SetBool(SeatedHash, false);
                TriggerExitPose();
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Operator anim exit release triggered after joystick neutralized.");
            }

            ApplyOperatorAnimatorOverrides();
        }

        private void TickJoystickPose(bool controllingCamera)
        {
            _joystickSmoothed = _joystickPhase.Tick(Mathf.Max(Time.unscaledDeltaTime, 1f / 240f));
            SetFloat(JoystickXHash, _joystickSmoothed.x);
            SetFloat(JoystickYHash, _joystickSmoothed.y);
            ApplyOperatorAnimatorOverrides();
            if (_runtimePose != null)
                _runtimePose.Tick(_joystickSmoothed, controllingCamera);
        }

        internal void SelectCamera(int slot)
        {
            if (_interactionsApiMode)
            {
                CCTVOperatorInteractionsBridge.TrySetInt(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.ActiveSlotInt,
                    Mathf.Clamp(slot, 0, 3));
                CCTVOperatorInteractionsBridge.TrySetInt(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.ActionButtonInt,
                    1);
                CCTVOperatorInteractionsBridge.TryFireTrigger(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.SelectCameraTrigger);
                CCTVOperatorInteractionsBridge.TryFireTrigger(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.ButtonPressTrigger);
                return;
            }

            SetInt(ActiveSlotHash, Mathf.Clamp(slot, 0, 3));
            SetInt(ActionButtonHash, 1);
            _buttonPressActionId = 1;
            FireTrigger(SelectCameraHash);
            FireTrigger(ButtonPressHash);
            _buttonPressLayerUntil = Time.unscaledTime + ButtonPressLayerSeconds;
            ApplyOperatorAnimatorOverrides();
            _runtimePose?.PulseButtonPress();
        }

        /// <summary>Slot sync without the button-press flourish (remote enter snapshot).</summary>
        internal void SetActiveSlotQuiet(int slot)
        {
            if (_interactionsApiMode)
            {
                CCTVOperatorInteractionsBridge.TrySetInt(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.ActiveSlotInt,
                    Mathf.Clamp(slot, 0, 3));
                return;
            }

            SetInt(ActiveSlotHash, Mathf.Clamp(slot, 0, 3));
        }

        internal void SetCameraControl(bool controllingCamera)
        {
            _cameraControlActive = controllingCamera;
            if (_interactionsApiMode)
            {
                CCTVOperatorInteractionsBridge.TrySetBool(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.CameraControlBool,
                    controllingCamera);
                CCTVOperatorInteractionsBridge.TryFireTrigger(
                    _interactionsApiHandle,
                    controllingCamera
                        ? Y4NGZPlayerAnimationBridge.JoystickGrabTrigger
                        : Y4NGZPlayerAnimationBridge.JoystickReleaseTrigger);
                if (!controllingCamera)
                    _joystickPhase.SetRequested(Vector2.zero);
                return;
            }

            SetBool(CameraControlHash, controllingCamera);
            FireTrigger(controllingCamera ? JoystickGrabHash : JoystickReleaseHash);
            if (!controllingCamera)
                _joystickPhase.SetRequested(Vector2.zero);
            _runtimePose?.SetCameraControl(controllingCamera);
        }

        internal void SetRadarLook(bool lookingAtRadar)
        {
            _radarLookActive = lookingAtRadar;
            if (_interactionsApiMode)
            {
                CCTVOperatorInteractionsBridge.TrySetBool(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.RadarLookBool,
                    lookingAtRadar);
                return;
            }

            SetBool(RadarLookHash, lookingAtRadar);
            ApplyOperatorAnimatorOverrides();
        }

        internal void OnJoystickMoved(Vector2 appliedDeltaDeg)
        {
            _lastJoystickMoveAt = Time.unscaledTime;
            _joystickPhase.SetRequested(appliedDeltaDeg);
        }

        /// <summary>Remote feed: absolute -1..1 deflection from the network, no delta scaling.</summary>
        internal void SetJoystickTargetAbsolute(Vector2 deflection, bool immediate = false)
        {
            _lastJoystickMoveAt = Time.unscaledTime;
            Vector2 direction = CCTVJoystickPhaseDriver.QuantizeCardinal(new Vector2(
                Mathf.Clamp(deflection.x, -1f, 1f),
                Mathf.Clamp(deflection.y, -1f, 1f)));
            if (immediate)
                _joystickPhase.SetImmediate(direction);
            else
                _joystickPhase.SetRequested(direction);
        }

        internal void PressButton(int buttonId, bool isStandUp)
        {
            if (_interactionsApiMode)
            {
                if (isStandUp)
                {
                    BeginGracefulEnd("stand-up");
                    return;
                }

                CCTVOperatorInteractionsBridge.TrySetInt(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.ActionButtonInt,
                    buttonId);
                CCTVOperatorInteractionsBridge.TryFireTrigger(
                    _interactionsApiHandle,
                    Y4NGZPlayerAnimationBridge.ButtonPressTrigger);
                return;
            }

            SetInt(ActionButtonHash, buttonId);
            if (isStandUp)
            {
                BeginGracefulEnd("stand-up");
                return;
            }
            _buttonPressActionId = buttonId;
            FireTrigger(ButtonPressHash);
            _buttonPressLayerUntil = Time.unscaledTime + ButtonPressLayerSeconds;
            ApplyOperatorAnimatorOverrides();
            _runtimePose?.PulseButtonPress();
        }

        internal void PreviewFirstPersonRightHandTune(int actionButton)
        {
            if (!_isLocal || !_active || _windingDown || _animator == null || !_controllerApplied)
                return;
            if (!ShouldShowFirstPersonArmsLayer())
                return;
            if (_buttonPressLayer < 0 || _buttonPressLayer >= _animator.layerCount)
                return;

            _buttonPressActionId = actionButton;
            SetInt(ActionButtonHash, actionButton);
            _buttonPressLayerUntil = Time.unscaledTime + 1.0f;
            ApplyOperatorAnimatorOverrides();
            int stateHash = Y4NGZPlayerAnimationBridge.IsBlueRightHandAction(actionButton)
                ? PressBlueCameraSwitchStateHash
                : PressGreenActionStateHash;
            try
            {
                // Edit mode is a static preview, not the real tap animation. Pin the
                // button layer at the contact point so live transforms are readable.
                _animator.Play(stateHash, _buttonPressLayer, 0.40f);
                if (_rightHandEditPreviewActionId != actionButton)
                {
                    _rightHandEditPreviewActionId = actionButton;
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] Right-hand edit preview holding {(Y4NGZPlayerAnimationBridge.IsBlueRightHandAction(actionButton) ? "blue" : "green")} press pose.");
                }
                // Manual IK frames let the engine evaluate this pinned state on
                // the next natural animator pass. For legacy A/B mode only,
                // preserve the old immediate preview evaluation; the centralized
                // LateUpdate arm pass still runs after it.
                if (!Y4NGZPlayerAnimationBridge.UseManualFirstPersonArmIk)
                    _animator.Update(0f);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Right-hand edit preview hold failed: {ex.Message}");
            }
        }

    }
}
