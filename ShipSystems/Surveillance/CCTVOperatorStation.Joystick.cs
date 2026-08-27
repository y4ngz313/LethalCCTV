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
    internal static partial class CCTVOperatorStation
    {
        private static void ResetJoystickMotionTarget(bool immediate = false)
        {
            if (immediate)
                _joystickPhase.Reset();
            else
                _joystickPhase.SetRequested(Vector2.zero);
            _lastJoystickMoveAt = Time.unscaledTime;
        }

        private static void UpdateJoystickMotion(bool force = false)
        {
            if (_joystickTiltPivot == null)
                return;

            bool idle = !_joystickCameraControlActive
                || Time.unscaledTime - _lastJoystickMoveAt > JoystickIdleAfterSeconds;
            if (idle)
                _joystickPhase.SetRequested(Vector2.zero);

            if (force)
                _joystickPhase.SetImmediate(Vector2.zero);

            Vector2 joystick = _joystickPhase.Tick(Mathf.Max(Time.unscaledDeltaTime, 1f / 240f));

            // The tilt pivot is StartMatchLever.leverAnimatorObject's transform.
            // Writing it every LateUpdate pins the throttle to the cached neutral
            // pose and visually cancels the vanilla ship start/land pull
            // animation. Once the operator stops steering and the stick has
            // decayed to rest, stop writing so the vanilla Animator owns it.
            if ((idle || force) && joystick.sqrMagnitude <= JoystickRestEpsilonSqr)
                return;

            ApplyJoystickTiltPose(joystick);
        }

        private static void ApplyJoystickTiltPose(Vector2 joystick)
        {
            if (_joystickTiltPivot == null)
                return;

            Quaternion baseWorldRotation = GetJoystickBaseWorldRotation();
            Quaternion targetWorldRotation = baseWorldRotation;
            float strength = Mathf.Clamp01(joystick.magnitude);
            if (strength > 0.0001f)
            {
                Vector3 neutralAxis = (baseWorldRotation * _joystickTiltNeutralLocalAxis).normalized;
                if (neutralAxis.sqrMagnitude < 0.0001f)
                    neutralAxis = Vector3.up;

                if (TryResolveJoystickTiltDirection(joystick, neutralAxis, out Vector3 tiltDirection))
                {
                    float tiltRadians = BrakeLeverCardinalTiltDeg * Mathf.Deg2Rad * strength;
                    Vector3 targetAxis =
                        neutralAxis * Mathf.Cos(tiltRadians) +
                        tiltDirection * Mathf.Sin(tiltRadians);
                    if (targetAxis.sqrMagnitude > 0.0001f)
                    {
                        Quaternion delta = Quaternion.FromToRotation(neutralAxis, targetAxis.normalized);
                        targetWorldRotation = delta * baseWorldRotation;
                    }
                }
            }

            SetJoystickWorldRotation(targetWorldRotation);
        }

        private static Quaternion GetJoystickBaseWorldRotation()
        {
            if (_joystickTiltPivot == null)
                return Quaternion.identity;

            Transform parent = _joystickTiltPivot.parent;
            return parent != null
                ? parent.rotation * _joystickTiltBaseLocalRotation
                : _joystickTiltBaseLocalRotation;
        }

        private static void SetJoystickWorldRotation(Quaternion worldRotation)
        {
            if (_joystickTiltPivot == null)
                return;

            Transform parent = _joystickTiltPivot.parent;
            if (parent != null)
                _joystickTiltPivot.localRotation = Quaternion.Inverse(parent.rotation) * worldRotation;
            else
                _joystickTiltPivot.rotation = worldRotation;
        }

        private static bool TryResolveJoystickTiltDirection(Vector2 joystick, Vector3 neutralAxis, out Vector3 tiltDirection)
        {
            tiltDirection = Vector3.zero;
            ResolveJoystickMotionBasis(out Vector3 operatorRight, out Vector3 operatorForward);

            Vector3 desired =
                operatorRight * joystick.x +
                operatorForward * joystick.y;
            desired = Vector3.ProjectOnPlane(desired, neutralAxis);
            if (desired.sqrMagnitude < 0.0001f)
                return false;

            tiltDirection = desired.normalized;
            return true;
        }

        private static void ResolveJoystickMotionBasis(out Vector3 operatorRight, out Vector3 operatorForward)
        {
            Transform basis =
                _operatorPoseAnchor != null ? _operatorPoseAnchor :
                _focusViewAnchor != null ? _focusViewAnchor :
                _root != null ? _root.transform :
                _joystickTiltPivot != null ? _joystickTiltPivot :
                null;

            operatorRight = basis != null ? basis.right : Vector3.right;
            operatorForward = basis != null ? basis.forward : Vector3.forward;

            if (operatorRight.sqrMagnitude < 0.0001f)
                operatorRight = Vector3.right;
            if (operatorForward.sqrMagnitude < 0.0001f)
                operatorForward = Vector3.forward;

            operatorRight.Normalize();
            operatorForward.Normalize();
        }

        private static void EnsureBrakeLeverControlRig()
        {
            if (_rightHandTarget != null && _joystickTiltPivot != null)
            {
                _brakeLeverResolveFailures = 0;
                return;
            }
            if (Time.unscaledTime < _nextBrakeLeverControlResolveAt)
                return;

            // #502 — geometric backoff so a lever that never resolves stops
            // costing a scan every second for the rest of the session.
            int index = Mathf.Min(_brakeLeverResolveFailures, BrakeLeverResolveBackoffSeconds.Length - 1);
            _nextBrakeLeverControlResolveAt = Time.unscaledTime + BrakeLeverResolveBackoffSeconds[index];
            if (_brakeLeverResolveFailures < BrakeLeverResolveBackoffSeconds.Length)
                _brakeLeverResolveFailures++;

            ConfigureBrakeLeverControlRig();
        }

        private static void ConfigureBrakeLeverControlRig()
        {
            Transform controlRoot = ResolveBrakeLeverControlTransform();
            _joystickTiltPivot = controlRoot;
            CaptureJoystickTiltBasePose(controlRoot);
            CreateOperatorHandTargets(controlRoot);
            _joystickPhase.Reset();
            _lastJoystickMoveAt = Time.unscaledTime;
            _joystickCameraControlActive = false;

            if (controlRoot == null)
            {
                if (!_warnedBrakeLeverControlMissing)
                {
                    _warnedBrakeLeverControlMissing = true;
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Vanilla brake lever control transform was not found; CCTV hand targets will use the station root until it resolves.");
                }
                return;
            }

            if (!_loggedBrakeLeverControlRig)
            {
                _loggedBrakeLeverControlRig = true;
                SurveillanceBootstrap.Log?.LogMessage(
                    $"[LethalCCTV][LeverGrip] control={GetPath(controlRoot)} " +
                    $"source={_rightHandGripSource} " +
                    $"world={(_rightHandTarget != null ? FormatVector(_rightHandTarget.position) : "<missing>")}.");
            }
        }

        private static Transform ResolveBrakeLeverControlTransform()
        {
            try
            {
                // #502 — scoped to the ship subtree; the start lever is ship
                // geometry, so a scene-wide FindObjectOfType was pure cost.
                // Falls back to the scene scan only when the ship transform is
                // not available yet (menu / very early load).
                StartMatchLever lever = ResolveStartMatchLever();
                if (lever != null)
                {
                    if (lever.leverAnimatorObject != null)
                        return lever.leverAnimatorObject.transform;
                    if (lever.triggerScript != null && lever.triggerScript.playerPositionNode != null)
                        return lever.triggerScript.playerPositionNode;
                    if (lever.triggerScript != null)
                        return lever.triggerScript.transform;
                    return lever.transform;
                }
            }
            catch
            {
            }

            return ResolveThrottleTransform();
        }

        /// <summary>Ship-scoped StartMatchLever lookup (#502).</summary>
        private static StartMatchLever ResolveStartMatchLever()
        {
            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            if (ship != null)
            {
                StartMatchLever scoped = ship.GetComponentInChildren<StartMatchLever>(includeInactive: true);
                if (scoped != null)
                    return scoped;
            }
            return UnityEngine.Object.FindObjectOfType<StartMatchLever>();
        }

        private static void CaptureJoystickTiltBasePose(Transform pivot)
        {
            _joystickTiltBaseLocalRotation = pivot != null
                ? pivot.localRotation
                : Quaternion.identity;
            _joystickTiltNeutralLocalAxis = Vector3.up;

            if (pivot == null)
                return;

            Quaternion baseWorldRotation = pivot.parent != null
                ? pivot.parent.rotation * _joystickTiltBaseLocalRotation
                : _joystickTiltBaseLocalRotation;
            Vector3 neutralWorldAxis = ResolveJoystickNeutralWorldAxis(pivot, baseWorldRotation);
            if (neutralWorldAxis.sqrMagnitude < 0.0001f)
                neutralWorldAxis = baseWorldRotation * Vector3.up;
            if (neutralWorldAxis.sqrMagnitude < 0.0001f)
                neutralWorldAxis = Vector3.up;

            _joystickTiltNeutralLocalAxis = Quaternion.Inverse(baseWorldRotation) * neutralWorldAxis.normalized;
            if (_joystickTiltNeutralLocalAxis.sqrMagnitude < 0.0001f)
                _joystickTiltNeutralLocalAxis = Vector3.up;
            else
                _joystickTiltNeutralLocalAxis.Normalize();
        }

        private static Vector3 ResolveJoystickNeutralWorldAxis(Transform pivot, Quaternion baseWorldRotation)
        {
            Vector3 best = Vector3.zero;
            float bestDistance = 0f;
            Vector3 pivotPosition = pivot.position;

            try
            {
                Renderer[] renderers = pivot.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    ConsiderJoystickAxisCandidate(renderers[i] != null ? renderers[i].bounds.center : pivotPosition, pivotPosition, ref best, ref bestDistance);
            }
            catch { }

            try
            {
                Collider[] colliders = pivot.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                    ConsiderJoystickAxisCandidate(colliders[i] != null ? colliders[i].bounds.center : pivotPosition, pivotPosition, ref best, ref bestDistance);
            }
            catch { }

            if (bestDistance > 0.025f)
            {
                if (Vector3.Dot(best, Vector3.up) < -0.05f)
                    best = -best;
                return best.normalized;
            }

            Vector3 fallback = baseWorldRotation * Vector3.up;
            if (Vector3.Dot(fallback, Vector3.up) < -0.05f)
                fallback = -fallback;
            return fallback.sqrMagnitude > 0.0001f ? fallback.normalized : Vector3.up;
        }

        private static void ConsiderJoystickAxisCandidate(Vector3 worldCenter, Vector3 pivotPosition, ref Vector3 best, ref float bestDistance)
        {
            Vector3 delta = worldCenter - pivotPosition;
            float distance = delta.magnitude;
            if (distance <= bestDistance)
                return;

            bestDistance = distance;
            best = delta;
        }

        private static bool TryResolveJoystickGripCenter(
            Transform pivot,
            out Vector3 gripCenter,
            out string source)
        {
            gripCenter = Vector3.zero;
            source = null;
            if (pivot == null)
                return false;

            Vector3 neutralAxis = pivot.rotation * _joystickTiltNeutralLocalAxis;
            if (neutralAxis.sqrMagnitude < 0.0001f)
                neutralAxis = pivot.up;
            if (neutralAxis.sqrMagnitude < 0.0001f)
                neutralAxis = Vector3.up;
            neutralAxis.Normalize();

            float bestScore = float.NegativeInfinity;
            try
            {
                Renderer[] renderers = pivot.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null)
                        continue;
                    ConsiderJoystickGripCandidate(
                        pivot,
                        renderer.transform,
                        renderer.bounds,
                        neutralAxis,
                        "renderer",
                        ref bestScore,
                        ref gripCenter,
                        ref source);
                }
            }
            catch { }

            try
            {
                Collider[] colliders = pivot.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    Collider collider = colliders[i];
                    if (collider == null)
                        continue;
                    ConsiderJoystickGripCandidate(
                        pivot,
                        collider.transform,
                        collider.bounds,
                        neutralAxis,
                        "collider",
                        ref bestScore,
                        ref gripCenter,
                        ref source);
                }
            }
            catch { }

            return source != null;
        }

        private static void ConsiderJoystickGripCandidate(
            Transform pivot,
            Transform candidateTransform,
            Bounds bounds,
            Vector3 neutralAxis,
            string kind,
            ref float bestScore,
            ref Vector3 bestPoint,
            ref string bestSource)
        {
            Vector3 delta = bounds.center - pivot.position;
            float distance = delta.magnitude;
            if (distance < 0.015f || distance > 0.75f)
                return;

            float along = Vector3.Dot(delta, neutralAxis);
            if (along < 0.005f)
                return;

            float lateral = (delta - neutralAxis * along).magnitude;
            float maxAxis = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (lateral > 0.25f || maxAxis > 0.75f)
                return;

            string candidateName = candidateTransform != null
                ? candidateTransform.name ?? string.Empty
                : string.Empty;
            string lowerName = candidateName.ToLowerInvariant();
            float semanticBonus =
                lowerName.Contains("knob") ||
                lowerName.Contains("grip") ||
                lowerName.Contains("handle") ||
                lowerName.Contains("ball")
                    ? 0.08f
                    : 0f;
            float score = along + semanticBonus - lateral * 0.2f;
            if (score <= bestScore)
                return;

            bestScore = score;
            bestPoint = bounds.center;
            bestSource = $"{kind}:{GetPath(candidateTransform)}";
        }

        private static void CreateOperatorHandTargets(Transform joystickRoot)
        {
            DestroyTarget(ref _rightHandTarget);
            DestroyTarget(ref _leftHandIdleTarget);
            DestroyTarget(ref _leftHandPressTarget);
            DestroyTarget(ref _lookTarget);

            if (_root == null)
                return;

            Transform rightParent = _joystickTiltPivot != null ? _joystickTiltPivot : joystickRoot;
            if (rightParent == null)
                rightParent = _root.transform;

            _rightHandTarget = CreateTarget(
                rightParent,
                "CCTVOperator_RightHandTarget",
                new Vector3(0f, 0.42f, 0f),
                new Vector3(72f, 0f, 88f));
            _rightHandGripSource = "authored-pivot-offset";
            if (_rightHandTarget != null &&
                TryResolveJoystickGripCenter(
                    rightParent,
                    out Vector3 physicalGripCenter,
                    out string physicalGripSource))
            {
                float authoredDistance = Vector3.Distance(
                    _rightHandTarget.position,
                    rightParent.position);
                float physicalDistance = Vector3.Distance(
                    physicalGripCenter,
                    rightParent.position);
                // Reject a whole-mesh bounds center that lands back at the
                // hinge. A distal handle/knob renderer or collider wins over
                // the old synthetic local offset and then rides the same pivot.
                if (physicalDistance >= Mathf.Max(0.015f, authoredDistance * 0.65f))
                {
                    _rightHandTarget.position = physicalGripCenter;
                    _rightHandGripSource = physicalGripSource;
                }
            }

            _leftHandIdleTarget = CreateTarget(
                _root.transform,
                "CCTVOperator_LeftHandIdleTarget",
                new Vector3(0.67f, 1.42f, 0.54f),
                new Vector3(82f, 4f, -98f));

            _leftHandPressTarget = CreateTarget(
                _root.transform,
                "CCTVOperator_LeftHandPressTarget",
                new Vector3(0.67f, 1.42f, 0.54f),
                new Vector3(82f, 4f, -98f));

            _lookTarget = CreateTarget(
                _root.transform,
                "CCTVOperator_LookTarget",
                new Vector3(0.45f, 1.50f, -0.08f),
                Vector3.zero);
        }

        private static void DestroyTarget(ref Transform target)
        {
            if (target != null)
                UnityEngine.Object.Destroy(target.gameObject);
            target = null;
        }

        private static Transform CreateTarget(Transform parent, string name, Vector3 localPosition, Vector3 localEuler)
        {
            GameObject target = new GameObject(name);
            target.transform.SetParent(parent, worldPositionStays: false);
            target.transform.localPosition = localPosition;
            target.transform.localRotation = Quaternion.Euler(localEuler);
            target.transform.localScale = Vector3.one;
            return target.transform;
        }
    }
}
