using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class ContractObjectiveProvider
    {
        private const string API_TYPE_NAME = "Y4NGZCompany.Contracts._Shared.MoonContractObjectiveMarkerApi";
        private const float REFRESH_INTERVAL = 0.25f;

        private static Type _apiType;
        private static MethodInfo _getMarkersMethod;
        private static bool _resolveAttempted;
        private static float _nextRefreshTime;
        private static IReadOnlyList<MarkerSnapshot> _cachedMarkers = Array.Empty<MarkerSnapshot>();

        internal readonly struct MarkerSnapshot
        {
            internal readonly string Label;
            internal readonly Vector3 Position;
            internal readonly float Radius;
            internal readonly int ColorKind;
            internal readonly bool IsComplete;

            internal MarkerSnapshot(string label, Vector3 position, float radius, int colorKind, bool isComplete)
            {
                Label = string.IsNullOrWhiteSpace(label) ? "OBJECTIVE" : label;
                Position = position;
                Radius = Mathf.Clamp(radius, 0.35f, 4f);
                ColorKind = colorKind;
                IsComplete = isComplete;
            }
        }

        internal static IReadOnlyList<MarkerSnapshot> GetMarkers()
        {
            if (Time.unscaledTime < _nextRefreshTime)
                return _cachedMarkers;
            _nextRefreshTime = Time.unscaledTime + REFRESH_INTERVAL;

            if (!TryResolveApi())
            {
                _cachedMarkers = Array.Empty<MarkerSnapshot>();
                return _cachedMarkers;
            }

            try
            {
                object result = _getMarkersMethod.Invoke(null, null);
                if (!(result is IEnumerable enumerable))
                {
                    _cachedMarkers = Array.Empty<MarkerSnapshot>();
                    return _cachedMarkers;
                }

                var markers = new List<MarkerSnapshot>(8);
                foreach (object item in enumerable)
                {
                    if (item == null)
                        continue;

                    Vector3 position = ReadMember(item, "Position", Vector3.zero);
                    if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z))
                        continue;

                    markers.Add(new MarkerSnapshot(
                        ReadMember(item, "Label", "OBJECTIVE"),
                        position,
                        ReadMember(item, "Radius", 1f),
                        ReadMember(item, "ColorKind", 0),
                        ReadMember(item, "IsComplete", false)));
                }

                _cachedMarkers = markers;
                return _cachedMarkers;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Contract objective marker read failed: {ex.Message}");
                _cachedMarkers = Array.Empty<MarkerSnapshot>();
                return _cachedMarkers;
            }
        }

        private static bool TryResolveApi()
        {
            if (_getMarkersMethod != null)
                return true;

            // One attempt, memoized: Contracted is a soft BepInEx dependency of this
            // plugin, so its absence is already final here. Re-probing on a timer cost a
            // Mono assembly-load probe every retry on a standalone install (#592).
            if (_resolveAttempted)
                return false;
            _resolveAttempted = true;

            _apiType = CompanyAssemblyBridge.ResolveContractedType(API_TYPE_NAME);
            _getMarkersMethod = _apiType?.GetMethod("GetActiveMarkers", BindingFlags.Public | BindingFlags.Static);

            if (_getMarkersMethod != null)
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Contract objective marker API connected.");
                return true;
            }

            return false;
        }

        private static T ReadMember<T>(object target, string name, T fallback)
        {
            if (target == null)
                return fallback;

            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object value = property != null
                    ? property.GetValue(target)
                    : type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

                if (value == null)
                    return fallback;
                if (value is T typed)
                    return typed;

                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return fallback;
            }
        }
    }
}
