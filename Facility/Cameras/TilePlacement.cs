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
        Exterior,
    }

    // Phase 1.9 — config-derived placement floats bundled. Geometry inputs
    // (Tile, worldAABB, sortedIndex) stay as their own parameters — they
    // come from different sources and at different lifecycle points than
    // these tuning knobs, so unifying them would obscure the data flow.
    // SurfaceMount.Compute reads WallMountHeightM; the legibility probe
    // (Probe/PlacementProbe) reads the rest. The four-corner AABB placement
    // that also used them was removed in #1313.
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
}
