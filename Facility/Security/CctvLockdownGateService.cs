using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Shared;
using SlidingLeafJambClipper = Y4NGZ.Compatibility.SlidingLeafJambClipper;
using Object = UnityEngine.Object;

namespace Y4NGZCompany.Facility.Security
{
    /// <summary>
    /// One lockdown boundary for both install shapes. Contracted remains the owner when its
    /// shared gate API is present; otherwise LethalCCTV stages local sliding gates from its own
    /// fixture bundle. Alarm state already replicates, so each client builds the same local-only
    /// presentation and no extra network prefab is required.
    /// </summary>
    internal static class CctvLockdownGateService
    {
        private const string GateAssetName = "ContainmentLockdownDoors";
        private const string SlamClipFileName = "freesound_community-metallic-door-shut-98740.mp3";
        private const float DoorClearanceM = 0.06f;
        private const float DoorSearchRadiusM = 5.5f;
        private const float DoorPanelThicknessM = 0.12f;
        // Mirrors Contracted's gate placement: the plate's back face sits this far in front of the
        // furthest door-side protrusion inside the opening (vanilla facility handles are separate
        // meshes reaching 0.12-0.17 m from the leaf centre), instead of 0.06 m off the leaf centre
        // where the handles poked straight through it.
        private const float HandleClearanceM = 0.02f;
        private const float FallbackFrontExtentM = 0.17f;
        private const float GateBlockerThicknessM = 0.28f;
        private const float MainFallbackWidthM = 2.98f;
        private const float MainFallbackHeightM = 3.07f;
        private const float FireFallbackWidthM = 1.51f;
        private const float FireFallbackHeightM = 3.28f;
        private const float MinimumWidthM = 1.1f;
        private const float MaximumWidthM = 4.6f;
        private const float MinimumHeightM = 1.9f;
        private const float MaximumHeightM = 5f;
        private const float MaximumVisualScale = 512f;

        private static readonly List<GameObject> PreparedGates = new List<GameObject>();
        private static readonly List<Vector3> PreparedPositions = new List<Vector3>();
        private static bool _standaloneActive;
        private static AudioClip _slamClip;
        private static bool _slamLoadRequested;
        private static bool _slamPending;
        private static bool _missingGateWarned;

        internal static bool LockdownActive =>
            ContractsBridge.HasLockdownGateApi
                ? ContractsBridge.LockdownActive
                : _standaloneActive;

        internal static void PrepareLockdownGates()
        {
            if (ContractsBridge.HasLockdownGateApi)
            {
                ContractsBridge.PrepareLockdownGates();
                return;
            }

            PurgeDestroyedGates();
            if (PreparedGates.Count > 0)
                return;

            EntranceTeleport[] entrances = Object.FindObjectsByType<EntranceTeleport>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            Array.Sort(entrances, CompareEntrances);
            Renderer[] renderers = Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            PreparedPositions.Clear();

            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null)
                    continue;
                Vector3 position = GetEntrancePosition(entrance);
                if (AlreadyPreparedAt(position))
                    continue;
                if (!TryCreateAnchor(entrance, renderers, out GateAnchor anchor))
                    continue;

                GameObject gate = CreateGate(anchor);
                if (gate == null)
                    continue;
                PreparedGates.Add(gate);
                PreparedPositions.Add(position);
            }

            EnsureSlamClip();
            if (PreparedGates.Count > 0)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Prepared {PreparedGates.Count} standalone alarm lockdown gate(s).");
            }
            else if (!_missingGateWarned)
            {
                _missingGateWarned = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] Standalone lockdown found no usable entrance endpoints; teleport blocking will still apply while the alarm is active.");
            }
        }

        internal static void BeginLockdown()
        {
            if (ContractsBridge.HasLockdownGateApi)
            {
                ContractsBridge.BeginLockdown();
                return;
            }

            PrepareLockdownGates();
            _standaloneActive = true;
            for (int i = 0; i < PreparedGates.Count; i++)
            {
                CctvLockdownGatePanel panel = PreparedGates[i]?.GetComponent<CctvLockdownGatePanel>();
                panel?.Close();
            }
        }

        internal static void EndLockdown()
        {
            if (ContractsBridge.HasLockdownGateApi)
            {
                ContractsBridge.EndLockdown();
                return;
            }

            _standaloneActive = false;
            _slamPending = false;
            for (int i = 0; i < PreparedGates.Count; i++)
            {
                GameObject gate = PreparedGates[i];
                if (gate == null)
                    continue;
                CctvLockdownGatePanel panel = gate.GetComponent<CctvLockdownGatePanel>();
                if (panel != null)
                    panel.OpenAndDestroy();
                else
                    Object.Destroy(gate);
            }
            PreparedGates.Clear();
            PreparedPositions.Clear();
        }

        internal static void PlayLockdownSlam()
        {
            if (ContractsBridge.HasLockdownGateApi)
            {
                ContractsBridge.PlayLockdownSlam();
                return;
            }

            _slamPending = true;
            EnsureSlamClip();
            if (_slamClip != null)
                PlayStandaloneSlam();
        }

        internal static void ResetRound()
        {
            if (ContractsBridge.HasLockdownGateApi)
                return;

            _standaloneActive = false;
            _slamPending = false;
            for (int i = 0; i < PreparedGates.Count; i++)
            {
                if (PreparedGates[i] != null)
                    Object.Destroy(PreparedGates[i]);
            }
            PreparedGates.Clear();
            PreparedPositions.Clear();
            _missingGateWarned = false;
        }

        private static void EnsureSlamClip()
        {
            if (_slamClip != null || _slamLoadRequested || SurveillanceBootstrap.Instance == null)
                return;

            _slamLoadRequested = true;
            FacilityAudioClipLoader.Request(
                SurveillanceBootstrap.Instance,
                SlamClipFileName,
                clip =>
                {
                    _slamClip = clip;
                    if (_slamPending && _standaloneActive && _slamClip != null)
                        PlayStandaloneSlam();
                });
        }

        private static void PlayStandaloneSlam()
        {
            if (_slamClip == null)
                return;
            _slamPending = false;
            for (int i = 0; i < PreparedGates.Count; i++)
            {
                GameObject gate = PreparedGates[i];
                if (gate == null)
                    continue;
                AudioSource source = gate.GetComponent<AudioSource>() ?? gate.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = 4f;
                source.maxDistance = 32f;
                source.volume = 0.9f;
                source.PlayOneShot(_slamClip, 1f);
            }
        }

        private static int CompareEntrances(EntranceTeleport a, EntranceTeleport b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            int cmp = a.entranceId.CompareTo(b.entranceId);
            if (cmp != 0) return cmp;
            cmp = a.isEntranceToBuilding.CompareTo(b.isEntranceToBuilding);
            if (cmp != 0) return cmp;
            Vector3 ap = GetEntrancePosition(a);
            Vector3 bp = GetEntrancePosition(b);
            cmp = Quantize(ap.x).CompareTo(Quantize(bp.x));
            if (cmp != 0) return cmp;
            cmp = Quantize(ap.z).CompareTo(Quantize(bp.z));
            if (cmp != 0) return cmp;
            return Quantize(ap.y).CompareTo(Quantize(bp.y));
        }

        private static int Quantize(float value) => Mathf.RoundToInt(value * 100f);

        private static bool AlreadyPreparedAt(Vector3 position)
        {
            for (int i = 0; i < PreparedPositions.Count; i++)
            {
                if (Vector3.SqrMagnitude(PreparedPositions[i] - position) < 0.0625f)
                    return true;
            }
            return false;
        }

        private static void PurgeDestroyedGates()
        {
            for (int i = PreparedGates.Count - 1; i >= 0; i--)
            {
                if (PreparedGates[i] == null)
                    PreparedGates.RemoveAt(i);
            }
            if (PreparedGates.Count == 0)
                PreparedPositions.Clear();
        }

        private static bool TryCreateAnchor(
            EntranceTeleport entrance,
            Renderer[] renderers,
            out GateAnchor anchor)
        {
            anchor = default;
            if (entrance == null)
                return false;

            bool mainEntrance = entrance.entranceId == 0;
            Vector3 entrancePosition = GetEntrancePosition(entrance);
            Renderer best = FindBestDoorRenderer(entrancePosition, renderers);
            Bounds bounds = best != null ? best.bounds : default;
            if (mainEntrance && !entrance.isEntranceToBuilding && TryGetTaggedMainDoor(out GameObject tagged))
            {
                Bounds taggedBounds = CalculateBounds(tagged);
                if (HorizontalDistance(taggedBounds.center, entrancePosition) <= DoorSearchRadiusM)
                {
                    best = tagged.GetComponentInChildren<Renderer>(true);
                    bounds = taggedBounds;
                }
            }

            if (best != null || bounds.size.sqrMagnitude > 1e-6f)
            {
                Vector3 forward = ResolveDoorForward(bounds, best != null ? best.transform : null, entrance);
                float width = ProjectedWidth(bounds, forward) - 0.06f;
                float height = bounds.size.y - 0.06f;
                bool measured = width >= MinimumWidthM && width <= MaximumWidthM
                    && height >= MinimumHeightM && height <= MaximumHeightM
                    && ProjectedDepth(bounds, forward) <= 1.4f;
                if (!measured)
                    GetFallbackSize(mainEntrance, out width, out height);
                Vector3 center = measured
                    ? bounds.center
                    : new Vector3(bounds.center.x, bounds.min.y + height * 0.5f, bounds.center.z);
                float frontExtent = MeasureDoorFrontExtent(renderers, bounds, forward);
                anchor = new GateAnchor(
                    center + forward * (frontExtent + HandleClearanceM + DoorPanelThicknessM * 0.5f),
                    Quaternion.LookRotation(forward, Vector3.up),
                    width,
                    height,
                    entrance.entranceId,
                    measured);
                return true;
            }

            Transform endpoint = entrance.entrancePoint != null ? entrance.entrancePoint : entrance.transform;
            if (endpoint == null)
                return false;
            GetFallbackSize(mainEntrance, out float fallbackWidth, out float fallbackHeight);
            Quaternion rotation = Quaternion.Euler(0f, endpoint.eulerAngles.y, 0f);
            Vector3 endpointForward = rotation * Vector3.forward;
            anchor = new GateAnchor(
                endpoint.position + endpointForward * DoorClearanceM + Vector3.up * (fallbackHeight * 0.5f),
                rotation,
                fallbackWidth,
                fallbackHeight,
                entrance.entranceId,
                measured: false);
            return true;
        }

        /// <summary>
        /// Furthest reach along <paramref name="forward"/> from the door centre of any renderer that
        /// sits inside the door's footprint (leaf, handles, push bars). Frames and walls are wider
        /// than the door and drop out; the result is never shallower than the deepest vanilla handle
        /// when nothing measurable is found.
        /// </summary>
        private static float MeasureDoorFrontExtent(Renderer[] renderers, Bounds door, Vector3 forward)
        {
            float extent = Mathf.Max(ProjectedDepth(door, forward) * 0.5f, 0f);
            bool measuredAny = false;
            if (renderers != null && door.size.sqrMagnitude > 1e-6f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
                float halfWidth = ProjectedWidth(door, forward) * 0.5f;
                const float margin = 0.06f;
                const float sizeMargin = 0.12f;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || !renderer.enabled || renderer.transform == null
                        || renderer.gameObject == null || !renderer.gameObject.activeInHierarchy)
                        continue;
                    if (!TryGetMeshBounds(renderer, out Bounds mesh))
                        continue;
                    Bounds world = default;
                    bool initialized = false;
                    EncapsulateTransformed(mesh, renderer.transform.localToWorldMatrix, ref world, ref initialized);
                    if (!initialized)
                        continue;
                    if (HorizontalDistance(world.center, door.center) > DoorSearchRadiusM)
                        continue;
                    string descriptor = GetRendererDescriptor(renderer);
                    if (descriptor.Contains("lethalcctv_alarmlockdowngate")
                        || descriptor.Contains("containmentlockdowndoors"))
                        continue;
                    float lateral = Mathf.Abs(Vector3.Dot(world.center - door.center, right));
                    if (lateral > halfWidth + margin)
                        continue;
                    if (world.max.y < door.min.y - margin || world.min.y > door.max.y + margin)
                        continue;
                    if (ProjectedWidth(world, forward) > ProjectedWidth(door, forward) + sizeMargin
                        || world.size.y > door.size.y + sizeMargin)
                        continue;

                    Vector3 min = world.min;
                    Vector3 max = world.max;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 point = new Vector3(
                            (corner & 1) == 0 ? min.x : max.x,
                            (corner & 2) == 0 ? min.y : max.y,
                            (corner & 4) == 0 ? min.z : max.z);
                        extent = Mathf.Max(extent, Vector3.Dot(point - door.center, forward));
                        measuredAny = true;
                    }
                }
            }

            return measuredAny ? extent : Mathf.Max(extent, FallbackFrontExtentM);
        }

        private static Renderer FindBestDoorRenderer(Vector3 entrancePosition, Renderer[] renderers)
        {
            Renderer best = null;
            float bestScore = float.NegativeInfinity;
            string bestDescriptor = null;
            if (renderers == null)
                return null;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || renderer.transform == null
                    || renderer.gameObject == null || !renderer.gameObject.activeInHierarchy)
                    continue;
                Bounds bounds;
                try { bounds = renderer.bounds; }
                catch { continue; }
                string descriptor = GetRendererDescriptor(renderer);
                if (descriptor.Contains("lethalcctv_alarmlockdowngate")
                    || descriptor.Contains("containmentlockdowndoors"))
                    continue;
                if (!TryScoreDoor(bounds, descriptor, entrancePosition, out float score))
                    continue;
                if (score < bestScore - 0.001f)
                    continue;
                if (Mathf.Abs(score - bestScore) <= 0.001f
                    && bestDescriptor != null
                    && string.CompareOrdinal(descriptor, bestDescriptor) >= 0)
                    continue;
                best = renderer;
                bestScore = score;
                bestDescriptor = descriptor;
            }
            return best;
        }

        private static bool TryScoreDoor(Bounds bounds, string descriptor, Vector3 entrancePosition, out float score)
        {
            score = 0f;
            float height = bounds.size.y;
            float span = Mathf.Max(bounds.size.x, bounds.size.z);
            float thinness = Mathf.Min(bounds.size.x, bounds.size.z);
            float distance = HorizontalDistance(bounds.center, entrancePosition);
            if (distance > DoorSearchRadiusM
                || Mathf.Abs(bounds.center.y - (entrancePosition.y + 1.55f)) > 3.25f
                || height < 1.55f || height > 5.4f
                || span < 0.65f || span > 4.8f
                || thinness > 2.2f)
                return false;

            if (descriptor.Contains("door")) score += 8f;
            if (descriptor.Contains("fire")) score += 5f;
            if (descriptor.Contains("exit")) score += 5f;
            if (descriptor.Contains("entrance")) score += 3f;
            if (descriptor.Contains("frame")) score += 1.5f;
            score -= distance * 1.35f;
            score -= Mathf.Abs(height - 2.8f) * 0.45f;
            score -= Mathf.Max(0f, span - 2.6f) * 1.4f;
            score -= thinness * 0.25f;
            return score > -6f;
        }

        private static GameObject CreateGate(GateAnchor anchor)
        {
            GameObject root = new GameObject("LethalCCTV_AlarmLockdownGate");
            root.transform.SetPositionAndRotation(anchor.Center, anchor.Rotation);
            GameObject prefab = CctvFixtureAssets.GetBundledAsset<GameObject>(GateAssetName);
            List<CctvLockdownGatePanel.SlidingHalf> halves = null;
            bool authored = prefab != null
                && CreateAuthoredVisual(root, prefab, anchor, out halves);
            if (!authored)
                halves = CreateFallbackVisual(root, anchor);
            if (halves == null || halves.Count < 2)
            {
                Object.Destroy(root);
                return null;
            }

            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.FreezeAll;
            NavMeshObstacle obstacle = root.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = Vector3.zero;
            obstacle.size = new Vector3(anchor.Width, anchor.Height, GateBlockerThicknessM);
            obstacle.carving = true;
            obstacle.carveOnlyStationary = true;
            obstacle.enabled = false;
            SetLayerRecursively(root, ResolvePhysicsLayer());

            CctvLockdownGatePanel panel = root.AddComponent<CctvLockdownGatePanel>();
            panel.Prepare(halves, obstacle);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Standalone lockdown gate staged entranceId={anchor.EntranceId} "
                + $"size=({anchor.Width:F2},{anchor.Height:F2}) measured={anchor.Measured} asset={(authored ? GateAssetName : "generated-fallback")}.");
            return root;
        }

        private static bool CreateAuthoredVisual(
            GameObject root,
            GameObject prefab,
            GateAnchor anchor,
            out List<CctvLockdownGatePanel.SlidingHalf> halves)
        {
            halves = null;
            GameObject visual = Object.Instantiate(prefab, root.transform);
            visual.name = GateAssetName;
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            SetActiveRecursively(visual, true);
            PrepareRenderers(visual);
            ApplySolidMaterials(visual);
            if (!TryCalculateLocalRendererBounds(visual.transform, out Bounds bounds)
                || bounds.size.x <= 0.001f || bounds.size.y <= 0.001f)
            {
                Object.Destroy(visual);
                return false;
            }

            Vector3 scale = new Vector3(
                Mathf.Clamp(anchor.Width / bounds.size.x, 0.05f, MaximumVisualScale),
                Mathf.Clamp(anchor.Height / bounds.size.y, 0.05f, MaximumVisualScale),
                bounds.size.z > 0.001f
                    ? Mathf.Clamp(DoorPanelThicknessM / bounds.size.z, 0.01f, MaximumVisualScale)
                    : 1f);
            visual.transform.localScale = scale;
            visual.transform.localPosition = -Vector3.Scale(bounds.center, scale);
            RemovePhysics(visual);
            halves = ResolveSlidingHalves(visual.transform, scale);
            if (halves.Count >= 2)
                return true;

            Object.Destroy(visual);
            halves = null;
            return false;
        }

        private static List<CctvLockdownGatePanel.SlidingHalf> CreateFallbackVisual(GameObject root, GateAnchor anchor)
        {
            List<CctvLockdownGatePanel.SlidingHalf> halves = new List<CctvLockdownGatePanel.SlidingHalf>(2);
            float halfWidth = anchor.Width * 0.5f;
            for (int i = 0; i < 2; i++)
            {
                float direction = i == 0 ? -1f : 1f;
                GameObject half = GameObject.CreatePrimitive(PrimitiveType.Cube);
                half.name = i == 0 ? "LockdownDoorLeft" : "LockdownDoorRight";
                half.transform.SetParent(root.transform, worldPositionStays: false);
                half.transform.localRotation = Quaternion.identity;
                half.transform.localScale = new Vector3(halfWidth, anchor.Height, DoorPanelThicknessM);
                Vector3 closed = Vector3.right * (direction * anchor.Width * 0.25f);
                Vector3 open = closed + Vector3.right * (direction * halfWidth);
                half.transform.localPosition = closed;
                Renderer renderer = half.GetComponent<Renderer>();
                if (renderer != null && renderer.material != null)
                    renderer.material.color = new Color(0.12f, 0.14f, 0.16f, 1f);
                SlidingLeafJambClipper clipper = SlidingLeafJambClipper.TryCreate(
                    half.transform, root.transform, direction, direction * anchor.Width * 0.5f, out _);
                halves.Add(new CctvLockdownGatePanel.SlidingHalf(half.transform, closed, open, clipper));
            }
            return halves;
        }

        private static List<CctvLockdownGatePanel.SlidingHalf> ResolveSlidingHalves(Transform visual, Vector3 visualScale)
        {
            List<CctvLockdownGatePanel.SlidingHalf> halves = new List<CctvLockdownGatePanel.SlidingHalf>();
            for (int i = 0; i < visual.childCount; i++)
            {
                Transform child = visual.GetChild(i);
                if (child == null || !TrySubtreeBoundsInSpace(visual, child, out Bounds inVisual))
                    continue;
                if (inVisual.size.x <= 0.001f)
                    continue;
                float direction = inVisual.center.x >= 0f ? 1f : -1f;
                Vector3 closed = child.localPosition;
                Vector3 open = closed + Vector3.right * (direction * inVisual.size.x);
                AddHalfCollider(child, visualScale);
                // #827: clip the leaf at its closed outer edge (the jamb plane) while it travels,
                // so it grows out of the frame instead of popping into view on the wall.
                float jambX = direction > 0f ? inVisual.max.x : inVisual.min.x;
                SlidingLeafJambClipper clipper = SlidingLeafJambClipper.TryCreate(child, visual, direction, jambX, out string clipFailure);
                if (clipper == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] Lockdown gate half '{child.name}' cannot be jamb-clipped ({clipFailure}); it will slide unclipped.");
                }
                halves.Add(new CctvLockdownGatePanel.SlidingHalf(child, closed, open, clipper));
            }
            return halves;
        }

        private static void AddHalfCollider(Transform half, Vector3 visualScale)
        {
            if (!TrySubtreeBoundsInSpace(half, half, out Bounds bounds))
                return;
            float zScale = Mathf.Abs(visualScale.z);
            float depth = zScale > 0.001f
                ? Mathf.Max(bounds.size.z, GateBlockerThicknessM / zScale)
                : bounds.size.z;
            BoxCollider collider = half.gameObject.AddComponent<BoxCollider>();
            collider.center = bounds.center;
            collider.size = new Vector3(bounds.size.x, bounds.size.y, depth);
        }

        private static void RemovePhysics(GameObject root)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null) continue;
                colliders[i].enabled = false;
                Object.Destroy(colliders[i]);
            }
            Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i] == null) continue;
                bodies[i].detectCollisions = false;
                bodies[i].isKinematic = true;
                Object.Destroy(bodies[i]);
            }
        }

        private static void ApplySolidMaterials(GameObject visual)
        {
            Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null) continue;
                Material[] sources = renderer.sharedMaterials;
                Material[] runtime = new Material[sources.Length];
                for (int i = 0; i < sources.Length; i++)
                {
                    Material source = sources[i];
                    if (source == null) continue;
                    Material material = new Material(source) { name = source.name + "_CctvSolid" };
                    material.renderQueue = (int)RenderQueue.Geometry;
                    material.SetOverrideTag("RenderType", "Opaque");
                    SetFloat(material, "_SurfaceType", 0f);
                    SetFloat(material, "_SrcBlend", (float)BlendMode.One);
                    SetFloat(material, "_DstBlend", (float)BlendMode.Zero);
                    SetFloat(material, "_ZWrite", 1f);
                    SetFloat(material, "_AlphaCutoffEnable", 0f);
                    SetFloat(material, "_Cull", (float)CullMode.Off);
                    SetFloat(material, "_DoubleSidedEnable", 1f);
                    material.DisableKeyword("_ALPHATEST_ON");
                    material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.EnableKeyword("_DOUBLESIDED_ON");
                    runtime[i] = material;
                }
                renderer.sharedMaterials = runtime;
            }
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void PrepareRenderers(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
        }

        private static bool TryCalculateLocalRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                if (TryGetMeshBounds(renderer, out Bounds mesh))
                {
                    Matrix4x4 matrix = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                    EncapsulateTransformed(mesh, matrix, ref bounds, ref initialized);
                }
                else
                {
                    EncapsulateTransformed(renderer.bounds, root.worldToLocalMatrix, ref bounds, ref initialized);
                }
            }
            return initialized;
        }

        private static bool TrySubtreeBoundsInSpace(Transform space, Transform subtree, out Bounds bounds)
        {
            bounds = default;
            bool initialized = false;
            Renderer[] renderers = subtree.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !TryGetMeshBounds(renderer, out Bounds mesh))
                    continue;
                Matrix4x4 matrix = space.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                EncapsulateTransformed(mesh, matrix, ref bounds, ref initialized);
            }
            return initialized;
        }

        private static bool TryGetMeshBounds(Renderer renderer, out Bounds bounds)
        {
            bounds = default;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                bounds = skinned.localBounds;
                return bounds.size.sqrMagnitude > 0.000001f;
            }
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                return false;
            bounds = filter.sharedMesh.bounds;
            return bounds.size.sqrMagnitude > 0.000001f;
        }

        private static void EncapsulateTransformed(
            Bounds source,
            Matrix4x4 matrix,
            ref Bounds target,
            ref bool initialized)
        {
            Vector3 min = source.min;
            Vector3 max = source.max;
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            {
                Vector3 point = matrix.MultiplyPoint3x4(new Vector3(
                    x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z));
                if (!initialized)
                {
                    target = new Bounds(point, Vector3.zero);
                    initialized = true;
                }
                else target.Encapsulate(point);
            }
        }

        private static void SetActiveRecursively(GameObject root, bool active)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
                if (transforms[i] != null) transforms[i].gameObject.SetActive(active);
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
                if (transforms[i] != null) transforms[i].gameObject.layer = layer;
        }

        private static int ResolvePhysicsLayer()
        {
            int layer = LayerMask.NameToLayer("Default");
            if (layer >= 0) return layer;
            layer = LayerMask.NameToLayer("Room");
            return layer >= 0 ? layer : 0;
        }

        private static bool TryGetTaggedMainDoor(out GameObject door)
        {
            door = null;
            try { door = GameObject.FindGameObjectWithTag("InsideEntranceDoor"); }
            catch { }
            return door != null;
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return default;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                if (renderers[i] != null) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static Vector3 ResolveDoorForward(Bounds bounds, Transform renderer, EntranceTeleport entrance)
        {
            Vector3 forward = renderer != null ? renderer.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            float x = Mathf.Abs(bounds.size.x);
            float z = Mathf.Abs(bounds.size.z);
            float largest = Mathf.Max(x, z);
            if (largest > 0.001f && Mathf.Min(x, z) / largest < 0.55f)
                forward = x <= z ? Vector3.right : Vector3.forward;
            Vector3 towardEntrance = GetEntrancePosition(entrance) - bounds.center;
            towardEntrance.y = 0f;
            if (towardEntrance.sqrMagnitude > 0.001f && Vector3.Dot(forward, towardEntrance) < 0f)
                forward = -forward;
            return forward.normalized;
        }

        private static float ProjectedWidth(Bounds bounds, Vector3 forward)
        {
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            return Mathf.Abs(right.x) * bounds.size.x + Mathf.Abs(right.z) * bounds.size.z;
        }

        private static float ProjectedDepth(Bounds bounds, Vector3 forward)
        {
            return Mathf.Abs(forward.x) * bounds.size.x + Mathf.Abs(forward.z) * bounds.size.z;
        }

        private static void GetFallbackSize(bool main, out float width, out float height)
        {
            width = main ? MainFallbackWidthM : FireFallbackWidthM;
            height = main ? MainFallbackHeightM : FireFallbackHeightM;
        }

        private static string GetRendererDescriptor(Renderer renderer)
        {
            string value = renderer.name + " ";
            Transform current = renderer.transform;
            for (int i = 0; current != null && i < 5; i++, current = current.parent)
                value += current.name + " ";
            return value.ToLowerInvariant();
        }

        private static Vector3 GetEntrancePosition(EntranceTeleport entrance)
        {
            return entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        private readonly struct GateAnchor
        {
            internal readonly Vector3 Center;
            internal readonly Quaternion Rotation;
            internal readonly float Width;
            internal readonly float Height;
            internal readonly int EntranceId;
            internal readonly bool Measured;

            internal GateAnchor(Vector3 center, Quaternion rotation, float width, float height, int entranceId, bool measured)
            {
                Center = center;
                Rotation = rotation;
                Width = width;
                Height = height;
                EntranceId = entranceId;
                Measured = measured;
            }
        }
    }

    internal sealed class CctvLockdownGatePanel : MonoBehaviour
    {
        private const float CloseDuration = 0.28f;
        private const float OpenDuration = 0.42f;

        internal readonly struct SlidingHalf
        {
            internal readonly Transform Transform;
            internal readonly Vector3 ClosedLocalPosition;
            internal readonly Vector3 OpenLocalPosition;
            /// <summary>Hides the part of the leaf outside the jamb while it travels; null keeps a plain slide.</summary>
            internal readonly SlidingLeafJambClipper Clipper;

            internal SlidingHalf(Transform transform, Vector3 closed, Vector3 open, SlidingLeafJambClipper clipper = null)
            {
                Transform = transform;
                ClosedLocalPosition = closed;
                OpenLocalPosition = open;
                Clipper = clipper;
            }
        }

        private readonly List<SlidingHalf> _halves = new List<SlidingHalf>();
        private NavMeshObstacle _obstacle;
        private Coroutine _routine;

        internal void Prepare(List<SlidingHalf> halves, NavMeshObstacle obstacle)
        {
            _halves.Clear();
            _halves.AddRange(halves);
            _obstacle = obstacle;
            for (int i = 0; i < _halves.Count; i++)
                if (_halves[i].Transform != null) _halves[i].Transform.localPosition = _halves[i].OpenLocalPosition;
            ApplyJambClips();
            SetObstacle(false);
            SetVisible(false);
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _halves.Count; i++) _halves[i].Clipper?.Dispose();
        }

        // Runs before the renderers are shown and after every position write, so no frame ever
        // draws a leaf outside the opening.
        private void ApplyJambClips()
        {
            for (int i = 0; i < _halves.Count; i++) _halves[i].Clipper?.Apply();
        }

        internal void Close()
        {
            ApplyJambClips();
            SetVisible(true);
            Restart(Animate(closing: true, CloseDuration, destroyWhenDone: false));
        }

        internal void OpenAndDestroy()
        {
            SetObstacle(false);
            Restart(Animate(closing: false, OpenDuration, destroyWhenDone: true));
        }

        private void Restart(IEnumerator routine)
        {
            if (_routine != null) StopCoroutine(_routine);
            _routine = StartCoroutine(routine);
        }

        private IEnumerator Animate(bool closing, float duration, bool destroyWhenDone)
        {
            Vector3[] from = new Vector3[_halves.Count];
            Vector3[] to = new Vector3[_halves.Count];
            for (int i = 0; i < _halves.Count; i++)
            {
                from[i] = _halves[i].Transform != null ? _halves[i].Transform.localPosition : Vector3.zero;
                to[i] = closing ? _halves[i].ClosedLocalPosition : _halves[i].OpenLocalPosition;
            }
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = 1f - Mathf.Pow(1f - Mathf.Clamp01(elapsed / duration), 3f);
                for (int i = 0; i < _halves.Count; i++)
                    if (_halves[i].Transform != null) _halves[i].Transform.localPosition = Vector3.Lerp(from[i], to[i], t);
                ApplyJambClips();
                yield return null;
            }
            for (int i = 0; i < _halves.Count; i++)
                if (_halves[i].Transform != null) _halves[i].Transform.localPosition = to[i];
            ApplyJambClips();
            if (closing) SetObstacle(true);
            else SetVisible(false);
            if (destroyWhenDone) Destroy(gameObject);
        }

        private void SetVisible(bool visible)
        {
            for (int i = 0; i < _halves.Count; i++)
            {
                Transform half = _halves[i].Transform;
                if (half == null) continue;
                Renderer[] renderers = half.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++) if (renderers[r] != null) renderers[r].enabled = visible;
                Collider[] colliders = half.GetComponentsInChildren<Collider>(true);
                for (int c = 0; c < colliders.Length; c++) if (colliders[c] != null) colliders[c].enabled = visible;
            }
        }

        private void SetObstacle(bool enabled)
        {
            if (_obstacle != null) _obstacle.enabled = enabled;
        }
    }

    [HarmonyPatch(typeof(EntranceTeleport), "TeleportPlayer")]
    internal static class CctvAlarmLockdownTeleportPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !CctvLockdownGateService.LockdownActive;
        }
    }
}
