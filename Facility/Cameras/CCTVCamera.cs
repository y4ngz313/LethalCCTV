using DunGen;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Rendering;

namespace Y4NGZCompany.Facility.Cameras
{
    public sealed class CCTVCamera : MonoBehaviour
    {
        private const string CameraScanNodeName = "LethalCCTV_CameraScanNode";
        private const int CameraScanNodeFallbackLayer = 22;
        private const int CameraScanNodeMaxRange = 8;
        private const float CameraScanNodeRadius = 0.3f;

        public int CameraIndex { get; internal set; }
        public Tile OwningTile { get; internal set; }
        public Camera Cam { get; internal set; }
        // Null in Phase 1. Phase 2's Promote() adds HDAdditionalCameraData on the
        // frame a camera is first promoted to a monitor slot and stashes it here.
        // Phase 1 deliberately leaves HDRP additional data unattached so disabled
        // cameras do not register with HDRP's per-frame iteration (probe / volume
        // contamination caused visible ceiling brightening when the component was
        // present on disabled cameras).
        public HDAdditionalCameraData HdrpData { get; internal set; }
        public Vector3 PlacementCornerLocal { get; internal set; }
        public int PlacementCornerId { get; internal set; }
        internal string DisplayLabel { get; set; }
        internal bool AllowsOperatorRotation { get; set; } = true;
        internal string ResolvedLabel => !string.IsNullOrEmpty(DisplayLabel)
            ? DisplayLabel
            : "CAM_" + CameraIndex.ToString("D2");
        // Phase 1.9 — mount mode + the room height (worldAABB.y, or
        // LocalBounds.y on the degenerate path) that drove the branch.
        // Read by the placement diagnostic log and by the (future) prefab
        // workstream to pick wall-prop vs hanging-ceiling-prop without
        // re-deriving the branch from tile geometry. Internal: the type
        // CameraMountMode is internal, and the only consumers (spawner
        // log, future prefab code) live in this assembly.
        internal CameraMountMode MountMode { get; set; }
        internal float DrivingRoomHeightM { get; set; }
        internal string PlacementSource { get; set; }
        internal string PlacementSurfaceKind { get; set; }
        internal string PlacementHitName { get; set; }
        internal int PlacementHitLayer { get; set; }
        internal Vector3 PlacementSurfaceNormal { get; set; }
        // #1313 diagnostics. PlacementTier names the placement pass that produced the
        // pose (strict, relaxed, expanded, expanded-relaxed, grand-room, reviewed,
        // authored, support-*); PlacementSurfaceDistanceM is the pose's distance from
        // the validated mount surface along PlacementSurfaceNormal (0 = no validated surface).
        public string PlacementTier;
        public float PlacementSurfaceDistanceM;
        internal float PlacementScore { get; set; }
        internal float PlacementHeightAboveFloorM { get; set; }
        internal float PlacementCenterSightDistanceM { get; set; }
        internal int PlacementFrustumHits { get; set; }
        internal int PlacementFrustumVoids { get; set; }
        internal int PlacementFrustumNearWall { get; set; }
        internal float PlacementDoorwayVisibilityScore { get; set; }
        internal float PlacementRoomFramingScore { get; set; }
        internal string PlacementAimProfile { get; set; }
        internal bool FromReviewedPlacementProfile { get; set; }
        internal string AuthoredPlacementId { get; set; }
        // StripActive field added in Phase 2 — see Phase 2 SPEC §3.3 strip-set lifecycle.
        // Phase 1 reads and writes nothing here. Phase 2's CCTVScheduler uses it to make
        // ApplyStripSet / RevertStripSet calls idempotent — apply only when transitioning
        // off → on, revert only when transitioning on → off.
        internal bool StripActive { get; set; }

        // Mouselook state. Spawner calls CaptureBaseline once after
        // SetPositionAndRotation; FocusMouselook.Tick mutates YawOffsetDeg /
        // PitchOffsetDeg per-frame on the active pane and calls ApplyOffsets
        // to write transform.rotation.
        //
        // The base orientation is stored as decomposed world-space scalars
        // (yaw around world-up, pitch around world-right) rather than as a
        // quaternion so the per-frame composition can never introduce roll:
        // ApplyOffsets rebuilds rotation from world axes every frame via
        // Euler(pitch, yaw, 0). Earlier "baseline * Euler(p,y,0)" model
        // multiplied yaw in the baseline's LOCAL frame — any non-level
        // baseline (a downward LookRotation from the spawn-time aim)
        // swept yaw around a tilted up-axis, producing Z-roll and upward
        // drift on horizontal pans. World-up yaw eliminates that class of
        // bug entirely.
        //
        // Parent tile transform does not move after spawn (verified —
        // DungeonCameraSpawner is the sole writer), so writing
        // transform.rotation (world-space) is stable.
        //
        // State dies with the GameObject on dungeon regen — no static dict.
        internal Placement.CameraPanEnvelope SafetyEnvelope { get; set; }
        internal float BaseYawDeg { get; private set; }
        internal float BasePitchDeg { get; private set; }
        internal float YawOffsetDeg { get; set; }
        internal float PitchOffsetDeg { get; set; }
        internal bool SecuritySweepSuppressed { get; set; }
        internal bool SecurityBroken { get; private set; }
        internal float SecuritySweepPhase { get; set; }
        // Focus-view movement yields this camera's automatic motor for a short,
        // per-camera idle window. State lives on the spawned camera so dungeon
        // regeneration clears it without a static registry.
        internal float ManualSweepHoldUntil { get; set; }
        internal Transform SecurityVisualPivot { get; set; }

        internal bool IsSecurityActive =>
            Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry.IsSecurityActive(this);

        // #563: active AND actually sweeping for players. Anything the player can see or
        // hear that says "this camera is hunting" reads this, not IsSecurityActive.
        internal bool IsDetectionLive =>
            Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry.IsDetectionLive(this);

        internal bool IsSecurityBroken =>
            SecurityBroken || Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry.IsCameraBroken(this);

        private ScanNodeProperties _cameraScanNode;

        private void Start()
        {
            EnsureCameraScanNode();
        }

        private void EnsureCameraScanNode()
        {
            Transform existing = transform.Find(CameraScanNodeName);
            GameObject scanObject = existing != null ? existing.gameObject : new GameObject(CameraScanNodeName);
            scanObject.transform.SetParent(transform, worldPositionStays: false);
            scanObject.transform.localPosition = Vector3.zero;
            scanObject.transform.localRotation = Quaternion.identity;
            scanObject.transform.localScale = Vector3.one;

            int scanLayer = LayerMask.NameToLayer("ScanNode");
            scanObject.layer = scanLayer >= 0 ? scanLayer : CameraScanNodeFallbackLayer;
            try
            {
                scanObject.tag = "DoNotSet";
            }
            catch (UnityException)
            {
                // Some modded profiles do not expose the vanilla tag table consistently.
            }

            SphereCollider collider = scanObject.GetComponent<SphereCollider>();
            if (collider == null)
                collider = scanObject.AddComponent<SphereCollider>();
            collider.enabled = true;
            collider.isTrigger = true;
            collider.center = Vector3.zero;
            collider.radius = CameraScanNodeRadius;

            _cameraScanNode = scanObject.GetComponent<ScanNodeProperties>();
            if (_cameraScanNode == null)
                _cameraScanNode = scanObject.AddComponent<ScanNodeProperties>();
            _cameraScanNode.headerText = "CCTV Camera";
            _cameraScanNode.minRange = 0;
            _cameraScanNode.maxRange = CameraScanNodeMaxRange;
            _cameraScanNode.requiresLineOfSight = true;
            _cameraScanNode.nodeType = 0;
            _cameraScanNode.creatureScanID = -1;
            _cameraScanNode.scrapValue = 0;
            UpdateCameraScanNodeStatus();
        }

        private void UpdateCameraScanNodeStatus()
        {
            if (_cameraScanNode != null)
                _cameraScanNode.subText = IsSecurityBroken ? "broken" : (SecurityRemotelyDisabled ? "offline" : "facility surveillance");
        }

        internal void MarkSecurityBroken()
        {
            SecurityBroken = true;
            SecuritySweepSuppressed = true;
            SetNightVisionFillLightActive(false);
            UpdateCameraScanNodeStatus();
        }

        // Remote shutdown (CctvSupportApi.TryDisableCamera / Field Mechanic hack).
        // Distinct from SecurityBroken: sabotage hides the whole prop's renderers,
        // while a remotely disabled camera keeps its prop visible, so the lens
        // blinker keys off this flag to go fully dark instead of idle-blinking.
        internal bool SecurityRemotelyDisabled { get; private set; }

        internal void MarkSecurityRemotelyDisabled()
        {
            SecurityRemotelyDisabled = true;
            SecuritySweepSuppressed = true;
            SetNightVisionFillLightActive(false);
            UpdateCameraScanNodeStatus();
        }

        internal void ApplySecurityAngles(float yawOffsetDeg, float pitchOffsetDeg)
        {
            if (IsSecurityBroken || SecuritySweepSuppressed) return;
            YawOffsetDeg = yawOffsetDeg;
            PitchOffsetDeg = pitchOffsetDeg;
            ApplyOffsets();
        }

        private Light _nightVisionFillLight;

        internal void CaptureBaseline()
        {
            // Decompose spawn-time world-space forward into yaw/pitch
            // scalars. Roll component of the spawn rotation is INTENTIONALLY
            // discarded — the spawn-time aim's Vector3.up reference in LookRotation
            // can introduce a non-zero roll on tilted views, which we don't
            // want in the rebuilt rotation.
            Vector3 fwd = transform.rotation * Vector3.forward;
            BaseYawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            BasePitchDeg = -Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        internal void ApplyOffsets()
        {
            // Apply the user's narrower pitch limit before the shared safety
            // gate. No later clamp may rotate away from the validated pose.
            PitchOffsetDeg = Mathf.Clamp(BasePitchDeg + PitchOffsetDeg, -5f,
                SurveillanceBootstrap.Config.PitchClampDeg) - BasePitchDeg;
            if (SafetyEnvelope != null)
            {
                float safeYaw = YawOffsetDeg, safePitch = PitchOffsetDeg;
                SafetyEnvelope.Constrain(ref safeYaw, ref safePitch);
                YawOffsetDeg = safeYaw; PitchOffsetDeg = safePitch;
                if (Cam != null) Cam.farClipPlane = Mathf.Max(Cam.farClipPlane, SafetyEnvelope.RequiredDistance);
            }
            float yaw = BaseYawDeg + YawOffsetDeg;
            float pitch = BasePitchDeg + PitchOffsetDeg;
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        internal void ResetOffsets()
        {
            YawOffsetDeg = 0f;
            PitchOffsetDeg = 0f;
        }

        internal void ConfigureDisabled()
        {
            Cam.enabled = false;
            Cam.targetTexture = null;
            SetNightVisionFillLightActive(false);
            // Defensive: zero culling and clear flags so even an accidental enable
            // produces no render work until Phase 2 explicitly configures the camera.
            Cam.cullingMask = 0;
            Cam.clearFlags = CameraClearFlags.Nothing;
        }

        internal void SetNightVisionFillLightActive(bool active)
        {
            if (!active)
            {
                if (_nightVisionFillLight != null) _nightVisionFillLight.enabled = false;
                return;
            }

            if (Cam == null ||
                SurveillanceBootstrap.Config == null ||
                !SurveillanceBootstrap.Config.CCTVFillLightEnabled.Value ||
                !SurveillanceBootstrap.Config.CCTVRenderFillLightEnabled.Value)
            {
                if (_nightVisionFillLight != null) _nightVisionFillLight.enabled = false;
                return;
            }

            Light fill = EnsureNightVisionFillLight();
            if (fill == null) return;

            fill.type = LightType.Spot;
            fill.color = Color.white;
            fill.intensity = Mathf.Max(0f, SurveillanceBootstrap.Config.CCTVFillLightIntensity.Value);
            fill.range = Mathf.Max(0.1f, SurveillanceBootstrap.Config.CCTVFillLightRange.Value);
            fill.spotAngle = Mathf.Clamp(Cam.fieldOfView + 10f, 1f, 179f);
            fill.shadows = LightShadows.None;
            // CullFactory warns on the CCTV camera's custom mask. The light is
            // enabled only while this camera renders, so the default all-layer
            // light mask is the quieter and equivalent scoped behavior.
            fill.cullingMask = -1;
            fill.enabled = fill.intensity > 0f;
        }

        private Light EnsureNightVisionFillLight()
        {
            if (_nightVisionFillLight != null) return _nightVisionFillLight;

            var go = new GameObject("LethalCCTV_NightVisionFillLight");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            _nightVisionFillLight = go.AddComponent<Light>();
            _nightVisionFillLight.enabled = false;
            return _nightVisionFillLight;
        }

        // The feed's immutable CCTV frame-settings profile. Core's CameraRenderProfile owns every
        // write: it disables the heavy HDRP passes a security feed does not need (post-FX,
        // screen-space shadows/SSR, reflection probes, volumetrics, decals, custom passes, motion
        // vectors, exposure, SSS, refraction/distortion), keeps TransparentObjects and atmospheric
        // scattering, and takes the configurable shadow-map/SSAO pair from config. Classifying the
        // camera as a Feed also makes it refuse every settings lease: an effect on the feed (the
        // squad-ping outline) is an overlay composited by NightVisionBaker after the bake, so a ping
        // can never turn the feed's custom passes on and black it out (#1219 G1/G2).
        internal void ApplyStripSet()
        {
            // Intentionally NOT guarded by StripActive — re-asserts the profile on every bind so a
            // pooled holder or a reused regen instance is correct by construction.
            if (HdrpData == null || Cam == null) return;

            StripActive = CameraRenderProfile.ApplyFeedProfile(
                Cam,
                SurveillanceBootstrap.Config?.CCTVShadowMapsEnabled.Value ?? true,
                SurveillanceBootstrap.Config?.CCTVAmbientOcclusionEnabled.Value ?? true);
        }

        internal void RevertStripSet()
        {
            if (!StripActive) return;
            if (Cam != null) CameraRenderProfile.ReleaseProfile(Cam);
            StripActive = false;
        }
    }
}
