using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CctvCommandTargetBridge
    {
        // Same assembly since #393 (Facility.Security moved here); the indirection is kept
        // because the rest of this bridge is written against MethodInfo, and only the
        // assembly qualifier had to change.
        private const string ApiTypeName = "Y4NGZCompany.Facility.Security.CctvCommandTargetApi, LethalCCTV";

        private static Type _apiType;
        private static bool _resolved;
        private static MethodInfo _describeTargetMethod;
        private static MethodInfo _getActiveTargetsMethod;
        private static MethodInfo _executeCommandMethod;

        internal sealed class TargetInfo
        {
            internal Component Component;
            internal string DisplayName;
            internal string CommandHint;
            internal Vector3 Position;
            internal float Radius;
        }

        internal static bool TryDescribeTarget(Component component, out TargetInfo info)
        {
            info = null;
            Resolve();
            if (_describeTargetMethod == null || component == null)
                return false;

            try
            {
                object descriptor = _describeTargetMethod.Invoke(null, new object[] { component });
                info = ReadDescriptor(descriptor);
                return info != null;
            }
            catch
            {
                return false;
            }
        }

        internal static List<TargetInfo> GetActiveTargets()
        {
            Resolve();
            var targets = new List<TargetInfo>();
            if (_getActiveTargetsMethod == null)
                return targets;

            try
            {
                if (!(_getActiveTargetsMethod.Invoke(null, Array.Empty<object>()) is IEnumerable enumerable))
                    return targets;

                foreach (object descriptor in enumerable)
                {
                    TargetInfo info = ReadDescriptor(descriptor);
                    if (info != null)
                        targets.Add(info);
                }
            }
            catch
            {
            }

            return targets;
        }

        internal static bool TryExecuteCommand(Component component, string command, out string statusText)
        {
            statusText = "UNSUPPORTED TARGET";
            Resolve();
            if (_executeCommandMethod == null || component == null)
                return false;

            try
            {
                object result = _executeCommandMethod.Invoke(null, new object[] { component, command ?? string.Empty });
                if (result == null)
                {
                    statusText = "NO RESPONSE";
                    return false;
                }

                statusText = ReadString(result, "StatusText", "COMMAND FAILED");
                return ReadBool(result, "Success", false);
            }
            catch
            {
                statusText = "COMMAND FAILED";
                return false;
            }
        }

        internal static bool TryFindMainframeTarget(out Component target, out string displayName, out string commandHint)
        {
            target = null;
            displayName = "MAINFRAME";
            commandHint = "hack";
            Resolve();
            if (_apiType == null) return false;

            try
            {
                object active = null;
                PropertyInfo activeProp = _apiType.GetProperty("MainframeSupport", BindingFlags.Public | BindingFlags.Static)
                                          ?? _apiType.GetProperty("Mainframe", BindingFlags.Public | BindingFlags.Static);
                if (activeProp != null)
                    active = activeProp.GetValue(null);

                if (active == null)
                {
                    Type supportType = Type.GetType("Y4NGZCompany.Facility.Mainframe.MainframeSupport, LethalCCTV", throwOnError: false);
                    PropertyInfo directActiveProp = supportType?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static);
                    if (directActiveProp != null)
                        active = directActiveProp.GetValue(null);
                }

                if (active == null) return false;

                target = active as Component;
                if (target == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Resolve()
        {
            if (_resolved && _apiType != null)
                return;

            _resolved = true;
            _apiType = Type.GetType(ApiTypeName, throwOnError: false);
            _describeTargetMethod = _apiType?.GetMethod("DescribeTarget", BindingFlags.Public | BindingFlags.Static);
            _getActiveTargetsMethod = _apiType?.GetMethod("GetActiveTargets", BindingFlags.Public | BindingFlags.Static);
            _executeCommandMethod = _apiType?.GetMethod("ExecuteCommand", BindingFlags.Public | BindingFlags.Static);
        }

        private static TargetInfo ReadDescriptor(object descriptor)
        {
            if (descriptor == null)
                return null;

            Component target = ReadComponent(descriptor, "Target");
            if (target == null)
                return null;

            return new TargetInfo
            {
                Component = target,
                DisplayName = ReadString(descriptor, "DisplayName", "SUPPORT NODE"),
                CommandHint = ReadString(descriptor, "CommandHint", string.Empty),
                Position = ReadVector3(descriptor, "Position", target.transform.position),
                Radius = Mathf.Clamp(ReadFloat(descriptor, "Radius", 0.9f), 0.35f, 4f),
            };
        }

        private static Component ReadComponent(object target, string memberName)
        {
            object value = ReadMember(target, memberName);
            return value as Component;
        }

        private static string ReadString(object target, string memberName, string fallback)
        {
            object value = ReadMember(target, memberName);
            return value as string ?? fallback;
        }

        private static bool ReadBool(object target, string memberName, bool fallback)
        {
            object value = ReadMember(target, memberName);
            return value is bool typed ? typed : fallback;
        }

        private static float ReadFloat(object target, string memberName, float fallback)
        {
            object value = ReadMember(target, memberName);
            if (value is float typed)
                return typed;
            if (value is double d)
                return (float)d;
            return fallback;
        }

        private static Vector3 ReadVector3(object target, string memberName, Vector3 fallback)
        {
            object value = ReadMember(target, memberName);
            return value is Vector3 typed ? typed : fallback;
        }

        private static object ReadMember(object target, string memberName)
        {
            if (target == null)
                return null;

            Type type = target.GetType();
            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
                return property.GetValue(target);

            FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(target);
        }
    }
}
