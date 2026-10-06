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
        private bool BuildBundledVisual(GameObject prefab)
        {
            float scale = ResolveScale();
            GameObject instance = Object.Instantiate(prefab, _visualRoot.transform);
            instance.name = "SecurityCameraPrefab";
            if (!PhysicalCameraVisualLoader.TryBindRig(instance, out _, out Transform yoke,
                    out Transform head, out Transform led, out _))
            {
                Object.DestroyImmediate(instance);
                return false;
            }

            // Authored in metres with the bracket origin on its wall-contact face.
            instance.transform.localPosition = Vector3.forward * SurfaceClearanceM;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one * scale;
            SetImportedVisualActiveAndRenderable(instance);
            NormalizeImportedVisual(instance);
            Object.DestroyImmediate(_aimPivot.gameObject);
            _aimPivot = head;
            _bundledYoke = yoke;
            _bundledRotatingHead = head;
            if (_holder != null) s_rotatingHeadsByHolder[_holder] = head;
            AddBundledLensDot(led, head, scale);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][CameraVisual] named rig bound {Describe(_holder)} yaw='{yoke.name}' pitch='{head.name}' LED='{led.name}'.");
            return true;
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

        private void AddBundledLensDot(Transform socket, Transform head, float scale)
        {
            Renderer renderer = head.GetComponent<Renderer>();
            Bounds bounds = renderer != null ? renderer.bounds : default;
            float maxDimension = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            float dotDiameter = Mathf.Clamp(0.12f * maxDimension, Mathf.Max(0.012f, 0.015f * scale), 0.06f);
            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "BundledLensRedDot";
            dot.transform.SetParent(socket, false);
            dot.transform.localPosition = Vector3.zero;
            dot.transform.localRotation = Quaternion.identity;
            Vector3 parentLossy = socket.lossyScale;
            float parentScale = Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(parentLossy.x), Mathf.Max(Mathf.Abs(parentLossy.y), Mathf.Abs(parentLossy.z))));
            dot.transform.localScale = Vector3.one * (dotDiameter / parentScale);
            // Placement raycasts run in this call; never leave a primitive collider until end of frame.
            Collider collider = dot.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            renderer = dot.GetComponent<Renderer>();
            renderer.sharedMaterial = EnsureLensMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            CCTVCameraLensBlinker.Attach(dot, _holder);
        }

        private void UpdateBundledAim(Vector3 targetForward, Quaternion kick)
        {
            Vector3 local = _bundledYoke.parent.InverseTransformDirection(targetForward);
            if (local.sqrMagnitude < 1e-6f) return;
            local.Normalize();
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -8f, 70f);
            Vector3 desired = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            // Keep the physical prop's existing 30-degree neutral-aim limit; the feed is independent.
            local = Vector3.RotateTowards(Vector3.forward, desired, MaxBundledAimDeltaDeg * Mathf.Deg2Rad, 0f);
            yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
            // Split the impact kick across the same mechanical axes instead of rolling the housing.
            Vector3 kicked = kick * Vector3.forward;
            yaw += Mathf.Atan2(kicked.x, kicked.z) * Mathf.Rad2Deg;
            pitch += -Mathf.Asin(Mathf.Clamp(kicked.y, -1f, 1f)) * Mathf.Rad2Deg;
            _bundledYoke.localRotation = Quaternion.Euler(0f, yaw, 0f);
            _aimPivot.localRotation = Quaternion.Euler(Mathf.Clamp(pitch, -8f, 70f), 0f, 0f);
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
