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
    /// Leans the visible animator body to the station's operator pose anchor and,
    /// only when no authored controller is available, applies the legacy humanoid
    /// IK fallback (a no-op on the vanilla generic player rig, kept for modded
    /// humanoid avatars).
    /// </summary>
    internal sealed class CCTVOperatorRuntimePose : MonoBehaviour
    {
        private const float PoseBlendInSpeed = 7.5f;
        private const float PoseBlendOutSpeed = 6f;
        private const float ButtonPressReleaseSpeed = 6f;

        private PlayerControllerB _player;
        private Animator _animator;
        private Transform _bodyTransform;
        private Vector3 _savedBodyLocalPosition;
        private Quaternion _savedBodyLocalRotation;
        private bool _savedApplyRootMotion;
        private bool _savedAnimatorEnabled;
        private bool _canDriveBodyTransform;
        private bool _useIkFallback;
        private bool _sessionActive;
        private bool _easingOut;
        private bool _cameraControlActive;
        private float _poseWeight;
        private float _buttonPressBlend;
        private Vector2 _joystick;
        private bool _loggedSetup;

        internal static CCTVOperatorRuntimePose Attach(PlayerControllerB player, Animator animator)
        {
            if (player == null || animator == null)
                return null;

            CCTVOperatorRuntimePose pose = animator.GetComponent<CCTVOperatorRuntimePose>();
            if (pose == null)
                pose = animator.gameObject.AddComponent<CCTVOperatorRuntimePose>();
            return pose;
        }

        internal void BeginSession(PlayerControllerB player, Animator animator, bool useIkFallback)
        {
            EndSession("replaced");

            _player = player;
            _animator = animator;
            _bodyTransform = animator != null ? animator.transform : null;
            _sessionActive = _player != null && _animator != null;
            _useIkFallback = useIkFallback;
            _easingOut = false;
            _cameraControlActive = false;
            _poseWeight = 0f;
            _buttonPressBlend = 0f;
            _joystick = Vector2.zero;
            _loggedSetup = false;

            if (!_sessionActive)
                return;

            _savedAnimatorEnabled = _animator.enabled;
            _savedApplyRootMotion = _animator.applyRootMotion;
            // Save LOCAL pose so restore stays correct even if the player root
            // moves during the exit wind-down.
            _savedBodyLocalPosition = _bodyTransform != null ? _bodyTransform.localPosition : Vector3.zero;
            _savedBodyLocalRotation = _bodyTransform != null ? _bodyTransform.localRotation : Quaternion.identity;
            // Place the visible metarig at the operator anchor while leaving the
            // PlayerControllerB root/collider untouched. The authored controller
            // owns bone pose; the builder freezes its full-body idle so this
            // placement drive no longer fights looping root/leg motion.
            _canDriveBodyTransform = CCTVOperatorStation.OperatorPoseAnchor != null;

            _animator.enabled = true;
            _animator.applyRootMotion = false;
            Tick(Vector2.zero, controllingCamera: false);
        }

        internal void BeginEaseOut()
        {
            if (!_sessionActive)
                return;
            _easingOut = true;
        }

        internal void EndSession(string reason)
        {
            if (!_sessionActive)
                return;

            if (_bodyTransform != null && _canDriveBodyTransform)
            {
                try
                {
                    _bodyTransform.localPosition = _savedBodyLocalPosition;
                    _bodyTransform.localRotation = _savedBodyLocalRotation;
                }
                catch { }
            }

            if (_animator != null)
            {
                try
                {
                    _animator.applyRootMotion = _savedApplyRootMotion;
                    _animator.enabled = _savedAnimatorEnabled;
                    _animator.Update(0f);
                }
                catch { }
            }

            _player = null;
            _animator = null;
            _bodyTransform = null;
            _canDriveBodyTransform = false;
            _useIkFallback = false;
            _sessionActive = false;
            _easingOut = false;
            _cameraControlActive = false;
            _poseWeight = 0f;
            _buttonPressBlend = 0f;
            _joystick = Vector2.zero;
        }

        internal void SetCameraControl(bool active)
        {
            _cameraControlActive = active;
        }

        internal void PulseButtonPress()
        {
            if (!_sessionActive)
                return;

            _buttonPressBlend = 1f;
        }

        internal void Tick(Vector2 joystick, bool controllingCamera)
        {
            if (!_sessionActive || _animator == null || _player == null)
                return;

            if (_player.isPlayerDead || !_player.isPlayerControlled)
            {
                EndSession("player-invalid");
                return;
            }

            _cameraControlActive = controllingCamera;
            _joystick = joystick;

            float dt = Mathf.Max(Time.unscaledDeltaTime, 1f / 240f);
            float targetWeight = _easingOut ? 0f : 1f;
            float speed = targetWeight > _poseWeight ? PoseBlendInSpeed : PoseBlendOutSpeed;
            _poseWeight = Mathf.Lerp(_poseWeight, targetWeight, 1f - Mathf.Exp(-speed * dt));
            _buttonPressBlend = Mathf.Lerp(_buttonPressBlend, 0f, 1f - Mathf.Exp(-ButtonPressReleaseSpeed * dt));

            DriveBodyPose();
            LogSetupOnce();
        }

        private void LateUpdate()
        {
            if (!_sessionActive)
                return;

            Tick(_joystick, _cameraControlActive);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            // Fallback only: the vanilla player rig is generic, so this never
            // fires there. Kept for modded humanoid avatars when no authored
            // controller is shipped.
            if (!_sessionActive || !_useIkFallback || _animator == null)
                return;
            if (_animator.avatar == null || !_animator.avatar.isHuman)
                return;

            Transform right = CCTVOperatorStation.RightHandTarget;
            Transform leftIdle = CCTVOperatorStation.LeftHandIdleTarget;
            Transform leftPress = CCTVOperatorStation.LeftHandPressTarget;
            Transform look = CCTVOperatorStation.LookTarget;

            if (right != null)
            {
                float weight = Mathf.Clamp01(_poseWeight);
                _animator.SetIKPositionWeight(AvatarIKGoal.RightHand, weight);
                _animator.SetIKRotationWeight(AvatarIKGoal.RightHand, weight);
                _animator.SetIKPosition(AvatarIKGoal.RightHand, right.position);
                _animator.SetIKRotation(AvatarIKGoal.RightHand, right.rotation);
            }

            if (leftIdle != null || leftPress != null)
            {
                Transform idle = leftIdle != null ? leftIdle : leftPress;
                Transform press = leftPress != null ? leftPress : leftIdle;
                float t = Mathf.Clamp01(_buttonPressBlend);
                Vector3 position = Vector3.Lerp(idle.position, press.position, t);
                Quaternion rotation = Quaternion.Slerp(idle.rotation, press.rotation, t);
                float weight = Mathf.Clamp01(_poseWeight * 0.92f);
                _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, weight);
                _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, weight);
                _animator.SetIKPosition(AvatarIKGoal.LeftHand, position);
                _animator.SetIKRotation(AvatarIKGoal.LeftHand, rotation);
            }

            if (look != null)
            {
                _animator.SetLookAtWeight(Mathf.Clamp01(_poseWeight) * 0.45f, 0.08f, 0.42f, 0f, 0.55f);
                _animator.SetLookAtPosition(look.position);
            }
        }

        private void DriveBodyPose()
        {
            if (!_canDriveBodyTransform || _bodyTransform == null)
                return;

            Transform pose = CCTVOperatorStation.OperatorPoseAnchor;
            Transform parent = _bodyTransform.parent;
            if (pose == null)
                return;

            // Rest pose recomputed from the saved LOCAL transform so a moving
            // player root (exit wind-down) keeps the body attached to it.
            Vector3 restPosition = parent != null
                ? parent.TransformPoint(_savedBodyLocalPosition)
                : _savedBodyLocalPosition;
            Quaternion restRotation = parent != null
                ? parent.rotation * _savedBodyLocalRotation
                : _savedBodyLocalRotation;

            Vector3 targetPosition = pose.position;
            Quaternion targetRotation = pose.rotation;
            if (!_useIkFallback)
            {
                // Authored clips already own the body pose. Driving the raw
                // station anchor rotation onto ScavengerModel strips its native
                // rig-space tilt correction and folds the player into the desk.
                // For authored mode, only pull the preview horizontally to the
                // console and keep the vanilla upright height/rotation.
                targetPosition.y = restPosition.y;
                targetRotation = restRotation;
            }

            _bodyTransform.SetPositionAndRotation(
                Vector3.Lerp(restPosition, targetPosition, _poseWeight),
                Quaternion.Slerp(restRotation, targetRotation, _poseWeight));
        }

        private void LogSetupOnce()
        {
            if (_loggedSetup)
                return;

            _loggedSetup = true;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Operator pose driver active: bodyDrive={_canDriveBodyTransform}, " +
                $"ikFallback={_useIkFallback} (authored controller {(!_useIkFallback ? "in use" : "missing")}).");
        }

        private void OnDestroy()
        {
            EndSession("component-destroyed");
        }
    }
}
