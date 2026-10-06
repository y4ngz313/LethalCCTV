using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Facility.Cameras.Placement;
using Y4NGZCompany.ShipSystems.Surveillance;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    public sealed partial class DungeonCameraSpawner
    {
        // T5 — placement diagnostic report. Summary line is ALWAYS-ON
        // (single line, low strip-debt at the eventual close-out).
        // Verbose per-component and per-pick lines are gated behind
        // placement debug logging per the signed-off verbosity decision.
        private void EmitPlacementDiagnosticReport(
            IReadOnlyList<RoomComponent> components,
            IReadOnlyList<CameraPick> rawPicks,
            IReadOnlyList<CameraPick> cappedPicks,
            bool capEngaged,
            int droppedCount)
        {
            int componentCount = components != null ? components.Count : 0;
            int rawCount = rawPicks != null ? rawPicks.Count : 0;
            int cappedCount = cappedPicks != null ? cappedPicks.Count : 0;
            int junctionMinDeg = _config.JunctionMinDegree.Value;
            int cap = _config.NormalizedMaximumCameraCount;
            bool verbose = _config.PlacementDebugLoggingEnabled.Value;

            if (verbose)
            {
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ============================================================");
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] === Phase 1.8 PLACEMENT diagnostic report ===");
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] Components: {componentCount}. (Compare against the Gate A multi-tile-room warning above; " +
                    "that warning's name-groups should mostly coincide with multi-tile components here. Mismatch is informational — " +
                    "duplicate-named tiles can legitimately fall in different components when separated by corridors.)");

                for (int i = 0; i < componentCount; i++)
                {
                    RoomComponent c = components[i];
                    if (c == null || c.Tiles == null) continue;
                    int junctions = 0, degree1 = 0;
                    for (int k = 0; k < c.Degrees.Count; k++)
                    {
                        if (c.Degrees[k] >= junctionMinDeg) junctions++;
                        if (c.Degrees[k] == 1) degree1++;
                    }
                    int camerasFromComponent = 0;
                    for (int p = 0; p < rawCount; p++)
                    {
                        if (ReferenceEquals(rawPicks[p].Component, c)) camerasFromComponent++;
                    }
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] comp[{i:D2}] tiles={c.Tiles.Count,3} junctions={junctions,2} degree1={degree1,2} " +
                        $"rootSrcIdx={c.RootSrcIndex,3} footprint={c.FootprintM2,7:F1}m² → cameras={camerasFromComponent}");
                }

                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ------------------------------------------------------------");
                for (int i = 0; i < cappedCount; i++)
                {
                    CameraPick p = cappedPicks[i];
                    string tileName = p.Tile != null ? p.Tile.name : "<null>";
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] pick[{i:D2}] tile='{tileName}' deg={p.Degree} fp={p.FootprintM2:F1}m² " +
                        $"mainPath={(p.IsOnMainPath ? "Y" : "N")} srcIdx={p.SrcIndex} reason={p.Reason}");
                }

                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ------------------------------------------------------------");
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] Cap engaged: {(capEngaged ? "yes" : "no")}. Dropped: {droppedCount} picks (cap={cap}). Final count: {cappedCount}.");
            }

            // Always-on summary line — survives the eventual diagnostic
            // strip-out close-out.
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] PLACEMENT summary: {componentCount} components → {rawCount} proposed → {cappedCount} after cap " +
                $"(capEngaged={capEngaged}, dropped={droppedCount}). Counts vary per dungeon roll — expected per SPEC " +
                $"(manor range ~8–18, hangar range ~6–16).");

            if (verbose)
            {
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ============================================================");
            }
        }

        // Phase 1.8 — Gate A. Per-tile room-vs-corridor verdict using the
        // TileExclusionFilter.IsCorridor predicate, independent of any other
        // exclusion rules so the user sees the raw classification (a tile
        // excluded as "mineshaft" or "fire-exit" elsewhere still shows its
        // room/corridor verdict here).
        //
        // The report flags two human-eyes concerns the heuristic alone won't
        // catch:
        //   1) Multi-tile-room candidates: tiles classified ROOM whose name
        //      appears more than once. Hand-merged rooms (rare, but seen on
        //      some custom flows) would get duplicate cameras under the
        //      "one camera per room tile" placement rule.
        //   2) Suspicious-dropped rooms: tiles classified CORRIDOR whose name
        //      contains a known room-noun ("Room", "Bedroom", "Study",
        //      "Cellar", "Storage", "Kitchen", "Dining", "Library",
        //      "Ballroom", "Office", "Bathroom"). A genuinely small room
        //      that fails both heuristic thresholds is exactly the kind of
        //      blind spot the SPEC warned about — easier to spot in the
        //      report than to discover as a coverage gap after deploy.
        private void EmitGateAClassificationReport(IReadOnlyList<Tile> allTiles)
        {
            int totalTiles = allTiles != null ? allTiles.Count : 0;
            float minDimThresh = _config.MinHorizontalDimensionM.Value;
            float minAreaThresh = _config.MinFloorAreaM2.Value;
            int maxCorridorDeg = _config.CorridorMaxConnectedDoorways.Value;

            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ============================================================");
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] === GATE A: Room/corridor classification report ===");
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] Heuristic (conjunction): ROOM iff connDeg > {maxCorridorDeg}  OR  minDim >= {minDimThresh}m  OR  floorArea >= {minAreaThresh}m². " +
                $"Corridor iff (connDeg <= {maxCorridorDeg}) AND fails BOTH size guards. Connected-degree is necessary-not-sufficient for corridor; ambiguous tiles resolve to ROOM by construction. (Tunable via 'Phase 1.8 Room-Aware Placement' config; values synced.)");
            SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] Tiles total: {totalTiles}");

            if (totalTiles == 0)
            {
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] Gate A: AllTiles empty — nothing to classify. Likely a dungeon-generator failure upstream.");
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ============================================================");
                return;
            }

            // Phase 1.8 Gate 1 — DunGen doorway-graph probe. Logged before
            // the per-tile verdict block so the data-soundness header is
            // human-visible up front. We don't change the size-based
            // classifier here; this is investigation-only instrumentation
            // for verifying that connected-doorway degree is a usable
            // signal for the Gate 2 revision (project lesson from the
            // mineshaft tag-rebind: never trust a DunGen API surface
            // without confirming the data is populated on the
            // actually-instantiated dungeon under test).
            //
            // Static evidence (decompiled DunGen + vanilla LC usage):
            //   Tile.AllDoorways      : List<Doorway>  (every socket on the tile)
            //   Tile.UsedDoorways     : List<Doorway>  (connected subset)
            //   Doorway.ConnectedDoorway : Doorway     (nullable peer)
            // Vanilla code (RoundManager.cs ~918, AdjacentRoomCullingModified
            // ~396) iterates UsedDoorways inside the same post-generation
            // event we hook, so connected degree == UsedDoorways.Count is
            // the natural runtime signal. Probe confirms on this dungeon.
            int tilesWithAllDoorways = 0;
            int tilesWithUsedDoorways = 0;
            int tilesWithStaleConnection = 0;
            var degreeHist = new Dictionary<int, int>();
            var degreeByName = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);

            // Pass 1 — per-tile verdict line. Builds the room-name-group dict
            // for the multi-tile flag in pass 2 alongside.
            var roomNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int roomCount = 0;
            int corridorCount = 0;
            int suspiciousDropped = 0;
            for (int i = 0; i < totalTiles; i++)
            {
                Tile t = allTiles[i];
                if (t == null)
                {
                    SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] Gate A tile {i:D2} <null> → skipped");
                    continue;
                }
                var size = t.Bounds.size;
                float minDim = (size.x < size.z) ? size.x : size.z;
                float area = size.x * size.z;
                bool isCorridor = _filter.IsCorridor(t);
                string verdict;
                if (isCorridor)
                {
                    corridorCount++;
                    verdict = "corridor";
                }
                else
                {
                    roomCount++;
                    verdict = "ROOM";
                    string nm = t.name ?? "<unnamed>";
                    if (roomNameCounts.TryGetValue(nm, out int c)) roomNameCounts[nm] = c + 1;
                    else roomNameCounts[nm] = 1;
                }

                string tileName = t.name ?? "<unnamed>";
                string flag = string.Empty;
                if (isCorridor && NameSuggestsRoom(tileName))
                {
                    flag = "  ← SUSPICIOUS-DROPPED (name suggests room)";
                    suspiciousDropped++;
                }

                // Gate 1 probe columns. -1 sentinel surfaces a null list
                // (the failure mode the project's mineshaft incident
                // taught us to catch — "API surface exists" is not the
                // same as "data is populated").
                int allDoorwayCount = -1;
                int connectedDegree = -1;
                int staleConn = 0;
                if (t.AllDoorways != null) allDoorwayCount = t.AllDoorways.Count;
                if (t.UsedDoorways != null)
                {
                    connectedDegree = t.UsedDoorways.Count;
                    for (int k = 0; k < t.UsedDoorways.Count; k++)
                    {
                        var dw = t.UsedDoorways[k];
                        if (dw == null || dw.ConnectedDoorway == null) staleConn++;
                    }
                }
                if (allDoorwayCount >= 0) tilesWithAllDoorways++;
                if (connectedDegree >= 0) tilesWithUsedDoorways++;
                if (staleConn > 0) tilesWithStaleConnection++;
                int histKey = connectedDegree < 0 ? -1 : connectedDegree;
                if (degreeHist.TryGetValue(histKey, out int hc)) degreeHist[histKey] = hc + 1;
                else degreeHist[histKey] = 1;
                if (!degreeByName.TryGetValue(tileName, out var inner))
                {
                    inner = new Dictionary<int, int>();
                    degreeByName[tileName] = inner;
                }
                if (inner.TryGetValue(histKey, out int ic)) inner[histKey] = ic + 1;
                else inner[histKey] = 1;

                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] Gate A tile {i:D2} {tileName,-32} " +
                    $"size=({size.x,5:F1},{size.y,5:F1},{size.z,5:F1}) " +
                    $"minDim={minDim,5:F1} area={area,6:F1} " +
                    $"allDw={allDoorwayCount,2} connDeg={connectedDegree,2} stale={staleConn} → {verdict}{flag}");
            }

            // Pass 2 — multi-tile-room candidate flags.
            int multiTileFlagged = 0;
            foreach (var kv in roomNameCounts)
            {
                if (kv.Value > 1) multiTileFlagged++;
            }

            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ------------------------------------------------------------");
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] Gate A summary: {totalTiles} tiles → {roomCount} rooms, {corridorCount} corridors. " +
                $"If Phase 1.8 placement proceeds: expected camera count = {roomCount} (one per room tile, no multi-tile dedup yet).");

            if (multiTileFlagged > 0)
            {
                SurveillanceBootstrap.Log.LogWarning(
                    $"[LethalCCTV] Gate A: {multiTileFlagged} multi-tile-room candidate(s) — tiles classified ROOM whose name appears more than once. Each will get its own camera under the current placement rule. Listed below:");
                foreach (var kv in roomNameCounts)
                {
                    if (kv.Value > 1)
                    {
                        SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV]   '{kv.Key}' × {kv.Value} ROOM tiles → would spawn {kv.Value} cameras for what may be one room");
                    }
                }
            }
            else
            {
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] Gate A: no multi-tile-room candidates (all ROOM tiles have unique names).");
            }

            if (suspiciousDropped > 0)
            {
                SurveillanceBootstrap.Log.LogWarning(
                    $"[LethalCCTV] Gate A: {suspiciousDropped} suspicious-dropped tile(s) — classified CORRIDOR but the name suggests a room. " +
                    "If any of these are genuine rooms you want covered, LOWER 'Min Horizontal Dimension' or 'Min Floor Area' in the config before approving placement. See per-tile lines above (← SUSPICIOUS-DROPPED).");
            }
            else
            {
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] Gate A: no suspicious-dropped tiles (no room-named tile fell into the corridor bucket).");
            }

            // Phase 1.8 Gate 1 — doorway-graph data-soundness + degree
            // distribution. Reads the per-tile probe data accumulated in
            // pass 1. The "stale" count is the data-trap-equivalent of
            // the mineshaft tag-rebind: a UsedDoorways entry that exists
            // but whose ConnectedDoorway is null means the list is not
            // a reliable "connected peers" signal on this dungeon — if
            // that count is non-zero the Gate 2 revision should fall
            // back to a hybrid heuristic rather than trust the degree.
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ------------------------------------------------------------");
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] Gate 1 probe — DunGen doorway-graph readout:");
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV]   AllDoorways non-null on {tilesWithAllDoorways}/{totalTiles} tiles; " +
                $"UsedDoorways non-null on {tilesWithUsedDoorways}/{totalTiles} tiles; " +
                $"tiles with at least one UsedDoorway whose ConnectedDoorway is null: {tilesWithStaleConnection}.");
            var sortedKeys = new List<int>(degreeHist.Keys);
            sortedKeys.Sort();
            var distBuf = new StringBuilder();
            for (int k = 0; k < sortedKeys.Count; k++)
            {
                int key = sortedKeys[k];
                int count = degreeHist[key];
                string label = key < 0 ? "null" : key.ToString();
                if (k > 0) distBuf.Append(", ");
                distBuf.Append(label).Append('→').Append(count);
            }
            SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV]   Connected-degree distribution (UsedDoorways.Count): {distBuf}");
            // Per-name × degree grouping. The signal we care about is
            // "tiles whose name repeats and whose connected degree is
            // consistent" — that's a sanity check that hangar's 28
            // HangarSquare 1 instances are uniformly degree-2 corridor
            // pieces rather than 28 different roles.
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV]   Per-name × connected-degree (suspected-corridor tells):");
            var sortedNames = new List<string>(degreeByName.Keys);
            sortedNames.Sort(StringComparer.Ordinal);
            for (int n = 0; n < sortedNames.Count; n++)
            {
                string nm = sortedNames[n];
                var inner = degreeByName[nm];
                int total = 0;
                foreach (var kv in inner) total += kv.Value;
                var innerBuf = new StringBuilder();
                var innerKeys = new List<int>(inner.Keys);
                innerKeys.Sort();
                for (int k = 0; k < innerKeys.Count; k++)
                {
                    int key = innerKeys[k];
                    int count = inner[key];
                    string label = key < 0 ? "null" : key.ToString();
                    if (k > 0) innerBuf.Append(", ");
                    innerBuf.Append("deg=").Append(label).Append("×").Append(count);
                }
                SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV]     '{nm}' × {total} → {innerBuf}");
            }

            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ============================================================");
        }

        private static bool NameSuggestsRoom(string tileName)
        {
            if (string.IsNullOrEmpty(tileName)) return false;
            for (int i = 0; i < s_roomNameHints.Length; i++)
            {
                if (tileName.IndexOf(s_roomNameHints[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // Phase 1.9 — full-coverage bounds probe. Three boxes per tile,
        // because the Phase 1.9 corner placement read placement.LocalBounds
        // (Cecil-confirmed tile-local AABB) while DunGen's tile.Bounds is
        // frame-unspecified and may return the world AABB — back-solving
        // observed manor localPos.y ≈ 15m against the 0.85 height fraction
        // showed the actual box it read (LocalBounds) is ~17.6m tall, close
        // to but not the same as the 20–24m tile.Bounds rows the prior
        // probe logged. The geometry fix's safety property is
        // "facility worldAABB.y ≈ facility LocalBounds.y" (within a few
        // cm); logging all three lets the next two rolls (facility +
        // manor) certify that property cheaply before any code change.
        // Per-tile in-set flags + the post-loop summary resolve whether
        // degenerate-worldAABB tiles overlap the existing entrance /
        // fire-exit exclusion set — if degenerates are already excluded
        // from placement, the §4 fallback is moot.
        private static void LogTileBoundsProbe(
            IReadOnlyList<Tile> allTiles,
            HashSet<Tile> entranceTiles,
            HashSet<Tile> fireExitTiles,
            float wallMountMinRoomHeightM)
        {
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ============================================================");
            SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] Tile bounds probe (extended; one-shot per spawner instance — restart the game between rolls for a fresh probe). Tiles: {allTiles.Count}.");

            int degenerateCount = 0;
            int degenerateEntrance = 0;
            int degenerateFireExit = 0;
            int degenerateNeither = 0;

            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile t = allTiles[i];
                if (t == null) continue;
                Bounds tileBounds = t.Bounds;
                Bounds local = t.Placement != null ? t.Placement.LocalBounds : default;
                Bounds world = ComputeWorldAabb(t);
                bool isEntrance = entranceTiles != null && entranceTiles.Contains(t);
                bool isFireExit = fireExitTiles != null && fireExitTiles.Contains(t);
                bool degenerate = world.size.sqrMagnitude < 1e-6f;
                if (degenerate)
                {
                    degenerateCount++;
                    if (isEntrance) degenerateEntrance++;
                    else if (isFireExit) degenerateFireExit++;
                    else degenerateNeither++;
                }

                string degenerateTag = degenerate ? " DEGENERATE" : string.Empty;
                string flags = (isEntrance ? "E" : "-") + (isFireExit ? "F" : "-");
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] probe[{i:D2}] tile={t.name} flags={flags}{degenerateTag} " +
                    $"tile.Bounds.size=({tileBounds.size.x:F2},{tileBounds.size.y:F2},{tileBounds.size.z:F2}) " +
                    $"placement.LocalBounds.size=({local.size.x:F2},{local.size.y:F2},{local.size.z:F2}) " +
                    $"worldAABB.size=({world.size.x:F2},{world.size.y:F2},{world.size.z:F2}) " +
                    $"tile.rotation={t.transform.rotation.eulerAngles}");
            }

            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] Tile bounds probe SUMMARY: degenerate worldAABB on {degenerateCount} of {allTiles.Count} tiles " +
                $"(E={degenerateEntrance}, F={degenerateFireExit}, neither={degenerateNeither}). " +
                $"Decision rule: if facility worldAABB.y ≈ facility LocalBounds.y across rows, the Phase 1.9 fix is safe to swap LocalBounds→worldAABB. " +
                $"If degenerate set overlaps entirely with E/F sets, the §4 fallback is moot (those tiles are already excluded from placement).");

            // Phase 1.9b — shaft-tile entry-floor probe. For each tile
            // that the Phase 1.9 corner placement routed to wall-mount (tile-local
            // worldAABB.y > wallMountMinRoomHeightM), log the
            // candidates for "the floor the wall camera SHOULD
            // reference":
            //   - localAABB.y range — the CURRENT reference is min.y,
            //     suspected wrong on multi-storey shafts because it
            //     points at the bottom of the shaft, not the entry
            //     floor.
            //   - Placement.Position.worldY — grounds local 0 against
            //     the world-Y values already shown by camera-instantiation
            //     log lines.
            //   - Each UsedDoorway's tile-local Y — the entry doorways
            //     define where the player walks IN, i.e. the candidate
            //     correct reference.
            //
            // Doorway tile-local Y is derived through the SAME
            // Placement-based TRS inverse used by ComputeWorldAabbLocal,
            // not tile.transform.worldToLocalMatrix (which misaligned
            // on rotated tiles in P1.9 Factory testing).
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ------------------------------------------------------------");
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] Shaft-tile entry-floor probe (P1.9b §3): tiles with tile-local worldAABB.y > {wallMountMinRoomHeightM:F1}m → wall-mount candidates. " +
                "Goal: identify the player-walkable entry-floor reference for wall cameras.");

            int shaftCount = 0;
            float lowestDoorwayLocalY = float.PositiveInfinity;
            float highestDoorwayLocalY = float.NegativeInfinity;
            int shaftsWithNoDoorways = 0;
            int shaftsMultiLevel = 0;

            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile t = allTiles[i];
                if (t == null || t.Placement == null) continue;
                Bounds localAabb = ComputeWorldAabbLocal(t);
                if (localAabb.size.sqrMagnitude < 1e-6f) continue;
                if (localAabb.size.y <= wallMountMinRoomHeightM) continue;

                shaftCount++;

                Matrix4x4 tileWorldToLocal = Matrix4x4.TRS(
                    t.Placement.Position, t.Placement.Rotation, Vector3.one).inverse;

                StringBuilder doorwayBuf = new StringBuilder();
                int doorwayCount = 0;
                float minDwY = float.PositiveInfinity;
                float maxDwY = float.NegativeInfinity;
                if (t.UsedDoorways != null)
                {
                    for (int k = 0; k < t.UsedDoorways.Count; k++)
                    {
                        var dw = t.UsedDoorways[k];
                        if (dw == null) continue;
                        Vector3 dwLocal = tileWorldToLocal.MultiplyPoint3x4(dw.transform.position);
                        if (doorwayCount > 0) doorwayBuf.Append(", ");
                        doorwayBuf.Append("d").Append(k).Append('=').Append(dwLocal.y.ToString("F2"));
                        doorwayCount++;
                        if (dwLocal.y < minDwY) minDwY = dwLocal.y;
                        if (dwLocal.y > maxDwY) maxDwY = dwLocal.y;
                        if (dwLocal.y < lowestDoorwayLocalY) lowestDoorwayLocalY = dwLocal.y;
                        if (dwLocal.y > highestDoorwayLocalY) highestDoorwayLocalY = dwLocal.y;
                    }
                }
                if (doorwayCount == 0) { shaftsWithNoDoorways++; doorwayBuf.Append("<none>"); }
                // >1m vertical spread across this tile's connected doorways
                // = "shaft connects on multiple floors" — the ambiguous case
                // §3 calls out (fix needs a rule for which level to pick).
                if (doorwayCount > 1 && (maxDwY - minDwY) > 1.0f) shaftsMultiLevel++;

                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] shaft[{i:D2}] tile={t.name} " +
                    $"localAABB.y=({localAabb.min.y:F2}..{localAabb.max.y:F2}) " +
                    $"origin.worldY={t.Placement.Position.y:F2} " +
                    $"usedDoorways[{doorwayCount}] localY: {doorwayBuf}");
            }

            if (shaftCount == 0)
            {
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] Shaft-tile entry-floor probe: 0 tiles with tile-local worldAABB.y > {wallMountMinRoomHeightM:F1}m on this dungeon. " +
                    "No wall-mount candidates here — the §3 probe needs a roll with a catwalk/stair interior.");
            }
            else
            {
                string loStr = lowestDoorwayLocalY == float.PositiveInfinity ? "n/a" : lowestDoorwayLocalY.ToString("F2");
                string hiStr = highestDoorwayLocalY == float.NegativeInfinity ? "n/a" : highestDoorwayLocalY.ToString("F2");
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] Shaft-tile entry-floor probe SUMMARY: {shaftCount} shaft tile(s). " +
                    $"Used-doorway tile-local Y across all shafts: [{loStr}..{hiStr}]. " +
                    $"Shafts with NO UsedDoorways: {shaftsWithNoDoorways}. " +
                    $"Shafts with doorways spanning >1m vertically (multi-level entry): {shaftsMultiLevel}. " +
                    "Decision rule: if all entry doorways sit near local y≈0 (origin / placement plane), the fix references local 0; " +
                    "if they consistently sit above localAABB.min.y by a known offset, the fix uses min(doorway.y); " +
                    "multi-level cases need an explicit rule (lowest / nearest-to-zero).");
            }
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ============================================================");
        }

        // Phase 1.9 — tile-local twin of ComputeWorldAabb. Same mesh walk,
        // only the starting matrix changes: compose tileWorldToLocal with
        // each MeshFilter's localToWorldMatrix BEFORE applying the
        // Σ|m_ij|·ext_j extent-expansion trick, so encapsulation happens
        // in tile-local space from the start. Approach (a) per the P1.9
        // diagnostic — never world-then-reaxis-into-local, because re-
        // axis-alignment on rotated tiles inflates extents and reintroduces
        // a smaller version of the bug this fix exists to kill.
        //
        // tileWorldToLocal is derived from tile.Placement (the DunGen
        // source-of-truth TRS): tile.transform.worldToLocalMatrix is NOT
        // used here because it was observed to misalign on rotated tiles in
        // Factory testing.
        //
        // Degenerate return matches ComputeWorldAabb: zero-size Bounds at
        // the origin (in tile-local that's Vector3.zero) when no child
        // MeshFilter exists or every sharedMesh is null. Callers check
        // size.sqrMagnitude and probe the tile's LocalBounds (FallbackBox)
        // instead in that case.
        // Exposed as internal (was private) so Cameras/Probe/PlacementProbe
        // can call the SAME tile-local AABB the P1.9 placement consumes —
        // duplicating the implementation in the probe would risk drift on
        // any future Approach-(a) refinement.
        internal static Bounds ComputeWorldAabbLocal(Tile tile)
        {
            if (tile == null || tile.Placement == null)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            // #1271: EntranceRoom.Pick, the promotions, each pick, the support-camera
            // injector and the radar feeds all ask for the same tiles, and each call
            // walked the tile with GetComponentsInChildren<MeshFilter>. The result is a
            // function of the tile's Placement (fixed after generation) and the transforms
            // under it, so it is reused until that hierarchy gains or loses a transform:
            // hierarchyCount of the tile's root counts every transform under the dungeon
            // root, so any object parented into (or out of) any tile - our camera holders,
            // visuals, alarm bars - clears the whole cache, as does a new dungeon (new
            // root). Not tracked: a child that moves in place (a sweeping camera head) or a
            // MeshFilter whose mesh is swapped; neither happens to the tile's own geometry.
            // Runtime callers after the pass (InteriorSupportCameraInjector,
            // QuadCameraAssignment, PlacementProbe) therefore get the bounds first computed for a
            // tile: moving children and mesh swaps that leave hierarchyCount unchanged never
            // refresh them.
            Transform root = tile.transform.root;
            int hierarchyCount = root.hierarchyCount;
            if (!ReferenceEquals(root, s_aabbCacheRoot) || hierarchyCount != s_aabbCacheHierarchyCount)
            {
                s_aabbCache.Clear();
                s_aabbCacheRoot = root;
                s_aabbCacheHierarchyCount = hierarchyCount;
            }
            int tileId = tile.GetInstanceID();
            if (s_aabbCache.TryGetValue(tileId, out Bounds cached))
            {
                return cached;
            }

            Bounds computed = ComputeWorldAabbLocalUncached(tile);
            s_aabbCache[tileId] = computed;
            return computed;
        }

        private static readonly Dictionary<int, Bounds> s_aabbCache = new Dictionary<int, Bounds>();
        private static Transform s_aabbCacheRoot;
        private static int s_aabbCacheHierarchyCount;

        internal static void ResetTileAabbCache()
        {
            s_aabbCache.Clear();
            s_aabbCacheRoot = null;
            s_aabbCacheHierarchyCount = 0;
        }

        private static Bounds ComputeWorldAabbLocalUncached(Tile tile)
        {
            MeshFilter[] filters = tile.GetComponentsInChildren<MeshFilter>(includeInactive: true);
            if (filters == null || filters.Length == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            // tileWorldToLocal = inverse of the world TRS DunGen used to
            // place the tile. Vector3.one scale matches the Cecil-confirmed
            // TRS construction (placement uses unit scale).
            Matrix4x4 tileWorldToLocal = Matrix4x4.TRS(
                tile.Placement.Position, tile.Placement.Rotation, Vector3.one).inverse;

            Bounds acc = default;
            bool hasAny = false;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                Bounds localMesh = mf.sharedMesh.bounds;
                Matrix4x4 m = tileWorldToLocal * mf.transform.localToWorldMatrix;
                Vector3 tileCenter = m.MultiplyPoint3x4(localMesh.center);
                Vector3 tileExt = new Vector3(
                    Mathf.Abs(m.m00) * localMesh.extents.x + Mathf.Abs(m.m01) * localMesh.extents.y + Mathf.Abs(m.m02) * localMesh.extents.z,
                    Mathf.Abs(m.m10) * localMesh.extents.x + Mathf.Abs(m.m11) * localMesh.extents.y + Mathf.Abs(m.m12) * localMesh.extents.z,
                    Mathf.Abs(m.m20) * localMesh.extents.x + Mathf.Abs(m.m21) * localMesh.extents.y + Mathf.Abs(m.m22) * localMesh.extents.z);
                Bounds b = new Bounds(tileCenter, tileExt * 2f);
                if (!hasAny) { acc = b; hasAny = true; }
                else acc.Encapsulate(b);
            }
            return hasAny ? acc : new Bounds(Vector3.zero, Vector3.zero);
        }

        // Computes a world-space AABB by encapsulating each child MeshFilter's
        // mesh-local bounds transformed through its localToWorldMatrix, using
        // the standard Σ|m_ij|·ext_j extent-expansion trick to re-axis-align
        // in world space. Uses MeshFilter.sharedMesh.bounds (mesh-local,
        // independent of enable state) with includeInactive:true so the
        // Renderer.bounds lifecycle pitfall (returns Vector3.zero when the
        // renderer is disabled/culled at probe time) is sidestepped.
        // Degenerate return (zero-size Bounds at tile.transform.position)
        // occurs when the tile has no child MeshFilter at all OR every
        // filter's sharedMesh is null — entrance/teleport-style tiles
        // whose visible geometry isn't parented to the DunGen tile root.
        private static Bounds ComputeWorldAabb(Tile tile)
        {
            MeshFilter[] filters = tile.GetComponentsInChildren<MeshFilter>(includeInactive: true);
            if (filters == null || filters.Length == 0)
            {
                return new Bounds(tile.transform.position, Vector3.zero);
            }
            Bounds acc = default;
            bool hasAny = false;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter mf = filters[i];
                if (mf.sharedMesh == null) continue;
                Bounds localMesh = mf.sharedMesh.bounds;
                Matrix4x4 m = mf.transform.localToWorldMatrix;
                Vector3 worldCenter = m.MultiplyPoint3x4(localMesh.center);
                // Transform the local AABB extents to a world-space AABB
                // extents by summing |m_ij| * local_extent_j for each i.
                Vector3 worldExt = new Vector3(
                    Mathf.Abs(m.m00) * localMesh.extents.x + Mathf.Abs(m.m01) * localMesh.extents.y + Mathf.Abs(m.m02) * localMesh.extents.z,
                    Mathf.Abs(m.m10) * localMesh.extents.x + Mathf.Abs(m.m11) * localMesh.extents.y + Mathf.Abs(m.m12) * localMesh.extents.z,
                    Mathf.Abs(m.m20) * localMesh.extents.x + Mathf.Abs(m.m21) * localMesh.extents.y + Mathf.Abs(m.m22) * localMesh.extents.z);
                Bounds worldB = new Bounds(worldCenter, worldExt * 2f);
                if (!hasAny) { acc = worldB; hasAny = true; }
                else acc.Encapsulate(worldB);
            }
            return hasAny ? acc : new Bounds(tile.transform.position, Vector3.zero);
        }
    }
}
