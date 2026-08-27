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
    // Adapted from Y4NGZInteractions LiveBodyAnimatorPresenter's
    // LocalCameraPositionStabilizer. It stays dormant during the authored CCTV
    // camera move, activates when MonitorFocus restores the standing root, and
    // waits for the delayed controller/rig restore before counting two final
    // LateUpdates. Rotation remains fully owned by normal mouse-look code.
    [DefaultExecutionOrder(32000)]
    internal sealed class CCTVLocalCameraPositionStabilizer : MonoBehaviour
    {
        private PlayerControllerB _player;
        private Transform _playerRoot;
        private Transform _cameraTransform;
        private Vector3 _playerLocalPosition;
        private int _releaseLateUpdates = -1;

        internal void Initialize(
            PlayerControllerB player,
            Transform playerRoot,
            Transform cameraTransform,
            Vector3 playerLocalPosition)
        {
            _player = player;
            _playerRoot = playerRoot;
            _cameraTransform = cameraTransform;
            _playerLocalPosition = playerLocalPosition;
            _releaseLateUpdates = -1;
            enabled = true;
        }

        internal void ReleaseAfterLateUpdates(int lateUpdates)
        {
            _releaseLateUpdates = Mathf.Max(1, lateUpdates);
            enabled = true;
        }

        internal void ApplyNow()
        {
            if (_playerRoot == null || _cameraTransform == null)
                return;
            _cameraTransform.position = _playerRoot.TransformPoint(_playerLocalPosition);
        }

        private void LateUpdate()
        {
            if (_releaseLateUpdates < 0)
                return;

            ApplyNow();
            if (_player != null && Y4NGZPlayerAnimationBridge.IsLocalSessionActive(_player))
                return;

            _releaseLateUpdates--;
            if (_releaseLateUpdates <= 0)
            {
                enabled = false;
                UnityEngine.Object.Destroy(this);
            }
        }
    }
}
