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
        private static Transform ResolveThrottleTransform()
        {
            if (_throttleTransform != null)
                return _throttleTransform;

            try
            {
                StartMatchLever lever = UnityEngine.Object.FindObjectOfType<StartMatchLever>();
                _throttleTransform = ResolveThrottleAssemblyTransform(lever);
            }
            catch
            {
                _throttleTransform = null;
            }

            return _throttleTransform;
        }

        private static Transform ResolveThrottleAssemblyTransform(StartMatchLever lever)
        {
            if (lever == null)
                return null;

            List<Transform> important = new List<Transform>(4);
            AddTransformIfValid(important, lever.transform);
            AddTransformIfValid(important, lever.triggerScript != null ? lever.triggerScript.transform : null);
            AddTransformIfValid(important, lever.triggerScript != null ? lever.triggerScript.playerPositionNode : null);
            AddTransformIfValid(important, lever.leverAnimatorObject != null ? lever.leverAnimatorObject.transform : null);

            Transform primary;
            string reason;
            Transform common = FindDeepestCommonAncestor(important);
            if (IsSafeThrottleAssemblyRoot(common))
            {
                primary = common;
                reason = "common";
            }
            else
            {
                Transform named = FindNamedThrottleAncestor(lever.leverAnimatorObject != null ? lever.leverAnimatorObject.transform : null);
                if (named == null)
                    named = FindNamedThrottleAncestor(lever.triggerScript != null ? lever.triggerScript.transform : null);
                if (named == null)
                    named = FindNamedThrottleAncestor(lever.transform);

                if (named != null)
                {
                    primary = named;
                    reason = "named-ancestor";
                }
                else
                {
                    primary = lever.transform;
                    reason = "script-fallback";
                }
            }

            LogThrottleAssemblyResolved(primary, reason);
            CollectThrottleSecondaryTargets(important, primary);
            CollectNearbyThrottleSecondaryTargets(lever, primary);
            DumpThrottleAssemblyHierarchy(lever, primary, reason);
            return primary;
        }

        // Any important lever piece outside the primary subtree must be carried along
        // by SyncThrottleSecondaryTargetsToPrimary, or the previous-pass bug returns
        // (interact trigger relocated, visible handle left behind).
        private static void CollectThrottleSecondaryTargets(List<Transform> important, Transform primary)
        {
            _throttleSecondaryTargets.Clear();
            _throttleSecondaryDefaultWorld.Clear();
            _throttleSecondaryDefaultWorldRotations.Clear();
            _throttleSecondaryDefaultLocalScales.Clear();
            if (important == null || primary == null)
                return;

            for (int i = 0; i < important.Count; i++)
            {
                Transform candidate = important[i];
                if (candidate == null || candidate == primary)
                    continue;
                if (IsAncestorOf(primary, candidate))
                    continue;
                if (IsAncestorOf(candidate, primary))
                    continue;

                bool covered = false;
                for (int j = 0; j < _throttleSecondaryTargets.Count; j++)
                {
                    if (_throttleSecondaryTargets[j] == candidate ||
                        IsAncestorOf(_throttleSecondaryTargets[j], candidate))
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered)
                    continue;

                AddThrottleSecondarySnapshot(candidate);
            }

            if (_throttleSecondaryTargets.Count > 0)
            {
                for (int i = 0; i < _throttleSecondaryTargets.Count; i++)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] Start lever secondary move target [{i}]: {GetPath(_throttleSecondaryTargets[i])} " +
                        $"world={FormatVector(_throttleSecondaryDefaultWorld[i])}");
                }
            }
        }

        private static void CollectNearbyThrottleSecondaryTargets(StartMatchLever lever, Transform primary)
        {
            if (lever == null || primary == null)
                return;

            Vector3 anchor = lever.leverAnimatorObject != null
                ? lever.leverAnimatorObject.transform.position
                : lever.transform.position;

            Transform[] transforms;
            try
            {
                transforms = Resources.FindObjectsOfTypeAll<Transform>();
            }
            catch
            {
                return;
            }

            int startCount = _throttleSecondaryTargets.Count;
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.scene.IsValid())
                    continue;

                string lower = candidate.name != null ? candidate.name.ToLowerInvariant() : string.Empty;
                if (lower.IndexOf("lever", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("brake", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("startgame", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("gearstick", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("gear stick", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("gear", StringComparison.Ordinal) < 0 &&
                    lower.IndexOf("throttle", StringComparison.Ordinal) < 0)
                    continue;

                if ((candidate.position - anchor).sqrMagnitude > ThrottleNearbyTransformRadius * ThrottleNearbyTransformRadius)
                    continue;

                string path = GetPath(candidate);
                if (path == null || path.IndexOf("Environment/HangarShip", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                AddThrottleSecondaryTarget(candidate, primary);
            }

            if (_throttleSecondaryTargets.Count > startCount)
            {
                for (int i = startCount; i < _throttleSecondaryTargets.Count; i++)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Start lever nearby move target [{i}]: {GetPath(_throttleSecondaryTargets[i])} " +
                        $"world={FormatVector(_throttleSecondaryDefaultWorld[i])}");
                }
            }

            CollectNearbyThrottleRendererTargets(primary, anchor);
        }

        private static void CollectNearbyThrottleRendererTargets(Transform primary, Vector3 anchor)
        {
            if (primary == null)
                return;

            Renderer[] renderers;
            try
            {
                renderers = Resources.FindObjectsOfTypeAll<Renderer>();
            }
            catch
            {
                return;
            }

            bool dumpCandidates = !_throttleRendererCandidatesDumped;
            _throttleRendererCandidatesDumped = true;
            if (dumpCandidates)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] --- start lever renderer candidates within {ThrottleRendererCandidateDumpRadius:F2}m of " +
                    $"anchor={FormatVector(anchor)} (one-shot) ---");
            }

            int startCount = _throttleSecondaryTargets.Count;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.gameObject == null || !renderer.gameObject.scene.IsValid())
                    continue;

                Transform candidate = renderer.transform;
                if (candidate == null)
                    continue;

                Bounds bounds;
                try { bounds = renderer.bounds; }
                catch { continue; }

                float centerDistance = Vector3.Distance(bounds.center, anchor);
                float closestDistance = Vector3.Distance(bounds.ClosestPoint(anchor), anchor);
                if (closestDistance > ThrottleRendererCandidateDumpRadius)
                    continue;

                string path = GetPath(candidate);
                Vector3 size = bounds.size;
                float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));

                // Verdict order matters only for the log label; the first failing
                // check names why a casing-like mesh was left behind.
                string verdict;
                bool include = false;
                if (candidate == primary || IsAncestorOf(primary, candidate))
                    verdict = "already-in-primary-subtree";
                else if (IsAncestorOf(candidate, primary))
                    verdict = "excluded: ancestor-of-primary";
                else if (path == null || path.IndexOf("Environment/HangarShip", StringComparison.OrdinalIgnoreCase) < 0)
                    verdict = "excluded: outside-HangarShip";
                else if (IsUnsafeNearbyThrottleRendererPath(path))
                    verdict = "excluded: unsafe-path-token";
                else if (maxAxis <= 0.01f)
                    verdict = "excluded: degenerate-bounds";
                else if (IsLikelyThrottleCasingRenderer(path, bounds, anchor, maxAxis, closestDistance))
                {
                    verdict = "INCLUDED: throttle-casing";
                    include = true;
                }
                else if (maxAxis > ThrottleNearbyRendererMaxAxis)
                    verdict = $"excluded: too-large maxAxis={maxAxis:F2}";
                else if (Mathf.Abs(bounds.center.y - anchor.y) > ThrottleNearbyRendererMaxVerticalDelta)
                    verdict = $"excluded: vertical-delta={Mathf.Abs(bounds.center.y - anchor.y):F2}";
                else if (centerDistance > ThrottleNearbyRendererCenterRadius && closestDistance > ThrottleNearbyRendererClosestRadius)
                    verdict = $"excluded: too-far center={centerDistance:F2} closest={closestDistance:F2}";
                else
                {
                    verdict = "INCLUDED";
                    include = true;
                }

                if (dumpCandidates)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV]   candidate {verdict}: {path} world={FormatVector(candidate.position)} " +
                        $"boundsCenter={FormatVector(bounds.center)} boundsSize={FormatVector(size)} " +
                        $"center={centerDistance:F2}m closest={closestDistance:F2}m");
                }

                if (include)
                    AddThrottleSecondaryTarget(candidate, primary);
            }

            if (dumpCandidates)
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] --- end start lever renderer candidates ---");

            if (_throttleSecondaryTargets.Count > startCount)
            {
                for (int i = startCount; i < _throttleSecondaryTargets.Count; i++)
                {
                    Transform target = _throttleSecondaryTargets[i];
                    if (target == null)
                        continue;

                    string boundsText = TryGetRendererBounds(target, out Bounds bounds)
                        ? $" boundsCenter={FormatVector(bounds.center)} boundsSize={FormatVector(bounds.size)}"
                        : string.Empty;
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] Start lever nearby renderer move target [{i}]: {GetPath(target)} " +
                        $"world={FormatVector(_throttleSecondaryDefaultWorld[i])}{boundsText}");
                }
            }
        }

        private static bool IsLikelyThrottleCasingRenderer(
            string path,
            Bounds bounds,
            Vector3 anchor,
            float maxAxis,
            float closestDistance)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // The 2026-06-12 run showed the red-circled plastic/metal throttle base
            // as Environment/HangarShip/.../ControlPanelWTexture. It was rejected
            // only because its max axis was 1.56m, slightly larger than the generic
            // nearby-renderer safety cap. Keep this exception narrow: it must be in
            // the ship, close enough that the lever anchor touches it, and low enough
            // to be the desk-mounted casing rather than a whole wall/desk chunk.
            if (path.IndexOf("Environment/HangarShip", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (path.IndexOf("ControlPanelWTexture", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (maxAxis > 1.85f)
                return false;
            if (closestDistance > 0.10f)
                return false;
            if (Mathf.Abs(bounds.center.y - anchor.y) > 0.55f)
                return false;

            return true;
        }

        private static bool IsUnsafeNearbyThrottleRendererPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            string lower = path.ToLowerInvariant();
            return lower.IndexOf("monitorwall", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("monitor wall", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("cctv", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("terminal", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("keyboard", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("phone", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("clipboard", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("battery", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("screen", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("button", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("light", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("poster", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("desk/table", StringComparison.Ordinal) >= 0;
        }

        private static void AddThrottleSecondaryTarget(Transform candidate, Transform primary)
        {
            if (candidate == null || primary == null || candidate == primary)
                return;
            if (IsAncestorOf(primary, candidate) || IsAncestorOf(candidate, primary))
                return;

            for (int i = _throttleSecondaryTargets.Count - 1; i >= 0; i--)
            {
                Transform existing = _throttleSecondaryTargets[i];
                if (existing == null)
                {
                    _throttleSecondaryTargets.RemoveAt(i);
                    _throttleSecondaryDefaultWorld.RemoveAt(i);
                    _throttleSecondaryDefaultWorldRotations.RemoveAt(i);
                    _throttleSecondaryDefaultLocalScales.RemoveAt(i);
                    continue;
                }

                if (existing == candidate || IsAncestorOf(existing, candidate))
                    return;

                if (IsAncestorOf(candidate, existing))
                {
                    _throttleSecondaryTargets.RemoveAt(i);
                    _throttleSecondaryDefaultWorld.RemoveAt(i);
                    _throttleSecondaryDefaultWorldRotations.RemoveAt(i);
                    _throttleSecondaryDefaultLocalScales.RemoveAt(i);
                }
            }

            AddThrottleSecondarySnapshot(candidate);
        }

        private static void AddThrottleSecondarySnapshot(Transform candidate)
        {
            if (candidate == null)
                return;

            _throttleSecondaryTargets.Add(candidate);
            _throttleSecondaryDefaultWorld.Add(candidate.position);
            _throttleSecondaryDefaultWorldRotations.Add(candidate.rotation);
            _throttleSecondaryDefaultLocalScales.Add(candidate.localScale);
        }

        // One-shot diagnostic so a log dump can verify exactly which transforms the
        // relocation touches and what lives under the chosen primary.
        private static void DumpThrottleAssemblyHierarchy(StartMatchLever lever, Transform primary, string reason)
        {
            if (_throttleHierarchyDumped || lever == null || primary == null)
                return;
            _throttleHierarchyDumped = true;

            try
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] --- start lever assembly dump (one-shot) ---");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   primary ({reason}): {GetPath(primary)}");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   lever script: {GetPath(lever.transform)}");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   trigger: {(lever.triggerScript != null ? GetPath(lever.triggerScript.transform) : "<null>")}");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   playerPositionNode: {(lever.triggerScript != null && lever.triggerScript.playerPositionNode != null ? GetPath(lever.triggerScript.playerPositionNode) : "<null>")}");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   leverAnimatorObject: {(lever.leverAnimatorObject != null ? GetPath(lever.leverAnimatorObject.transform) : "<null>")}");
                for (int i = 0; i < primary.childCount; i++)
                {
                    Transform child = primary.GetChild(i);
                    if (child == null)
                        continue;
                    bool hasRenderer = child.GetComponentInChildren<Renderer>(includeInactive: true) != null;
                    SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   primary child[{i}]: {child.name} renderers={hasRenderer}");
                }
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   placement group ({1 + _throttleSecondaryTargets.Count} member(s)):");
                LogThrottleGroupMember("primary", primary);
                for (int i = 0; i < _throttleSecondaryTargets.Count; i++)
                    LogThrottleGroupMember($"secondary[{i}]", _throttleSecondaryTargets[i]);
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] --- end start lever assembly dump ---");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Lever assembly dump failed: {ex.Message}");
            }
        }

        private static void LogThrottleGroupMember(string role, Transform member)
        {
            if (member == null)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]     {role}: <null>");
                return;
            }

            string boundsText = TryGetRendererBounds(member, out Bounds bounds)
                ? $" boundsCenter={FormatVector(bounds.center)} boundsSize={FormatVector(bounds.size)}"
                : " (no renderers)";
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV]     {role}: {GetPath(member)} world={FormatVector(member.position)}{boundsText}");
        }

        private static void AddTransformIfValid(List<Transform> transforms, Transform transform)
        {
            if (transforms == null || transform == null)
                return;
            if (!transforms.Contains(transform))
                transforms.Add(transform);
        }

        private static Transform FindDeepestCommonAncestor(List<Transform> transforms)
        {
            if (transforms == null || transforms.Count == 0)
                return null;

            Transform candidate = transforms[0];
            while (candidate != null)
            {
                bool containsAll = true;
                for (int i = 1; i < transforms.Count; i++)
                {
                    if (!IsAncestorOf(candidate, transforms[i]))
                    {
                        containsAll = false;
                        break;
                    }
                }

                if (containsAll)
                    return candidate;

                candidate = candidate.parent;
            }

            return null;
        }

        private static bool IsAncestorOf(Transform ancestor, Transform child)
        {
            if (ancestor == null || child == null)
                return false;

            Transform current = child;
            while (current != null)
            {
                if (current == ancestor)
                    return true;
                current = current.parent;
            }

            return false;
        }

        private static bool IsSafeThrottleAssemblyRoot(Transform transform)
        {
            if (transform == null)
                return false;
            if (IsUnsafeThrottleRootName(transform.name))
                return false;

            if (NameLooksLikeThrottleAssembly(transform.name))
                return true;

            Renderer[] renderers = transform.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers != null && renderers.Length > 24)
                return false;

            if (TryGetRendererBounds(transform, out Bounds bounds))
            {
                Vector3 size = bounds.size;
                if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > 4.0f)
                    return false;
            }

            return true;
        }

        private static Transform FindNamedThrottleAncestor(Transform start)
        {
            Transform current = start;
            while (current != null)
            {
                if (IsUnsafeThrottleRootName(current.name))
                    return null;
                if (NameLooksLikeThrottleAssembly(current.name))
                    return current;
                current = current.parent;
            }

            return null;
        }

        private static bool NameLooksLikeThrottleAssembly(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string lower = name.ToLowerInvariant();
            return lower.IndexOf("start", StringComparison.Ordinal) >= 0 && lower.IndexOf("lever", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("brake", StringComparison.Ordinal) >= 0 && lower.IndexOf("lever", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("gearstick", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("gear stick", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("throttle", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("matchlever", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("startmatchlever", StringComparison.Ordinal) >= 0;
        }

        private static bool IsUnsafeThrottleRootName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string lower = name.ToLowerInvariant();
            return lower == "environment" ||
                   lower == "hangarship" ||
                   lower == "shipmodels2b" ||
                   lower == "monitorswall" ||
                   lower == "monitorwall" ||
                   lower == "systems" ||
                   lower == "gamesystems" ||
                   lower.IndexOf("shipinside", StringComparison.Ordinal) >= 0;
        }

        private static bool TryGetRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
                return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            bool initialized = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                if (!initialized)
                {
                    bounds = renderer.bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return initialized;
        }

        private static void LogThrottleAssemblyResolved(Transform transform, string reason)
        {
            if (transform == null)
                return;

            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Start lever placement target resolved via {reason}: {GetPath(transform)}");
        }
    }
}
