using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed partial class OperatorAnimSession
    {
        private Transform _apiControlPanel;
        private Transform _apiRightIndexTip;
        private Transform _apiRightIndexBase;
        private Transform _apiRightIndexJoint;
        private float _apiLastPressWindow = -1f;
        private Vector3 _apiPressStart;
        private Quaternion _apiPressStartRotation;
        private Vector3 _apiRightLastPosition;
        private Quaternion _apiRightLastRotation;
        private bool _apiRightLastValid;
        private Transform _entryIndexTip;

        private void ApplyEntryButtonContact(Transform station, float seconds, Vector3 gripWrist,
            ref Vector3 position, ref Quaternion rotation)
        {
            if (_manualLeftArmIk?.Tip == null || !Y4NGZPlayerAnimationBridge.TryResolveAccessButtonPressSurface(
                    out Vector3 cap, out Vector3 normal)) return;
            Transform hand = _manualLeftArmIk.Tip;
            if (_entryIndexTip == null) _entryIndexTip = FindChildRecursive(hand, "finger2.L.001_end");
            Quaternion pressHand = CctvControlGeometry.PalmDown(normal, station.right);
            Quaternion pressRotation = pressHand * Quaternion.Inverse(_manualLeftArmIk.TargetToTipRotation);
            Vector3 tipOffset = _entryIndexTip != null
                ? Quaternion.Inverse(hand.rotation) * (_entryIndexTip.position - hand.position)
                : new Vector3(0.005f, 0.18f, 0f);
            Vector3 contact = cap + normal * 0.003f - pressHand * tipOffset;
            Vector3 rest = station.TransformPoint(new Vector3(-0.11f, 0.92f, 0.73f));
            position = CctvControlGeometry.EntryHandPosition(seconds, rest, contact, gripWrist, normal);
            float transfer = CctvControlGeometry.Ease((seconds - CctvIntroTiming.PressRelease - 0.12f) /
                (CctvIntroTiming.GripArrival - CctvIntroTiming.PressRelease - 0.12f));
            rotation = Quaternion.Slerp(pressRotation, rotation, transfer);
            if (_isLocal && seconds >= CctvIntroTiming.PressContact)
                MonitorFocus.CompleteStationFeedFlipAtPressContact();
        }

        private Quaternion ResolveLeverContactRotation(Transform station)
        {
            Vector3 normal = CCTVOperatorStation.LeverGripUp;
            Quaternion hand = CctvControlGeometry.PalmDown(normal, CCTVOperatorStation.LeverSteeringDelta * station.right);
            return _manualLeftArmIk != null ? hand * Quaternion.Inverse(_manualLeftArmIk.TargetToTipRotation) : hand;
        }

        private void ApplyApiRightControlContact(Transform station, Transform target,
            Vector3 rest, Quaternion restRotation)
        {
            if (_windingDown && _apiRightLastValid)
            {
                // Preserve the visible control pose until the existing position-
                // only exit retract takes over. The old authored rest would snap
                // the new palm-down wrist back to its former rotated pose.
                target.SetPositionAndRotation(station.TransformPoint(_apiRightLastPosition),
                    station.rotation * _apiRightLastRotation);
                return;
            }
            Vector3 position = rest;
            Quaternion rotation = restRotation;
            bool pressing = _active && !_windingDown && (_isLocal ? MonitorFocus.IsEnterCameraPathComplete : Time.unscaledTime - _operatorPoseStartedAt >= MonitorFocus.OperatorEnterDurationSeconds) && IsRightHandActionPressWindowActive();
            if (_apiControlPanel == null && station.parent != null)
                _apiControlPanel = station.parent.Find("ControlPanelWTexture");
            if (pressing && _apiControlPanel != null && _manualRightArmIk?.Tip != null)
            {
                if (_apiLastPressWindow != _buttonPressLayerUntil)
                {
                    _apiLastPressWindow = _buttonPressLayerUntil;
                    _apiPressStart = _apiRightLastValid ? station.TransformPoint(_apiRightLastPosition) : rest;
                    _apiPressStartRotation = _apiRightLastValid ? station.rotation * _apiRightLastRotation : restRotation;
                }
                Vector3 cap = _apiControlPanel.TransformPoint(CctvControlGeometry.RedButton);
                Vector3 normal = _apiControlPanel.localToWorldMatrix.inverse.transpose.MultiplyVector(CctvControlGeometry.ButtonNormal).normalized;
                if (Vector3.Dot(normal, Vector3.up) < 0) normal = -normal;
                Quaternion hand = CctvControlGeometry.PalmDown(normal, station.right);
                Quaternion pressRotation = hand * Quaternion.Inverse(_manualRightArmIk.TargetToTipRotation);
                float seconds = Time.unscaledTime - (_buttonPressLayerUntil - ButtonPressLayerSeconds);
                float weight = CctvControlGeometry.Ease(seconds / 0.26f);
                rotation = Quaternion.Slerp(_apiPressStartRotation, pressRotation, weight);
                Transform wrist = _manualRightArmIk.Tip;
                if (_apiRightIndexTip == null)
                    _apiRightIndexTip = FindChildRecursive(wrist, "finger2.R.001_end");
                if (_apiRightIndexBase == null)
                    _apiRightIndexBase = FindChildRecursive(wrist, "finger2.R");
                if (_apiRightIndexJoint == null)
                    _apiRightIndexJoint = FindChildRecursive(wrist, "finger2.R.001");
                // The terminal donor curls the index finger. Extend just that
                // finger for a button press, then measure the resulting tip.
                // Animator evaluation supplies the unmodified pose each frame.
                if (_apiRightIndexBase != null)
                    _apiRightIndexBase.localRotation = Quaternion.Slerp(_apiRightIndexBase.localRotation, Quaternion.identity, weight);
                if (_apiRightIndexJoint != null)
                    _apiRightIndexJoint.localRotation = Quaternion.Slerp(_apiRightIndexJoint.localRotation, Quaternion.identity, weight);
                Vector3 tipOffset = _apiRightIndexTip != null
                    ? Quaternion.Inverse(wrist.rotation) * (_apiRightIndexTip.position - wrist.position)
                    : new Vector3(0.05f, 0.18f, -0.07f);
                Vector3 contact = cap + normal * 0.003f - (rotation * _manualRightArmIk.TargetToTipRotation) * tipOffset;
                position = CctvControlGeometry.PressPosition(seconds, _apiPressStart, rest, contact, normal);
            }
            target.SetPositionAndRotation(position, rotation);
            _apiRightLastPosition = station.InverseTransformPoint(position);
            _apiRightLastRotation = Quaternion.Inverse(station.rotation) * rotation;
            _apiRightLastValid = true;
        }
    }
}
