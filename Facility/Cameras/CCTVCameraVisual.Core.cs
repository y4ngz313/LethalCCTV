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
    internal sealed partial class CCTVCameraVisual : MonoBehaviour
    {
        private const float EstimatedSurfaceInsetM = 0.28f;
        private const float MinSnapOffsetM = 0.05f;
        private const float MaxSnapOffsetCeilingM = 3.0f;

        private const float TargetImportedCameraMaxDimensionM = 0.48f;
        // Gap between the prefab's rearmost renderer face and the mount
        // surface. Anything past ~1cm reads as the camera floating off its
        // wall, so keep this just large enough to avoid z-fighting.
        private const float SurfaceClearanceM = 0.005f;
        private static PlacementMasks _cachedMasks;
        private static bool _cachedMasksResolved;
        private static Material _fallbackBodyMaterial;
        private static Material _fallbackDarkMaterial;
        private static Material _fallbackLensMaterial;
        private static readonly List<Vector3> s_probeDirs = new List<Vector3>(16);
        // List for ordered iteration + in-place null pruning; the set is its
        // membership index, kept in lockstep. IsRegisteredPhysicalCameraRenderer
        // is called once per culler-disabled renderer per CCTV render pass, so a
        // List.Contains linear scan there was O(disabled renderers x cameras).
        private static readonly List<Renderer> s_physicalCameraRenderers = new List<Renderer>(64);
        private static readonly HashSet<Renderer> s_physicalCameraRendererSet = new HashSet<Renderer>();
        private static readonly List<RendererVisibilityState> s_hiddenPhysicalCameraRenderers = new List<RendererVisibilityState>(64);
        private static readonly HashSet<Camera> s_cctvFeedCameras = new HashSet<Camera>();
        private static bool s_renderPipelineHooksRegistered;
        private static int s_physicalCameraHideDepth;
        // Visible rotating dome per feed camera, so world-space indicators (the
        // detection warning beam) can mount on the prop the player actually
        // sees instead of the invisible feed transform.
        private static readonly Dictionary<CCTVCamera, Transform> s_rotatingHeadsByHolder = new Dictionary<CCTVCamera, Transform>(16);

        private readonly List<Renderer> _registeredPhysicalCameraRenderers = new List<Renderer>(16);
        private CCTVCamera _holder;
        private GameObject _visualRoot;
        // Visual gimbal limit for the bundled dome head: the spherical housing
        // window only exposes the dome's central cone, so the prop's aim delta
        // from the assembled rest pose is clamped even when the feed camera
        // sweeps further.
        private const float MaxBundledAimDeltaDeg = 30f;

        private Transform _aimPivot;
        private bool _built;
        private bool _failed;
        private float _nextBuildAttemptAt;
        private bool _aimPivotPositionLocked;
        private Vector3 _neutralAimForward;
        private Quaternion _neutralPivotRotation = Quaternion.identity;
        private Transform _bundledRotatingHead;

        // #563 impact flinch. The aim pivot's rotation is recomputed from the feed
        // transform every LateUpdate, so a kick written straight onto the transform is
        // erased on the next frame. It is instead held here as a damped oscillation and
        // multiplied onto the freshly computed pose below, which makes it compose with a
        // sweeping camera instead of fighting it.
        private const float DamageKickPeakDegrees = 14f;
        private const float DamageKickDampingPerSecond = 7f;
        private const float DamageKickFrequencyHz = 5.5f;
        private const float DamageKickCutoffDegrees = 0.05f;
        private Vector3 _damageKickAxis = Vector3.right;
        private float _damageKickPeakDegrees;
        private float _damageKickElapsed;

        internal static void AttachTo(CCTVCamera holder)
        {
            if (holder == null || holder.gameObject == null) return;
            if (SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.PhysicalCameraVisualsEnabled.Value) return;
            if (holder.GetComponent<CCTVCameraVisual>() != null) return;

            CCTVCameraVisual visual = holder.gameObject.AddComponent<CCTVCameraVisual>();
            visual._holder = holder;
        }

        private void LateUpdate()
        {
            if (_holder == null)
            {
                DestroyVisual();
                enabled = false;
                return;
            }

            if (SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.PhysicalCameraVisualsEnabled.Value)
            {
                DestroyVisual();
                enabled = false;
                return;
            }

            if (!_built && !_failed && Time.unscaledTime >= _nextBuildAttemptAt)
            {
                _nextBuildAttemptAt = Time.unscaledTime + 0.5f;
                TryBuild();
            }

            if (_built && _aimPivot != null && _holder.transform != null && !_holder.IsSecurityBroken)
            {
                Quaternion pose = _aimPivotPositionLocked
                    ? ComputeLockedAimRotation(_holder.transform.forward)
                    : _holder.transform.rotation;

                // The flinch is applied on top of the authoritative pose, never instead of
                // it, so the head keeps sweeping/tracking through the recoil and settles
                // back onto the exact rotation it would have had.
                Quaternion kick = ConsumeDamageKick();
                if (kick != Quaternion.identity)
                    pose *= kick;

                if (_aimPivotPositionLocked)
                    _aimPivot.rotation = pose;
                else
                    _aimPivot.SetPositionAndRotation(_holder.transform.position, pose);
            }
            else if (_damageKickPeakDegrees > 0f)
            {
                // The killing blow's kick must not outlive the pose branch: this same
                // component can rebuild a fresh _aimPivot (visuals toggled off and on),
                // and a stale kick computed against the OLD pivot's local axes would
                // resume against the new head for its remaining ring-down.
                _damageKickPeakDegrees = 0f;
                _damageKickElapsed = 0f;
            }
        }

        /// <summary>
        /// #563: registers an impact flinch on the visible head. Called from
        /// <see cref="CctvBreakableCamera"/> on every registered hit;
        /// <paramref name="worldHitDirection"/> is the swing/shot direction, which decides
        /// which way the head snaps.
        /// </summary>
        internal void ApplyDamageKick(Vector3 worldHitDirection)
        {
            if (_aimPivot == null)
                return;

            // Axis in the pivot's own space, because the kick is post-multiplied onto the
            // pose. A hit straight down the lens axis crosses to zero, so it falls back to
            // a pure pitch nod rather than a NaN rotation.
            Vector3 local = worldHitDirection.sqrMagnitude > 1e-4f
                ? _aimPivot.InverseTransformDirection(worldHitDirection.normalized)
                : Vector3.forward;
            Vector3 axis = Vector3.Cross(Vector3.forward, local);
            _damageKickAxis = axis.sqrMagnitude > 1e-4f ? axis.normalized : Vector3.right;
            _damageKickPeakDegrees = DamageKickPeakDegrees;
            _damageKickElapsed = 0f;
        }

        // Damped oscillation: a hard snap away from the impact that rings down over a
        // few tenths of a second. Returns identity once it has decayed past the point of
        // being visible, which is also what stops the per-frame work.
        private Quaternion ConsumeDamageKick()
        {
            if (_damageKickPeakDegrees <= 0f)
                return Quaternion.identity;

            _damageKickElapsed += Time.unscaledDeltaTime;
            float amplitude = _damageKickPeakDegrees * Mathf.Exp(-DamageKickDampingPerSecond * _damageKickElapsed);
            if (amplitude <= DamageKickCutoffDegrees)
            {
                _damageKickPeakDegrees = 0f;
                return Quaternion.identity;
            }

            float angle = amplitude * Mathf.Cos(_damageKickElapsed * DamageKickFrequencyHz * Mathf.PI * 2f);
            return Quaternion.AngleAxis(angle, _damageKickAxis);
        }

        private void OnDestroy()
        {
            DestroyVisual();
        }

        private void TryBuild()
        {
            if (_holder == null || _holder.transform == null) return;

            if (!TryResolveSnap(_holder, out VisualSnap snap))
            {
                _failed = true;
                if (ShouldLogVerbose())
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][CameraVisual] skipped {Describe(_holder)}: no wall/ceiling support within {ResolveMaxSnapOffset():F2}m; " +
                        "no prop and no breakable hitbox for this camera.");
                }
                return;
            }

            Transform parent = _holder.OwningTile != null ? _holder.OwningTile.transform : _holder.transform.parent;
            _visualRoot = new GameObject($"LethalCCTV_CameraVisual_{_holder.CameraIndex:D2}");
            if (parent != null) _visualRoot.transform.SetParent(parent, worldPositionStays: false);
            _visualRoot.transform.SetPositionAndRotation(snap.SurfacePoint, snap.SurfaceRotation);

            _aimPivot = new GameObject("AimPivot").transform;
            _aimPivot.SetParent(_visualRoot.transform, worldPositionStays: true);
            _aimPivot.SetPositionAndRotation(_holder.transform.position, _holder.transform.rotation);

            GameObject prefab = PhysicalCameraVisualLoader.TryLoad();
            if (prefab != null)
                BuildBundledVisual(prefab);
            else
                BuildFallbackVisual(snap);

            RegisterPhysicalCameraRenderers(_visualRoot);

            _built = true;
            // The aim pivot, not the visual root: every head mesh (bundled and fallback)
            // is parented under it and it is re-posed each LateUpdate as the camera
            // sweeps, so only a hitbox under it stays on the visible head.
            GameObject hitboxHost = _aimPivot != null
                ? _aimPivot.gameObject
                : (_visualRoot != null ? _visualRoot : _holder.gameObject);
            CctvBreakableCamera.AttachTo(_holder, hitboxHost);
            CountRenderers(_visualRoot, out int rendererCount, out int enabledRenderers, out int activeRenderers);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][CameraVisual] visible prop spawned {Describe(_holder)} source={snap.Source} " +
                $"renderers={rendererCount} enabled={enabledRenderers} active={activeRenderers} " +
                $"surface=({snap.SurfacePoint.x:F2},{snap.SurfacePoint.y:F2},{snap.SurfacePoint.z:F2}).");
            if (ShouldLogVerbose())
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][CameraVisual] spawned {Describe(_holder)} source={snap.Source} " +
                    $"offset={snap.Distance:F2}m surface=({snap.SurfacePoint.x:F2},{snap.SurfacePoint.y:F2},{snap.SurfacePoint.z:F2}).");
            }
        }

    }
}
