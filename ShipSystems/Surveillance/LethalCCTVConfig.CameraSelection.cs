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
                new ConfigDefinition(CameraPlacementSection, "Exclude Main Entrance Rooms"),
                false,
                new ConfigDescription("When on, rooms that hold the main entrance get no camera. Off by default, so the main entrance is watched. Host decides."));

            ExcludeFireExitTiles = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Fire Exit Rooms"),
                false,
                new ConfigDescription("When on, rooms that hold a fire exit get no camera. Off by default, so fire exits are watched. Host decides."));

            ExcludeMineshaftTunnels = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Mineshaft Tunnels"),
                true,
                new ConfigDescription("When on, the long tunnels of the mineshaft interior get no cameras. On by default, since tunnel feeds show little but rock walls. Host decides."));

            ExcludeTinyTiles = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Tiny Rooms"),
                true,
                new ConfigDescription("When on, tiny rooms get no camera: rooms with one or no connected doorway and a floor area under Tiny Room Max Floor Area. On by default. Host decides."));

            TinyTileMaxFloorAreaM2 = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Tiny Room Max Floor Area"),
                25.0f,
                new ConfigDescription("Floor area in square metres below which a room with one or no connected doorway counts as tiny. At the default of 25, that is about a 5 by 5 metre room; only used while Exclude Tiny Rooms is on. Host decides."));

            TileNameExclusionPatterns = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Excluded Room Names"),
                string.Empty,
                new ConfigDescription("Comma-separated parts of room names; any room whose name contains one of them gets no camera, ignoring upper and lower case. Empty by default, so no room is left out by name. Host decides."));
        }

        private void BindRoomAwarePlacement(ConfigFile cfg)
        {
            ExcludeCorridors = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Exclude Corridors"),
                true,
                new ConfigDescription("When on, narrow passages that count as corridors get no camera, since their feeds mostly show walls; Corridor Max Connected Doorways, Min Horizontal Dimension and Min Floor Area decide what counts as a corridor. On by default, though Minimum Cameras can still add corridors back to reach its count. Host decides."));

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
                new ConfigDescription("A room with more connected doorways than this is never treated as a corridor, whatever its size. At the default of 2, only passages with one way in and one way out (or fewer) can be corridors, and only if they are also under Min Horizontal Dimension and Min Floor Area. Host decides."));

            MinHorizontalDimensionM = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Min Horizontal Dimension"),
                8.5f,
                new ConfigDescription("A room at least this many metres wide on its narrower side is always a room, never a corridor. At the default of 8.5, anything narrower can still count as a room if its floor area reaches Min Floor Area. Host decides."));

            MinFloorAreaM2 = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Min Floor Area"),
                80.0f,
                new ConfigDescription("A room with at least this much floor area, in square metres, is always a room, never a corridor, even when it is narrow. At the default of 80, a narrow passage smaller than 80 square metres with few doorways counts as a corridor. Host decides."));

            // Phase 1.8 Placement T1 — knobs that feed the future cluster→
            // select→cap pipeline (T2-T5). Bound here so a fresh deploy of
            // T1 alone produces an updated BepInEx config file with the new
            // defaults but no observable behavior change (nothing reads
            // these until T5 wires the pipeline into the spawner).
            JunctionMinDegree = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Junction Min Connected Doorways"),
                3,
                new ConfigDescription("Only used when One Camera Per Room is off: among rooms joined directly to each other, every room with at least this many connected doorways is a junction and gets a camera. At the default of 3, rooms where paths branch get cameras and simple walk-through rooms do not. Host decides."));

            MinimumCameraCount = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Minimum Cameras"),
                12,
                new ConfigDescription(
                    "If room selection finds fewer cameras than this, corridors are added back until the count is reached or none are left. The default is 12; 0 turns the minimum off, and Maximum Cameras wins if it is lower. Host decides.",
                    new AcceptableValueRange<int>(0, 64)));

            MaximumCameraCount = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Maximum Cameras"),
                24,
                new ConfigDescription(
                    "The most cameras one building can have, counting cameras built into the interior; when there are too many, Camera Budget Priority decides which to keep. The default is 24; 0 turns off cameras inside buildings. Host decides.",
                    new AcceptableValueRange<int>(0, 64)));

            CameraBudgetPriority = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Camera Budget Priority"),
                "MainPathThenDegreeThenArea",
                new ConfigDescription("Decides which cameras to keep when there are more than Maximum Cameras; the kept cameras are still spread around the building. MainPathThenDegreeThenArea (the default) keeps rooms on the main route through the building first, then rooms with more doorways, then bigger rooms; LargestAreaFirst keeps the biggest rooms first; HighestDegreeFirst keeps rooms with the most doorways first. Host decides."));

            LinearChainCoverage = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Coverage Without Junctions"),
                "DegreeOneEndpoints",
                new ConfigDescription("Only used when One Camera Per Room is off: how to cover a group of joined rooms in which no room reaches Junction Min Connected Doorways. DegreeOneEndpoints (the default) puts a camera in each dead-end room at the ends of the group; LargestTileOnly puts one camera in the largest room. Host decides."));

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
            // wide margin, which is the point. The C# name keeps the
            // historical "Leaf" spelling; config v3 renamed the key to
            // "Extra Room Camera Min Area" and CctvConfigSurfaceMigration
            // moves values saved under the old "Leaf Camera Min Area" key.
            LeafCameraMinAreaM2 = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "Extra Room Camera Min Area"),
                80.0f,
                new ConfigDescription("Only used when One Camera Per Room is off: in a group of joined rooms that has junctions, any other room with at least this much floor area, in square metres, also gets its own camera. At the default of 80, large side rooms are covered; 0 turns this off. Host decides."));

            OneCameraPerTile = cfg.BindSyncedEntry(
                new ConfigDefinition(CameraPlacementSection, "One Camera Per Room"),
                true,
                new ConfigDescription("When on, every room that is not excluded gets a camera, wherever one can be mounted. On by default; turn off to place cameras only where paths branch and in large rooms, as set by Junction Min Connected Doorways, Coverage Without Junctions and Extra Room Camera Min Area. Host decides."));

            // The Phase 1.9 mount-mode threshold and wall-mount height, and
            // the T1 "Dry Run Camera Placement" STOP flag, left the config
            // file for 1.0 (#575). The first two are placement calibration
            // (now constants on LethalCCTVConfig); the dry-run flag was a
            // first-deploy safety that has long since served its purpose and
            // only ever produced a facility with no cameras if flipped.
        }
    }
}
