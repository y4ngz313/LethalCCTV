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
        /// <summary>
        /// Per-frame station upkeep: event subscriptions, the brake-lever
        /// control rig, joystick motion, the periodic focus-anchor re-aim, and
        /// (last) the optional debug placement editor. Named TickDebugPlacement
        /// until #502 — everything but the final line is production work.
        /// </summary>
        internal static void TickStation()
        {
            EnsureStationEventSubscriptions();
            EnsureBrakeLeverControlRig();
            UpdateJoystickMotion();

            // Anchor aiming re-derives the radar pose from the monitor's screen
            // geometry, which measured 8-18ms per call on a loaded moon. Both the
            // anchors and the monitors they aim at are static ship geometry, so a
            // periodic refresh is indistinguishable from per-frame; the callers
            // that need it exact (focus entry, debug placement) pass force: true.
            if (Time.unscaledTime >= _nextFocusAnchorAimAt)
            {
                _nextFocusAnchorAimAt = Time.unscaledTime + FocusAnchorAimIntervalSeconds;
                EnsureFocusAnchorAimed();
            }

            _placementEditor?.Tick();
        }

        internal static void StartDebugPlacement(string targetName)
        {
            AllowDebugStationSpawn(2f);
            Ensure(allowUnpurchasedForDebug: true);
            string normalized = NormalizePlacementTarget(targetName);
            if (normalized == "focus" || normalized == "radar")
            {
                EnsureFocusAnchorAimed(force: true);
                EnsureRadarAnchorAimed(force: true);
            }
            GetPlacementEditor().Start(targetName);
        }

        internal static bool IsDebugPlacementActive => _placementEditor != null && _placementEditor.IsActive;

        internal static bool IsEditingDebugPlacement(string targetName)
        {
            return _placementEditor != null && _placementEditor.IsEditingTarget(targetName);
        }

        internal static bool CancelDebugPlacement()
        {
            if (_placementEditor == null || !_placementEditor.IsActive)
                return false;

            _placementEditor.Cancel();
            return true;
        }

        private static void AllowDebugStationSpawn(float seconds)
        {
            _debugStationSpawnUntil = Mathf.Max(_debugStationSpawnUntil, Time.unscaledTime + Mathf.Max(0f, seconds));
        }

        private static bool IsDebugStationSpawnActive()
        {
            return (_placementEditor != null && _placementEditor.IsActive) || Time.unscaledTime < _debugStationSpawnUntil;
        }

        internal static void DumpControlGeometry(string reason = "manual")
        {
            try
            {
                EnsureBrakeLeverControlRig();

                StartMatchLever lever = null;
                try
                {
                    lever = ResolveStartMatchLever();
                }
                catch
                {
                    lever = null;
                }

                Vector3 anchor = ResolveControlGeometryAnchor(lever);
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] --- CCTV control geometry dump ({reason}) anchor={FormatVectorPrecise(anchor)} ---");
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV]   frames root={GetPath(_root != null ? _root.transform : null)} " +
                    $"pose={GetPath(_operatorPoseAnchor)} focus={GetPath(_focusViewAnchor)} lever={GetPath(_joystickTiltPivot)}");

                LogControlTransform("stationRoot", _root != null ? _root.transform : null);
                LogControlTransform("focusViewAnchor", _focusViewAnchor);
                LogControlTransform("radarViewAnchor", _radarViewAnchor);
                LogControlTransform("operatorPoseAnchor", _operatorPoseAnchor);
                LogControlTransform("joystickTiltPivot", _joystickTiltPivot);
                LogControlTransform("rightHandTarget", _rightHandTarget);
                LogControlTransform("leftHandIdleTarget", _leftHandIdleTarget);
                LogControlTransform("leftHandPressTarget", _leftHandPressTarget);
                LogControlTransform("lookTarget", _lookTarget);

                if (lever == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV]   StartMatchLever was not found during control geometry dump.");
                }
                else
                {
                    LogControlTransform("startMatchLever", lever.transform);
                    LogControlTransform("startMatchLever.trigger", lever.triggerScript != null ? lever.triggerScript.transform : null);
                    LogControlTransform(
                        "startMatchLever.playerPositionNode",
                        lever.triggerScript != null ? lever.triggerScript.playerPositionNode : null);
                    LogControlTransform(
                        "startMatchLever.leverAnimatorObject",
                        lever.leverAnimatorObject != null ? lever.leverAnimatorObject.transform : null);
                }

                List<ControlGeometryCandidate> rendererCandidates = CollectControlRendererCandidates(anchor);
                DumpControlCandidates("renderer", rendererCandidates, ControlGeometryRendererDumpLimit);

                List<ControlGeometryCandidate> colliderCandidates = CollectControlColliderCandidates(anchor);
                DumpControlCandidates("collider", colliderCandidates, ControlGeometryColliderDumpLimit);

                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] --- end CCTV control geometry dump ---");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV control geometry dump failed: {ex.Message}");
            }
        }

        private static Vector3 ResolveControlGeometryAnchor(StartMatchLever lever)
        {
            if (lever != null && lever.leverAnimatorObject != null)
            {
                Transform leverTransform = lever.leverAnimatorObject.transform;
                if (TryGetRendererBounds(leverTransform, out Bounds bounds))
                    return bounds.center;
                return leverTransform.position;
            }

            if (_joystickTiltPivot != null)
            {
                if (TryGetRendererBounds(_joystickTiltPivot, out Bounds bounds))
                    return bounds.center;
                return _joystickTiltPivot.position;
            }

            if (lever != null)
                return lever.transform.position;
            if (_operatorPoseAnchor != null)
                return _operatorPoseAnchor.position;
            if (_root != null)
                return _root.transform.position;
            return Vector3.zero;
        }

        private static void LogControlTransform(string label, Transform transform)
        {
            if (transform == null)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   GEOM {label}: <null>");
                return;
            }

            LogControlPoint(label, transform.position, GetPath(transform));
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV]   GEOM {label}.rotation worldEuler={FormatVectorPrecise(transform.eulerAngles)} " +
                $"localEuler={FormatVectorPrecise(transform.localEulerAngles)} lossyScale={FormatVectorPrecise(transform.lossyScale)}");

            if (!TryGetRendererBounds(transform, out Bounds bounds))
                return;

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV]   GEOM {label}.bounds size={FormatVectorPrecise(bounds.size)} " +
                $"center={FormatPointInFrames(bounds.center)}");
            LogControlPoint(label + ".boundsTop", bounds.center + Vector3.up * bounds.extents.y, null);

            Vector3 towardOperator = _operatorPoseAnchor != null
                ? _operatorPoseAnchor.position - bounds.center
                : (_root != null ? -_root.transform.forward : Vector3.forward);
            towardOperator.y = 0f;
            if (towardOperator.sqrMagnitude < 0.0001f)
                towardOperator = _root != null ? -_root.transform.forward : Vector3.forward;
            towardOperator.Normalize();
            float horizontalExtent = Mathf.Max(bounds.extents.x, bounds.extents.z);
            LogControlPoint(label + ".boundsTowardOperator", bounds.center + towardOperator * horizontalExtent, null);
        }

        private static List<ControlGeometryCandidate> CollectControlRendererCandidates(Vector3 anchor)
        {
            List<ControlGeometryCandidate> candidates = new List<ControlGeometryCandidate>();
            Renderer[] renderers;
            try
            {
                renderers = Resources.FindObjectsOfTypeAll<Renderer>();
            }
            catch
            {
                return candidates;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.gameObject == null || !renderer.gameObject.scene.IsValid())
                    continue;

                Bounds bounds;
                try { bounds = renderer.bounds; }
                catch { continue; }

                AddControlGeometryCandidate(candidates, renderer.transform, bounds, anchor, "renderer");
            }

            SortControlGeometryCandidates(candidates);
            return candidates;
        }

        private static List<ControlGeometryCandidate> CollectControlColliderCandidates(Vector3 anchor)
        {
            List<ControlGeometryCandidate> candidates = new List<ControlGeometryCandidate>();
            Collider[] colliders;
            try
            {
                colliders = Resources.FindObjectsOfTypeAll<Collider>();
            }
            catch
            {
                return candidates;
            }

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.gameObject == null || !collider.gameObject.scene.IsValid())
                    continue;

                Bounds bounds;
                try { bounds = collider.bounds; }
                catch { continue; }

                AddControlGeometryCandidate(candidates, collider.transform, bounds, anchor, "collider");
            }

            SortControlGeometryCandidates(candidates);
            return candidates;
        }

        private static void AddControlGeometryCandidate(
            List<ControlGeometryCandidate> candidates,
            Transform transform,
            Bounds bounds,
            Vector3 anchor,
            string source)
        {
            if (candidates == null || transform == null)
                return;

            string path = GetPath(transform);
            if (!IsControlGeometryPathInScope(path))
                return;

            float closestDistance = Vector3.Distance(bounds.ClosestPoint(anchor), anchor);
            if (closestDistance > ControlGeometryCandidateRadius)
                return;

            Vector3 size = bounds.size;
            float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            float centerDistance = Vector3.Distance(bounds.center, anchor);
            bool namedLikeControl = PathLooksLikeControlGeometry(path);
            bool compactControl = maxAxis <= 0.55f;
            bool closeToLever = closestDistance <= 0.35f || centerDistance <= 0.95f;
            if (!namedLikeControl && !compactControl && !closeToLever)
                return;
            if (!namedLikeControl && maxAxis > 1.50f)
                return;
            if (maxAxis <= 0.001f)
                return;

            candidates.Add(new ControlGeometryCandidate
            {
                Transform = transform,
                Path = path,
                Bounds = bounds,
                CenterDistance = centerDistance,
                ClosestDistance = closestDistance,
                MaxAxis = maxAxis,
                Source = source
            });
        }

        private static void SortControlGeometryCandidates(List<ControlGeometryCandidate> candidates)
        {
            if (candidates == null)
                return;

            candidates.Sort((a, b) =>
            {
                int closest = a.ClosestDistance.CompareTo(b.ClosestDistance);
                return closest != 0 ? closest : a.CenterDistance.CompareTo(b.CenterDistance);
            });
        }

        private static void DumpControlCandidates(string source, List<ControlGeometryCandidate> candidates, int limit)
        {
            int count = candidates != null ? candidates.Count : 0;
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   GEOM {source}Candidates count={count} dumpLimit={limit}");
            if (count == 0)
                return;

            int dumped = Mathf.Min(count, limit);
            for (int i = 0; i < dumped; i++)
            {
                ControlGeometryCandidate candidate = candidates[i];
                if (candidate == null)
                    continue;

                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV]   GEOM {source}[{i}] path={candidate.Path} " +
                    $"center={FormatPointInFrames(candidate.Bounds.center)} size={FormatVectorPrecise(candidate.Bounds.size)} " +
                    $"centerDist={candidate.CenterDistance:F3} closest={candidate.ClosestDistance:F3} " +
                    $"maxAxis={candidate.MaxAxis:F3} source={candidate.Source}");

                Vector3 top = candidate.Bounds.center + Vector3.up * candidate.Bounds.extents.y;
                Vector3 towardOperator = _operatorPoseAnchor != null
                    ? _operatorPoseAnchor.position - candidate.Bounds.center
                    : (_root != null ? -_root.transform.forward : Vector3.forward);
                towardOperator.y = 0f;
                if (towardOperator.sqrMagnitude < 0.0001f)
                    towardOperator = _root != null ? -_root.transform.forward : Vector3.forward;
                towardOperator.Normalize();
                Vector3 nearFace = candidate.Bounds.center + towardOperator * Mathf.Max(candidate.Bounds.extents.x, candidate.Bounds.extents.z);

                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV]   GEOM {source}[{i}].top={FormatPointInFrames(top)} " +
                    $"nearFace={FormatPointInFrames(nearFace)}");
            }
        }

        private static bool IsControlGeometryPathInScope(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.IndexOf("Environment/HangarShip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf(RootName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   PathLooksLikeControlGeometry(path);
        }

        private static bool PathLooksLikeControlGeometry(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("knob", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("dial", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("lever", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("brake", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("gear", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("throttle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("panel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("red", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("green", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("blue", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LogControlPoint(string label, Vector3 world, string path)
        {
            string pathText = string.IsNullOrWhiteSpace(path) ? string.Empty : $" path={path}";
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   GEOM {label}{pathText} {FormatPointInFrames(world)}");
        }

        private static string FormatPointInFrames(Vector3 world)
        {
            return $"world={FormatVectorPrecise(world)} " +
                   $"rootLocal={FormatLocalPoint(_root != null ? _root.transform : null, world)} " +
                   $"poseLocal={FormatLocalPoint(_operatorPoseAnchor, world)} " +
                   $"focusLocal={FormatLocalPoint(_focusViewAnchor, world)} " +
                   $"leverLocal={FormatLocalPoint(_joystickTiltPivot, world)}";
        }

        private static string FormatLocalPoint(Transform frame, Vector3 world)
        {
            return frame != null ? FormatVectorPrecise(frame.InverseTransformPoint(world)) : "<n/a>";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:0.00}, {value.y:0.00}, {value.z:0.00})";
        }

        private static string FormatVectorPrecise(Vector3 value)
        {
            return $"({value.x:0.000}, {value.y:0.000}, {value.z:0.000})";
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            string path = transform.name;
            Transform parent = transform.parent;
            int depth = 0;
            while (parent != null && depth++ < 32)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        private static string GetPluginDirectory()
        {
            try
            {
                string location = typeof(SurveillanceBootstrap).Assembly.Location;
                return string.IsNullOrWhiteSpace(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }
    }
}
