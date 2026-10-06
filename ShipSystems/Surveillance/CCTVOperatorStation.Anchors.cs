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
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVOperatorStation
    {
        internal static Quaternion LeverSteeringDelta => _joystickSessionOwned && _joystickTiltPivot != null
            ? _joystickTiltPivot.rotation * Quaternion.Inverse(GetJoystickBaseWorldRotation())
            : Quaternion.identity;
        internal static Vector3 LeverGripUp => LeverSteeringDelta * Vector3.up;

        internal static bool TryResolveLeverGripPoint(out Vector3 point, out string source)
        {
            EnsureBrakeLeverControlRig();
            Transform grip = _rightHandTarget != null
                ? _rightHandTarget
                : _joystickTiltPivot;
            if (grip == null)
            {
                point = Vector3.zero;
                source = "unresolved";
                return false;
            }

            point = grip.position;
            source = _rightHandTarget != null
                ? _rightHandGripSource
                : "lever-pivot-fallback";
            return true;
        }

        /// <summary>
        /// World center of the lower-right (radar) monitor screen. The aim path
        /// that consumes this runs on the focused-frame camera step, and the
        /// resolve below walks every mesh index, so the answer is cached in
        /// renderer-local space: the monitor is fixed to the wall, but the ship
        /// itself moves, and a cached world point would go stale in orbit.
        /// </summary>
        internal static bool TryResolveRadarMonitorScreenCenter(out Vector3 center)
        {
            if (_radarScreenCenterResolved && _radarScreenRenderer != null && !HasRadarScreenSurfaceMoved())
            {
                // Rigid re-projection of a point already validated at resolve time.
                center = _radarScreenRenderer.transform.TransformPoint(_radarScreenCenterLocal);
                return true;
            }

            center = Vector3.zero;
            // Failure backoff: the uncached resolve below walks every mesh index
            // (mesh.vertices allocates the full vertex array each call) and the
            // periodic anchor-aim caller fires every 0.5s, so a persistently
            // failing resolve re-paid the 8-18ms scan plus GC pressure on every
            // tick. The monitor is static ship geometry; retrying every few
            // seconds is indistinguishable. A successful resolve caches and
            // never comes back through here.
            if (Time.unscaledTime < _nextRadarScreenCenterResolveAt)
                return false;
            _nextRadarScreenCenterResolveAt = Time.unscaledTime + RadarScreenCenterRetryIntervalSeconds;

            _radarScreenCenterResolved = false;
            if (!TryResolveRadarMonitorSurface(out _radarScreenRenderer, out int materialIndex, out bool giScreen) ||
                _radarScreenRenderer == null)
            {
                _radarScreenRenderer = null;
                return false;
            }

            if (!TryResolveRadarMonitorScreenCenterUncached(_radarScreenRenderer, materialIndex, giScreen, out center))
                return false;

            _radarScreenCenterLocal = _radarScreenRenderer.transform.InverseTransformPoint(center);
            _radarScreenCenterResolved = true;
            return true;
        }

        /// <summary>
        /// #598 — the cached radar center is pinned to one renderer. GI rebuilding its
        /// MonitorGroup destroys that renderer (caught by the null check), but a config
        /// change or a re-pick that lands the radar on a DIFFERENT, still-live GI screen
        /// would otherwise keep aiming at the old one. Only a live GI binding is compared;
        /// the vanilla wall renderer never changes identity under the station's feet.
        /// </summary>
        private static bool HasRadarScreenSurfaceMoved()
        {
            if (!CCTVVanillaMonitorDisplay.TryGetBoundRadarScreen(out MeshRenderer current, out _, out bool giScreen))
                return false;
            return giScreen && current != null && current != _radarScreenRenderer;
        }

        private static bool TryResolveRadarMonitorScreenCenterUncached(
            MeshRenderer renderer,
            int materialIndex,
            bool generalImprovementsScreen,
            out Vector3 center)
        {
            center = Vector3.zero;
            if (renderer == null)
                return false;

            // Prefer the lit screen submesh, for the same reason
            // TryResolveMainMonitorScreenFrame does: renderer.bounds is the
            // whole-mesh world AABB, and on the shared multi-screen wall mesh
            // its center is not on any one screen. Aiming the radar glance at
            // that center put the viewpoint 152 degrees off the CCTV view in
            // the 2026-07-31 run, which is what made the F1 radar editor
            // unusable. The whole-renderer center stays as a last resort;
            // AimRadarAnchorFromFocusDefault cone-checks whatever comes back.
            // A GeneralImprovements screen is its own single-material mesh, so there
            // the whole-mesh bounds are the screen and TryResolveScreenFaceBounds
            // takes that path directly (#598).
            if (TryResolveScreenFaceBounds(renderer, materialIndex, generalImprovementsScreen, out Bounds screenBounds))
            {
                center = screenBounds.center;
                if (IsPlausibleShipWorldPoint(center))
                    return true;
            }

            center = renderer.bounds.center;
            return IsPlausibleShipWorldPoint(center);
        }

        /// <summary>
        /// Submesh index of the radar screen's lit face, taken from whichever
        /// vanilla ManualCameraRenderer drives the resolved renderer. Returns
        /// -1 when no driver claims it, which makes TryResolveRendererSubmeshBounds
        /// decline rather than guess at a submesh.
        /// </summary>
        private static int ResolveRadarMonitorMaterialIndex(MeshRenderer renderer)
        {
            if (renderer == null)
                return -1;

            try
            {
                StartOfRound sor = StartOfRound.Instance;
                if (sor == null)
                    return -1;

                int materialCount = renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;
                ManualCameraRenderer[] drivers = { sor.insideCameraScreen, sor.securityCameraScreen };
                for (int i = 0; i < drivers.Length; i++)
                {
                    ManualCameraRenderer driver = drivers[i];
                    if (driver == null || driver.mesh != renderer)
                        continue;
                    if (driver.materialIndex >= 0 && driver.materialIndex < materialCount)
                        return driver.materialIndex;
                }
            }
            catch { }

            return -1;
        }

        private static void CreateAnchors(Transform parent)
        {
            GameObject focus = new GameObject("CCTVStationFocusViewAnchor");
            focus.transform.SetParent(parent, worldPositionStays: false);
            focus.transform.localPosition = FocusLocalPosition;
            focus.transform.localRotation = Quaternion.Euler(FocusLocalEuler);
            _focusViewAnchor = focus.transform;

            GameObject radar = new GameObject("CCTVStationRadarViewAnchor");
            radar.transform.SetParent(parent, worldPositionStays: false);
            radar.transform.localPosition = RadarFocusLocalPosition;
            radar.transform.localRotation = Quaternion.Euler(RadarFocusLocalEuler);
            _radarViewAnchor = radar.transform;

            GameObject playerRoot = new GameObject("CCTVStationPlayerRootAnchor");
            playerRoot.transform.SetParent(parent, worldPositionStays: false);
            playerRoot.transform.localPosition = PlayerRootLocalPosition;
            playerRoot.transform.localRotation = Quaternion.Euler(PlayerRootLocalEuler);
            _playerRootAnchor = playerRoot.transform;

            GameObject pose = new GameObject("CCTVStationOperatorPoseAnchor");
            pose.transform.SetParent(parent, worldPositionStays: false);
            pose.transform.localPosition = OperatorPoseLocalPosition;
            pose.transform.localRotation = Quaternion.Euler(OperatorPoseLocalEuler);
            _operatorPoseAnchor = pose.transform;

            EnsureNoteAnchor();

            // Fresh anchors start from the code-authored station pose. Runtime
            // focus deliberately avoids local profile overrides for these anchors.
            _focusAnchorAimApplied = false;
        }

        /// <summary>
        /// #582 — idempotent spawn pose for the CCTV sticky note, as a child of the
        /// station root. It lives on the root rather than on the note itself because
        /// the placement editor moves its target transform directly and
        /// GrabbableObject.Update re-pins a settled prop every frame; an editor
        /// driving the live note would simply fight vanilla. The root is re-derived
        /// from the monitor wall on every station spawn and parented to the
        /// elevator, so an authored anchor pose survives ship movement and reload.
        /// Returns null until the station root exists.
        /// </summary>
        internal static Transform EnsureNoteAnchor()
        {
            if (_root == null)
                return null;

            if (_noteAnchor == null)
            {
                GameObject note = new GameObject("CCTVStationNoteAnchor");
                note.transform.SetParent(_root.transform, worldPositionStays: false);
                _noteAnchor = note.transform;
                ApplyNotePlacement(_noteAnchor);
            }

            return _noteAnchor;
        }

        // Focus anchor placement relative to the resolved screen center: distance
        // back from the screen along the wall normal, height below the screen center
        // (so the camera looks slightly up and keeps desk/joystick in frame), and
        // the aim point dropped slightly below center so the monitor sits a touch
        // above frame center.
        private static bool _focusAnchorAimApplied;
        private static bool _radarAimRejectionLogged;
        private static MeshRenderer _radarScreenRenderer;
        private static Vector3 _radarScreenCenterLocal;
        private static bool _radarScreenCenterResolved;
        private const float RadarScreenCenterRetryIntervalSeconds = 3f;
        private static float _nextRadarScreenCenterResolveAt;

        // Design intent for the radar glance is RadarFocusLocalEuler minus
        // FocusLocalEuler, about 47 degrees of yaw. This bound leaves room for
        // the real screen geometry while still catching a resolved center that
        // is not the lower-right monitor at all.
        private const float RadarAimMaxAngleFromFocusDeg = 60f;

        // The station placement editor and local placement profile own the focus
        // anchor. Do not auto-recenter it from monitor bounds here; that overrides
        // the authored/operator-tuned view and moves the player camera off target.
        internal static void EnsureFocusAnchorAimed(bool force = false)
        {
            if (_root == null || _focusViewAnchor == null)
                return;

            _focusAnchorAimApplied = true;
            EnsureRadarAnchorAimed(force);
        }

        internal static bool ShouldUseExactStationFocusPose(Transform anchor)
        {
            if (anchor == null)
                return false;
            if (anchor == _focusViewAnchor)
                return true;
            if (anchor == _radarViewAnchor)
                return true;
            return false;
        }

        internal static void EnsureRadarAnchorAimed(bool force = false)
        {
            if (_radarViewAnchor == null || _focusViewAnchor == null)
                return;
            if (IsEditingDebugPlacement("radar"))
                return;
            if (HasSavedPlacement("radar"))
                return;
            if (!force && !_focusAnchorAimApplied)
                return;

            AimRadarAnchorFromFocusDefault();
        }

        // Place AND aim the default focus anchor from the lower-left (main CCTV)
        // monitor's actual screen center, so the final viewport derives from the
        // monitor itself — never from where the player stood when pressing E, and
        // never from the desk-relative hand-tuned spot (which sat right of the
        // screen). Only high-confidence center sources are used — the mapScreen
        // submesh bounds or the screenLevelDescription rect; whole-renderer bounds
        // span several monitors and historically pulled the view into the center
        // divider. A user-saved focus placement always wins.
        private static void AimRadarAnchorFromFocusDefault()
        {
            if (_radarViewAnchor == null || _focusViewAnchor == null || HasSavedPlacement("radar"))
                return;
            if (IsEditingDebugPlacement("radar"))
                return;

            _radarViewAnchor.position = _focusViewAnchor.position;
            _radarViewAnchor.localScale = Vector3.one;

            // Authored offset first, so a rejected look-at still leaves a
            // usable pose behind for the F1 editor to start from.
            _radarViewAnchor.rotation = _focusViewAnchor.rotation * Quaternion.Euler(
                RadarFocusLocalEuler.x - FocusLocalEuler.x,
                RadarFocusLocalEuler.y - FocusLocalEuler.y,
                RadarFocusLocalEuler.z - FocusLocalEuler.z);

            if (!TryResolveRadarMonitorScreenCenter(out Vector3 radarCenter))
                return;

            // Solve against the eye the glance renders from, not the raw
            // anchor: MonitorFocus pulls the presentation eye back and up
            // before it writes the camera, and at ~1m from the wall that
            // parallax is worth several degrees on its own.
            Vector3 eye = _focusViewAnchor.position;
            Vector3 focusForward = _focusViewAnchor.forward;
            if (MonitorFocus.TryGetStationFocusPresentationEye(
                    out Vector3 presentationEye,
                    out Quaternion presentationRotation))
            {
                eye = presentationEye;
                focusForward = presentationRotation * Vector3.forward;
            }

            Vector3 toRadar = radarCenter - eye;
            if (toRadar.sqrMagnitude <= 0.0001f || focusForward.sqrMagnitude <= 0.0001f)
                return;
            toRadar.Normalize();

            // The radar monitor sits just off the main screen; the authored
            // design intent is a 28-degree glance. A resolved center that
            // demands a much bigger turn is not the lower-right monitor, and
            // silently adopting it is what produced a viewpoint facing behind
            // the operator. Keep the authored offset in that case.
            float angleFromFocus = Vector3.Angle(focusForward.normalized, toRadar);
            if (angleFromFocus > RadarAimMaxAngleFromFocusDeg)
            {
                if (!_radarAimRejectionLogged)
                {
                    _radarAimRejectionLogged = true;
                    SurveillanceBootstrap.Log?.LogMessage(
                        $"[LethalCCTV][RadarAim] rejected screen-center aim {angleFromFocus:0.0}deg off the CCTV view " +
                        $"(bound={RadarAimMaxAngleFromFocusDeg:0}deg center={FormatVector(radarCenter)} eye={FormatVector(eye)}); " +
                        "holding the authored offset. Author one with F1 > 6 to override.");
                }
                return;
            }

            _radarAimRejectionLogged = false;
            _radarViewAnchor.rotation = Quaternion.LookRotation(toRadar, Vector3.up);
        }

        private static Vector3 ResolveMainMonitorInward(Vector3 screenCenter)
        {
            Vector3 inward = Vector3.zero;
            if (_root != null)
            {
                inward = _root.transform.position - screenCenter;
                inward.y = 0f;
                if (inward.sqrMagnitude >= 1e-4f)
                    return inward;
            }

            try
            {
                StartOfRound sor = StartOfRound.Instance;
                RectTransform rect = sor != null && sor.screenLevelDescription != null
                    ? sor.screenLevelDescription.rectTransform
                    : null;
                RectTransform parentRect = rect != null ? rect.parent as RectTransform : null;
                RectTransform target = parentRect != null ? parentRect : rect;
                if (target != null)
                {
                    inward = ResolveInteriorDirection(ResolveShipTransform(), screenCenter, target.forward);
                    inward.y = 0f;
                    if (inward.sqrMagnitude >= 1e-4f)
                        return inward;
                }
            }
            catch { }

            Transform ship = ResolveShipTransform();
            inward = ship != null ? ship.position - screenCenter : Vector3.back;
            inward.y = 0f;
            return inward;
        }

        internal static bool TryResolveMainMonitorScreenFrame(
            out Vector3 center,
            out Vector3 normal,
            out Vector3 right,
            out Vector3 up,
            out float halfWidth,
            out float halfHeight)
        {
            center = Vector3.zero;
            normal = Vector3.zero;
            right = Vector3.right;
            up = Vector3.up;
            halfWidth = 0f;
            halfHeight = 0f;

            if (TryResolveMainMonitorSurface(out MeshRenderer physicalRenderer, out int materialIndex, out bool giScreen) &&
                physicalRenderer != null)
            {
                if (TryResolveScreenFaceBounds(physicalRenderer, materialIndex, giScreen, out Bounds screenBounds))
                {
                    center = screenBounds.center;
                    if (IsPlausibleShipWorldPoint(center) && TryBuildScreenFrameFromBounds(screenBounds, center, out normal, out right, out up, out halfWidth, out halfHeight))
                        return true;
                }
            }

            // The screenLevelDescription rect lives on the vanilla map-screen canvas, which
            // GI hides along with the mesh behind it (#598). Under better monitors it is no
            // more visible than Cube.001, so it is not a fallback — declining is.
            if (IsGeneralImprovementsWallVisible())
                return false;

            try
            {
                StartOfRound sor = StartOfRound.Instance;
                RectTransform rect = sor != null && sor.screenLevelDescription != null
                    ? sor.screenLevelDescription.rectTransform
                    : null;
                RectTransform parentRect = rect != null ? rect.parent as RectTransform : null;
                RectTransform target = parentRect != null ? parentRect : rect;
                if (target != null)
                {
                    Vector3[] corners = new Vector3[4];
                    target.GetWorldCorners(corners);
                    center = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
                    if (IsPlausibleShipWorldPoint(center) && TryBuildScreenFrameFromCorners(corners, center, target.forward, target.right, target.up, out normal, out right, out up, out halfWidth, out halfHeight))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool TryBuildScreenFrameFromBounds(
            Bounds bounds,
            Vector3 center,
            out Vector3 normal,
            out Vector3 right,
            out Vector3 up,
            out float halfWidth,
            out float halfHeight)
        {
            normal = ResolveMainMonitorInward(center);
            if (normal.sqrMagnitude < 1e-4f)
            {
                right = Vector3.right;
                up = Vector3.up;
                halfWidth = 0f;
                halfHeight = 0f;
                return false;
            }

            normal.Normalize();
            right = Vector3.Cross(Vector3.up, normal);
            if (right.sqrMagnitude < 1e-4f)
                right = Vector3.Cross(Vector3.forward, normal);
            if (right.sqrMagnitude < 1e-4f)
                right = Vector3.right;
            right.Normalize();
            up = Vector3.Cross(normal, right);
            if (up.sqrMagnitude < 1e-4f)
                up = Vector3.up;
            else
                up.Normalize();

            Vector3 extents = bounds.extents;
            halfWidth = 0f;
            halfHeight = 0f;
            for (int ix = -1; ix <= 1; ix += 2)
            {
                for (int iy = -1; iy <= 1; iy += 2)
                {
                    for (int iz = -1; iz <= 1; iz += 2)
                    {
                        Vector3 corner = center + new Vector3(extents.x * ix, extents.y * iy, extents.z * iz);
                        Vector3 local = corner - center;
                        halfWidth = Mathf.Max(halfWidth, Mathf.Abs(Vector3.Dot(local, right)));
                        halfHeight = Mathf.Max(halfHeight, Mathf.Abs(Vector3.Dot(local, up)));
                    }
                }
            }

            return halfWidth > 0.10f && halfHeight > 0.08f;
        }

        private static bool TryBuildScreenFrameFromCorners(
            Vector3[] corners,
            Vector3 center,
            Vector3 candidateNormal,
            Vector3 candidateRight,
            Vector3 candidateUp,
            out Vector3 normal,
            out Vector3 right,
            out Vector3 up,
            out float halfWidth,
            out float halfHeight)
        {
            normal = ResolveInteriorDirection(ResolveShipTransform(), center, candidateNormal);
            if (normal.sqrMagnitude < 1e-4f)
                normal = ResolveMainMonitorInward(center);
            if (normal.sqrMagnitude < 1e-4f)
            {
                right = Vector3.right;
                up = Vector3.up;
                halfWidth = 0f;
                halfHeight = 0f;
                return false;
            }

            normal.Normalize();
            right = Vector3.ProjectOnPlane(candidateRight, normal);
            if (right.sqrMagnitude < 1e-4f)
                right = Vector3.Cross(Vector3.up, normal);
            if (right.sqrMagnitude < 1e-4f)
                right = Vector3.right;
            right.Normalize();

            up = Vector3.ProjectOnPlane(candidateUp, normal);
            if (up.sqrMagnitude < 1e-4f)
                up = Vector3.Cross(normal, right);
            if (up.sqrMagnitude < 1e-4f)
                up = Vector3.up;
            up.Normalize();

            halfWidth = 0f;
            halfHeight = 0f;
            for (int i = 0; corners != null && i < corners.Length; i++)
            {
                Vector3 local = corners[i] - center;
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(Vector3.Dot(local, right)));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(Vector3.Dot(local, up)));
            }

            return halfWidth > 0.10f && halfHeight > 0.08f;
        }

        /// <summary>
        /// #598 — the main (CCTV feed) screen surface the player actually sees, plus the
        /// material slot on it. Under GeneralImprovements' better monitors the display
        /// layer's bound GI screen is authoritative and the vanilla <c>Cube.001</c> is
        /// renderer- and collider-disabled; measuring it would frame a hidden mesh. With GI
        /// absent (or better monitors off) this is exactly the pre-#598 vanilla resolve.
        /// </summary>
        private static bool TryResolveMainMonitorSurface(
            out MeshRenderer renderer,
            out int materialIndex,
            out bool generalImprovementsScreen)
        {
            if (CCTVVanillaMonitorDisplay.TryGetBoundFeedScreen(out renderer, out materialIndex, out generalImprovementsScreen) &&
                generalImprovementsScreen && renderer != null)
            {
                return true;
            }

            generalImprovementsScreen = false;
            if (IsGeneralImprovementsWallVisible())
            {
                // GI owns the wall but this feed has no screen on it. The vanilla mesh is
                // hidden, so there is nothing to measure — decline instead of framing a
                // surface the player cannot see.
                renderer = null;
                materialIndex = -1;
                return false;
            }

            renderer = TryResolveLowerLeftMonitorRenderer();
            if (renderer == null)
            {
                materialIndex = -1;
                return false;
            }

            materialIndex = ResolveLowerLeftMonitorMaterialIndex(renderer);
            return true;
        }

        /// <summary>#598 — as <see cref="TryResolveMainMonitorSurface"/>, for the radar screen.</summary>
        private static bool TryResolveRadarMonitorSurface(
            out MeshRenderer renderer,
            out int materialIndex,
            out bool generalImprovementsScreen)
        {
            if (CCTVVanillaMonitorDisplay.TryGetBoundRadarScreen(out renderer, out materialIndex, out generalImprovementsScreen) &&
                generalImprovementsScreen && renderer != null)
            {
                return true;
            }

            generalImprovementsScreen = false;
            if (IsGeneralImprovementsWallVisible())
            {
                renderer = null;
                materialIndex = -1;
                return false;
            }

            renderer = TryResolveLowerRightMonitorRenderer();
            if (renderer == null)
            {
                materialIndex = -1;
                return false;
            }

            materialIndex = ResolveRadarMonitorMaterialIndex(renderer);
            return true;
        }

        /// <summary>
        /// True while GeneralImprovements' replacement wall is the one on screen, which is
        /// exactly when the vanilla <c>Cube.001</c> / <c>SingleScreen</c> meshes are
        /// renderer- and collider-disabled and must not be used as anchor geometry.
        /// </summary>
        private static bool IsGeneralImprovementsWallVisible()
        {
            return GeneralImprovementsMonitorCompat.IsLoaded &&
                   GeneralImprovementsMonitorCompat.AreBetterMonitorsActive();
        }

        /// <summary>
        /// World bounds of one screen face.
        ///
        /// A multi-material renderer (the vanilla shared monitor mesh) only answers
        /// correctly per submesh — <c>renderer.bounds</c> spans several screens and its
        /// center sits on no screen at all. A GI screen is its own single-material mesh,
        /// so its whole-mesh bounds ARE the screen, and walking a submesh there would be
        /// both pointless and, with a stale vanilla material index, out of range.
        /// <paramref name="allowWholeMeshBounds"/> gates that second path so the vanilla
        /// resolve keeps its existing "submesh or nothing" behaviour unchanged.
        /// </summary>
        private static bool TryResolveScreenFaceBounds(
            MeshRenderer renderer,
            int materialIndex,
            bool allowWholeMeshBounds,
            out Bounds bounds)
        {
            bounds = default;
            if (renderer == null)
                return false;

            int slotCount;
            try { slotCount = renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0; }
            catch { slotCount = 0; }

            if (slotCount > 1 &&
                materialIndex >= 0 &&
                materialIndex < slotCount &&
                TryResolveRendererSubmeshBounds(renderer, materialIndex, out bounds))
            {
                return true;
            }

            if (!allowWholeMeshBounds)
                return false;

            bounds = renderer.bounds;
            return bounds.size.sqrMagnitude > 1e-6f;
        }

        // #716 C5. EnsureFocusAnchorAimed runs every 5s and each pass re-ran the scene-wide
        // GameObject.Find path walks below plus a FindObjectOfType<ShipTeleporter>, measured
        // at 3.31ms on a zero-camera pass. All four targets are static ship geometry, so the
        // resolved references are cached and only re-derived when the cache goes Unity-null
        // (scene reload / ship rebuild).
        private static MeshRenderer _cachedLowerLeftMonitorRenderer;
        private static MeshRenderer _cachedLowerRightMonitorRenderer;
        private static ShipTeleporter _cachedShipTeleporter;
        private static Transform _cachedShipTransform;

        private static MeshRenderer TryResolveLowerLeftMonitorRenderer()
        {
            if (_cachedLowerLeftMonitorRenderer != null)
                return _cachedLowerLeftMonitorRenderer;
            _cachedLowerLeftMonitorRenderer = ResolveLowerLeftMonitorRendererUncached();
            return _cachedLowerLeftMonitorRenderer;
        }

        private static MeshRenderer ResolveLowerLeftMonitorRendererUncached()
        {
            try
            {
                Transform monitor = StartOfRound.Instance != null && StartOfRound.Instance.elevatorTransform != null
                    ? StartOfRound.Instance.elevatorTransform.Find("ShipModels2b/MonitorWall/Cube.001")
                    : null;
                if (monitor != null)
                {
                    MeshRenderer renderer = monitor.GetComponent<MeshRenderer>();
                    if (renderer != null)
                        return renderer;
                }
            }
            catch { }

            GameObject cube = GameObject.Find("Environment/HangarShip/ShipModels2b/MonitorWall/Cube.001");
            return cube != null ? cube.GetComponent<MeshRenderer>() : null;
        }

        private static MeshRenderer TryResolveLowerRightMonitorRenderer()
        {
            if (_cachedLowerRightMonitorRenderer != null)
                return _cachedLowerRightMonitorRenderer;
            _cachedLowerRightMonitorRenderer = ResolveLowerRightMonitorRendererUncached();
            return _cachedLowerRightMonitorRenderer;
        }

        private static MeshRenderer ResolveLowerRightMonitorRendererUncached()
        {
            GameObject exact = GameObject.Find(LowerRightMonitorPath);
            MeshRenderer exactRenderer = exact != null ? exact.GetComponent<MeshRenderer>() : null;
            if (exactRenderer != null)
                return exactRenderer;

            Transform monitor = StartOfRound.Instance != null && StartOfRound.Instance.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.Find("ShipModels2b/MonitorWall/SingleScreen")
                : null;
            if (monitor != null)
            {
                MeshRenderer renderer = monitor.GetComponent<MeshRenderer>();
                if (renderer != null)
                    return renderer;
            }

            StartOfRound sor = StartOfRound.Instance;
            MeshRenderer fromInside = MonitorWallScreenOrNull(sor != null ? sor.insideCameraScreen : null);
            if (fromInside != null)
                return fromInside;
            return MonitorWallScreenOrNull(sor != null ? sor.securityCameraScreen : null);
        }

        private static MeshRenderer MonitorWallScreenOrNull(ManualCameraRenderer driver)
        {
            MeshRenderer renderer = driver != null ? driver.mesh : null;
            if (renderer == null)
                return null;

            MeshRenderer left = TryResolveLowerLeftMonitorRenderer();
            if (renderer == left)
                return null;

            string name = renderer.gameObject.name ?? string.Empty;
            if (name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            // Descendant, not direct child (#598): the vanilla screens hang straight off
            // MonitorWall, but GeneralImprovements' live screens sit at
            // MonitorWall/MonitorGroup(Clone)/Monitors/<group>/<screen>, and an exact
            // parent-name test rejected every one of them. The hydraulic-door monitor,
            // which this guard exists to exclude, is outside MonitorWall entirely.
            return IsUnderMonitorWall(renderer.transform) ? renderer : null;
        }

        private static bool IsUnderMonitorWall(Transform transform)
        {
            for (Transform current = transform != null ? transform.parent : null;
                 current != null;
                 current = current.parent)
            {
                if (string.Equals(current.name, "MonitorWall", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static int ResolveLowerLeftMonitorMaterialIndex(MeshRenderer renderer)
        {
            int materialCount;
            try { materialCount = renderer != null && renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0; }
            catch { materialCount = 0; }

            try
            {
                // mapScreen.materialIndex is a slot on mapScreen.mesh, so it only means
                // anything for THIS renderer when this renderer IS that mesh.
                // GeneralImprovements repoints mapScreen at its own single-material
                // BigMiddle frame, where the vanilla index is both wrong and out of
                // range (#598).
                StartOfRound sor = StartOfRound.Instance;
                ManualCameraRenderer mapScreen = sor != null ? sor.mapScreen : null;
                if (mapScreen != null &&
                    mapScreen.mesh == renderer &&
                    mapScreen.materialIndex >= 0 &&
                    mapScreen.materialIndex < materialCount)
                {
                    return mapScreen.materialIndex;
                }
            }
            catch { }

            // Vanilla Cube.001 screen slot. Declining beats indexing off the end of a
            // mesh this constant was never measured against.
            return materialCount > 1 ? 1 : -1;
        }

        private static bool TryResolveRendererSubmeshBounds(MeshRenderer renderer, int materialIndex, out Bounds bounds)
        {
            bounds = default;
            if (renderer == null || materialIndex < 0)
                return false;

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || materialIndex >= mesh.subMeshCount)
                return false;
            if (!mesh.isReadable)
                return false;

            int[] indices;
            try
            {
                indices = mesh.GetIndices(materialIndex);
            }
            catch
            {
                return false;
            }

            Vector3[] vertices = mesh.vertices;
            if (indices == null || indices.Length == 0 || vertices == null || vertices.Length == 0)
                return false;

            bool initialized = false;
            Transform transform = renderer.transform;
            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                if (index < 0 || index >= vertices.Length)
                    continue;

                Vector3 world = transform.TransformPoint(vertices[index]);
                if (!initialized)
                {
                    bounds = new Bounds(world, Vector3.zero);
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(world);
                }
            }

            return initialized;
        }

        private static bool IsPlausibleShipWorldPoint(Vector3 point)
        {
            if (float.IsNaN(point.x) || float.IsNaN(point.y) || float.IsNaN(point.z) ||
                float.IsInfinity(point.x) || float.IsInfinity(point.y) || float.IsInfinity(point.z))
                return false;

            Transform ship = ResolveShipTransform();
            if (ship != null && Mathf.Abs(point.y - ship.position.y) > 12f)
                return false;

            return point.y > -10f && point.y < 25f;
        }

        private static bool TryResolveStationPose(out Transform ship, out Vector3 worldPosition, out Quaternion worldRotation, out string source)
        {
            ship = ResolveShipTransform();
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            source = "unresolved";

            if (ship == null)
                return false;

            StartOfRound sor = StartOfRound.Instance;
            RectTransform profitRect = sor != null && sor.profitQuotaMonitorBGImage != null
                ? sor.profitQuotaMonitorBGImage.rectTransform
                : null;
            RectTransform deadlineRect = sor != null && sor.deadlineMonitorBGImage != null
                ? sor.deadlineMonitorBGImage.rectTransform
                : null;

            if (profitRect != null && deadlineRect != null)
            {
                Vector3 slotDelta = deadlineRect.position - profitRect.position;
                if (slotDelta.sqrMagnitude < 0.0001f)
                    slotDelta = deadlineRect.right * 0.75f;

                // Anchor on the PROFIT rect, not the deadline rect: monitor-styling
                // mods (Contracted) collapse the deadline rect onto the profit rect,
                // and every authored station placement was tuned against the root
                // that collapsed layout produced. Anchoring on profit yields that
                // same root under both the collapsed and the pure-vanilla layout
                // (vanilla's real 0.75m slot delta otherwise shifted the whole
                // station 0.75m sideways on fresh installs, #569).
                Vector3 monitorTarget = profitRect.position + slotDelta * StationSlotOffsetFromDeadline;
                Vector3 inward = ResolveInteriorDirection(ship, monitorTarget, deadlineRect.forward);
                Vector3 towardMonitor = -inward;
                towardMonitor.y = 0f;
                if (towardMonitor.sqrMagnitude < 0.0001f)
                    towardMonitor = ship.forward;
                towardMonitor.Normalize();

                worldPosition = monitorTarget + inward * StationDistanceFromMonitor + Vector3.down * StationDropFromTopMonitor;
                worldRotation = Quaternion.LookRotation(towardMonitor, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
                source =
                    $"top-monitor-ui monitorTarget={FormatVector(monitorTarget)} inward={FormatVector(inward)} " +
                    $"profitWorld={FormatVector(profitRect.position)} deadlineWorld={FormatVector(deadlineRect.position)}";
                return true;
            }

            Transform monitorWall = ResolveMonitorWall();
            if (monitorWall == null)
                return false;

            Vector3 fallbackTarget = monitorWall.position;
            Vector3 fallbackInward = ResolveInteriorDirection(ship, fallbackTarget, monitorWall.forward);
            Vector3 fallbackTowardMonitor = -fallbackInward;
            fallbackTowardMonitor.y = 0f;
            if (fallbackTowardMonitor.sqrMagnitude < 0.0001f)
                fallbackTowardMonitor = ship.forward;
            fallbackTowardMonitor.Normalize();

            worldPosition = fallbackTarget + fallbackInward * StationDistanceFromMonitor + Vector3.down * 1.2f;
            worldRotation = Quaternion.LookRotation(fallbackTowardMonitor, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
            source = $"monitor-wall-fallback monitorWallWorld={FormatVector(fallbackTarget)} inward={FormatVector(fallbackInward)}";
            return true;
        }

        private static Vector3 ResolveInteriorDirection(Transform ship, Vector3 monitorTarget, Vector3 candidateNormal)
        {
            Vector3 normal = candidateNormal;
            normal.y = 0f;
            if (normal.sqrMagnitude < 0.0001f)
                normal = ship.forward;
            normal.y = 0f;
            if (normal.sqrMagnitude < 0.0001f)
                normal = Vector3.forward;
            normal.Normalize();

            Vector3 interiorReference = ship.position;
            try
            {
                if (_cachedShipTeleporter == null)
                    _cachedShipTeleporter = UnityEngine.Object.FindObjectOfType<ShipTeleporter>();
                ShipTeleporter teleporter = _cachedShipTeleporter;
                if (teleporter != null)
                    interiorReference = teleporter.transform.position;
            }
            catch
            {
                interiorReference = ship.position;
            }

            Vector3 toInterior = interiorReference - monitorTarget;
            toInterior.y = 0f;
            if (toInterior.sqrMagnitude < 0.0001f)
                return normal;

            return Vector3.Dot(toInterior, normal) >= 0f ? normal : -normal;
        }

        private static Transform ResolveShipTransform()
        {
            if (StartOfRound.Instance != null && StartOfRound.Instance.elevatorTransform != null)
                return StartOfRound.Instance.elevatorTransform;

            if (_cachedShipTransform != null)
                return _cachedShipTransform;

            GameObject hangar = GameObject.Find("Environment/HangarShip");
            _cachedShipTransform = hangar != null ? hangar.transform : null;
            return _cachedShipTransform;
        }
    }
}
