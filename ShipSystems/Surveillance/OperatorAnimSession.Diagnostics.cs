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
        private void ResetHandTrace()
        {
            _handTraceEnabled = false;
            _handTraceNextMilestoneIndex = 0;
            _handTraceFrame = -1;
            _handTraceMilestoneSeconds = 0f;
            _handTraceDriverEntryCount = 0;
            _handTraceDriveApplicationCount = 0;
            _handTraceRigEvaluateCount = 0;
            _handTraceBaselineCaptured = false;
            _handTraceBaselineTargetPosition = Vector3.zero;
            _handTraceBaselineWristPosition = Vector3.zero;
            _handTraceBaselineTipPosition = Vector3.zero;
        }

        private void TryBeginHandTraceMilestone()
        {
            if (!_handTraceEnabled || !_handDriveEnterActive ||
                _handTraceNextMilestoneIndex >= HandTraceMilestonesSeconds.Length)
                return;

            float elapsed = Mathf.Max(0f, Time.unscaledTime - _handDriveEnterStartedAt);
            float milestone = HandTraceMilestonesSeconds[_handTraceNextMilestoneIndex];
            if (elapsed + 0.0001f < milestone)
                return;

            _handTraceMilestoneSeconds = milestone;
            _handTraceNextMilestoneIndex++;
            _handTraceFrame = Time.frameCount;
            _handTraceBaselineCaptured = false;
            TraceHandSnapshot("A-post-animator/pre-camera");
        }

        private void TraceHandSnapshot(string stage)
        {
            if (!_handTraceEnabled || _handTraceFrame != Time.frameCount ||
                _animator == null || Player == null)
                return;

            if ((stage.StartsWith("A-", StringComparison.Ordinal) ||
                 stage.StartsWith("C-", StringComparison.Ordinal)) &&
                (Mathf.Abs(_handTraceMilestoneSeconds - 0.56f) < 0.0001f ||
                 Mathf.Abs(_handTraceMilestoneSeconds - 1.38f) < 0.0001f))
            {
                LogFirstPersonLeftArmChainPose(
                    $"{stage}@{_handTraceMilestoneSeconds:0.00}");
            }

            Camera camera = Player.gameplayCamera;
            Transform target = ResolveFirstPersonLeftHandTarget();
            Transform wrist = _animator.transform.Find(FirstPersonLeftHandPath);
            Transform indexProximal = wrist != null ? wrist.Find("finger2.L") : null;
            Transform indexDistal = indexProximal != null ? indexProximal.Find("finger2.L.001") : null;
            Transform fingertip = indexDistal != null && indexDistal.childCount > 0
                ? indexDistal.GetChild(0)
                : null;
            Transform armsRoot = _animator.transform.Find("ScavengerModelArmsOnly");
            Transform leftShoulder = _manualLeftArmIk?.Root != null
                ? _manualLeftArmIk.Root.parent
                : null;
            Transform rightShoulder = _manualRightArmIk?.Root != null
                ? _manualRightArmIk.Root.parent
                : null;
            Transform lever = CCTVOperatorStation.RightHandTarget != null
                ? CCTVOperatorStation.RightHandTarget
                : CCTVOperatorStation.JoystickTiltPivot;
            bool haveButton = Y4NGZPlayerAnimationBridge.TryResolveAccessButtonPressSurface(
                out Vector3 buttonPoint,
                out _);

            if (!_handTraceBaselineCaptured && stage.StartsWith("A-", StringComparison.Ordinal))
            {
                _handTraceBaselineCaptured = true;
                _handTraceBaselineTargetPosition = target != null ? target.position : Vector3.zero;
                _handTraceBaselineWristPosition = wrist != null ? wrist.position : Vector3.zero;
                _handTraceBaselineTipPosition = fingertip != null ? fingertip.position : Vector3.zero;
            }

            string targetDelta = target != null && _handTraceBaselineCaptured
                ? FormatTraceVector(target.position - _handTraceBaselineTargetPosition)
                : "<n/a>";
            string wristDelta = wrist != null && _handTraceBaselineCaptured
                ? FormatTraceVector(wrist.position - _handTraceBaselineWristPosition)
                : "<n/a>";
            string tipDelta = fingertip != null && _handTraceBaselineCaptured
                ? FormatTraceVector(fingertip.position - _handTraceBaselineTipPosition)
                : "<n/a>";
            float elapsed = Mathf.Max(0f, Time.unscaledTime - _handDriveEnterStartedAt);
            float tipToButton = fingertip != null && haveButton
                ? Vector3.Distance(fingertip.position, buttonPoint)
                : -1f;
            float wristToTarget = wrist != null && target != null
                ? Vector3.Distance(wrist.position, target.position)
                : -1f;
            float wristToLever = wrist != null && lever != null
                ? Vector3.Distance(wrist.position, lever.position)
                : -1f;

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][HandTrace] milestone={_handTraceMilestoneSeconds:0.00}s " +
                $"frame={Time.frameCount} elapsed={elapsed:0.000}s stage={stage} " +
                $"calls=hook:{_handTraceDriverEntryCount}/drive:{_handTraceDriveApplicationCount}/rig:{_handTraceRigEvaluateCount} " +
                $"{FormatTraceAnimatorState()} " +
                $"camera={FormatTracePose(camera != null ? camera.transform : null, camera)} " +
                $"armsRoot={FormatTracePose(armsRoot, camera)} " +
                $"shoulder.L={FormatTracePose(leftShoulder, camera)} " +
                $"shoulder.R={FormatTracePose(rightShoulder, camera)} " +
                $"target={FormatTracePose(target, camera)} wrist={FormatTracePose(wrist, camera)} " +
                $"tip={FormatTracePose(fingertip, camera)} " +
                $"button={(haveButton ? FormatTracePoint(buttonPoint, camera) : "<missing>")} " +
                $"lever={FormatTracePose(lever, camera)} " +
                $"distance=tip-button:{FormatTraceDistance(tipToButton)}/wrist-target:{FormatTraceDistance(wristToTarget)}/wrist-lever:{FormatTraceDistance(wristToLever)} " +
                $"deltaFromA=target:{targetDelta}/wrist:{wristDelta}/tip:{tipDelta} " +
                $"{BuildManualIkTrace()} " +
                $"{BuildRigDiagTrace()} " +
                $"{BuildFingerTrace(wrist)}");
        }

        private string BuildManualIkTrace()
        {
            return
                $"[ManualIK] config:{Y4NGZPlayerAnimationBridge.UseManualFirstPersonArmIk}/" +
                $"L:{DescribeManualIkChainTrace(_manualLeftArmIk)}/" +
                $"R:{DescribeManualIkChainTrace(_manualRightArmIk)}";
        }

        private static string DescribeManualIkChainTrace(ManualArmIkChain chain)
        {
            if (chain == null)
                return "<missing>";
            string distance = chain.LastTipTargetDistance >= 0f
                ? chain.LastTipTargetDistance.ToString("0.0000") + "m"
                : "<not-solved>";
            string rootTargetDistance = chain.LastRootTargetDistance >= 0f
                ? chain.LastRootTargetDistance.ToString("0.000") + "m"
                : "<not-solved>";
            string reach = chain.LastReach >= 0f
                ? chain.LastReach.ToString("0.000") + "m"
                : "<not-solved>";
            return
                $"tip-target:{distance}@frame:{chain.LastSolvedFrame}" +
                $"/root:{FormatTraceVector(chain.LastRootPosition)}" +
                $"/rootTarget:{rootTargetDistance}" +
                $"/reach:{reach}" +
                $"/fresh:{chain.LastSolvedFrame == Time.frameCount}" +
                $"/pole:{chain.LastPoleHint}";
        }

        private void LogFirstPersonLeftArmChainPose(string phase)
        {
            try
            {
                if (_animator == null)
                    throw new InvalidOperationException("animator is missing");

                string[] paths =
                {
                    "ScavengerModelArmsOnly",
                    "ScavengerModelArmsOnly/metarig",
                    "ScavengerModelArmsOnly/metarig/spine.003",
                    "ScavengerModelArmsOnly/metarig/spine.003/shoulder.L",
                    "ScavengerModelArmsOnly/metarig/spine.003/shoulder.L/arm.L_upper",
                    "ScavengerModelArmsOnly/metarig/spine.003/shoulder.L/arm.L_upper/arm.L_lower",
                    FirstPersonLeftHandPath,
                };

                for (int i = 0; i < paths.Length; i++)
                {
                    Transform node = _animator.transform.Find(paths[i]);
                    if (node == null)
                        throw new InvalidOperationException($"node is missing: {paths[i]}");

                    string handScale = i == paths.Length - 1
                        ? $" lossyScale={FormatTraceVector(node.lossyScale)}"
                        : string.Empty;
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][ChainPose] phase={phase} node={node.name} " +
                        $"worldPos={FormatTraceVector(node.position)} " +
                        $"localPosition={FormatTraceVector(node.localPosition)} " +
                        $"localEulerAngles={FormatTraceVector(node.localEulerAngles)}" +
                        handScale);
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ChainPose] phase={phase} failed: {ex.Message}");
            }
        }

        private string FormatTraceAnimatorState()
        {
            if (_firstPersonArmsLayer < 0 || _firstPersonArmsLayer >= _animator.layerCount)
                return "fpLayer=<missing>";

            try
            {
                AnimatorStateInfo current = _animator.GetCurrentAnimatorStateInfo(_firstPersonArmsLayer);
                bool transitioning = _animator.IsInTransition(_firstPersonArmsLayer);
                string next = "<none>";
                if (transitioning)
                {
                    AnimatorStateInfo nextState = _animator.GetNextAnimatorStateInfo(_firstPersonArmsLayer);
                    next = $"{nextState.fullPathHash}@{nextState.normalizedTime:0.000}";
                }

                AnimatorClipInfo[] clips = _animator.GetCurrentAnimatorClipInfo(_firstPersonArmsLayer);
                var clipText = new StringBuilder();
                for (int i = 0; i < clips.Length; i++)
                {
                    if (i > 0)
                        clipText.Append(',');
                    clipText.Append(clips[i].clip != null ? clips[i].clip.name : "<null>");
                    clipText.Append('@').Append(clips[i].weight.ToString("0.00"));
                }

                return
                    $"fpLayer=index:{_firstPersonArmsLayer}/weight:{_animator.GetLayerWeight(_firstPersonArmsLayer):0.000}/" +
                    $"state:{current.fullPathHash}/short:{current.shortNameHash}/time:{current.normalizedTime:0.000}/" +
                    $"transition:{transitioning}/next:{next}/clips:[{clipText}]";
            }
            catch (Exception ex)
            {
                return $"fpLayer=<error:{ex.GetType().Name}>";
            }
        }

        // Bounded diagnostics for the milestone trace: is the RigBuilder
        // graph alive, what are the rig layer weights, and what weight do the
        // arm constraints report — the discriminators for "why didn't the IK
        // solve" that the pose trace alone cannot answer.
        private string BuildRigDiagTrace()
        {
            try
            {
                Component[] rigBuilders = ResolveFirstPersonRightHandRigBuilders();
                if (rigBuilders == null || rigBuilders.Length == 0 || rigBuilders[0] == null)
                    return "rig=<none>";

                Component rigBuilder = rigBuilders[0];
                var text = new StringBuilder("rig=graph:");
                text.Append(DescribeRigGraph(rigBuilder));
                text.Append("/builderEnabled:").Append(DescribeBehaviourEnabled(rigBuilder));

                if (ReadReflectedMemberNoByRef(rigBuilder, "layers") is
                    System.Collections.IEnumerable layers)
                {
                    text.Append("/layers:[");
                    bool first = true;
                    foreach (object layer in layers)
                    {
                        if (layer == null)
                            continue;
                        if (!first)
                            text.Append(';');
                        first = false;

                        object rig = ReadReflectedMemberNoByRef(layer, "rig");
                        object active = ReadReflectedMemberNoByRef(layer, "active");
                        string name = rig is Component rigComponent ? rigComponent.name : "<null>";
                        string weight = "?";
                        if (ReadReflectedMemberNoByRef(rig, "weight") is float w)
                            weight = w.ToString("0.00");
                        text.Append(name).Append("@w:").Append(weight)
                            .Append(active is bool on && on ? "/on" : "/off");
                    }
                    text.Append(']');
                }

                AppendConstraintDiag(text, "/L:", "LeftArm");
                AppendConstraintDiag(text, "/R:", "RightArm");
                return text.ToString();
            }
            catch (Exception ex)
            {
                return $"rig=<error:{ex.GetType().Name}>";
            }
        }

        private void AppendConstraintDiag(StringBuilder text, string label, string nodeName)
        {
            text.Append(label);
            Transform arms = _animator != null ? _animator.transform.Find("ScavengerModelArmsOnly") : null;
            Transform node = FindChildRecursive(arms, nodeName);
            if (node == null)
            {
                text.Append("<missing>");
                return;
            }

            bool any = false;
            Component[] components = node.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component is Transform)
                    continue;
                object weight = ReadReflectedMemberNoByRef(component, "weight");
                if (!(weight is float constraintWeight))
                    continue;
                if (any)
                    text.Append(',');
                any = true;
                try
                {
                    text.Append(component.GetType().Name)
                        .Append("@w:")
                        .Append(constraintWeight.ToString("0.00"));
                }
                catch
                {
                    text.Append(component.GetType().Name).Append("@w:<error>");
                }
            }

            if (!any)
                text.Append("<no-constraint>");
        }

        private static string BuildFingerTrace(Transform wrist)
        {
            if (wrist == null)
                return "fingers=0/10";

            int resolved = 0;
            var text = new StringBuilder("fingerLocalQ=[");
            for (int digit = 1; digit <= 5; digit++)
            {
                Transform proximal = wrist.Find($"finger{digit}.L");
                Transform distal = proximal != null ? proximal.Find($"finger{digit}.L.001") : null;
                if (digit > 1)
                    text.Append(';');
                text.Append('f').Append(digit).Append(':');
                if (proximal != null)
                {
                    resolved++;
                    text.Append(FormatTraceQuaternion(proximal.localRotation));
                }
                else
                {
                    text.Append("<missing>");
                }
                text.Append('/');
                if (distal != null)
                {
                    resolved++;
                    text.Append(FormatTraceQuaternion(distal.localRotation));
                }
                else
                {
                    text.Append("<missing>");
                }
            }
            text.Append(']');
            return $"fingers={resolved}/10 {text}";
        }

        private static string FormatTracePose(Transform transform, Camera camera)
        {
            if (transform == null)
                return "<missing>";
            return
                $"w:{FormatTraceVector(transform.position)}/v:{FormatTraceViewport(transform.position, camera)}/" +
                $"l:{FormatTraceVector(transform.localPosition)}/lq:{FormatTraceQuaternion(transform.localRotation)}";
        }

        private static string FormatTracePoint(Vector3 point, Camera camera)
        {
            return $"w:{FormatTraceVector(point)}/v:{FormatTraceViewport(point, camera)}";
        }

        private static string FormatTraceViewport(Vector3 point, Camera camera)
        {
            return camera != null ? FormatTraceVector(camera.WorldToViewportPoint(point)) : "<missing>";
        }

        private static string FormatTraceVector(Vector3 value)
        {
            return $"({value.x:0.000},{value.y:0.000},{value.z:0.000})";
        }

        private static string FormatTraceQuaternion(Quaternion value)
        {
            return $"({value.x:0.000},{value.y:0.000},{value.z:0.000},{value.w:0.000})";
        }

        private static string FormatTraceDistance(float value)
        {
            return value >= 0f ? value.ToString("0.000") : "<missing>";
        }

        private static void ApplyPoseTuning(
            Transform target,
            Y4NGZPlayerAnimationBridge.FirstPersonRightHandPoseTuning tuning,
            ref PoseTuningApplication previous)
        {
            if (target == null)
            {
                previous = default;
                return;
            }

            UndoPoseTuningIfStillApplied(target, ref previous);

            if (tuning.Offset.sqrMagnitude <= 0.0000001f && tuning.Euler.sqrMagnitude <= 0.0000001f)
                return;

            Vector3 basePosition = target.localPosition;
            Quaternion baseRotation = target.localRotation;
            Quaternion tuningRotation = Quaternion.Euler(tuning.Euler);

            target.localPosition = basePosition + tuning.Offset;
            target.localRotation = baseRotation * tuningRotation;

            previous.Target = target;
            previous.BaseLocalPosition = basePosition;
            previous.BaseLocalRotation = baseRotation;
            previous.AppliedLocalPosition = target.localPosition;
            previous.AppliedLocalRotation = target.localRotation;
            previous.Active = true;
        }

        private static void UndoPoseTuningIfStillApplied(Transform target, ref PoseTuningApplication previous)
        {
            if (!previous.Active)
                return;

            if (previous.Target == target && target != null)
            {
                bool positionStillApplied =
                    (target.localPosition - previous.AppliedLocalPosition).sqrMagnitude <= 0.000001f;
                bool rotationStillApplied =
                    Quaternion.Angle(target.localRotation, previous.AppliedLocalRotation) <= 0.1f;

                if (positionStillApplied && rotationStillApplied)
                {
                    target.localPosition = previous.BaseLocalPosition;
                    target.localRotation = previous.BaseLocalRotation;
                }
            }

            previous = default;
        }

        private Transform ResolveFirstPersonRightHandTarget()
        {
            if (_firstPersonRightHandTarget != null)
                return _firstPersonRightHandTarget;

            Transform root = _animator != null ? _animator.transform : null;
            _firstPersonRightHandTarget = FindChildRecursive(root, "ArmsRightArm_target");
            if (_firstPersonRightHandTarget == null && !_firstPersonRightHandTargetMissingLogged)
            {
                _firstPersonRightHandTargetMissingLogged = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] First-person right-hand tuning target not found: ArmsRightArm_target.");
            }
            else if (_firstPersonRightHandTarget != null)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] First-person right-hand tuning target resolved: " +
                    GetTransformPath(_firstPersonRightHandTarget));
            }

            return _firstPersonRightHandTarget;
        }

        private Transform ResolveFirstPersonLeftHandTarget()
        {
            if (_firstPersonLeftHandTarget != null)
                return _firstPersonLeftHandTarget;

            Transform root = _animator != null ? _animator.transform : null;
            _firstPersonLeftHandTarget = FindChildRecursive(root, "ArmsLeftArm_target");
            if (_firstPersonLeftHandTarget == null && !_firstPersonLeftHandTargetMissingLogged)
            {
                _firstPersonLeftHandTargetMissingLogged = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] First-person left-hand tuning target not found: ArmsLeftArm_target.");
            }
            else if (_firstPersonLeftHandTarget != null)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] First-person left-hand tuning target resolved: " +
                    GetTransformPath(_firstPersonLeftHandTarget));
            }

            return _firstPersonLeftHandTarget;
        }

        private void LogChainIkConstraintBindings(string phase)
        {
            if (!_isLocal || _animator == null)
                return;

            try
            {
                Transform rigArms = FindChildRecursive(_animator.transform, "RigArms");
                if (rigArms == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][RigBinding] phase={phase} RigArms transform is missing; " +
                        "ChainIK target binding could not be inspected.");
                    return;
                }

                LogChainIkConstraintBindingsForArm(
                    phase,
                    rigArms,
                    "LeftArm",
                    ResolveFirstPersonLeftHandTarget());
                LogChainIkConstraintBindingsForArm(
                    phase,
                    rigArms,
                    "RightArm",
                    ResolveFirstPersonRightHandTarget());
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RigBinding] phase={phase} ChainIK inspection failed: {ex.Message}");
            }
        }

        private static void LogChainIkConstraintBindingsForArm(
            string phase,
            Transform rigArms,
            string armNodeName,
            Transform driverTarget)
        {
            Transform armNode = FindChildRecursive(rigArms, armNodeName);
            if (armNode == null)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RigBinding] phase={phase} arm={armNodeName} node is missing under " +
                    $"{GetTransformPath(rigArms)}.");
                return;
            }

            bool found = false;
            Component[] components = armNode.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component constraint = components[i];
                if (constraint == null ||
                    !string.Equals(constraint.GetType().Name, "ChainIKConstraint", StringComparison.Ordinal))
                {
                    continue;
                }

                found = true;
                try
                {
                    // RigConstraint<TJob,TData,TBinder>.data returns ref TData.
                    // PropertyInfo.GetValue cannot invoke that ByRef getter, so
                    // read the serialized struct field and inspect the boxed copy.
                    object data = ReadConstraintDataField(constraint, out string dataRead);
                    Transform configuredTarget = ReadConstraintDataTransform(
                        data,
                        "m_Target",
                        "target",
                        out string targetRead);
                    Transform root = ReadConstraintDataTransform(
                        data,
                        "m_Root",
                        "root",
                        out string rootRead);
                    Transform tip = ReadConstraintDataTransform(
                        data,
                        "m_Tip",
                        "tip",
                        out string tipRead);
                    object weightValue = ReadReflectedMemberNoByRef(constraint, "weight");
                    string weight = weightValue is float w ? w.ToString("0.00") : "<missing>";
                    bool bothTargetsPresent = configuredTarget != null && driverTarget != null;
                    bool sameReference = bothTargetsPresent && ReferenceEquals(configuredTarget, driverTarget);
                    string details =
                        $"phase={phase} arm={armNodeName} " +
                        $"constraint={constraint.GetType().Name}@{GetTransformPath(constraint.transform)} " +
                        $"data={dataRead} " +
                        $"data.target={GetTransformPath(configuredTarget)}({targetRead}) " +
                        $"data.root={(root != null ? root.name : "<null>")}({rootRead}) " +
                        $"data.tip={(tip != null ? tip.name : "<null>")}({tipRead}) " +
                        $"weight={weight} driverTarget={GetTransformPath(driverTarget)} " +
                        $"targetReferenceEqual={sameReference}";

                    if (bothTargetsPresent && !sameReference)
                    {
                        SurveillanceBootstrap.Log?.LogWarning(
                            "[LethalCCTV][RigBinding][IK TARGET MISMATCH] " + details +
                            ". THE DRIVER WRITES A DIFFERENT TRANSFORM; THIS CHAIN CANNOT FOLLOW THE DRIVER TARGET.");
                    }
                    else if (!bothTargetsPresent)
                    {
                        SurveillanceBootstrap.Log?.LogWarning(
                            "[LethalCCTV][RigBinding][TARGET MISSING] " + details + ".");
                    }
                    else
                    {
                        SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][RigBinding] " + details + ".");
                    }
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV][RigBinding] phase={phase} arm={armNodeName} " +
                        $"constraint={constraint.GetType().Name}@{GetTransformPath(constraint.transform)} " +
                        $"inspection failed: {ex.Message}");
                }
            }

            if (!found)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RigBinding] phase={phase} arm={armNodeName} " +
                    "has no ChainIKConstraint component in its descendants.");
            }
        }

        private static object ReadConstraintDataField(object constraint, out string readResult)
        {
            readResult = "<constraint-null>";
            if (constraint == null)
                return null;

            try
            {
                Type constraintType = constraint.GetType();
                FieldInfo dataField = constraintType.GetField(
                    "m_Data",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                // Private base fields are not returned by Type.GetField on a
                // derived type. Keep the prescribed direct lookup first, then
                // walk the generic RigConstraint base types if necessary.
                Type declaringType = constraintType;
                while (dataField == null && declaringType != null)
                {
                    dataField = declaringType.GetField(
                        "m_Data",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);
                    declaringType = declaringType.BaseType;
                }

                if (dataField == null)
                {
                    readResult = "<m_Data-missing>";
                    return null;
                }

                object boxedData = dataField.GetValue(constraint);
                readResult = $"field:{dataField.DeclaringType?.Name}.m_Data";
                return boxedData;
            }
            catch (Exception ex)
            {
                readResult = $"<m_Data-error:{ex.GetType().Name}>";
                return null;
            }
        }

        private static Transform ReadConstraintDataTransform(
            object boxedData,
            string serializedFieldName,
            string propertyName,
            out string readResult)
        {
            readResult = "<data-null>";
            if (boxedData == null)
                return null;

            Type dataType = boxedData.GetType();
            try
            {
                FieldInfo field = dataType.GetField(
                    serializedFieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null)
                {
                    // Publicized/editor variants can expose the serialized
                    // backing field while retaining the same name.
                    field = dataType.GetField(
                        serializedFieldName,
                        BindingFlags.Instance | BindingFlags.Public);
                }

                if (field != null)
                {
                    object value = field.GetValue(boxedData);
                    readResult = "field:" + serializedFieldName;
                    return value as Transform;
                }
            }
            catch (Exception ex)
            {
                readResult = $"<field-error:{ex.GetType().Name}>";
                return null;
            }

            try
            {
                PropertyInfo property = dataType.GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public);
                if (property == null)
                {
                    readResult = $"<{serializedFieldName}/{propertyName}-missing>";
                    return null;
                }

                MethodInfo getter = property.GetGetMethod(nonPublic: true);
                if (property.PropertyType.IsByRef || getter == null || getter.ReturnType.IsByRef)
                {
                    readResult = "<property-skipped:ByRef>";
                    return null;
                }

                object value = property.GetValue(boxedData);
                readResult = "property:" + propertyName;
                return value as Transform;
            }
            catch (Exception ex)
            {
                readResult = $"<property-error:{ex.GetType().Name}>";
                return null;
            }
        }

        private static object ReadReflectedMemberNoByRef(object owner, string memberName)
        {
            if (owner == null || string.IsNullOrEmpty(memberName))
                return null;

            try
            {
                Type type = owner.GetType();
                const BindingFlags flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo field = type.GetField(memberName, flags);
                if (field != null)
                    return field.GetValue(owner);

                PropertyInfo property = type.GetProperty(memberName, flags);
                MethodInfo getter = property?.GetGetMethod(nonPublic: true);
                if (property == null || property.PropertyType.IsByRef ||
                    getter == null || getter.ReturnType.IsByRef)
                {
                    return null;
                }
                return property.GetValue(owner);
            }
            catch
            {
                return null;
            }
        }

    }
}
