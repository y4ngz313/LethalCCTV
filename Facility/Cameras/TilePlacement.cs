using DunGen;
using UnityEngine;

namespace Y4NGZCompany.Facility.Cameras
{
    // Phase 1.9 — mount mode surfaced on each pick so the placement diagnostic
    // log can be read at a glance, and so the (future) prefab workstream can
    // route to wall-prop vs hanging-ceiling-prop without re-deriving the
    // branch from geometry. Stored on CCTVCamera alongside the corner data.
    internal enum CameraMountMode
    {
        Ceiling,
        Wall,
        DegenerateFallback,
        Exterior,
    }

    // Phase 1.9 — config-derived placement floats bundled. Geometry inputs
    // (Tile, worldAABB, sortedIndex) stay as their own parameters — they
    // come from different sources and at different lifecycle points than
    // these tuning knobs, so unifying them would obscure the data flow.
    // There is deliberately no aim-height knob here: the ceiling-mount branch
    // uses a fixed pitch (CeilingMountPitchDownDegrees), not a look-at target,
    // per the P1.9 REVIEW finding that look-at produces 50–60° steeper pitch on
    // small rooms (the geometry-dependent pitch is the wrong shape for
    // surveillance — see the CeilingMountPitchDownDegrees comment). The old
    // `Placement Aim Height Fraction` bind that described it was removed in the
    // 2026-08-05 dead-config sweep.
    internal readonly struct PlacementParams
    {
        public readonly float HeightFraction;          // ceiling-mount camera Y, fraction of worldAABB height
        public readonly float HorizontalFraction;      // corner offset, fraction of worldAABB horizontal extents
        public readonly float WallMountMinRoomHeightM; // branch threshold on worldAABB.y
        public readonly float WallMountHeightM;        // wall-mount camera height above worldAABB.min.y

        public PlacementParams(
            float heightFraction,
            float horizontalFraction,
            float wallMountMinRoomHeightM,
            float wallMountHeightM)
        {
            HeightFraction = heightFraction;
            HorizontalFraction = horizontalFraction;
            WallMountMinRoomHeightM = wallMountMinRoomHeightM;
            WallMountHeightM = wallMountHeightM;
        }
    }

    public static class TilePlacement
    {
        // Phase 1.9 — cap for the §5 degenerate-worldAABB fallback. 5m
        // chosen so a degenerate-tile camera doesn't sit jarringly below
        // working cameras in the same interior (manor working cameras
        // land ~6–8m up); still comfortably below any plausible ceiling
        // so the fallback cannot reproduce the original 15m bug.
        private const float DegenerateFallbackCapAboveFloorM = 5.0f;

        // Phase 1.9 — ceiling-mount downward pitch in degrees. Kept as a
        // FIXED room-geometry-independent angle, NOT derived from a
        // look-at target. The REVIEW gate during P1.9 implementation
        // computed look-at pitch for representative facility room sizes
        // (4–32m square at ~8m ceiling) and got 29°–77° below
        // horizontal — vs the existing 15°, that would re-aim every
        // facility camera ~14–62° steeper, turning "look across the
        // room with slight downward tilt" into "look almost straight
        // down at the floor in front of the camera," which is the
        // opposite of useful surveillance. The fixed 15° was
        // intentional, not a proxy error: surveillance wants the same
        // mostly-horizontal sweep on any room size. The aim formulation
        // is the place this branch DELIBERATELY diverges from SPEC
        // §4b's "aim DOWN at min.y + size.y*0.3" wording (which
        // described the SPEC's mental model of the prior code, not
        // what the code actually did — that wording was approved on the
        // assumption it'd land near 15°; it doesn't). Wall-mount keeps
        // a look-at-centroid because there the formulation is
        // pitch-0-by-construction (camera Y == aim Y) and so robust to
        // room size.
        private const float CeilingMountPitchDownDegrees = 15.0f;

        // Phase 1.9 — ComputeP2 derives placement from the tile-local
        // worldAABB (the tight extent of actually-rendered geometry) when
        // available, falling back to a capped LocalBounds path on tiles
        // with no encapsulable mesh. Branches on real room height to a
        // ceiling-mount (high corner, look-at-target) for normal rooms
        // and a wall-mount (corner at fixed height, aim at room centroid)
        // for tall vertical tiles (shafts, catwalks, stairwells) where
        // no ceiling-relative height is usable.
        //
        // Frame: ALL math here is tile-local. Both worldAabbLocal and
        // placement.LocalBounds are tile-local; the final
        //   placement.Position + placement.Rotation * localPos
        // conversion at the bottom is the only world-space step. Caller
        // is responsible for producing worldAabbLocal via the tile-local
        // mesh walk (DungeonCameraSpawner.ComputeWorldAabbLocal) — do NOT
        // feed a world-space AABB through here; rotated tiles would
        // misplace.
        internal static (Vector3 worldPos, Quaternion worldRot, int cornerId, Vector3 cornerLocal,
                         CameraMountMode mountMode, float drivingRoomHeightM)
            ComputeP2(Tile tile, Bounds worldAabbLocal, int sortedIndex, in PlacementParams p, int cornerVariantOffset = 0)
        {
            int cornerId = (((sortedIndex + cornerVariantOffset) % 4) + 4) % 4;
            float signX = (cornerId & 1) == 0 ? -1f : 1f;
            float signZ = (cornerId & 2) == 0 ? -1f : 1f;

            TilePlacementData placement = tile.Placement;

            // Branch 1 — degenerate worldAABB (no encapsulable mesh).
            // Falls back to placement.LocalBounds geometry with the
            // height clamped per §5 so a 20m+ LocalBounds can't park
            // the camera 15m up (the bug this whole fix exists to kill).
            // Aim formulation matches Branch 3 (yaw toward centroid +
            // fixed CeilingMountPitchDownDegrees pitch) so a degenerate-
            // tile camera aims like a normal ceiling-mount one.
            bool degenerate = worldAabbLocal.size.sqrMagnitude < 1e-6f;
            if (degenerate)
            {
                Bounds fb = placement.LocalBounds;
                Vector3 fbCenter = fb.center;
                Vector3 fbExt = fb.extents;
                float fbHeightUncapped = p.HeightFraction * fb.size.y;
                float fbHeightCapped = Mathf.Min(fbHeightUncapped, DegenerateFallbackCapAboveFloorM);
                Vector3 localPosFb = new Vector3(
                    fbCenter.x + signX * p.HorizontalFraction * fbExt.x,
                    fb.min.y + fbHeightCapped,
                    fbCenter.z + signZ * p.HorizontalFraction * fbExt.z);
                Quaternion localRotFb = CeilingMountRotation(localPosFb, fbCenter);

                Vector3 cornerLocalFb = new Vector3(signX * fbExt.x, 0f, signZ * fbExt.z);
                Vector3 worldPosFb = placement.Position + placement.Rotation * localPosFb;
                Quaternion worldRotFb = placement.Rotation * localRotFb;
                return (worldPosFb, worldRotFb, cornerId, cornerLocalFb,
                        CameraMountMode.DegenerateFallback, fb.size.y);
            }

            Vector3 center = worldAabbLocal.center;
            Vector3 ext = worldAabbLocal.extents;
            Vector3 min = worldAabbLocal.min;
            float roomHeight = worldAabbLocal.size.y;

            // Branch 2 — wall-mount (tall vertical tile: shaft/catwalk/
            // stairwell with no usable ceiling). Camera at the same
            // horizontal corner as ceiling-mount but at a fixed height
            // above the room floor, aimed at the horizontal centroid at
            // the same height — purely horizontal look INTO the room
            // volume. The corner-to-centroid direction guarantees the
            // camera looks across the room regardless of which corner
            // it lands in (robust against staring at the near wall or
            // out a doorway).
            float wallMountThreshold = Mathf.Max(p.WallMountMinRoomHeightM, 18f);
            if (roomHeight > wallMountThreshold)
            {
                Vector3 localPosWall = new Vector3(
                    center.x + signX * p.HorizontalFraction * ext.x,
                    min.y + p.WallMountHeightM,
                    center.z + signZ * p.HorizontalFraction * ext.z);
                Vector3 aimTargetWall = new Vector3(
                    center.x,
                    min.y + p.WallMountHeightM,
                    center.z);
                Quaternion localRotWall = LookAtSafe(aimTargetWall - localPosWall);

                Vector3 cornerLocalWall = new Vector3(signX * ext.x, 0f, signZ * ext.z);
                Vector3 worldPosWall = placement.Position + placement.Rotation * localPosWall;
                Quaternion worldRotWall = placement.Rotation * localRotWall;
                return (worldPosWall, worldRotWall, cornerId, cornerLocalWall,
                        CameraMountMode.Wall, roomHeight);
            }

            // Branch 3 — ceiling-mount (normal room). High corner at
            // HeightFraction of real room height. Aim = yaw toward
            // centroid + fixed CeilingMountPitchDownDegrees down. The
            // P1.9 fix replaces ONLY the box (Bounds → worldAABB), not
            // the aim formulation; see CeilingMountPitchDownDegrees for
            // why the fixed pitch is deliberate.
            Vector3 localPos = new Vector3(
                center.x + signX * p.HorizontalFraction * ext.x,
                min.y + p.HeightFraction * roomHeight,
                center.z + signZ * p.HorizontalFraction * ext.z);
            Quaternion localRot = CeilingMountRotation(localPos, center);

            Vector3 cornerLocal = new Vector3(signX * ext.x, 0f, signZ * ext.z);
            Vector3 worldPos = placement.Position + placement.Rotation * localPos;
            Quaternion worldRot = placement.Rotation * localRot;
            return (worldPos, worldRot, cornerId, cornerLocal,
                    CameraMountMode.Ceiling, roomHeight);
        }

        // Ceiling-mount rotation: yaw the camera toward the horizontal
        // centroid (so it faces INTO the room across the diagonal from
        // its corner), then apply a fixed downward pitch. Independent
        // of room height — surveillance wants the same "look across the
        // room with slight downward tilt" regardless of room size.
        // Degenerate yaw guard (camera AT centroid x/z) falls back to
        // Vector3.forward, matching the pre-P1.9 sqrMagnitude guard.
        private static Quaternion CeilingMountRotation(Vector3 localPos, Vector3 centroidLocal)
        {
            Vector3 lookFlat = new Vector3(
                centroidLocal.x - localPos.x,
                0f,
                centroidLocal.z - localPos.z);
            Quaternion yawQuat = Quaternion.LookRotation(
                lookFlat.sqrMagnitude > 1e-6f ? lookFlat : Vector3.forward,
                Vector3.up);
            Quaternion pitchQuat = Quaternion.Euler(CeilingMountPitchDownDegrees, 0f, 0f);
            return yawQuat * pitchQuat;
        }

        // Wall-mount rotation: pure look-at-target (camera Y == aim Y by
        // construction, so pitch is 0). Quaternion.LookRotation NaNs on
        // zero-direction input; fall back to Vector3.forward for the
        // degenerate "camera AT centroid in a tiny room" case.
        private static Quaternion LookAtSafe(Vector3 direction)
        {
            return Quaternion.LookRotation(
                direction.sqrMagnitude > 1e-6f ? direction : Vector3.forward,
                Vector3.up);
        }
    }
}
