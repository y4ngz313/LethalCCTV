using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed partial class OperatorAnimSession
    {
        private readonly Dictionary<Transform, Quaternion> _operatorFingers = new Dictionary<Transform, Quaternion>();
        private float _operatorPoseStartedAt;
        private int _operatorPoseFrame = -1;
        private bool _operatorLastPoseValid;
        private Vector3 _operatorLastLeft;
        private Quaternion _operatorLastLeftRotation;
        private readonly List<ApiPoseTransformSnapshot> _remoteOperatorTargets = new List<ApiPoseTransformSnapshot>();

        private void InitializeOperatorPose()
        {
            _operatorPoseStartedAt = Time.unscaledTime;
            _operatorPoseFrame = -1;
            _operatorLastPoseValid = false;
            _apiRightLastValid = false;
            _apiLastPressWindow = -1f;
            _apiLeverPalmOffsetResolved = false;
            _handDriveEnterActive = false;
            _operatorFingers.Clear();
            if (_rightArmPresentationScaleCaptured && _rightArmPresentationShoulder != null)
                _rightArmPresentationShoulder.localScale = _rightArmPresentationCachedLocalScale;
            _remoteOperatorTargets.Clear();
            if (!_isLocal && _animator != null)
            {
                CaptureApiPoseTransform(_remoteOperatorTargets, "LeftArm_target",
                    FindChildRecursive(_animator.transform, "LeftArm_target"));
                CaptureApiPoseTransform(_remoteOperatorTargets, "RightArm_target",
                    FindChildRecursive(_animator.transform, "RightArm_target"));
            }
            // Keep the shipped idle body/finger pose; one procedural path owns
            // access-button contact, the transfer to the lever, and steering.
            if (_animator != null)
            {
                _animator.ResetTrigger(EnterHash);
                StartDirectOperatorIdlePose();
            }
            Transform arms = Player != null ? Player.localArmsTransform : null;
            if (arms == null) return;
            // Also reacquire after a same-chair controller/editor refresh; that
            // replacement ends the old session without another ChairEntered event.
            CCTVOperatorStation.BeginJoystickSession(Player);
            if (!_isLocal)
            {
                CaptureRemoteOperatorChains();
            }
            Transform fingerRoot = _isLocal ? arms : _animator?.transform;
            if (fingerRoot == null) return;
            foreach (Transform bone in fingerRoot.GetComponentsInChildren<Transform>(true))
                if (bone.name.StartsWith("finger") && (_isLocal || !bone.IsChildOf(arms)))
                    _operatorFingers[bone] = bone.localRotation;
        }

        private void RestoreOperatorPoseTargets()
        {
            foreach (ApiPoseTransformSnapshot saved in _remoteOperatorTargets)
            {
                if (saved.Transform == null) continue;
                saved.Transform.localPosition = saved.LocalPosition;
                saved.Transform.localRotation = saved.LocalRotation;
            }
            _remoteOperatorTargets.Clear();
            _operatorFingers.Clear();
            _operatorPoseFrame = -1;
        }

        private void CaptureRemoteOperatorChains()
        {
            if (_animator == null) return;
            _manualLeftArmIk = CaptureRemoteChain("L", "LeftArm_target");
            _manualRightArmIk = CaptureRemoteChain("R", "RightArm_target");
        }

        private ManualArmIkChain CaptureRemoteChain(string side, string targetName)
        {
            Transform tip = null;
            foreach (Transform bone in _animator.GetComponentsInChildren<Transform>(true))
                if (bone.name == "hand." + side && !HasAncestorOrSelfNamed(bone, "ScavengerModelArmsOnly"))
                { tip = bone; break; }
            Transform target = FindChildRecursive(_animator.transform, targetName);
            if (tip == null || target == null) return null;
            string path = tip.name;
            for (Transform parent = tip.parent; parent != null && parent != _animator.transform; parent = parent.parent)
                path = parent.name + "/" + path;
            return CaptureManualArmIkChain(side, path, target, "remote-operator");
        }

        internal void TickRemoteOperatorPose()
        {
            if (_isLocal || (!_controllerApplied && !_interactionsApiMode) || (!_active && !_windingDown)) return;
            if (!IsManualArmIkChainAvailable(_manualLeftArmIk) || !IsManualArmIkChainAvailable(_manualRightArmIk)) return;
            ApplyOperatorPose();
            SolveManualArmIk(_manualLeftArmIk);
            SolveManualArmIk(_manualRightArmIk);
        }

        private void ApplyOperatorPose()
        {
            if ((!_controllerApplied && !_interactionsApiMode) ||
                (!_active && !_windingDown) || _operatorPoseFrame == Time.frameCount) return;
            Transform arms = Player != null ? Player.localArmsTransform : null;
            Transform station = CCTVOperatorStation.OperatorPoseAnchor?.parent;
            if (arms == null || station == null) return;
            // Lift the presentation hide before both chain capture and every solve.
            if (_rightArmPresentationScaleCaptured && _rightArmPresentationShoulder != null)
                _rightArmPresentationShoulder.localScale = _rightArmPresentationCachedLocalScale;
            if (_isLocal && !EnsureInteractionsApiManualArmIkChains("operator-pose")) return;
            if (_rightArmPresentationScaleCaptured && _rightArmPresentationShoulder != null)
                _rightArmPresentationShoulder.localScale = _rightArmPresentationCachedLocalScale;
            _operatorPoseFrame = Time.frameCount;

            float seconds = _windingDown ? MonitorFocus.OperatorEnterDurationSeconds : !_isLocal
                ? Time.unscaledTime - _operatorPoseStartedAt : MonitorFocus.IsEnterCameraPathComplete
                ? MonitorFocus.OperatorEnterDurationSeconds : Mathf.Max(0f, MonitorFocus.EnterPresentationClockSeconds);
            // Apply the very same smoothed input immediately before measuring
            // contact. Station upkeep/vanilla animation must not leave the local
            // hand reading a different lever phase from the observer hand.
            CCTVOperatorStation.ApplySessionJoystick(Player, _joystickSmoothed);
            // Only the local arms-only root gets the first-person shoulder frame.
            // Remote shoulders and the networked player root remain animator-owned.
            if (_isLocal)
            {
                Vector3 shoulderLine = station.TransformDirection(new Vector3(-0.342f, 0f, 0.940f));
                Vector3 currentLine = _manualLeftArmIk.Root.position - _manualRightArmIk.Root.position;
                arms.rotation = Quaternion.FromToRotation(currentLine.normalized, shoulderLine.normalized) * arms.rotation;
                Vector3 shoulder = new Vector3(-0.12f, 1.14f, 0.56f) +
                    new Vector3(-0.32f, -0.12f, 0f) * (1f - CctvControlGeometry.Ease(seconds / 1.05f));
                arms.position += station.TransformPoint(shoulder) -
                    (_manualLeftArmIk.Root.position + _manualRightArmIk.Root.position) * 0.5f;
            }

            float fingerClose = CctvControlGeometry.Ease((seconds - 0.74f) / 0.34f);
            foreach (var pair in _operatorFingers)
            {
                if (pair.Key == null) continue;
                pair.Key.localRotation = pair.Key.name.Contains(".L") && !pair.Key.name.StartsWith("finger1")
                    ? Quaternion.Slerp(Quaternion.identity, pair.Value, fingerClose) : pair.Value;
            }

            Transform left = _manualLeftArmIk.Target;
            Transform right = _manualRightArmIk.Target;
            Quaternion leftRotation = ResolveLeverContactRotation(station);
            if (!TryResolveLeverWristTarget(leftRotation, out Vector3 wrist, out Vector3 grip, out Vector3 palm)) return;
            float reach = CctvControlGeometry.Ease((seconds - CctvIntroTiming.PressRelease) /
                (CctvIntroTiming.GripArrival - CctvIntroTiming.PressRelease));
            Vector3 leftPosition = Vector3.Lerp(station.TransformPoint(new Vector3(-0.11f, 0.92f, 0.73f)), wrist, reach) +
                station.up * (0.025f * Mathf.Sin(reach * Mathf.PI));
            if (!_windingDown && seconds < CctvIntroTiming.GripArrival)
                ApplyEntryButtonContact(station, seconds, wrist, ref leftPosition, ref leftRotation);
            if (_windingDown && _operatorLastPoseValid)
            {
                leftPosition = station.TransformPoint(_operatorLastLeft);
                leftRotation = station.rotation * _operatorLastLeftRotation;
            }
            left.SetPositionAndRotation(leftPosition, leftRotation);
            Quaternion rightRotation = CctvControlGeometry.PalmDown(station.up, station.right) *
                Quaternion.Inverse(_manualRightArmIk.TargetToTipRotation);
            ApplyApiRightControlContact(station, right, station.TransformPoint(new Vector3(-0.10f, 0.70f, 0.18f)), rightRotation);
            if (!_windingDown)
            {
                _operatorLastLeft = station.InverseTransformPoint(leftPosition);
                _operatorLastLeftRotation = Quaternion.Inverse(station.rotation) * leftRotation;
                _operatorLastPoseValid = true;
                if (reach >= 1f) ArmApiLeverContactPostSolveSample(grip, wrist, palm);
            }
            else
            {
                Vector3 leftRest, rightRest;
                if (_isLocal)
                {
                    if (!TryResolveCurrentHandRestPoses(out leftRest, out _, out rightRest, out _)) return;
                }
                else
                {
                    // Remote full-body hands retract below their own shoulders;
                    // never use that player's inactive gameplay camera frame.
                    leftRest = _manualLeftArmIk.Root.position - station.up *
                        ((_manualLeftArmIk.UpperLength + _manualLeftArmIk.LowerLength) * 0.75f);
                    rightRest = _manualRightArmIk.Root.position - station.up *
                        ((_manualRightArmIk.UpperLength + _manualRightArmIk.LowerLength) * 0.75f);
                }
                float duration = Mathf.Max(0.05f, Y4NGZPlayerAnimationBridge.ApiExitRetractSeconds);
                float start = _interactionsApiMode ? _interactionsApiExitStartedAt : _exitTriggeredAt;
                float elapsed = start > 0f ? Time.unscaledTime - start : 0f;
                float blend = Smooth01(Mathf.Clamp01((elapsed -
                    (CCTVOperatorInteractionsBridge.ExitClipLengthSeconds - duration)) / duration));
                left.position = Vector3.Lerp(leftPosition, leftRest, blend);
                right.position = Vector3.Lerp(right.position, rightRest, blend);
            }
        }
    }
}
