using System;
using BepInEx.Configuration;
using CSync.Extensions;
using CSync.Lib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    public partial class LethalCCTVConfig
    {
        private void BindCameraSelection(ConfigFile cfg)
        {
            // === Phase 1 — Camera Exclusion ===
            ExcludeEntranceTiles = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Entrance Tiles"),
                false,
                new ConfigDescription("If true, tiles holding an EntranceTeleport with isEntranceToBuilding == true get no CCTV camera. Default is false: entrance tiles get cameras (Overwatch can see the main entrance)."));

            ExcludeFireExitTiles = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Fire Exit Tiles"),
                false,
                new ConfigDescription("If true, tiles holding an EntranceTeleport with isEntranceToBuilding == false get no CCTV camera. Default is false: fire-exit tiles get cameras."));

            ExcludeMineshaftTunnels = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Mineshaft Tunnels"),
                true,
                new ConfigDescription("If true, tiles tagged with RoundManager.MineshaftTunnelTag are excluded. Mineshaft tunnels are long and visually uninteresting; default excludes them."));

            ExcludeTinyTiles = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Tiny Tiles"),
                true,
                new ConfigDescription("If true, tiles with one or zero used doorways AND floor area below TinyTileMaxFloorAreaM2 are excluded."));

            TinyTileMaxFloorAreaM2 = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Tiny Tile Max Floor Area"),
                25.0f,
                new ConfigDescription("Floor area (x * z) threshold in square meters. Tiles below this are considered tiny when Exclude Tiny Tiles is true."));

            TileNameExclusionPatterns = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Tile Name Exclusion Patterns"),
                string.Empty,
                new ConfigDescription("Comma-separated list of substrings. If a tile's GameObject name contains any of them (case-insensitive), it's excluded. Empty by default."));
        }

        private void BindRoomAwarePlacement(ConfigFile cfg)
        {
            ExcludeCorridors = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Corridors"),
                true,
                new ConfigDescription("If true, narrow / small-footprint tiles classified as corridors by the room-aware heuristic get no CCTV camera. Default true: corridors are typically long sparse passageways that produce wall-and-skybox feeds. Synced — host's choice propagates to clients so cameras spawn on the same tiles for everyone."));

            // Conjunction predicate per Phase 1.8 Gate 2: a tile is corridor
            // iff connected-doorway degree is at most this AND it fails the
            // size guards. Degree alone never makes a tile a corridor —
            // high-degree junctions stay rooms regardless of size. Gate 1
            // confirmed connectedDegree == UsedDoorways.Count is populated
            // and reliable at classify-time on both probed tilesets;
            // default 2 matches the standard "pass-through" corridor shape.
            CorridorMaxConnectedDoorways = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Corridor Max Connected Doorways"),
                2,
                new ConfigDescription("Upper bound on connected-doorway degree (Tile.UsedDoorways.Count) for a tile to be ELIGIBLE for corridor classification. A tile with more connected doorways than this is a junction/room and is never classified as a corridor regardless of size. Default 2 (standard pass-through). The size guards (Min Horizontal Dimension / Min Floor Area) still have to fail for the tile to actually be a corridor — degree is necessary, not sufficient. Synced."));

            MinHorizontalDimensionM = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Min Horizontal Dimension"),
                8.5f,
                new ConfigDescription("Lower bound on min(Bounds.size.x, Bounds.size.z), in metres, for a tile to be classified as a room. Reference data: RAV_Tallway corridor is 6m on its narrow axis; RAV_Ballroom is 18m. Default bumped 8.0 → 8.5 after the Gate A close-out (2026-05): 8m clovers were sitting exactly on the 8.0 boundary and float-noise from placement put their measured minDim at 7.999x on some runs (→ corridor) and ≥8.0 on others (→ room), making clover classification placement-dependent. 8.5 gives 8m tiles a stable ~0.5m margin below the line; smallest real manor rooms are 11m+ so no real room is threatened. Tiles failing this AND the floor-area floor are classified as corridors. Synced."));

            MinFloorAreaM2 = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Min Floor Area"),
                80.0f,
                new ConfigDescription("Floor-area floor in square metres (Bounds.size.x * Bounds.size.z). OR'd with the min-horizontal-dimension test so a small-but-boxy room (low min-dim, but enough total floor to be interesting) still classifies as a room. Reference: RAV_Tallway is ~36m², RAV_Ballroom is ~324m². Synced."));

            // Phase 1.8 Placement T1 — knobs that feed the future cluster→
            // select→cap pipeline (T2-T5). Bound here so a fresh deploy of
            // T1 alone produces an updated BepInEx config file with the new
            // defaults but no observable behavior change (nothing reads
            // these until T5 wires the pipeline into the spawner).
            JunctionMinDegree = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Junction Min Degree"),
                3,
                new ConfigDescription("In a multi-tile room-component (a connected group of ROOM tiles linked directly to each other through doorways with no corridor between), a tile is treated as a 'junction' iff its UsedDoorways.Count is >= this value. Each junction tile gets its own camera. Default 3 = first non-pass-through degree (degree-2 cells are pass-throughs even when the cell itself is a room; degree-3+ is a decision point — the natural CCTV vantage). Independent of dungeon-roll size by construction. Synced."));

            MinimumCameraCount = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraCountsSection, "Minimum Cameras"),
                12,
                new ConfigDescription(
                    "Target minimum camera count for a dungeon. If normal room selection produces fewer, eligible corridor tiles are added until this count is reached or no valid tiles remain. Set to 0 to disable the minimum. If this exceeds Maximum Cameras, the maximum wins. Host authoritative and synced.",
                    new AcceptableValueRange<int>(0, 64)));

            MaximumCameraCount = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraCountsSection, "Maximum Cameras"),
                24,
                new ConfigDescription(
                    "Hard maximum camera count for a dungeon, including authored cameras. Lowest-priority procedural spaces are dropped first. Set to 0 to disable dungeon cameras. Host authoritative and synced.",
                    new AcceptableValueRange<int>(0, 64)));

            CameraBudgetPriority = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraCountsSection, "Camera Budget Priority"),
                "MainPathThenDegreeThenArea",
                new ConfigDescription("Sort order used to decide which proposed cameras to keep when the count exceeds Maximum Cameras. Values: 'MainPathThenDegreeThenArea' (default; main-path tiles first, then connected-degree desc, then footprint desc), 'LargestAreaFirst', 'HighestDegreeFirst'. The tile's source index in AllTiles is ALWAYS the final tiebreaker so the same proposed-pick set truncates identically on host and client. Unknown values fall back to default with a one-shot warning. Synced (so host's preference propagates)."));

            LinearChainCoverage = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Linear Chain Coverage"),
                "DegreeOneEndpoints",
                new ConfigDescription("How to place cameras in a multi-tile component that has NO junction tiles (every tile is degree <= 2 — a linear chain of rooms, uncommon). Values: 'DegreeOneEndpoints' (default; one camera at each degree-1 endpoint tile so a chain is covered at both ends — threats enter from endpoints), 'LargestTileOnly' (one camera at the largest-footprint tile, ties broken by lowest source index). A ring (no junctions AND no degree-1 endpoints) always falls back to the largest-tile rule regardless of this setting. Unknown values fall back to default with a one-shot warning. Synced."));

            // D1 + D4 — large uncovered-room coverage. Resolves an open
            // design item from the Phase 1.8 placement workstream: a large
            // room hanging off a junction (e.g. GrayBigLibrary at ~1280m²)
            // was not plausibly covered by the junction's sightline and
            // would otherwise be blind. D1 covered degree-1 leaves only;
            // D4 widened to subgraph degree <= 2 after runtime found large
            // degree-2 through-rooms falling through every selection
            // branch on per-roll-randomized topology. The threshold was
            // later lowered to the 80m² Min Floor Area room floor (the
            // original 256m² = a 16×16 tile is what the description used
            // to claim; #575 corrected the mismatch). It is NOT tuned to
            // any observed roll — the documented Gray cases clear it by a
            // wide margin, which is the point. The
            // config field name and key text are kept as "Leaf Camera Min
            // Area" so existing .cfg files still bind to the same entry
            // across the D1→D4 upgrade; description below is updated to
            // reflect the wider semantics. C# identifier is also kept
            // for the same compatibility reason.
            LeafCameraMinAreaM2 = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Leaf Camera Min Area"),
                80.0f,
                new ConfigDescription("In a multi-tile room-component that already has junction tiles, every NON-JUNCTION room (subgraph degree <= 2 in the component — i.e. a degree-1 leaf OR a degree-2 through-room; subgraph degree, NOT raw doorway count, so corridor-side doorways do not count) whose floor area in m² is at least this value receives its own camera IN ADDITION to the component's junction cameras. Applies ONLY inside the junction branch — the linear-chain branch already covers degree-1 endpoints, the ring branch has no leaves, the single-tile branch has no leaves. Set to 0 to disable the rule entirely (no-op; output is byte-identical to pre-D1 behaviour). Default 80.0 m², matching the Min Floor Area room floor. NAME NOTE: the entry is still called 'Leaf Camera Min Area' for .cfg compatibility with installs that already have the D1-era line; the rule's behaviour is wider than just leaves as of D4. NOTE: CHANGING THE CODE DEFAULT DOES NOT MIGRATE STALE CONFIG FILES — if you installed an earlier build the entry is already pinned in LethalCCTV.cfg at its prior value; delete the line (or the whole file) and let it regenerate to pick up the current default. Synced (host's choice propagates)."));

            OneCameraPerTile = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "One Camera Per Tile"),
                true,
                new ConfigDescription("When true (default), the room-aware selector places one camera in every non-excluded room tile rather than only at junction / large-leaf tiles. The strict junction-only selection yielded 3-4 cameras on typical Facility rolls, which the operator reported as too low for a multi-room role. The surface-mount pass in the spawner still filters tiles that can't physically host a camera, so this rule still respects mount-feasibility. Set to false to revert to the old junction-only selection. Synced."));

            // The Phase 1.9 mount-mode threshold and wall-mount height, and
            // the T1 "Dry Run Camera Placement" STOP flag, left the config
            // file for 1.0 (#575). The first two are placement calibration
            // (now constants on LethalCCTVConfig); the dry-run flag was a
            // first-deploy safety that has long since served its purpose and
            // only ever produced a facility with no cameras if flipped.
        }
    }
}
