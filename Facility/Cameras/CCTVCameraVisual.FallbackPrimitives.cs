using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Y4NGZCompany.Facility.Cameras.Placement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Shared;

namespace Y4NGZCompany.Facility.Cameras
{
    internal sealed partial class CCTVCameraVisual
    {
        private void BuildFallbackVisual(VisualSnap snap)
        {
            float scale = ResolveScale();
            float distance = Mathf.Max(0.12f, snap.Distance);

            GameObject plate = CreatePrimitivePart(
                "MountPlate", PrimitiveType.Cube, _visualRoot.transform,
                new Vector3(0f, 0f, 0.015f),
                new Vector3(0.36f, 0.24f, 0.03f) * scale,
                EnsureBodyMaterial());

            GameObject arm = CreatePrimitivePart(
                "MountArm", PrimitiveType.Cube, _visualRoot.transform,
                new Vector3(0f, 0f, Mathf.Min(distance * 0.5f, 0.20f)),
                new Vector3(0.07f, 0.07f, Mathf.Min(distance, 0.36f)) * scale,
                EnsureDarkMaterial());

            GameObject body = CreatePrimitivePart(
                "CameraBody", PrimitiveType.Cube, _aimPivot,
                new Vector3(0f, 0f, -0.14f * scale),
                new Vector3(0.24f, 0.16f, 0.20f) * scale,
                EnsureBodyMaterial());

            float hoodCenterY = (0.16f * 0.5f + 0.035f * 0.5f + 0.006f) * scale;
            GameObject hood = CreatePrimitivePart(
                "CameraHood", PrimitiveType.Cube, _aimPivot,
                new Vector3(0f, hoodCenterY, -0.06f * scale),
                new Vector3(0.30f, 0.035f, 0.22f) * scale,
                EnsureDarkMaterial());

            GameObject lens = CreatePrimitivePart(
                "LensRedDot", PrimitiveType.Sphere, _aimPivot,
                new Vector3(0f, 0f, 0.015f * scale),
                Vector3.one * 0.055f * scale,
                EnsureLensMaterial());
            CCTVCameraLensBlinker.Attach(lens, _holder);

            DisableShadows(plate);
            DisableShadows(arm);
            DisableShadows(body);
            DisableShadows(hood);
            DisableShadows(lens);
        }

        private static bool TryResolveSnap(CCTVCamera holder, out VisualSnap snap)
        {
            snap = default;
            Transform t = holder.transform;
            Vector3 viewPos = t.position;
            float maxDistance = ResolveMaxSnapOffset();
            PlacementMasks masks = ResolveMasks();
            if (!masks.Valid) return false;

            // Procedural placement already recorded the support normal that passed all
            // safety gates. Resolve that wall first and require an aligned normal; a broad
            // nearest-surface probe near a corner can otherwise snap half the visual onto
            // the adjacent wall even though the holder itself was placed correctly.
            Vector3 storedNormal = holder.PlacementSurfaceNormal;
            if (storedNormal.sqrMagnitude > 1e-6f)
            {
                storedNormal.Normalize();
                if (Physics.Raycast(
                        viewPos,
                        -storedNormal,
                        out RaycastHit storedHit,
                        maxDistance,
                        masks.MountMask,
                        QueryTriggerInteraction.Ignore)
                    && IsWallOrCeiling(storedHit.normal)
                    && Vector3.Dot(storedHit.normal.normalized, storedNormal) >= 0.85f)
                {
                    snap = CreateSnap(viewPos, t.rotation, storedHit.point, storedNormal, storedHit.distance, "stored-normal-raycast");
                    return true;
                }

                Vector3 supportPoint = viewPos - storedNormal * EstimatedSurfaceInsetM;
                snap = CreateSnap(viewPos, t.rotation, supportPoint, storedNormal, EstimatedSurfaceInsetM, "stored-normal");
                return true;
            }

            BuildProbeDirections(holder, t);

            bool found = false;
            RaycastHit bestHit = default;
            float bestScore = float.PositiveInfinity;

            for (int i = 0; i < s_probeDirs.Count; i++)
            {
                Vector3 dir = s_probeDirs[i];
                if (dir.sqrMagnitude < 1e-6f) continue;
                dir.Normalize();

                if (!Physics.Raycast(viewPos, dir, out RaycastHit hit, maxDistance, masks.MountMask, QueryTriggerInteraction.Ignore))
                    continue;
                if (!IsWallOrCeiling(hit.normal))
                    continue;

                float score = hit.distance;
                if (hit.normal.y > 0.45f) score += 10f;
                if (score >= bestScore) continue;

                bestScore = score;
                bestHit = hit;
                found = true;
            }

            if (found)
            {
                Vector3 outward = viewPos - bestHit.point;
                if (outward.sqrMagnitude < 1e-6f)
                    outward = -s_probeDirs[0];
                snap = CreateSnap(viewPos, t.rotation, bestHit.point, outward.normalized, bestHit.distance, "raycast");
                return true;
            }

            Vector3 fallbackOutward = ResolveFallbackOutward(holder, t);
            Vector3 fallbackSurfacePoint = viewPos - fallbackOutward * EstimatedSurfaceInsetM;
            snap = CreateSnap(
                viewPos,
                t.rotation,
                fallbackSurfacePoint,
                fallbackOutward,
                EstimatedSurfaceInsetM,
                "camera-forward-fallback");
            return true;
        }

        private static Vector3 ResolveFallbackOutward(CCTVCamera holder, Transform transform)
        {
            if (holder != null && holder.MountMode == CameraMountMode.Ceiling)
                return Vector3.down;

            Vector3 outward = transform != null ? transform.forward : Vector3.forward;
            if (outward.sqrMagnitude < 1e-6f)
                outward = Vector3.forward;
            return outward.normalized;
        }

        private static VisualSnap CreateSnap(
            Vector3 viewPos, Quaternion viewRot, Vector3 surfacePoint,
            Vector3 outward, float distance, string source)
        {
            if (outward.sqrMagnitude < 1e-6f)
                outward = -(viewRot * Vector3.forward);
            outward.Normalize();

            Vector3 up = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(outward, up)) > 0.88f)
            {
                up = Vector3.ProjectOnPlane(viewRot * Vector3.forward, outward);
                if (up.sqrMagnitude < 1e-6f)
                    up = Vector3.ProjectOnPlane(viewRot * Vector3.up, outward);
                if (up.sqrMagnitude < 1e-6f)
                    up = Vector3.forward;
            }

            Quaternion surfaceRotation = Quaternion.LookRotation(outward, up.normalized);
            return new VisualSnap(surfacePoint, surfaceRotation, distance, source);
        }

        private static void BuildProbeDirections(CCTVCamera holder, Transform t)
        {
            s_probeDirs.Clear();

            Vector3 normal = holder.PlacementSurfaceNormal;
            if (normal.sqrMagnitude > 1e-6f)
            {
                normal.Normalize();
                s_probeDirs.Add(-normal);
                s_probeDirs.Add(normal);
            }

            Vector3 forward = t.rotation * Vector3.forward;
            Vector3 right = t.rotation * Vector3.right;
            Vector3 up = t.rotation * Vector3.up;
            s_probeDirs.Add(-forward);
            s_probeDirs.Add(forward);
            s_probeDirs.Add(-right);
            s_probeDirs.Add(right);
            s_probeDirs.Add(-up);
            s_probeDirs.Add(up);
            s_probeDirs.Add(Vector3.up);
            s_probeDirs.Add(Vector3.down);
            s_probeDirs.Add(Vector3.left);
            s_probeDirs.Add(Vector3.right);
            s_probeDirs.Add(Vector3.forward);
            s_probeDirs.Add(Vector3.back);
        }

        private static bool IsWallOrCeiling(Vector3 normal)
        {
            if (normal.y < -0.45f) return true;
            if (Mathf.Abs(normal.y) < 0.65f) return true;
            return false;
        }

        private static PlacementMasks ResolveMasks()
        {
            if (_cachedMasksResolved) return _cachedMasks;
            _cachedMasks = PlacementMask.Resolve(verboseLog: false);
            _cachedMasksResolved = true;
            return _cachedMasks;
        }

        private static GameObject CreatePrimitivePart(
            string name, PrimitiveType primitive, Transform parent,
            Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(primitive);
            go.name = name;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = localScale;

            Collider collider = go.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            return go;
        }

        private void NormalizeImportedVisualPlacement(GameObject instance, float scale)
        {
            if (instance == null || _visualRoot == null) return;

            if (!TryComputeLocalRendererBounds(instance, _visualRoot.transform, out Bounds localBounds))
                return;

            float currentMax = Mathf.Max(localBounds.size.x, Mathf.Max(localBounds.size.y, localBounds.size.z));
            float targetMax = TargetImportedCameraMaxDimensionM * Mathf.Max(0.05f, scale);
            if (currentMax > 0.001f && Mathf.Abs(currentMax - targetMax) > 0.01f)
            {
                float factor = targetMax / currentMax;
                instance.transform.localScale *= factor;
                if (!TryComputeLocalRendererBounds(instance, _visualRoot.transform, out localBounds))
                    return;
            }

            Vector3 localPosition = instance.transform.localPosition;
            localPosition.z += SurfaceClearanceM - localBounds.min.z;
            instance.transform.localPosition = localPosition;
        }

        // Shared by the bundled auto-scale/seat pass and CctvBreakableCamera's hitbox
        // sizing. Renderers that are disabled, on an inactive GameObject, or that report
        // a degenerate (zero-extent) box are skipped: SuppressBundledFloatingDotRing
        // deactivates the "rotatingcamera_plane" dot ring, and an inactive renderer can
        // report a zero-size box sitting at the world origin, which would drag the union
        // tens of metres away from the prop.
        //
        // The per-renderer 8-corner fold is deliberate rather than "union the world AABBs
        // then fold once": the union-first form is strictly more conservative under
        // rotation and would change the bundled auto-scale factor this helper already
        // feeds. Renderer counts here are single digits.
        internal static bool TryComputeLocalRendererBounds(GameObject root, Transform localRoot, out Bounds bounds)
        {
            bounds = default;
            if (root == null || localRoot == null) return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            bool found = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                if (renderer.gameObject == null || !renderer.gameObject.activeInHierarchy) continue;

                Bounds worldBounds = renderer.bounds;
                if (worldBounds.extents.sqrMagnitude <= 1e-8f) continue;

                Vector3 center = worldBounds.center;
                Vector3 extents = worldBounds.extents;
                for (int x = -1; x <= 1; x += 2)
                {
                    for (int y = -1; y <= 1; y += 2)
                    {
                        for (int z = -1; z <= 1; z += 2)
                        {
                            Vector3 worldPoint = center + Vector3.Scale(extents, new Vector3(x, y, z));
                            Vector3 localPoint = localRoot.InverseTransformPoint(worldPoint);
                            if (!found)
                            {
                                bounds = new Bounds(localPoint, Vector3.zero);
                                found = true;
                            }
                            else
                            {
                                bounds.Encapsulate(localPoint);
                            }
                        }
                    }
                }
            }

            return found;
        }
        private static void CountRenderers(GameObject root, out int rendererCount, out int enabledRenderers, out int activeRenderers)
        {
            rendererCount = 0;
            enabledRenderers = 0;
            activeRenderers = 0;
            if (root == null) return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                rendererCount++;
                if (renderer.enabled) enabledRenderers++;
                if (renderer.gameObject != null && renderer.gameObject.activeInHierarchy) activeRenderers++;
            }
        }

        private static void SetImportedVisualActiveAndRenderable(GameObject instance)
        {
            if (instance == null) return;

            Transform[] transforms = instance.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform transform = transforms[i];
                if (transform != null && transform.gameObject != null)
                    transform.gameObject.SetActive(true);
            }

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                renderer.enabled = true;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }
        }
        private static void NormalizeImportedVisual(GameObject instance)
        {
            if (instance == null) return;

            Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null) Object.Destroy(colliders[i]);
            }

            Light[] lights = instance.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null) lights[i].enabled = false;
            }

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                renderer.enabled = true;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }

            RepairImportedMaterials(instance);
        }

        private static bool IsBundledHeadRenderer(Renderer renderer, float largestVolume)
        {
            if (renderer == null) return false;

            string descriptor = BuildDescriptor(renderer);
            if (HasPathSegment(renderer, "base") || HasPathSegment(renderer, "stem"))
                return false;

            if (HasPathSegment(renderer, "camera") ||
                ContainsAny(descriptor, "camerajoint", "cameraendjoint", "camera_map", "lens", "hood", "housing", "case"))
                return true;

            return largestVolume > 0.0001f && EstimateRendererVolume(renderer) >= largestVolume * 0.35f;
        }

        private static string BuildDescriptor(Renderer renderer)
        {
            if (renderer == null) return string.Empty;

            string descriptor = BuildHierarchyPath(renderer.transform);
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null)
                    descriptor += "/" + material.name;
            }

            return descriptor.ToLowerInvariant();
        }

        private static string BuildHierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;

            var parts = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                string name = current.name ?? string.Empty;
                if (string.Equals(name, "SecurityCameraPrefab", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SecurityCameraPrefab_", StringComparison.OrdinalIgnoreCase))
                    break;

                parts.Push(name);
                current = current.parent;
            }

            return string.Join("/", parts.ToArray());
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < needles.Length; i++)
            {
                string needle = needles[i];
                if (!string.IsNullOrEmpty(needle) && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool HasPathSegment(Renderer renderer, string segment)
        {
            return renderer != null && HasPathSegment(BuildHierarchyPath(renderer.transform), segment);
        }

        private static bool HasPathSegment(string path, string segment)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(segment)) return false;

            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static float EstimateRendererVolume(Renderer renderer)
        {
            if (renderer == null) return 0f;
            Vector3 size = renderer.bounds.size;
            return Mathf.Abs(size.x * size.y * size.z);
        }

        private readonly struct VisualSnap
        {
            public readonly Vector3 SurfacePoint;
            public readonly Quaternion SurfaceRotation;
            public readonly float Distance;
            public readonly string Source;

            public VisualSnap(Vector3 surfacePoint, Quaternion surfaceRotation, float distance, string source)
            {
                SurfacePoint = surfacePoint;
                SurfaceRotation = surfaceRotation;
                Distance = distance;
                Source = source;
            }
        }
    }
}
