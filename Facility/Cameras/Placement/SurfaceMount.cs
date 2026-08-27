using System.Collections.Generic;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // 0.0.36 surface-mount placement — the real-surface candidate generator.
    //
    // Replaced the legacy synthetic-AABB-corner scorer/aimer model (deleted
    // 2026-08-05) with one that RAYCASTS against actual dungeon collision
    // geometry to find physical mount surfaces, then gates each candidate on
    // whether it is (a) physically supported, (b) not embedded in a collider,
    // and (c) shows a useful, non-void, non-near-wall default view. The
    // spawner consumes the ranked candidate list and runs redundancy
    // suppression on top. This is the ONLY placement path — there is no
    // fallback scorer.
    //
    // Acceptance properties this module enforces, BY CONSTRUCTION:
    //   • No floating cameras — every candidate's anchor comes from a
    //     raycast hit on the MountMask and is inset a fixed small distance
    //     back along the surface normal into open air. No hit ⇒ no candidate.
    //   • No in-wall cameras — a CheckSphere on the SolidMask at the final
    //     point must be empty.
    //   • No floor cameras / no skyward shafts — floor-facing surfaces
    //     (normal points up) are rejected; mount height above the entry
    //     floor is clamped to a plausible ceiling/wall band.
    //   • No near-wall / skybox default views — center useful-distance gate
    //     plus a deterministic frustum sweep that rejects views whose rays
    //     mostly escape to open void.
    //
    // Determinism: candidate origins, probe directions, and frustum samples
    // are fixed compile-time sets. Physics queries against static geometry
    // are deterministic. Ranking is by score with stable integer tiebreaks
    // (kind, origin index, direction index, hit-name ordinal). No Time, no
    // Random, no GetInstanceID. Host and client run identical builds against
    // identical DunGen-seeded geometry ⇒ identical candidate sets.
    internal static class SurfaceMount
    {
        internal enum SurfaceKind { Ceiling = 0, Corner = 1, Wall = 2 }

        internal readonly struct Candidate
        {
            public readonly Vector3 WorldPos;
            public readonly Quaternion WorldRot;
            public readonly Vector3 Forward;          // cached WorldRot * forward
            public readonly CameraMountMode MountMode; // Ceiling | Wall
            public readonly SurfaceKind Kind;
            public readonly float Score;

            // diagnostics
            public readonly float MountHeightAboveFloorM;
            public readonly float CenterUsefulDistM;
            public readonly int FrustumHits;
            public readonly int FrustumVoids;
            public readonly int FrustumNearWall;
            public readonly Vector3 SupportNormal;     // world, points into room
            public readonly string HitName;
            public readonly int HitLayer;
            public readonly int CornerId;              // packed kind*100+origin*10+dir for log readback
            public readonly Vector3 CornerLocal;       // support normal stash (diagnostic field reuse)
            public readonly float RoomHeightM;
            public readonly float DoorwayVisibilityScore;
            public readonly float RoomFramingScore;
            public readonly string AimProfile;

            public Candidate(
                Vector3 worldPos, Quaternion worldRot, Vector3 forward,
                CameraMountMode mountMode, SurfaceKind kind, float score,
                float mountHeight, float centerUsefulDist,
                int frustumHits, int frustumVoids, int frustumNearWall,
                Vector3 supportNormal, string hitName, int hitLayer,
                int cornerId, float roomHeightM,
                float doorwayVisibilityScore = 0f,
                float roomFramingScore = 0f,
                string aimProfile = "default")
            {
                WorldPos = worldPos;
                WorldRot = worldRot;
                Forward = forward;
                MountMode = mountMode;
                Kind = kind;
                Score = score;
                MountHeightAboveFloorM = mountHeight;
                CenterUsefulDistM = centerUsefulDist;
                FrustumHits = frustumHits;
                FrustumVoids = frustumVoids;
                FrustumNearWall = frustumNearWall;
                SupportNormal = supportNormal;
                HitName = hitName ?? "<null>";
                HitLayer = hitLayer;
                CornerId = cornerId;
                CornerLocal = supportNormal;
                RoomHeightM = roomHeightM;
                DoorwayVisibilityScore = doorwayVisibilityScore;
                RoomFramingScore = roomFramingScore;
                AimProfile = string.IsNullOrEmpty(aimProfile) ? "default" : aimProfile;
            }
        }

        internal readonly struct SafetyGateResult
        {
            public readonly Vector3 Forward;
            public readonly Quaternion WorldRot;
            public readonly float MountHeightAboveFloorM;
            public readonly float CenterUsefulDistM;
            public readonly int FrustumHits;
            public readonly int FrustumVoids;
            public readonly int FrustumNearWall;

            public SafetyGateResult(
                Vector3 forward, Quaternion worldRot, float mountHeightAboveFloorM,
                float centerUsefulDistM, int frustumHits, int frustumVoids, int frustumNearWall)
            {
                Forward = forward;
                WorldRot = worldRot;
                MountHeightAboveFloorM = mountHeightAboveFloorM;
                CenterUsefulDistM = centerUsefulDistM;
                FrustumHits = frustumHits;
                FrustumVoids = frustumVoids;
                FrustumNearWall = frustumNearWall;
            }
        }
        // ---- origin sampling (tile-local XZ) -------------------------------
        private const float OriginQuarterFrac = 0.45f; // quarter points at ±0.45*ext
        private const float CeilProbeHeightM = 1.6f;   // up-cast origin height above entry floor
        private const float OriginClearRadiusM = 0.25f;

        // ---- surface classification ----------------------------------------
        // hit.normal.y < -CeilingNormalMinDownY  → ceiling (faces down into room)
        // |hit.normal.y| < WallNormalMaxAbsY      → wall (vertical-ish)
        // hit.normal.y >  WallNormalMaxAbsY       → floor (rejected; this is the
        //                                            "camera on the floor" bug)
        private const float CeilingNormalMinDownY = 0.5f;
        private const float WallNormalMaxAbsY = 0.5f;

        // ---- mount geometry ------------------------------------------------
        // Must be greater than MountClearRadiusM: the follow-up CheckSphere
        // verifies the camera body is not embedded, so centering the sphere
        // closer to the surface than its radius would reject every legitimate
        // flat wall/ceiling hit by overlapping the very surface we snapped to.
        private const float SurfaceInsetM = 0.28f;     // inward offset along normal
        private const float MinSurfaceDistM = 1.0f;    // ignore surfaces nearer than this to origin
        private const float MaxUpCastM = 22f;
        private const float HorizCastMarginM = 1.0f;
        private const float MountClearRadiusM = 0.16f; // embedding check radius
        private static readonly Vector3 CameraBodyOverlapHalfExtentsM = new Vector3(0.22f, 0.18f, 0.30f);
        // The mounting plate is 0.36 x 0.24 m. Probe slightly beyond it so every
        // accepted procedural pose has continuous wall/ceiling support under the
        // complete asset instead of merely touching at its origin near an edge.
        private const float SupportFootprintHalfWidthM = 0.21f;
        private const float SupportFootprintHalfHeightM = 0.15f;
        private const float SupportNormalMinDot = 0.85f;
        private const float CornerProbeM = 1.6f;       // perpendicular wall probe for corner-ness

        // ---- mount-height window above entry floor -------------------------
        private const float MinMountHeightM = 2.3f;    // clearly above player eye level
        private const float MaxMountHeightM = 6.5f;    // taller ⇒ a shaft roof, not a room ceiling

        // Hallway tier. Many hallway tiles have ceilings below the 3.0m
        // default wall-probe height, so a 3.0m probe generates zero wall
        // candidates there (the probe origin's clear-sphere sits inside the
        // ceiling before the height gate is ever consulted). The hallway
        // probe therefore runs lower than the room default — but above eye
        // level: head-height hallway cameras read as floating props, not
        // ceiling-corner security hardware. On hallway-shaped tiles the wall
        // probe ALWAYS runs at this height and the min-height gate follows.
        private const float HallwayWallProbeHeightM = 2.4f;
        private const float HallwayMinMountHeightM = 2.0f;

        // Extra wall-probe heights swept above the base probe. The wall hit
        // lands at the same Y as the probe origin, so without a sweep no
        // candidate above the single probe height can ever exist — which is
        // why cameras used to cluster at one height. Tiers whose origin
        // would sit inside/above the tile ceiling are skipped cheaply.
        private static readonly float[] RoomProbeHeightStepsM = { 0f, 0.9f, 1.8f };
        private static readonly float[] HallwayProbeHeightStepsM = { 0f, 0.8f };
        private const float ProbeTierCeilingClearM = 0.3f;

        // Large-room tier: rooms with a big footprint read best from a high
        // vantage, so the wall probe climbs toward the ceiling (still inside
        // the MaxMountHeightM gate) instead of staying at door height.
        private const float LargeRoomMinAreaM2 = 60f;
        private const float LargeRoomProbeHeightFraction = 0.55f;
        private const float LargeRoomMaxProbeHeightM = 4.3f;

        // Hallway doorway-header mounts: the deliberate hallway camera spot
        // is the wall directly above a doorway opening, looking down the
        // corridor — mirrors the alarm bar's above-opening logic.
        private const float DoorwayHeaderDefaultTopM = 2.3f;
        private const float DoorwayHeaderMaxTopM = 3.2f;
        private const float DoorwayHeaderClearanceM = 0.3f;
        private const float DoorwayHeaderFillerFlushToleranceM = 0.06f;

        // ---- aim ------------------------------------------------------------
        private const float WallAimPitchDownDeg = 16f;
        private const float CeilAimHeightAboveFloorM = 0.6f;
        private const float MaxPitchUpDeg = 8f;
        private const float MaxPitchDownDeg = 70f;

        // ---- view quality ---------------------------------------------------
        private const float MinUsefulCenterDistM = 2.5f;
        private const float MinUsefulCenterDistRelaxedM = 1.2f;
        private const float NearWallKneeM = 2.0f;
        // Hardcoded (NOT the local FarClipPlane config) so the not-skybox
        // verdict — which decides whether a camera is KEPT, i.e. affects
        // camera count — is identical on host and client regardless of each
        // player's local far-clip setting.
        private const float VoidProbeDistM = 40f;
        private const float HalfFovDeg = 37.5f;        // ~75° FOV default
        private const float VoidRayMaxFrac = 0.5f;     // >half rays escaping ⇒ reject

        // ---- within-tile dedup (so the redundancy fallback list is diverse) -
        private const float DupPosM = 1.0f;
        private const float DupDot = 0.97f;
        private const float TileLocalMountMarginM = 0.35f;

        // ---- scoring --------------------------------------------------------
        private const float ScoreCeilingBase = 80f;
        private const float ScoreCornerBase = 520f;
        private const float ScoreWallBase = 360f;
        // High enough that a genuinely higher vantage beats a same-kind
        // low mount (1m of height = 40 pts), still far below the kind/
        // profile bonuses so it re-ranks within a tier, not across tiers.
        private const float ScoreHeightWeight = 40f;
        private const float ScoreUsefulDistWeight = 4f;
        private const float ScoreUsefulDistCapM = 18f;
        private const float ScoreFrustumWeight = 120f;
        private const float ScoreNearWallPenalty = 60f;
        private const float ScoreDoorwayVisibleWeight = 1.0f;
        private const float ScoreRoomFramingWeight = 180f;
        private const float ScoreHallwayProfileBonus = 420f;
        // Doorway-header mounts are THE hallway look; they must outrank a
        // plain hallway-long wall mount from the origin sweep.
        private const float ScoreDoorwayHeaderBonus = 620f;
        // A candidate that can actually see the tile's focus point (main
        // entrance door, fixture) must beat every generic vantage.
        private const float ScoreFocusTargetBonus = 900f;
        private const float GrandRoomMinFootprintM2 = 55f;
        private const float GrandRoomBackWallFraction = 0.72f;
        private const float GrandRoomTargetHeightM = 1.4f;
        private const float GrandRoomScoreBonus = 2000f;

        // Deterministic frustum sample offsets (yawDeg, pitchDeg) within the
        // ~75° cone. Index 0 is the center ray. Same fixed set every call.
        private static readonly Vector2[] s_frustumOffsets =
        {
            new Vector2(0f, 0f),
            new Vector2(25f, 0f), new Vector2(-25f, 0f),
            new Vector2(0f, 18f), new Vector2(0f, -18f),
            new Vector2(25f, 18f), new Vector2(25f, -18f),
            new Vector2(-25f, 18f), new Vector2(-25f, -18f),
        };

        // 8 horizontal probe directions in tile-local XZ: 4 cardinal + 4
        // corner-biased (scaled by extents so a rectangular room aims its
        // diagonals at actual corners).
        // Returns local (unnormalized-ok) directions; caller normalizes.

        internal static List<Candidate> Compute(
            Tile tile,
            Bounds box,                 // tile-local AABB (rendered geometry, or capped LocalBounds)
            int sortedIndex,
            in PlacementParams p,
            in PlacementMasks masks,
            in CameraSemanticContext semantic,
            bool relaxed,
            bool debug)
        {
            var result = new List<Candidate>(16);
            if (tile == null || tile.Placement == null) return result;
            if (box.size.sqrMagnitude < 1e-6f) return result;

            TilePlacementData placement = tile.Placement;
            Vector3 centerL = box.center;
            Vector3 ext = box.extents;
            float dFloorY = ComputeEntryFloorY(tile, placement, box.min.y);
            float roomHeight = box.size.y;
            float worldFloorY = ToWorld(placement, new Vector3(centerL.x, dFloorY, centerL.z)).y;
            float minUsefulCenter = relaxed ? MinUsefulCenterDistRelaxedM : MinUsefulCenterDistM;
            bool hallwayShape = TileShapeClassifier.IsHallwayShape(semantic.Shape);
            float wallProbeHeightM = hallwayShape
                ? Mathf.Min(p.WallMountHeightM, HallwayWallProbeHeightM)
                : p.WallMountHeightM;
            if (!hallwayShape && box.size.x * box.size.z >= LargeRoomMinAreaM2)
            {
                wallProbeHeightM = Mathf.Clamp(
                    roomHeight * LargeRoomProbeHeightFraction,
                    p.WallMountHeightM,
                    LargeRoomMaxProbeHeightM);
            }

            // Horizontal cast length: reach the far wall of the AABB plus a
            // margin, clamped so a degenerate-huge box can't fire a 200m ray.
            float horizCast = Mathf.Min(box.size.magnitude + HorizCastMarginM, VoidProbeDistM);

            // Origins: center + 4 quarter points (tile-local XZ).
            float qx = OriginQuarterFrac * ext.x;
            float qz = OriginQuarterFrac * ext.z;
            Vector2[] originsXZ =
            {
                new Vector2(centerL.x, centerL.z),
                new Vector2(centerL.x + qx, centerL.z + qz),
                new Vector2(centerL.x - qx, centerL.z + qz),
                new Vector2(centerL.x + qx, centerL.z - qz),
                new Vector2(centerL.x - qx, centerL.z - qz),
            };

            Vector3 upWorld = placement.Rotation * Vector3.up;

            // Aim target for ceiling mounts: low-ish point at room center.
            Vector3 ceilAimWorld = ToWorld(placement,
                new Vector3(centerL.x, dFloorY + CeilAimHeightAboveFloorM, centerL.z));

            for (int oi = 0; oi < originsXZ.Length; oi++)
            {
                // --- ceiling probe: up-cast from a low interior origin ------
                Vector3 ceilOriginL = new Vector3(originsXZ[oi].x, dFloorY + CeilProbeHeightM, originsXZ[oi].y);
                Vector3 ceilOriginW = ToWorld(placement, ceilOriginL);
                if (!Physics.CheckSphere(ceilOriginW, OriginClearRadiusM, masks.MountMask, QueryTriggerInteraction.Ignore))
                {
                    if (Physics.Raycast(ceilOriginW, upWorld, out RaycastHit upHit, MaxUpCastM,
                            masks.MountMask, QueryTriggerInteraction.Ignore)
                        && upHit.distance >= MinSurfaceDistM
                        && upHit.normal.y < -CeilingNormalMinDownY)
                    {
                        Vector3 normal = upHit.normal;                       // points down into room
                        Vector3 mountPos = upHit.point + normal * SurfaceInsetM;
                        Vector3 lookDir;
                        string ceilProfile;
                        float ceilAimScore;
                        if (!TryChooseFocusAim(mountPos, in semantic, in masks, out lookDir, out ceilProfile, out ceilAimScore))
                        {
                            lookDir = ceilAimWorld - mountPos;
                            ceilProfile = "default";
                            ceilAimScore = 0f;
                        }
                        TryAddCandidate(
                            result, masks, mountPos, lookDir, normal,
                            SurfaceKind.Ceiling, CameraMountMode.Ceiling,
                            worldFloorY, roomHeight, minUsefulCenter,
                            upHit.collider, oi, /*dir*/ 9, ceilAimScore, ceilProfile,
                            in semantic, relaxed, debug, sortedIndex, tile);
                    }
                }

                // --- wall probes: horizontal casts swept over several probe
                // heights. The wall hit lands at the same Y as its origin, so
                // each extra tier is the only way a higher candidate can exist.
                float[] probeSteps = hallwayShape ? HallwayProbeHeightStepsM : RoomProbeHeightStepsM;
                for (int hi = 0; hi < probeSteps.Length; hi++)
                {
                    float tierProbeHeightM = wallProbeHeightM + probeSteps[hi];
                    if (tierProbeHeightM > MaxMountHeightM) continue;
                    // Skip tiers whose origin would sit inside/above the ceiling.
                    if (dFloorY + tierProbeHeightM > box.max.y - ProbeTierCeilingClearM && hi > 0) continue;
                    Vector3 wallOriginL = new Vector3(originsXZ[oi].x, dFloorY + tierProbeHeightM, originsXZ[oi].y);
                    Vector3 wallOriginW = ToWorld(placement, wallOriginL);
                    if (Physics.CheckSphere(wallOriginW, OriginClearRadiusM, masks.MountMask, QueryTriggerInteraction.Ignore))
                        continue;

                    for (int di = 0; di < 8; di++)
                    {
                        Vector3 dirL = HorizontalDirLocal(di, ext);
                        Vector3 dirW = (placement.Rotation * dirL).normalized;
                        if (!Physics.Raycast(wallOriginW, dirW, out RaycastHit wHit, horizCast,
                                masks.MountMask, QueryTriggerInteraction.Ignore))
                            continue;
                        if (wHit.distance < MinSurfaceDistM) continue;
                        if (Mathf.Abs(wHit.normal.y) >= WallNormalMaxAbsY) continue; // floor/ceiling, not a wall

                        Vector3 normalH = new Vector3(wHit.normal.x, 0f, wHit.normal.z);
                        if (normalH.sqrMagnitude < 1e-6f) continue;
                        normalH.Normalize();

                        Vector3 mountPos = wHit.point + wHit.normal * SurfaceInsetM;
                        if (!IsWithinExpandedTileBox(placement, box, mountPos, TileLocalMountMarginM, out Vector3 mountLocal))
                        {
                            if (debug)
                            {
                                LogReject(
                                    sortedIndex, tile, SurfaceKind.Wall, "outside-tile",
                                    DistanceOutsideExpandedTileBox(box, mountLocal, TileLocalMountMarginM),
                                    oi, di, wHit.collider);
                            }
                            continue;
                        }

                        // Corner-ness: probe perpendicular to the wall normal; if a
                        // second wall is close on either side, this is a corner.
                        bool isCorner = IsNearCorner(mountPos, normalH, masks.MountMask);
                        SurfaceKind kind = isCorner ? SurfaceKind.Corner : SurfaceKind.Wall;
                        float aimScore;
                        string aimProfile;

                        // Aim: into the room (−normal) with a fixed downward pitch.
                        // Built component-wise (horizontal·cos + world-down·sin) so
                        // the downward tilt is exact and free of any AngleAxis
                        // handedness ambiguity — intoRoom is a horizontal unit
                        // vector, Vector3.down is orthogonal to it, so the sum is a
                        // unit vector pitched down by exactly WallAimPitchDownDeg.
                        Vector3 lookDir;
                        if (!TryChooseFocusAim(mountPos, in semantic, in masks, out lookDir, out aimProfile, out aimScore) &&
                            !TryChooseHallwayAim(mountPos, kind, in semantic, out lookDir, out aimProfile, out aimScore) &&
                            !TryChooseDoorwayAim(mountPos, in semantic, in masks, out lookDir, out aimScore))
                        {
                            Vector3 intoRoom = -normalH;
                            float wallPitchRad = WallAimPitchDownDeg * Mathf.Deg2Rad;
                            lookDir = intoRoom * Mathf.Cos(wallPitchRad) + Vector3.down * Mathf.Sin(wallPitchRad);
                            aimScore = 0f;
                            aimProfile = "default";
                        }
                        else if (string.IsNullOrEmpty(aimProfile))
                        {
                            aimProfile = "doorway";
                        }

                        TryAddCandidate(
                            result, masks, mountPos, lookDir, wHit.normal,
                            kind, CameraMountMode.Wall,
                            worldFloorY, roomHeight, minUsefulCenter,
                            wHit.collider, oi, di, aimScore, aimProfile,
                            in semantic, relaxed, debug, sortedIndex, tile);
                    }
                }
            }

            // Hallway tiles additionally probe the wall header directly above
            // each doorway — the deliberate hallway camera spot.
            if (hallwayShape)
            {
                AddDoorwayHeaderCandidates(
                    result, tile, box, placement, worldFloorY, roomHeight, minUsefulCenter,
                    in masks, in semantic, relaxed, debug, sortedIndex);
            }

            // Rank best→worst, stable deterministic tiebreaks.
            result.Sort(CompareCandidates);

            // Collapse near-identical candidates so the redundancy fallback
            // list offers genuinely different vantage points.
            return Dedup(result);
        }

        // Above-doorway hallway mounts: seat the camera on the wall header
        // over each doorway opening, aimed down the corridor. Mirrors the
        // interior alarm bar's above-opening logic, including the flush-
        // filler guard so a tall static door face is never mistaken for the
        // header wall.
        private static void AddDoorwayHeaderCandidates(
            List<Candidate> outList,
            Tile tile,
            Bounds box,
            TilePlacementData placement,
            float worldFloorY,
            float roomHeight,
            float minUsefulCenter,
            in PlacementMasks masks,
            in CameraSemanticContext semantic,
            bool relaxed,
            bool debug,
            int sortedIndex)
        {
            IReadOnlyList<DoorwayFact> doorways = semantic.Doorways;
            if (doorways == null) return;

            int count = Mathf.Min(doorways.Count, 8);
            for (int i = 0; i < count; i++)
            {
                Vector3 doorPos = doorways[i].WorldPosition;
                Vector3 intoTile = -doorways[i].WorldForward;
                intoTile.y = 0f;
                if (intoTile.sqrMagnitude < 1e-6f) continue;
                intoTile.Normalize();

                // Opening top: up-probe from just inside the opening.
                float openingTopY = doorPos.y + DoorwayHeaderDefaultTopM;
                Vector3 topProbe = doorPos + intoTile * 0.12f + Vector3.up * 0.4f;
                if (Physics.Raycast(topProbe, Vector3.up, out RaycastHit topHit, 6f,
                        masks.MountMask, QueryTriggerInteraction.Ignore))
                {
                    openingTopY = Mathf.Min(topHit.point.y, doorPos.y + DoorwayHeaderMaxTopM);
                }

                float camY = openingTopY + DoorwayHeaderClearanceM;
                Vector3 probeOrigin = new Vector3(doorPos.x, camY, doorPos.z) + intoTile * 0.6f;
                if (!Physics.Raycast(probeOrigin, -intoTile, out RaycastHit wHit, 1.4f,
                        masks.MountMask, QueryTriggerInteraction.Ignore))
                    continue;
                if (Mathf.Abs(wHit.normal.y) >= WallNormalMaxAbsY) continue;

                Vector3 normalH = new Vector3(wHit.normal.x, 0f, wHit.normal.z);
                if (normalH.sqrMagnitude < 1e-6f) continue;
                normalH.Normalize();

                // Flush-filler guard: whatever fills the opening at body
                // height sitting flush with the mount plane means the
                // "header" is a tall door face, not wall.
                Vector3 fillerOrigin = doorPos + Vector3.up * 1.2f + intoTile * 0.6f;
                if (Physics.Raycast(fillerOrigin, -intoTile, out RaycastHit fillerHit, 1.4f,
                        masks.MountMask, QueryTriggerInteraction.Ignore)
                    && Mathf.Abs(Vector3.Dot(fillerHit.point - wHit.point, normalH)) < DoorwayHeaderFillerFlushToleranceM)
                {
                    continue;
                }

                Vector3 mountPos = wHit.point + wHit.normal * SurfaceInsetM;
                if (!IsWithinExpandedTileBox(placement, box, mountPos, TileLocalMountMarginM, out _))
                    continue;

                Vector3 lookDir;
                string aimProfile = "hallway-doorway-header";
                float aimScore = 560f;
                if (!TryChooseFocusAim(mountPos, in semantic, in masks, out lookDir, out _, out _) &&
                    !TryChooseHallwayAim(mountPos, SurfaceKind.Wall, in semantic, out lookDir, out _, out _))
                {
                    Vector3 flat = semantic.RoomCenter - mountPos;
                    flat.y = 0f;
                    if (flat.sqrMagnitude < 1e-6f) continue;
                    float pitchRad = WallAimPitchDownDeg * Mathf.Deg2Rad;
                    lookDir = flat.normalized * Mathf.Cos(pitchRad) + Vector3.down * Mathf.Sin(pitchRad);
                }

                TryAddCandidate(
                    outList, masks, mountPos, lookDir, wHit.normal,
                    SurfaceKind.Wall, CameraMountMode.Wall,
                    worldFloorY, roomHeight, minUsefulCenter,
                    wHit.collider, /*origin*/ 9, /*dir*/ Mathf.Min(i, 9), aimScore, aimProfile,
                    in semantic, relaxed, debug, sortedIndex, tile);
            }
        }

        internal static bool TryComputeGrandRoomOverview(
            Tile tile,
            Bounds box,
            int sortedIndex,
            in PlacementMasks masks,
            bool debug,
            out Candidate candidate)
        {
            candidate = default;
            if (tile == null || tile.Placement == null) return false;
            if (box.size.sqrMagnitude < 1e-6f) return false;
            if (!LooksLikeGrandStairRoom(tile, box)) return false;

            TilePlacementData placement = tile.Placement;
            CameraSemanticContext semantic = CameraSemanticContextBuilder.Build(tile, box);
            Vector3 centerL = box.center;
            Vector3 ext = box.extents;
            float dFloorY = ComputeEntryFloorY(tile, placement, box.min.y);
            float worldFloorY = ToWorld(placement, new Vector3(centerL.x, dFloorY, centerL.z)).y;
            if (!TryResolveEntranceSideDoorwayLocal(tile, placement, centerL, out Vector3 entranceLocal))
                return false;

            Vector3 entranceOffset = new Vector3(entranceLocal.x - centerL.x, 0f, entranceLocal.z - centerL.z);
            if (entranceOffset.sqrMagnitude < 1e-6f) return false;

            Vector3 backLocal = centerL;
            float xNorm = Mathf.Abs(entranceOffset.x) / Mathf.Max(ext.x, 0.01f);
            float zNorm = Mathf.Abs(entranceOffset.z) / Mathf.Max(ext.z, 0.01f);
            if (xNorm >= zNorm)
                backLocal.x = centerL.x - Mathf.Sign(entranceOffset.x) * ext.x * GrandRoomBackWallFraction;
            else
                backLocal.z = centerL.z - Mathf.Sign(entranceOffset.z) * ext.z * GrandRoomBackWallFraction;

            Vector3 upWorld = placement.Rotation * Vector3.up;
            Vector3 probeWorld = ToWorld(placement, new Vector3(backLocal.x, dFloorY + CeilProbeHeightM, backLocal.z));
            if (Physics.CheckSphere(probeWorld, OriginClearRadiusM, masks.MountMask, QueryTriggerInteraction.Ignore))
                return false;

            if (!Physics.Raycast(probeWorld, upWorld, out RaycastHit hit, MaxUpCastM, masks.MountMask, QueryTriggerInteraction.Ignore))
                return false;
            if (hit.distance < MinSurfaceDistM || hit.normal.y >= -CeilingNormalMinDownY)
                return false;

            Vector3 mountPos = hit.point + hit.normal * SurfaceInsetM;
            Vector3 targetWorld = ToWorld(
                placement,
                new Vector3(entranceLocal.x, dFloorY + GrandRoomTargetHeightM, entranceLocal.z));
            Vector3 lookDir = targetWorld - mountPos;
            float doorwayScore = 500f - Mathf.Abs(lookDir.magnitude - 7f) * 18f;

            var one = new List<Candidate>(1);
            TryAddCandidate(
                one, masks, mountPos, lookDir, hit.normal,
                SurfaceKind.Ceiling, CameraMountMode.Ceiling,
                worldFloorY, box.size.y, MinUsefulCenterDistRelaxedM,
                hit.collider, originIdx: 8, dirIdx: 9, doorwayScore, "grand-room-overview",
                in semantic, relaxed: false, debug, sortedIndex, tile);

            if (one.Count == 0) return false;
            Candidate c = one[0];
            candidate = new Candidate(
                c.WorldPos, c.WorldRot, c.Forward,
                c.MountMode, c.Kind, c.Score + GrandRoomScoreBonus,
                c.MountHeightAboveFloorM, c.CenterUsefulDistM,
                c.FrustumHits, c.FrustumVoids, c.FrustumNearWall,
                c.SupportNormal, c.HitName, c.HitLayer,
                cornerId: 899, c.RoomHeightM,
                c.DoorwayVisibilityScore, c.RoomFramingScore, "grand-room-overview");
            return true;
        }

        private static bool LooksLikeGrandStairRoom(Tile tile, Bounds box)
        {
            float area = Mathf.Abs(box.size.x * box.size.z);
            if (area < GrandRoomMinFootprintM2) return false;
            if (Mathf.Min(box.size.x, box.size.z) < 4f) return false;

            string name = tile.name ?? string.Empty;
            if (ContainsToken(name, "stair") || ContainsToken(name, "grand") ||
                ContainsToken(name, "main") || ContainsToken(name, "foyer"))
                return true;
            return HasChildNameToken(tile.transform, "stair") ||
                   HasChildNameToken(tile.transform, "grand") ||
                   HasChildNameToken(tile.transform, "foyer");
        }

        private static bool TryResolveEntranceSideDoorwayLocal(
            Tile tile,
            TilePlacementData placement,
            Vector3 centerLocal,
            out Vector3 doorwayLocal)
        {
            doorwayLocal = centerLocal;
            if (tile?.UsedDoorways == null || tile.UsedDoorways.Count == 0) return false;

            Matrix4x4 worldToLocal = Matrix4x4.TRS(
                placement.Position, placement.Rotation, Vector3.one).inverse;
            bool found = false;
            float bestScore = float.PositiveInfinity;
            for (int i = 0; i < tile.UsedDoorways.Count; i++)
            {
                Doorway doorway = tile.UsedDoorways[i];
                if (doorway == null) continue;
                Vector3 local = worldToLocal.MultiplyPoint3x4(doorway.transform.position);
                Tile peer = doorway.ConnectedDoorway != null ? doorway.ConnectedDoorway.Tile : null;
                float peerProgress = peer != null && peer.Placement != null
                    ? Mathf.Max(0f, peer.Placement.PathDepth) + Mathf.Max(0f, peer.Placement.BranchDepth) * 0.65f
                    : 999f;
                float centerDistanceBonus = -new Vector2(local.x - centerLocal.x, local.z - centerLocal.z).magnitude * 0.01f;
                float score = peerProgress + centerDistanceBonus + i * 0.001f;
                if (score >= bestScore) continue;
                bestScore = score;
                doorwayLocal = local;
                found = true;
            }
            return found;
        }

        private static bool HasChildNameToken(Transform root, string token)
        {
            if (root == null) return false;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null) continue;
                if (ContainsToken(child.name, token)) return true;
                if (HasChildNameToken(child, token)) return true;
            }
            return false;
        }

        private static bool ContainsToken(string value, string token)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Build + gate a candidate; append to list if it survives. Centralizes
        // the embedding check, mount-height window, pitch gate, and view-quality
        // sweep so ceiling, wall, and legacy fallback paths share one source of truth.
        private static void TryAddCandidate(
            List<Candidate> outList, in PlacementMasks masks,
            Vector3 mountPos, Vector3 lookDir, Vector3 supportNormalWorld,
            SurfaceKind kind, CameraMountMode mountMode,
            float worldFloorY, float roomHeight, float minUsefulCenter,
            Collider hitCol, int originIdx, int dirIdx,
            float doorwayScore, string aimProfile,
            in CameraSemanticContext semantic,
            bool relaxed, bool debug, int sortedIndex, Tile tile)
        {
            if (lookDir.sqrMagnitude < 1e-6f) return;

            // Hallway tier: wall mounts may sit at player-head height
            // (matching the lowered probe origin); everything else keeps the
            // above-head minimum.
            float minMountHeight = mountMode == CameraMountMode.Wall
                    && TileShapeClassifier.IsHallwayShape(semantic.Shape)
                ? HallwayMinMountHeightM
                : MinMountHeightM;

            Vector3 forward = lookDir.normalized;
            Quaternion rot = Quaternion.LookRotation(forward, Vector3.up);
            if (!TryEvaluateSafetyGates(
                    in masks, mountPos, rot, forward, kind, worldFloorY, minUsefulCenter, minMountHeight,
                    supportNormalWorld, hitCol, originIdx, dirIdx, debug, sortedIndex, tile, out SafetyGateResult gate))
            {
                return;
            }

            // Score: surface-type preference (ceiling > corner > wall), then
            // mounting height, useful view distance, and frustum hit ratio,
            // minus a near-wall penalty.
            float score = kind == SurfaceKind.Ceiling ? ScoreCeilingBase
                        : kind == SurfaceKind.Corner ? ScoreCornerBase
                        : ScoreWallBase;
            score += Mathf.Min(gate.MountHeightAboveFloorM, MaxMountHeightM) * ScoreHeightWeight;
            score += Mathf.Min(gate.CenterUsefulDistM, ScoreUsefulDistCapM) * ScoreUsefulDistWeight;
            score += (gate.FrustumHits / (float)s_frustumOffsets.Length) * ScoreFrustumWeight;
            if (gate.CenterUsefulDistM < NearWallKneeM)
                score -= (NearWallKneeM - gate.CenterUsefulDistM) * ScoreNearWallPenalty;
            float roomFramingScore = gate.FrustumHits / (float)s_frustumOffsets.Length -
                                     gate.FrustumNearWall * 0.08f - gate.FrustumVoids * 0.12f;
            score += doorwayScore * ScoreDoorwayVisibleWeight;
            score += roomFramingScore * ScoreRoomFramingWeight;
            if (semantic.Shape == CameraTileShape.LongStraightHallway && aimProfile == "hallway-long")
                score += ScoreHallwayProfileBonus;
            if (semantic.Shape == CameraTileShape.LShapedHallway && kind == SurfaceKind.Corner)
                score += ScoreHallwayProfileBonus;
            if (aimProfile == "hallway-doorway-header")
                score += ScoreDoorwayHeaderBonus;
            if (aimProfile == "focus-target")
                score += ScoreFocusTargetBonus;

            int cornerId = (int)kind * 100 + originIdx * 10 + dirIdx;
            int hitLayer = hitCol != null ? hitCol.gameObject.layer : -1;
            string hitName = hitCol != null ? hitCol.name : "none";

            outList.Add(new Candidate(
                mountPos, gate.WorldRot, gate.Forward, mountMode, kind, score,
                gate.MountHeightAboveFloorM, gate.CenterUsefulDistM,
                gate.FrustumHits, gate.FrustumVoids, gate.FrustumNearWall,
                supportNormalWorld, hitName, hitLayer, cornerId, roomHeight,
                doorwayScore, roomFramingScore, aimProfile));
        }

        internal static bool TryValidateLegacyFallbackPose(
            Tile tile,
            Bounds box,
            int sortedIndex,
            in PlacementMasks masks,
            Vector3 worldPos,
            Quaternion worldRot,
            CameraMountMode mountMode,
            int cornerId,
            bool debug,
            out SafetyGateResult gate)
        {
            gate = default;
            if (tile == null || tile.Placement == null) return false;
            if (box.size.sqrMagnitude < 1e-6f) return false;

            TilePlacementData placement = tile.Placement;
            Vector3 centerL = box.center;
            float dFloorY = ComputeEntryFloorY(tile, placement, box.min.y);
            float worldFloorY = ToWorld(placement, new Vector3(centerL.x, dFloorY, centerL.z)).y;
            Vector3 forward = worldRot * Vector3.forward;
            if (forward.sqrMagnitude < 1e-6f) return false;
            forward.Normalize();
            Quaternion normalizedRot = Quaternion.LookRotation(forward, Vector3.up);

            return TryEvaluateSafetyGates(
                in masks, worldPos, normalizedRot, forward, InferSurfaceKind(mountMode),
                worldFloorY, MinUsefulCenterDistRelaxedM, MinMountHeightM,
                Vector3.zero, null, 9, cornerId,
                debug, sortedIndex, tile, out gate);
        }

        private static bool TryEvaluateSafetyGates(
            in PlacementMasks masks,
            Vector3 mountPos,
            Quaternion rot,
            Vector3 forward,
            SurfaceKind kind,
            float worldFloorY,
            float minUsefulCenter,
            float minMountHeight,
            Vector3 supportNormal,
            Collider supportCollider,
            int originIdx,
            int dirIdx,
            bool debug,
            int sortedIndex,
            Tile tile,
            out SafetyGateResult gate)
        {
            gate = default;
            if (forward.sqrMagnitude < 1e-6f) return false;
            forward.Normalize();

            if (supportCollider != null && LooksLikeFixtureObstruction(supportCollider, 0f))
            {
                if (debug) LogReject(sortedIndex, tile, kind, "fixture-support", 0f, originIdx, dirIdx, supportCollider);
                return false;
            }

            if (supportCollider != null && !HasContinuousSupportFootprint(
                    mountPos, rot, supportNormal, supportCollider, masks.MountMask))
            {
                if (debug) LogReject(sortedIndex, tile, kind, "unsupported-footprint", 0f, originIdx, dirIdx, supportCollider);
                return false;
            }

            // Mount-height window above the entry floor.
            float mountHeight = mountPos.y - worldFloorY;
            if (mountHeight < minMountHeight || mountHeight > MaxMountHeightM)
            {
                if (debug) LogReject(sortedIndex, tile, kind, "height", mountHeight, originIdx, dirIdx, supportCollider);
                return false;
            }

            // Embedding: the camera center and full visual body must sit in open air.
            if (Physics.CheckSphere(mountPos, MountClearRadiusM, masks.SolidMask, QueryTriggerInteraction.Ignore))
            {
                if (debug) LogReject(sortedIndex, tile, kind, "embedded", mountHeight, originIdx, dirIdx, supportCollider);
                return false;
            }

            if (BodyOverlapsBlockedCollider(mountPos, rot, masks.BodyOverlapMask, supportCollider, out Collider bodyHit))
            {
                if (debug) LogReject(sortedIndex, tile, kind, "body-overlap", 0f, originIdx, dirIdx, bodyHit);
                return false;
            }

            // Pitch gate (positive = looking down).
            float pitchDeg = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (pitchDeg < -MaxPitchUpDeg || pitchDeg > MaxPitchDownDeg)
            {
                if (debug) LogReject(sortedIndex, tile, kind, "pitch", pitchDeg, originIdx, dirIdx, supportCollider);
                return false;
            }

            // Center useful distance: how far the camera can see before the
            // default forward hits interior structure. SolidMask is deliberately
            // NOT used here: Terrain/Default can be the outside shell, and
            // accepting those hits produced skybox-looking interior feeds.
            float centerDist;
            if (Physics.Raycast(mountPos, forward, out RaycastHit cHit, VoidProbeDistM,
                    masks.MountMask, QueryTriggerInteraction.Ignore))
            {
                if (LooksLikeFixtureObstruction(cHit.collider, cHit.distance))
                {
                    if (debug) LogReject(sortedIndex, tile, kind, "fixture-obstruction", cHit.distance, originIdx, dirIdx, supportCollider);
                    return false;
                }
                centerDist = cHit.distance;
            }
            else
            {
                // The user's failure mode is a default feed looking through
                // the world into skybox/void. A center ray that hits nothing
                // is not a legible default view, even if side rays catch walls.
                if (debug) LogReject(sortedIndex, tile, kind, "center-void", VoidProbeDistM, originIdx, dirIdx, supportCollider);
                return false;
            }

            if (centerDist < minUsefulCenter)
            {
                if (debug) LogReject(sortedIndex, tile, kind, "near-wall", centerDist, originIdx, dirIdx, supportCollider);
                return false;
            }

            // Frustum sweep: count rays that hit useful interior structure vs
            // rays that escape to void/outside-shell/skybox vs near-wall rays.
            int hits = 0, voids = 0, nearWall = 0;
            for (int f = 0; f < s_frustumOffsets.Length; f++)
            {
                Vector2 off = s_frustumOffsets[f];
                Vector3 fdir = (rot * Quaternion.Euler(off.y, off.x, 0f)) * Vector3.forward;
                if (Physics.Raycast(mountPos, fdir, out RaycastHit fHit, VoidProbeDistM,
                        masks.MountMask, QueryTriggerInteraction.Ignore))
                {
                    hits++;
                    if (fHit.distance < NearWallKneeM) nearWall++;
                }
                else
                {
                    voids++;
                }
            }

            float voidFrac = voids / (float)s_frustumOffsets.Length;
            if (voidFrac > VoidRayMaxFrac)
            {
                if (debug) LogReject(sortedIndex, tile, kind, "void/skybox", voidFrac, originIdx, dirIdx, supportCollider);
                return false;
            }

            gate = new SafetyGateResult(forward, rot, mountHeight, centerDist, hits, voids, nearWall);
            return true;
        }

        private static bool BodyOverlapsBlockedCollider(
            Vector3 mountPos, Quaternion rot, int mask, Collider supportCollider, out Collider blockingCollider)
        {
            blockingCollider = null;
            if (mask == 0) return false;

            Collider[] overlaps = Physics.OverlapBox(
                mountPos, CameraBodyOverlapHalfExtentsM, rot, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlaps.Length; i++)
            {
                Collider c = overlaps[i];
                if (c == null) continue;
                if (supportCollider != null && ReferenceEquals(c, supportCollider)) continue;
                blockingCollider = c;
                return true;
            }

            return false;
        }

        private static SurfaceKind InferSurfaceKind(CameraMountMode mountMode)
        {
            return mountMode == CameraMountMode.Wall ? SurfaceKind.Wall : SurfaceKind.Ceiling;
        }

        private static bool HasContinuousSupportFootprint(
            Vector3 mountPos,
            Quaternion rotation,
            Vector3 supportNormal,
            Collider supportCollider,
            int mountMask)
        {
            if (supportCollider == null || mountMask == 0)
                return false;

            Vector3 normal = supportNormal;
            if (normal.sqrMagnitude < 1e-6f)
                normal = mountPos - supportCollider.ClosestPoint(mountPos);
            if (normal.sqrMagnitude < 1e-6f)
            {
                // Reviewed/legacy poses intentionally pass no support collider and never
                // reach this path. This fallback only covers unusual MeshCollider
                // ClosestPoint behavior on a procedural raycast hit.
                Vector3 reverse = rotation * Vector3.back;
                if (!Physics.Raycast(
                        mountPos,
                        reverse,
                        out RaycastHit centerHit,
                        SurfaceInsetM + 0.12f,
                        mountMask,
                        QueryTriggerInteraction.Ignore))
                {
                    return false;
                }
                normal = centerHit.normal;
            }
            normal.Normalize();

            Vector3 tangent = Vector3.ProjectOnPlane(rotation * Vector3.right, normal);
            if (tangent.sqrMagnitude < 1e-6f)
                tangent = Vector3.Cross(Vector3.up, normal);
            if (tangent.sqrMagnitude < 1e-6f)
                tangent = Vector3.Cross(Vector3.forward, normal);
            tangent.Normalize();
            Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;
            Vector3 surfaceCenter = mountPos - normal * SurfaceInsetM;

            return HasSupportAt(surfaceCenter, normal, Vector3.zero, mountMask)
                && HasSupportAt(surfaceCenter, normal, tangent * SupportFootprintHalfWidthM, mountMask)
                && HasSupportAt(surfaceCenter, normal, -tangent * SupportFootprintHalfWidthM, mountMask)
                && HasSupportAt(surfaceCenter, normal, bitangent * SupportFootprintHalfHeightM, mountMask)
                && HasSupportAt(surfaceCenter, normal, -bitangent * SupportFootprintHalfHeightM, mountMask)
                && HasSupportAt(surfaceCenter, normal,
                    tangent * SupportFootprintHalfWidthM + bitangent * SupportFootprintHalfHeightM, mountMask)
                && HasSupportAt(surfaceCenter, normal,
                    tangent * SupportFootprintHalfWidthM - bitangent * SupportFootprintHalfHeightM, mountMask)
                && HasSupportAt(surfaceCenter, normal,
                    -tangent * SupportFootprintHalfWidthM + bitangent * SupportFootprintHalfHeightM, mountMask)
                && HasSupportAt(surfaceCenter, normal,
                    -tangent * SupportFootprintHalfWidthM - bitangent * SupportFootprintHalfHeightM, mountMask);
        }

        private static bool HasSupportAt(
            Vector3 surfaceCenter,
            Vector3 expectedNormal,
            Vector3 offset,
            int mountMask)
        {
            Vector3 origin = surfaceCenter + offset + expectedNormal * 0.08f;
            if (!Physics.Raycast(
                    origin,
                    -expectedNormal,
                    out RaycastHit hit,
                    0.18f,
                    mountMask,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            return Vector3.Dot(hit.normal.normalized, expectedNormal) >= SupportNormalMinDot;
        }

        // Highest-priority aim: look at the tile's focus point (main-entrance
        // door, fixture) when the mount has clear line of sight to it. The
        // profile name feeds the focus-target score bonus so a candidate that
        // can actually see the point of interest wins the ranking.
        private static bool TryChooseFocusAim(
            Vector3 mountPos,
            in CameraSemanticContext semantic,
            in PlacementMasks masks,
            out Vector3 lookDir,
            out string aimProfile,
            out float aimScore)
        {
            lookDir = Vector3.zero;
            aimProfile = null;
            aimScore = 0f;
            if (!semantic.HasFocusTarget) return false;

            Vector3 target = semantic.FocusTarget;
            Vector3 delta = target - mountPos;
            float dist = delta.magnitude;
            if (dist < 1.0f) return false;
            if (Physics.Linecast(mountPos, target, masks.MountMask, QueryTriggerInteraction.Ignore))
                return false;

            lookDir = delta / dist;
            aimProfile = "focus-target";
            aimScore = 700f - Mathf.Abs(dist - 6f) * 12f;
            return true;
        }

        private static bool TryChooseHallwayAim(
            Vector3 mountPos,
            SurfaceKind kind,
            in CameraSemanticContext semantic,
            out Vector3 lookDir,
            out string aimProfile,
            out float hallwayScore)
        {
            lookDir = Vector3.zero;
            aimProfile = null;
            hallwayScore = 0f;

            if (semantic.Shape == CameraTileShape.LongStraightHallway)
            {
                Vector3 axis = semantic.LongAxis;
                axis.y = 0f;
                if (axis.sqrMagnitude < 1e-6f) return false;
                axis.Normalize();

                Vector3 toCenter = semantic.RoomCenter - mountPos;
                toCenter.y = 0f;
                if (toCenter.sqrMagnitude > 1e-6f && Vector3.Dot(axis, toCenter) < 0f)
                    axis = -axis;

                float pitchRad = WallAimPitchDownDeg * Mathf.Deg2Rad;
                lookDir = axis * Mathf.Cos(pitchRad) + Vector3.down * Mathf.Sin(pitchRad);
                aimProfile = "hallway-long";
                hallwayScore = 520f;
                return true;
            }

            if (semantic.Shape == CameraTileShape.LShapedHallway && kind == SurfaceKind.Corner)
            {
                Vector3 toCenter = semantic.RoomCenter - mountPos;
                toCenter.y = 0f;
                if (toCenter.sqrMagnitude < 1e-6f) return false;
                Vector3 flatDir = toCenter.normalized;
                float pitchRad = WallAimPitchDownDeg * Mathf.Deg2Rad;
                lookDir = flatDir * Mathf.Cos(pitchRad) + Vector3.down * Mathf.Sin(pitchRad);
                aimProfile = "hallway-l-corner";
                hallwayScore = 520f;
                return true;
            }

            return false;
        }

        private static bool TryChooseDoorwayAim(
            Vector3 mountPos,
            in CameraSemanticContext semantic,
            in PlacementMasks masks,
            out Vector3 lookDir,
            out float doorwayScore)
        {
            lookDir = Vector3.zero;
            doorwayScore = 0f;
            IReadOnlyList<DoorwayFact> doorways = semantic.Doorways;
            if (doorways == null || doorways.Count == 0) return false;

            bool found = false;
            float bestScore = float.NegativeInfinity;
            Vector3 bestDir = Vector3.zero;
            for (int i = 0; i < doorways.Count; i++)
            {
                Vector3 target = doorways[i].WorldPosition + Vector3.up * 1.25f;
                Vector3 delta = target - mountPos;
                float dist = delta.magnitude;
                if (dist < 0.5f) continue;
                if (Physics.Linecast(mountPos, target, masks.MountMask, QueryTriggerInteraction.Ignore))
                    continue;

                float score = 500f - Mathf.Abs(dist - 7f) * 18f;
                if (score <= bestScore) continue;
                bestScore = score;
                bestDir = delta / dist;
                found = true;
            }

            if (!found) return false;
            lookDir = bestDir;
            doorwayScore = bestScore;
            return true;
        }

        private static bool LooksLikeFixtureObstruction(Collider collider, float distance)
        {
            if (collider == null || distance > 3.5f) return false;
            string name = collider.name ?? string.Empty;
            return ContainsToken(name, "pipe") ||
                   ContainsToken(name, "vent") ||
                   ContainsToken(name, "beam") ||
                   ContainsToken(name, "furniture") ||
                   ContainsToken(name, "shelf") ||
                   ContainsToken(name, "rack");
        }

        private static bool IsNearCorner(Vector3 mountPos, Vector3 wallNormalH, int mountMask)
        {
            Vector3 tangent = Vector3.Cross(Vector3.up, wallNormalH);
            if (tangent.sqrMagnitude < 1e-6f) return false;
            tangent.Normalize();
            bool left = Physics.Raycast(mountPos, tangent, CornerProbeM, mountMask, QueryTriggerInteraction.Ignore);
            bool right = Physics.Raycast(mountPos, -tangent, CornerProbeM, mountMask, QueryTriggerInteraction.Ignore);
            return left || right;
        }

        // 4 cardinal + 4 corner-biased local XZ directions.
        private static Vector3 HorizontalDirLocal(int di, Vector3 ext)
        {
            switch (di)
            {
                case 0: return new Vector3(1f, 0f, 0f);
                case 1: return new Vector3(-1f, 0f, 0f);
                case 2: return new Vector3(0f, 0f, 1f);
                case 3: return new Vector3(0f, 0f, -1f);
                case 4: return new Vector3(ext.x, 0f, ext.z);
                case 5: return new Vector3(ext.x, 0f, -ext.z);
                case 6: return new Vector3(-ext.x, 0f, ext.z);
                default: return new Vector3(-ext.x, 0f, -ext.z);
            }
        }

        private static int CompareCandidates(Candidate a, Candidate b)
        {
            int c = b.Score.CompareTo(a.Score); // higher score first
            if (c != 0) return c;
            c = ((int)a.Kind).CompareTo((int)b.Kind);
            if (c != 0) return c;
            c = a.CornerId.CompareTo(b.CornerId);
            if (c != 0) return c;
            return string.CompareOrdinal(a.HitName, b.HitName);
        }

        private static List<Candidate> Dedup(List<Candidate> sorted)
        {
            var kept = new List<Candidate>(sorted.Count);
            for (int i = 0; i < sorted.Count; i++)
            {
                Candidate ci = sorted[i];
                bool dup = false;
                for (int j = 0; j < kept.Count; j++)
                {
                    Candidate cj = kept[j];
                    if (Vector3.Distance(ci.WorldPos, cj.WorldPos) < DupPosM &&
                        Vector3.Dot(ci.Forward, cj.Forward) > DupDot)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup) kept.Add(ci);
            }
            return kept;
        }

        private static Vector3 ToWorld(TilePlacementData placement, Vector3 local)
            => placement.Position + placement.Rotation * local;

        private static bool IsWithinExpandedTileBox(
            TilePlacementData placement, Bounds box, Vector3 worldPos, float margin, out Vector3 local)
        {
            Matrix4x4 worldToLocal = Matrix4x4.TRS(
                placement.Position, placement.Rotation, Vector3.one).inverse;
            local = worldToLocal.MultiplyPoint3x4(worldPos);
            Vector3 min = box.min;
            Vector3 max = box.max;
            return local.x >= min.x - margin && local.x <= max.x + margin &&
                   local.y >= min.y - margin && local.y <= max.y + margin &&
                   local.z >= min.z - margin && local.z <= max.z + margin;
        }

        private static float DistanceOutsideExpandedTileBox(Bounds box, Vector3 local, float margin)
        {
            Vector3 min = box.min;
            Vector3 max = box.max;
            float dx = Mathf.Max(min.x - margin - local.x, local.x - (max.x + margin), 0f);
            float dy = Mathf.Max(min.y - margin - local.y, local.y - (max.y + margin), 0f);
            float dz = Mathf.Max(min.z - margin - local.z, local.z - (max.z + margin), 0f);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static void LogReject(
            int sortedIndex, Tile tile, SurfaceKind kind, string reason,
            float value, int originIdx, int dirIdx, Collider hitCol)
        {
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] SURFACE_REJECT tile={sortedIndex} name={tile?.name} kind={kind} " +
                $"reason={reason} value={value:F2} origin={originIdx} dir={dirIdx} " +
                $"hit={(hitCol != null ? hitCol.name : "none")}");
        }

        // Entry-floor Y — lowest tile-local Y across connected UsedDoorways,
        // via the placement-matrix inverse (NOT tile.transform.worldToLocal,
        // which misaligns on rotated tiles). Kept self-contained here, per the
        // codebase's self-containment idiom for the placement files.
        private static float ComputeEntryFloorY(Tile tile, TilePlacementData placement, float floorFallback)
        {
            if (tile?.UsedDoorways == null || placement == null) return floorFallback;
            Matrix4x4 worldToLocal = Matrix4x4.TRS(
                placement.Position, placement.Rotation, Vector3.one).inverse;
            float best = float.PositiveInfinity;
            bool found = false;
            for (int k = 0; k < tile.UsedDoorways.Count; k++)
            {
                var dw = tile.UsedDoorways[k];
                if (dw == null || dw.ConnectedDoorway == null) continue;
                Vector3 local = worldToLocal.MultiplyPoint3x4(dw.transform.position);
                if (local.y < best) { best = local.y; found = true; }
            }
            return found ? best : floorFallback;
        }
    }
}
