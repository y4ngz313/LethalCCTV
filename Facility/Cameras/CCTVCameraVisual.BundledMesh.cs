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
        private void BuildBundledVisual(GameObject prefab)
        {
            float scale = ResolveScale();

            GameObject instance = Object.Instantiate(prefab, _visualRoot.transform);
            instance.name = "SecurityCameraPrefab";
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one * scale;
            SetImportedVisualActiveAndRenderable(instance);
            NormalizeImportedVisual(instance);
            SuppressBundledFloatingDotRing(instance);
            NormalizeImportedVisualPlacement(instance, scale);
            bool movedHead = TryReparentBundledRotatingParts(instance);
            if (movedHead && _bundledRotatingHead != null && _holder != null)
                s_rotatingHeadsByHolder[_holder] = _bundledRotatingHead;
            AddBundledLensDot(movedHead && _bundledRotatingHead != null ? _bundledRotatingHead.gameObject : instance, scale);
        }

        internal static Transform TryGetRotatingHead(CCTVCamera holder)
        {
            if (holder == null)
                return null;
            if (s_rotatingHeadsByHolder.TryGetValue(holder, out Transform head) && head != null)
                return head;

            // The procedural fallback camera has no imported rotating-head node;
            // its AimPivot is the equivalent pitchable camera head.
            CCTVCameraVisual visual = holder.GetComponent<CCTVCameraVisual>();
            return visual != null ? visual._aimPivot : null;
        }

        private bool TryReparentBundledRotatingParts(GameObject instance)
        {
            if (instance == null || _aimPivot == null) return false;

            Transform namedRotatingRoot = FindBundledNamedRotatingRoot(instance.transform);
            if (namedRotatingRoot != null)
            {
                LockAimPivotToCurrentPosition(namedRotatingRoot);
                namedRotatingRoot.SetParent(_aimPivot, worldPositionStays: true);
                _bundledRotatingHead = namedRotatingRoot;
                return true;
            }

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return false;

            float largestVolume = 0f;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                largestVolume = Mathf.Max(largestVolume, EstimateRendererVolume(renderer));
            }

            var moveRoots = new List<Transform>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !IsBundledHeadRenderer(renderer, largestVolume)) continue;

                Transform moveRoot = FindBundledHeadMoveRoot(renderer.transform, instance.transform);
                AddBundledMoveRoot(moveRoots, moveRoot, instance.transform);
            }

            if (moveRoots.Count == 0) return false;

            for (int i = 0; i < moveRoots.Count; i++)
            {
                Transform root = moveRoots[i];
                if (root == null) continue;
                if (!_aimPivotPositionLocked) LockAimPivotToCurrentPosition(root);
                root.SetParent(_aimPivot, worldPositionStays: true);
                if (_bundledRotatingHead == null) _bundledRotatingHead = root;
            }

            return true;
        }

        private static Transform FindBundledNamedRotatingRoot(Transform root)
        {
            if (root == null) return null;

            Transform[] children = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || child == root) continue;
                string name = child.name ?? string.Empty;
                if (name.IndexOf("StaticMount", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("RotatingCamera", StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;
            }

            return null;
        }

        private static void AddBundledMoveRoot(List<Transform> moveRoots, Transform candidate, Transform prefabRoot)
        {
            if (moveRoots == null || candidate == null || candidate == prefabRoot) return;

            for (int i = moveRoots.Count - 1; i >= 0; i--)
            {
                Transform existing = moveRoots[i];
                if (existing == null)
                {
                    moveRoots.RemoveAt(i);
                    continue;
                }

                if (existing == candidate || IsAncestorOf(existing, candidate))
                    return;
                if (IsAncestorOf(candidate, existing))
                    moveRoots.RemoveAt(i);
            }

            moveRoots.Add(candidate);
        }

        private static bool IsAncestorOf(Transform possibleAncestor, Transform transform)
        {
            if (possibleAncestor == null || transform == null) return false;

            Transform current = transform.parent;
            while (current != null)
            {
                if (current == possibleAncestor)
                    return true;
                current = current.parent;
            }

            return false;
        }

        private static Transform FindBundledHeadMoveRoot(Transform rendererTransform, Transform prefabRoot)
        {
            if (rendererTransform == null || prefabRoot == null || rendererTransform == prefabRoot) return null;

            Transform current = rendererTransform;
            Transform best = null;
            while (current != null && current != prefabRoot)
            {
                if (IsBundledHeadTransformName(current.name))
                    best = current;
                current = current.parent;
            }

            return best != null && best != prefabRoot ? best : rendererTransform;
        }

        private static bool IsBundledHeadTransformName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string normalized = name.Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(".", string.Empty)
                .ToLowerInvariant();
            return normalized.Contains("camera") ||
                   normalized.Contains("rotatingcamera") ||
                   normalized.Contains("lens") ||
                   normalized.Contains("hood") ||
                   normalized.Contains("housing") ||
                   normalized.Contains("case");
        }

        private void AddBundledLensDot(GameObject rotatingHead, float scale)
        {
            if (rotatingHead == null || _holder == null || _holder.transform == null) return;
            if (rotatingHead.transform.Find("BundledLensRedDot") != null) return;

            // Descend into the RotatingCamera subtree if we were handed the
            // whole prefab (reparent may have failed): searching the whole
            // instance lets a larger housing sphere win the dome pick, which
            // floated the dot off the camera.
            Transform searchRoot = rotatingHead.transform;
            if (searchRoot.name.IndexOf("RotatingCamera", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Transform named = FindBundledNamedRotatingRoot(searchRoot);
                if (named != null) searchRoot = named;
            }

            // Anchor the dot to the dome sphere renderer's OWN transform so it
            // is rigidly parented to the dome and can never float relative to
            // it. Skip the dot entirely rather than place a floating one.
            Renderer dome = FindBundledDomeRenderer(searchRoot);
            if (dome == null)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][CameraVisual] lens dot skipped {Describe(_holder)}: no dome sphere under '{searchRoot.name}'.");
                return;
            }

            Transform anchor = dome.transform;
            // The split FBX bakes the lens tilt into the rotating group's local
            // +Z, so its forward is the true lens direction.
            Vector3 forward = searchRoot.forward;
            if (forward.sqrMagnitude < 0.001f)
                forward = _neutralAimForward.sqrMagnitude > 0.001f ? _neutralAimForward : Vector3.forward;
            forward.Normalize();

            // Support of the dome treated as an ellipsoid (not its world AABB:
            // for a rotated sphere the AABB support over-extends by up to
            // √3×radius, which visibly floated the dot off the dome surface).
            Bounds headBounds = dome.bounds;
            Bounds domeLocal = dome.localBounds;
            Vector3 lossy = anchor.lossyScale;
            Vector3 fLocal = anchor.InverseTransformDirection(forward);
            if (fLocal.sqrMagnitude < 1e-6f) fLocal = Vector3.forward;
            fLocal.Normalize();
            float ax = domeLocal.extents.x * Mathf.Abs(lossy.x) * fLocal.x;
            float ay = domeLocal.extents.y * Mathf.Abs(lossy.y) * fLocal.y;
            float az = domeLocal.extents.z * Mathf.Abs(lossy.z) * fLocal.z;
            float forwardExtent = Mathf.Sqrt(ax * ax + ay * ay + az * az);
            Vector3 domeCenterW = anchor.TransformPoint(domeLocal.center);
            float maxDimension = Mathf.Max(headBounds.size.x, Mathf.Max(headBounds.size.y, headBounds.size.z));
            float dotDiameter = Mathf.Clamp(0.12f * maxDimension, Mathf.Max(0.012f, 0.015f * scale), 0.06f);

            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "BundledLensRedDot";
            dot.transform.SetParent(anchor, worldPositionStays: true);
            dot.transform.position = domeCenterW + forward * (forwardExtent + 0.008f);
            dot.transform.rotation = Quaternion.LookRotation(forward, Mathf.Abs(forward.y) > 0.9f ? Vector3.forward : Vector3.up);
            Vector3 parentLossy = anchor.lossyScale;
            float parentScale = Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(parentLossy.x), Mathf.Max(Mathf.Abs(parentLossy.y), Mathf.Abs(parentLossy.z))));
            dot.transform.localScale = Vector3.one * (dotDiameter / parentScale);

            Collider collider = dot.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            Renderer renderer = dot.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = EnsureLensMaterial();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            CCTVCameraLensBlinker.Attach(dot, _holder);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][CameraVisual] lens dot {Describe(_holder)} anchored to '{anchor.name}' domeCenter=({domeCenterW.x:F2},{domeCenterW.y:F2},{domeCenterW.z:F2}) fwdExtent={forwardExtent:F3} fwd=({forward.x:F2},{forward.y:F2},{forward.z:F2}).");
        }

        // Largest sphere-named renderer under the rotating subtree — the dome
        // ball (Sphere_002); the lens cylinder and suppressed dot-ring are not
        // spheres, and the housing spheres live under StaticMount, not here.
        private static Renderer FindBundledDomeRenderer(Transform rotatingRoot)
        {
            if (rotatingRoot == null) return null;
            Renderer[] renderers = rotatingRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
            Renderer best = null;
            float bestVolume = 0f;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer.gameObject == null || !renderer.gameObject.activeInHierarchy)
                    continue;
                if ((renderer.name ?? string.Empty).IndexOf("sphere", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                float volume = EstimateRendererVolume(renderer);
                if (best == null || volume > bestVolume)
                {
                    best = renderer;
                    bestVolume = volume;
                }
            }
            return best;
        }

        private void LockAimPivotToCurrentPosition(Transform pivotSource)
        {
            if (_aimPivot == null || pivotSource == null) return;

            // Pivot about the dome ball's center: the split model is a ball-and-
            // socket joint (spherical StaticMount housing around the spherical
            // RotatingCamera dome), so ONLY rotations about the ball center keep
            // the head seated in the housing. The whole rotating group's bounds
            // center is pushed forward by the lens cylinder — rotating about it
            // translates the dome out of its socket (the "disassembled" bug).
            if (TryFindBundledDomeBounds(pivotSource, out Bounds domeBounds))
                _aimPivot.position = domeBounds.center;
            else if (TryComputeWorldRendererBounds(pivotSource, out Bounds headBounds))
                _aimPivot.position = headBounds.center;
            else
                _aimPivot.position = pivotSource.position;
            _neutralAimForward = _visualRoot != null ? _visualRoot.transform.forward : _aimPivot.forward;
            _neutralPivotRotation = _aimPivot.rotation;
            _aimPivotPositionLocked = true;
        }

        // The dome ball is the largest sphere-named renderer under the rotating
        // group (RotatingCamera_Sphere_002 in the split FBX); the lens cylinder
        // and the suppressed dot-ring plane are deliberately excluded.
        private static bool TryFindBundledDomeBounds(Transform rotatingRoot, out Bounds domeBounds)
        {
            domeBounds = default;
            if (rotatingRoot == null) return false;

            Renderer[] renderers = rotatingRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
            Renderer best = null;
            float bestVolume = 0f;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer.gameObject == null || !renderer.gameObject.activeInHierarchy)
                    continue;
                string name = renderer.name ?? string.Empty;
                if (name.IndexOf("sphere", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                float volume = EstimateRendererVolume(renderer);
                if (best == null || volume > bestVolume)
                {
                    best = renderer;
                    bestVolume = volume;
                }
            }

            if (best == null) return false;
            domeBounds = best.bounds;
            return true;
        }

        private Quaternion ComputeLockedAimRotation(Vector3 targetForward)
        {
            Vector3 neutral = _neutralAimForward;
            if (neutral.sqrMagnitude < 1e-6f || targetForward.sqrMagnitude < 1e-6f)
                return _neutralPivotRotation;

            neutral.Normalize();
            targetForward.Normalize();
            Quaternion delta;
            if (Vector3.Dot(neutral, targetForward) < -0.999f)
            {
                Vector3 axis = Vector3.Cross(neutral, Vector3.up);
                if (axis.sqrMagnitude < 1e-6f)
                    axis = Vector3.Cross(neutral, Vector3.right);
                delta = Quaternion.AngleAxis(180f, axis.normalized);
            }
            else
            {
                // Rotate away from the assembled rest pose by exactly the
                // neutral->aim delta; identity delta reproduces the authored
                // assembled model.
                delta = Quaternion.FromToRotation(neutral, targetForward);
            }

            // The housing only exposes the dome's central cone: past ~30 degrees
            // the lens visually collides with the mount rim. The FEED camera may
            // aim wherever it likes; the physical prop saturates at the gimbal
            // limit like a real dome camera hitting its stops.
            delta = Quaternion.RotateTowards(Quaternion.identity, delta, MaxBundledAimDeltaDeg);
            return delta * _neutralPivotRotation;
        }

        private static bool TryComputeWorldRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            if (root == null) return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            bool found = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer.gameObject == null || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return found;
        }

        private static void SuppressBundledFloatingDotRing(GameObject instance)
        {
            if (instance == null) return;

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                string descriptor = BuildDescriptor(renderer);
                if (descriptor.IndexOf("rotatingcamera_plane", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                renderer.enabled = false;
                if (renderer.gameObject != null)
                    renderer.gameObject.SetActive(false);
            }
        }
        private void RegisterPhysicalCameraRenderers(GameObject root)
        {
            UnregisterPhysicalCameraRenderers();
            if (root == null) return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                _registeredPhysicalCameraRenderers.Add(renderer);
                if (s_physicalCameraRendererSet.Add(renderer))
                    s_physicalCameraRenderers.Add(renderer);
            }
        }

        private void UnregisterPhysicalCameraRenderers()
        {
            for (int i = 0; i < _registeredPhysicalCameraRenderers.Count; i++)
            {
                Renderer renderer = _registeredPhysicalCameraRenderers[i];
                if (renderer != null && s_physicalCameraRendererSet.Remove(renderer))
                    s_physicalCameraRenderers.Remove(renderer);
            }

            _registeredPhysicalCameraRenderers.Clear();
        }

        /// <summary>
        /// Membership check for CctvTileCullingBypass: physical CCTV camera
        /// meshes are tile-parented, so the culler can index them under a
        /// tile. The bypass must never re-enable one mid-pass — this hider
        /// disabled it on purpose so the camera cannot film its own housing.
        /// </summary>
        internal static bool IsRegisteredPhysicalCameraRenderer(Renderer renderer)
        {
            return renderer != null && s_physicalCameraRendererSet.Contains(renderer);
        }

        internal static void RegisterCctvFeedCamera(Camera camera)
        {
            if (camera == null) return;
            EnsureRenderPipelineHooksRegistered();
            s_cctvFeedCameras.Add(camera);
        }

        internal static void UnregisterCctvFeedCamera(Camera camera)
        {
            if (camera == null) return;
            s_cctvFeedCameras.Remove(camera);
        }

        internal static void HidePhysicalCameraRenderersForCctv()
        {
            s_physicalCameraHideDepth++;
            if (s_physicalCameraHideDepth > 1) return;

            s_hiddenPhysicalCameraRenderers.Clear();
            for (int i = s_physicalCameraRenderers.Count - 1; i >= 0; i--)
            {
                Renderer renderer = s_physicalCameraRenderers[i];
                if (renderer == null)
                {
                    // Destroyed-but-still-referenced: the fake-null instance is
                    // still a valid key, so drop it from the index too.
                    s_physicalCameraRendererSet.Remove(renderer);
                    s_physicalCameraRenderers.RemoveAt(i);
                    continue;
                }

                s_hiddenPhysicalCameraRenderers.Add(new RendererVisibilityState(renderer, renderer.enabled));
                renderer.enabled = false;
            }
        }

        internal static void RestorePhysicalCameraRenderersAfterCctv()
        {
            if (s_physicalCameraHideDepth <= 0) return;
            s_physicalCameraHideDepth--;
            if (s_physicalCameraHideDepth > 0) return;

            for (int i = 0; i < s_hiddenPhysicalCameraRenderers.Count; i++)
            {
                s_hiddenPhysicalCameraRenderers[i].Restore();
            }

            s_hiddenPhysicalCameraRenderers.Clear();
        }

        private static void EnsureRenderPipelineHooksRegistered()
        {
            if (s_renderPipelineHooksRegistered) return;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            s_renderPipelineHooksRegistered = true;
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera != null && s_cctvFeedCameras.Contains(camera))
                HidePhysicalCameraRenderersForCctv();
        }

        private static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera != null && s_cctvFeedCameras.Contains(camera))
                RestorePhysicalCameraRenderersAfterCctv();
        }

        private struct RendererVisibilityState
        {
            private readonly Renderer _renderer;
            private readonly bool _enabled;

            internal RendererVisibilityState(Renderer renderer, bool enabled)
            {
                _renderer = renderer;
                _enabled = enabled;
            }

            internal void Restore()
            {
                if (_renderer != null)
                    _renderer.enabled = _enabled;
            }
        }

    }
}
