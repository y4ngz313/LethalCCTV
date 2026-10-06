using System;
using System.Collections.Generic;
using System.Text;
using DunGen;
using DunGen.Tags;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.ShipSystems.Surveillance;

namespace Y4NGZCompany.Facility.Cameras
{
    public sealed class TileExclusionFilter
    {
        public const string ReasonMineshaftTunnel = "mineshaft-tunnel";
        public const string ReasonTinyTile = "tiny-tile";
        public const string ReasonNamePattern = "name-pattern";
        public const string ReasonEntrance = "entrance-tile";
        public const string ReasonFireExit = "fire-exit-tile";
        public const string ReasonCorridor = "corridor";
        public const string ReasonPaddedCell = "padded-cell";

        // Phase 1.7-blocker fix — DunGen Tag.id defaults to -1; an uninitialized
        // RoundManager.MineshaftTunnelTag on a modded moon collides against any
        // tile whose Tags list also contains an id-=-1 entry, causing 38/38
        // false-positive "mineshaft" classifications and zero spawned cameras.
        // We harden the predicate with two layers, gated by one-shot latches
        // that surface what the tag actually resolves to. Static so the
        // "at most once per session" contract isn't broken if the spawner
        // ever constructs a second filter mid-process.
        private const string ExpectedMineshaftTunnelName = "MineshaftTunnel";
        private const int PROBE_TILE_SAMPLE_COUNT = 5;
        private static bool _mineshaftProbeEmitted;
        private static bool _mineshaftIdSkipWarnEmitted;
        private static bool _mineshaftNameSkipWarnEmitted;

        private readonly LethalCCTVConfig _config;

        public TileExclusionFilter(LethalCCTVConfig config)
        {
            _config = config;
        }

        // Entrance and fire-exit rules require precomputed sets because the
        // EntranceTeleport components in LC are not children of any DunGen tile
        // (they live under Environment/Teleports/ as scene-root siblings) and the
        // dungeon-side scripts only spawn after our OnFinishedGeneratingDungeon
        // hook fires. #1283: DungeonCameraSpawner runs synchronously inside that
        // event and builds the sets by closest-center attribution from the
        // entrance and fire-exit pads it reads off the tiles' SpawnSyncedObject
        // markers. See 01_phase1_filter_rewrite.md for the full design.
        public bool ShouldInclude(
            Tile tile,
            out string excludedReason,
            ISet<Tile> entranceTiles,
            ISet<Tile> fireExitTiles)
        {
            excludedReason = null;
            if (tile == null) { excludedReason = "null-tile"; return false; }

            // #874: Core's per-interior exclusion (the Rubber Rooms padded cells) is not a
            // camera preference, so no config switch and no corridor top-up can re-admit it.
            if (InteriorPlacementService.IsTileExcludedFromPlacement(tile))
            {
                excludedReason = ReasonPaddedCell;
                return false;
            }

            if (_config.ExcludeMineshaftTunnels.Value && IsMineshaftTunnel(tile))
            {
                excludedReason = ReasonMineshaftTunnel;
                return false;
            }

            if (_config.ExcludeTinyTiles.Value && IsTinyTile(tile))
            {
                excludedReason = ReasonTinyTile;
                return false;
            }

            string namePatterns = _config.TileNameExclusionPatterns.Value;
            if (!string.IsNullOrEmpty(namePatterns) && MatchesNamePattern(tile, namePatterns))
            {
                excludedReason = ReasonNamePattern;
                return false;
            }

            if (_config.ExcludeEntranceTiles.Value && entranceTiles != null && entranceTiles.Contains(tile))
            {
                excludedReason = ReasonEntrance;
                return false;
            }

            if (_config.ExcludeFireExitTiles.Value && fireExitTiles != null && fireExitTiles.Contains(tile))
            {
                excludedReason = ReasonFireExit;
                return false;
            }

            // Phase 1.8 — sixth rule. Sits last so it doesn't pre-empt the
            // earlier-rule reasons in the per-rule exclusion count (a tile that
            // is BOTH a mineshaft AND classifies as a corridor should be
            // counted as mineshaft, the more specific reason). Same predicate
            // sign as the other Exclude* rules: true config + matching tile
            // ⇒ exclude.
            if (_config.ExcludeCorridors.Value && IsCorridor(tile))
            {
                excludedReason = ReasonCorridor;
                return false;
            }

            return true;
        }

        // Composite mineshaft predicate, hardened against DunGen Tag.id=-1
        // default-value collisions observed on Gray*-tile manor moons (Phase
        // 1.7-blocker diagnostic, 2026-05-20). A tile is "mineshaft" iff:
        //   (1) RoundManager.MineshaftTunnelTag is a non-null Tag with ID >= 0
        //   (2) DunGen's TagManager resolves that ID to the literal name
        //       "MineshaftTunnel" (rules out a re-bound or renamed tag asset)
        //   (3) the tile's Tags list contains that Tag (vanilla LC's id-equality
        //       check — DunGen.Tags.TagContainer.HasTag is List<Tag>.Contains)
        // Failing (1) or (2) emits a one-shot LOUD WARN explaining the skip and
        // returns false (do NOT exclude) — better to spawn a few unwanted
        // tunnel cameras on a misconfigured moon than to silently spawn zero
        // cameras anywhere, which is the bug we're fixing. Failing (3) is the
        // normal "not a mineshaft tile" path and is silent.
        private bool IsMineshaftTunnel(Tile tile)
        {
            RoundManager rm = RoundManager.Instance;
            if (rm == null || rm.MineshaftTunnelTag == null || tile.Tags == null) return false;

            // Stash so we don't re-read a property that internally calls into
            // the DunGen TagManager twice; also avoids racing against any mod
            // that mutates the singleton mid-iteration.
            var mst = rm.MineshaftTunnelTag;
            int mstId = mst.ID;

            bool placementDebugLogging = _config.PlacementDebugLoggingEnabled.Value;
            if (placementDebugLogging)
                EmitMineshaftProbeOnce(rm, mst, mstId);

            // (a) ID-validity guard.
            if (mstId < 0)
            {
                if (placementDebugLogging)
                    EmitMineshaftIdSkipWarnOnce(mstId);
                return false;
            }

            // (b) Name second-opinion. TryResolveTagName returns null if the
            // DunGenSettings singleton or TagManager is unreachable, or if the
            // ID isn't registered — all treated as "untrustworthy, skip".
            string resolvedName = TryResolveTagName(mstId);
            if (resolvedName != ExpectedMineshaftTunnelName)
            {
                if (placementDebugLogging)
                    EmitMineshaftNameSkipWarnOnce(mstId, resolvedName);
                return false;
            }

            return tile.Tags.HasTag(mst);
        }

        // Wraps DunGenSettings.Instance.TagManager.TryGetNameFromID with full
        // null-guarding + a defensive catch. The singleton getter constructs
        // an instance asset on first access; in the LC deploy it always exists,
        // but we don't bet the spawn pipeline on that.
        private static string TryResolveTagName(int id)
        {
            try
            {
                var settings = DunGenSettings.Instance;
                if (settings == null) return null;
                var manager = settings.TagManager;
                if (manager == null) return null;
                return manager.TryGetNameFromID(id);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV] TileExclusionFilter.TryResolveTagName({id}) threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void EmitMineshaftProbeOnce(RoundManager rm, Tag mst, int mstId)
        {
            if (_mineshaftProbeEmitted) return;
            _mineshaftProbeEmitted = true;

            string resolvedName = TryResolveTagName(mstId);
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] Mineshaft-tag probe: RoundManager.MineshaftTunnelTag ID={mstId} resolvedName='{resolvedName ?? "<null>"}' " +
                $"(expected ID>=0 with name '{ExpectedMineshaftTunnelName}').");

            IReadOnlyList<Tile> all = TryGetAllTilesFromDungeon();
            if (all == null || all.Count == 0)
            {
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] Mineshaft-tag probe: AllTiles unavailable at predicate-call time — tile-side sample suppressed.");
                return;
            }

            int sampleCount = Math.Min(PROBE_TILE_SAMPLE_COUNT, all.Count);
            for (int i = 0; i < sampleCount; i++)
            {
                Tile t = all[i];
                if (t == null) continue;
                string name = t.name;
                var sb = new StringBuilder();
                if (t.Tags != null && t.Tags.Tags != null)
                {
                    for (int j = 0; j < t.Tags.Tags.Count; j++)
                    {
                        var tag = t.Tags.Tags[j];
                        if (tag == null) { sb.Append("<null>"); }
                        else { sb.Append(tag.ID); }
                        if (j + 1 < t.Tags.Tags.Count) sb.Append(',');
                    }
                }
                else
                {
                    sb.Append("<no-tags>");
                }
                SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] Mineshaft-tag probe: tile[{i}] name='{name}' tagIDs=[{sb}]");
            }
        }

        private static void EmitMineshaftIdSkipWarnOnce(int mstId)
        {
            if (_mineshaftIdSkipWarnEmitted) return;
            _mineshaftIdSkipWarnEmitted = true;

            int colliders = CountTilesWithTagId(mstId);
            // #716 F4: was a nine-line banner. One line carries the same facts.
            SurveillanceBootstrap.Log.LogWarning(
                "[LethalCCTV] Mineshaft tile filter disabled this session (ID-validity guard): " +
                $"RoundManager.MineshaftTunnelTag.ID={mstId} (< 0 = DunGen default, tag asset uninitialized on this scene). " +
                "Using the predicate would falsely match every tile carrying any default-id tag, so mineshaft tiles " +
                $"will not be excluded this run. Tiles in this dungeon carrying a tag with id={mstId}: {colliders}.");
        }

        private static void EmitMineshaftNameSkipWarnOnce(int mstId, string resolvedName)
        {
            if (_mineshaftNameSkipWarnEmitted) return;
            _mineshaftNameSkipWarnEmitted = true;

            // #716 F4: was a nine-line banner. One line carries the same facts.
            SurveillanceBootstrap.Log.LogWarning(
                "[LethalCCTV] Mineshaft tile filter disabled this session (name second-opinion): " +
                $"RoundManager.MineshaftTunnelTag.ID={mstId} but the DunGen TagManager resolves that ID to " +
                $"'{resolvedName ?? "<null>"}' (expected '{ExpectedMineshaftTunnelName}'). A mod has rebound or renamed " +
                "the tag asset, so the id-equality predicate is no longer meaningful and mineshaft tiles will not be " +
                "excluded this run.");
        }

        // Same dungeon resolution path DungeonCameraSpawner uses. Wrapped in
        // try/catch because anything that touches RoundManager.dungeonGenerator
        // mid-pipeline can NRE if a mod tears it down between rounds.
        private static IReadOnlyList<Tile> TryGetAllTilesFromDungeon()
        {
            try
            {
                var rm = RoundManager.Instance;
                var rtd = rm != null ? rm.dungeonGenerator : null;
                var gen = rtd != null ? rtd.Generator : null;
                var dungeon = gen != null ? gen.CurrentDungeon : null;
                return dungeon != null ? dungeon.AllTiles : null;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV] TileExclusionFilter probe: AllTiles resolution threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static int CountTilesWithTagId(int id)
        {
            var all = TryGetAllTilesFromDungeon();
            if (all == null) return -1;
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var t = all[i];
                if (t == null || t.Tags == null || t.Tags.Tags == null) continue;
                for (int j = 0; j < t.Tags.Tags.Count; j++)
                {
                    var tag = t.Tags.Tags[j];
                    if (tag != null && tag.ID == id) { n++; break; }
                }
            }
            return n;
        }

        private bool IsTinyTile(Tile tile)
        {
            int usedDoorwayCount = tile.UsedDoorways != null ? tile.UsedDoorways.Count : 0;
            if (usedDoorwayCount > 1) return false;
            var size = tile.Bounds.size;
            float floorArea = size.x * size.z;
            return floorArea < _config.TinyTileMaxFloorAreaM2.Value;
        }

        // Phase 1.8 — room-vs-corridor classifier (Gate 2: conjunction).
        // Corridor iff BOTH:
        //   (a) connected-doorway degree <= CorridorMaxConnectedDoorways
        //   (b) size fails BOTH guards:
        //         minDim   = min(size.x, size.z) >= MinHorizontalDimensionM
        //         floorArea = size.x * size.z    >= MinFloorAreaM2
        // Equivalent ROOM clause: connDeg > max OR minDim >= 8m OR area >= 80m².
        //
        // Gate 1 established that degree alone does NOT separate room from
        // corridor on either probed tileset — manor's connDeg=2 bucket
        // mixed genuine corridors (CloverTile, HallwayTileType*) with
        // genuine rooms (Greenhouse, Birthday, Window, ThinStair), and
        // hangar's TheGarage (a big room) was connDeg=1. So degree is
        // necessary-not-sufficient for corridor: a high-degree tile is a
        // junction/room regardless of size, and a large tile is a room
        // regardless of degree. The conjunction encodes the bias-toward-
        // room rule by construction — a misclassified room costs a blind
        // room (no camera), so ambiguity must resolve to ROOM.
        //
        // Defensive bias when the degree signal is unavailable: a null
        // UsedDoorways list is treated as high-degree (lowDegree=false)
        // so a small unknown-degree tile resolves to ROOM rather than
        // falling through to a size-only corridor verdict. Gate 1 probe
        // measured 0 null-list tiles on both confirming dungeons; this is
        // a safety, not a fast path.
        //
        // Degenerate bounds (zero/negative on either horizontal axis) are
        // still treated as corridors — a tile with no meaningful floor is
        // never a useful camera site regardless of degree. bounds.size.y
        // is ignored: corridor tiles often share ceiling height with rooms.
        internal bool IsCorridor(Tile tile)
        {
            if (tile == null) return false;
            var size = tile.Bounds.size;
            if (size.x <= 0f || size.z <= 0f) return true;

            // Bias toward ROOM when UsedDoorways is unavailable: a null
            // list maps to int.MaxValue so the degree gate fails and the
            // tile is classified ROOM without consulting size.
            int connectedDegree = tile.UsedDoorways != null ? tile.UsedDoorways.Count : int.MaxValue;
            int maxCorridorDeg = _config.CorridorMaxConnectedDoorways.Value;
            if (connectedDegree > maxCorridorDeg) return false;

            float minDim = size.x < size.z ? size.x : size.z;
            float floorArea = size.x * size.z;

            bool clearsMinDim = minDim >= _config.MinHorizontalDimensionM.Value;
            bool clearsArea = floorArea >= _config.MinFloorAreaM2.Value;
            return !(clearsMinDim || clearsArea);
        }

        private static bool MatchesNamePattern(Tile tile, string commaSeparatedPatterns)
        {
            string tileName = tile.name;
            if (string.IsNullOrEmpty(tileName)) return false;
            string[] patterns = commaSeparatedPatterns.Split(',');
            for (int i = 0; i < patterns.Length; i++)
            {
                string p = patterns[i].Trim();
                if (string.IsNullOrEmpty(p)) continue;
                if (tileName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
