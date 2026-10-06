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
        // P2.0 §6 — additive post-cap promotion layer. Pure function of
        // allTiles + the current orderedPicks; does NOT modify the
        // selector or the capper. Eviction order matches the active
        // CameraBudgetPriority (the same comparator the capper used to
        // truncate), so the survivors of this method are exactly the
        // set the capper would have produced at cap-1, plus the entrance.
        //
        // Constraints honored (per §6 sign-off):
        //   - One-for-one swap; never grows past the cap.
        //   - Deterministic eviction target (capper's sort is totally
        //     ordered on SrcIndex; same on host and client).
        //   - No-op when the entrance is already in orderedPicks
        //     (common case — emits the "none (already-picked)" log line).
        //   - Cap == 0 / empty pick set / no entrance found → no-op.
        private List<CameraPick> ApplyEntrancePromotion(
            IReadOnlyList<Tile> allTiles,
            List<CameraPick> orderedPicks)
        {
            // Prefer the tile the MAIN EntranceTeleport actually attributed to
            // (ground truth: the player walks in there) over EntranceRoom's
            // name/pathdepth heuristics; fall back to the heuristic pick when
            // attribution found nothing this roll.
            Placement.EntranceRoom.Result entranceResult;
            if (_mainEntranceTile != null && TryFindSrcIndex(allTiles, _mainEntranceTile, out int mainEntranceSrc))
            {
                entranceResult = new Placement.EntranceRoom.Result(
                    _mainEntranceTile, mainEntranceSrc, "teleport-attribution", "none", -1);
            }
            else
            {
                entranceResult = Placement.EntranceRoom.Pick(allTiles);
            }

            if (!entranceResult.Found)
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                    SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] ENTRANCE_PROMOTION none (no entrance candidate found)");
                return orderedPicks;
            }

            // Already in orderedPicks? Common case for low-cap-pressure
            // rolls or when the entrance is a high-degree junction. Log
            // for next-roll visibility but emit no behavior change.
            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (ReferenceEquals(orderedPicks[i].Tile, entranceResult.Tile))
                {
                    if (_config.PlacementDebugLoggingEnabled.Value)
                    {
                        SurveillanceBootstrap.Log.LogInfo(
                            $"[LethalCCTV] ENTRANCE_PROMOTION none (already-picked) entrance=idx{entranceResult.SrcIndex} name={entranceResult.Tile.name} picked_by={entranceResult.PickedBy} token={entranceResult.MatchedToken}");
                    }
                    return orderedPicks;
                }
            }

            // Empty pick set — no slot to evict. Edge case; do not grow.
            // The entrance cannot be promoted without breaking the cap
            // budget, so log and return unchanged.
            if (orderedPicks.Count == 0)
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] ENTRANCE_PROMOTION skipped (empty pick set) entrance=idx{entranceResult.SrcIndex} name={entranceResult.Tile.name}");
                }
                return orderedPicks;
            }

            // Sort a clone by the active capper-priority comparator.
            // Lowest-priority NON-promoted pick = the eviction target
            // (the capper would have dropped it first at a tighter cap).
            // Earlier-promoted picks are exempt so promotions can never
            // evict each other's guarantees.
            if (!TryPickEvictionTarget(orderedPicks, out CameraPick evicted))
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] ENTRANCE_PROMOTION skipped (no evictable pick — all picks promoted) entrance=idx{entranceResult.SrcIndex} name={entranceResult.Tile.name}");
                }
                return orderedPicks;
            }

            // Construct a CameraPick for the entrance tile. Component is
            // null because the entrance was identified outside the
            // selector's component pipeline; downstream code consults
            // Component only on the placement diagnostic verbose path
            // (which guards against null already — see EmitPlacementDiagnosticReport).
            Tile et = entranceResult.Tile;
            Bounds eaabb = ComputeWorldAabbLocal(et);
            float eFootprint = eaabb.size.x * eaabb.size.z;
            int eDegree = et.UsedDoorways != null ? et.UsedDoorways.Count : 0;
            bool eMain = et.Placement != null && et.Placement.IsOnMainPath;
            CameraPick promoted = new CameraPick(
                et, component: null,
                reason: CameraPickReason.EntrancePromotion,
                degree: eDegree, footprintM2: eFootprint,
                isOnMainPath: eMain, srcIndex: entranceResult.SrcIndex);

            // Rebuild the list with the eviction excluded and the
            // promotion inserted, then re-sort for instantiation order
            // (so the new tile lands in a stable slot, not at the end).
            List<CameraPick> result = new List<CameraPick>(orderedPicks.Count);
            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (ReferenceEquals(orderedPicks[i].Tile, evicted.Tile)) continue;
                result.Add(orderedPicks[i]);
            }
            result.Add(promoted);
            result.Sort(ComparePicksForInstantiation);

            if (_config.PlacementDebugLoggingEnabled.Value)
            {
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] ENTRANCE_PROMOTION evicted=idx{evicted.SrcIndex} name={evicted.Tile?.name} " +
                    $"inserted=idx{entranceResult.SrcIndex} name={et.name} " +
                    $"picked_by={entranceResult.PickedBy} token={entranceResult.MatchedToken} entranceDoor={entranceResult.EntranceDoorIdx}");
            }

            return result;
        }

        private List<CameraPick> ApplyApparatusPromotion(
            IReadOnlyList<Tile> allTiles,
            List<CameraPick> orderedPicks,
            IReadOnlyList<Vector3> apparatusPositions)
        {
            _apparatusTile = null;
            _apparatusWorldPosition = Vector3.zero;
            _hasApparatusTarget = false;

            if (allTiles == null || orderedPicks == null)
                return orderedPicks;
            if (!TryResolveApparatusPromotionTarget(
                    allTiles,
                    apparatusPositions,
                    out Tile apparatusTile,
                    out int apparatusSrcIndex,
                    out Vector3 apparatusPosition))
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                    SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] APPARATUS_PROMOTION none (no facility apparatus found)");
                return orderedPicks;
            }

            _apparatusTile = apparatusTile;
            _apparatusWorldPosition = apparatusPosition;
            _hasApparatusTarget = true;

            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (!ReferenceEquals(orderedPicks[i].Tile, apparatusTile))
                    continue;
                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] APPARATUS_PROMOTION none (already-picked) tile=idx{apparatusSrcIndex} name={apparatusTile.name}");
                }
                return orderedPicks;
            }

            if (orderedPicks.Count == 0)
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                    SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] APPARATUS_PROMOTION skipped (empty pick set)");
                return orderedPicks;
            }

            if (!TryPickEvictionTarget(orderedPicks, out CameraPick evicted))
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] APPARATUS_PROMOTION skipped (no evictable pick - all picks promoted) tile=idx{apparatusSrcIndex} name={apparatusTile.name}");
                }
                return orderedPicks;
            }

            Bounds apparatusAabb = ComputeWorldAabbLocal(apparatusTile);
            float footprint = apparatusAabb.size.sqrMagnitude >= 1e-6f
                ? apparatusAabb.size.x * apparatusAabb.size.z
                : FallbackBox(apparatusTile).size.x * FallbackBox(apparatusTile).size.z;
            int degree = apparatusTile.UsedDoorways != null ? apparatusTile.UsedDoorways.Count : 0;
            bool isOnMainPath = apparatusTile.Placement != null && apparatusTile.Placement.IsOnMainPath;
            CameraPick promoted = new CameraPick(
                apparatusTile,
                component: null,
                reason: CameraPickReason.ApparatusPromotion,
                degree: degree,
                footprintM2: footprint,
                isOnMainPath: isOnMainPath,
                srcIndex: apparatusSrcIndex);

            List<CameraPick> result = new List<CameraPick>(orderedPicks.Count);
            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (!ReferenceEquals(orderedPicks[i].Tile, evicted.Tile))
                    result.Add(orderedPicks[i]);
            }
            result.Add(promoted);
            result.Sort(ComparePicksForInstantiation);

            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] APPARATUS_PROMOTION pos=({apparatusPosition.x:F2},{apparatusPosition.y:F2},{apparatusPosition.z:F2}) "
                + $"evicted=idx{evicted.SrcIndex} name={evicted.Tile?.name} inserted=idx{apparatusSrcIndex} name={apparatusTile.name}");
            return result;
        }

        // Mainframe-room coverage guarantee, mirroring ApplyFireExitPromotion.
        // The mainframe is an authored injected tile owned by the LGUContractHUD
        // module; this assembly cannot reference that type, so the tile is
        // identified structurally: a component named MainframeAuthoredTileMarker
        // or the authored-template token stamped into the tile prefab's name.
        // Both are seed-stable and identical on host and clients.
        private List<CameraPick> ApplyMainframePromotion(
            IReadOnlyList<Tile> allTiles,
            List<CameraPick> orderedPicks)
        {
            if (allTiles == null || orderedPicks == null)
                return orderedPicks;

            Tile mainframeTile = null;
            int mainframeSrcIndex = -1;
            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile candidate = allTiles[i];
                if (candidate == null || !IsMainframeTile(candidate))
                    continue;
                mainframeTile = candidate;
                mainframeSrcIndex = i;
                break;
            }

            if (mainframeTile == null)
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                    SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] MAINFRAME_PROMOTION none (no mainframe tile on this interior)");
                return orderedPicks;
            }

            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (!ReferenceEquals(orderedPicks[i].Tile, mainframeTile))
                    continue;

                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] MAINFRAME_PROMOTION none (already-picked) tile=idx{mainframeSrcIndex} name={mainframeTile.name}");
                }
                return orderedPicks;
            }

            if (orderedPicks.Count == 0)
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                    SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] MAINFRAME_PROMOTION skipped (empty pick set)");
                return orderedPicks;
            }

            if (!TryPickEvictionTarget(orderedPicks, out CameraPick evicted))
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] MAINFRAME_PROMOTION skipped (no evictable pick — all picks promoted) tile=idx{mainframeSrcIndex} name={mainframeTile.name}");
                }
                return orderedPicks;
            }

            Bounds mainframeAabb = ComputeWorldAabbLocal(mainframeTile);
            float footprint = mainframeAabb.size.sqrMagnitude >= 1e-6f
                ? mainframeAabb.size.x * mainframeAabb.size.z
                : FallbackBox(mainframeTile).size.x * FallbackBox(mainframeTile).size.z;
            int degree = mainframeTile.UsedDoorways != null ? mainframeTile.UsedDoorways.Count : 0;
            bool isOnMainPath = mainframeTile.Placement != null && mainframeTile.Placement.IsOnMainPath;
            CameraPick promoted = new CameraPick(
                mainframeTile,
                component: null,
                reason: CameraPickReason.MainframePromotion,
                degree: degree,
                footprintM2: footprint,
                isOnMainPath: isOnMainPath,
                srcIndex: mainframeSrcIndex);

            List<CameraPick> result = new List<CameraPick>(orderedPicks.Count);
            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (ReferenceEquals(orderedPicks[i].Tile, evicted.Tile))
                    continue;
                result.Add(orderedPicks[i]);
            }
            result.Add(promoted);
            result.Sort(ComparePicksForInstantiation);

            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] MAINFRAME_PROMOTION evicted=idx{evicted.SrcIndex} name={evicted.Tile?.name} inserted=idx{mainframeSrcIndex} name={mainframeTile.name}");
            return result;
        }

        private static bool IsMainframeTile(Tile tile)
        {
            if (tile == null) return false;
            string tileName = tile.name ?? string.Empty;
            if (tileName.IndexOf("MainframeAuthoredTemplate", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            Component[] components = tile.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component != null && component.GetType().Name == "MainframeAuthoredTileMarker")
                    return true;
            }
            return false;
        }

        private List<CameraPick> ApplyObjectivePromotion(
            IReadOnlyList<Tile> allTiles,
            List<CameraPick> orderedPicks)
        {
            if (allTiles == null || orderedPicks == null)
                return orderedPicks;

            if (!TryResolveObjectivePromotionTarget(allTiles, out Tile objectiveTile, out int objectiveSrcIndex, out ContractObjectiveProvider.MarkerSnapshot marker))
                return orderedPicks;

            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (!ReferenceEquals(orderedPicks[i].Tile, objectiveTile))
                    continue;

                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] OBJECTIVE_PROMOTION none (already-picked) marker='{marker.Label}' tile=idx{objectiveSrcIndex} name={objectiveTile.name}");
                }
                return orderedPicks;
            }

            if (orderedPicks.Count == 0)
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] OBJECTIVE_PROMOTION skipped (empty pick set) marker='{marker.Label}' tile=idx{objectiveSrcIndex} name={objectiveTile.name}");
                }
                return orderedPicks;
            }

            if (!TryPickEvictionTarget(orderedPicks, out CameraPick evicted))
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] OBJECTIVE_PROMOTION skipped (no evictable pick — all picks promoted) marker='{marker.Label}' tile=idx{objectiveSrcIndex} name={objectiveTile.name}");
                }
                return orderedPicks;
            }

            Bounds objectiveAabb = ComputeWorldAabbLocal(objectiveTile);
            float footprint = objectiveAabb.size.sqrMagnitude >= 1e-6f
                ? objectiveAabb.size.x * objectiveAabb.size.z
                : FallbackBox(objectiveTile).size.x * FallbackBox(objectiveTile).size.z;
            int degree = objectiveTile.UsedDoorways != null ? objectiveTile.UsedDoorways.Count : 0;
            bool isOnMainPath = objectiveTile.Placement != null && objectiveTile.Placement.IsOnMainPath;
            CameraPick promoted = new CameraPick(
                objectiveTile,
                component: null,
                reason: CameraPickReason.ObjectivePromotion,
                degree: degree,
                footprintM2: footprint,
                isOnMainPath: isOnMainPath,
                srcIndex: objectiveSrcIndex);

            List<CameraPick> result = new List<CameraPick>(orderedPicks.Count);
            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (ReferenceEquals(orderedPicks[i].Tile, evicted.Tile))
                    continue;
                result.Add(orderedPicks[i]);
            }
            result.Add(promoted);
            result.Sort(ComparePicksForInstantiation);

            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] OBJECTIVE_PROMOTION marker='{marker.Label}' pos=({marker.Position.x:F2},{marker.Position.y:F2},{marker.Position.z:F2}) evicted=idx{evicted.SrcIndex} name={evicted.Tile?.name} inserted=idx{objectiveSrcIndex} name={objectiveTile.name}");
            return result;
        }

        // Fire-exit coverage guarantee, mirroring ApplyEntrancePromotion.
        // Fire-exit tiles are excluded by TileExclusionFilter, so absent
        // this pass the selector can never place a camera on one. Same
        // one-for-one post-cap swap contract as the other promotions:
        // never grows past the cap, deterministic on srcIndex, no-op when
        // a fire-exit tile is already picked.
        private List<CameraPick> ApplyFireExitPromotion(
            IReadOnlyList<Tile> allTiles,
            List<CameraPick> orderedPicks,
            ISet<Tile> fireExitTiles)
        {
            if (allTiles == null || orderedPicks == null)
                return orderedPicks;

            if (fireExitTiles == null || fireExitTiles.Count == 0)
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                    SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] FIREEXIT_PROMOTION none (no fire-exit tiles on this interior)");
                return orderedPicks;
            }

            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (orderedPicks[i].Tile == null || !fireExitTiles.Contains(orderedPicks[i].Tile))
                    continue;

                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] FIREEXIT_PROMOTION none (already-picked) tile=idx{orderedPicks[i].SrcIndex} name={orderedPicks[i].Tile.name}");
                }
                return orderedPicks;
            }

            if (orderedPicks.Count == 0)
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                    SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] FIREEXIT_PROMOTION skipped (empty pick set)");
                return orderedPicks;
            }

            // Deterministic target: the lowest-srcIndex fire-exit tile.
            // allTiles order is the dungeon's canonical AllTiles order,
            // identical on host and clients.
            Tile fireExitTile = null;
            int fireExitSrcIndex = -1;
            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile candidate = allTiles[i];
                if (candidate == null || !fireExitTiles.Contains(candidate))
                    continue;
                fireExitTile = candidate;
                fireExitSrcIndex = i;
                break;
            }
            if (fireExitTile == null)
                return orderedPicks;

            if (!TryPickEvictionTarget(orderedPicks, out CameraPick evicted))
            {
                if (_config.PlacementDebugLoggingEnabled.Value)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] FIREEXIT_PROMOTION skipped (no evictable pick — all picks promoted) tile=idx{fireExitSrcIndex} name={fireExitTile.name}");
                }
                return orderedPicks;
            }

            Bounds fireExitAabb = ComputeWorldAabbLocal(fireExitTile);
            float footprint = fireExitAabb.size.sqrMagnitude >= 1e-6f
                ? fireExitAabb.size.x * fireExitAabb.size.z
                : FallbackBox(fireExitTile).size.x * FallbackBox(fireExitTile).size.z;
            int degree = fireExitTile.UsedDoorways != null ? fireExitTile.UsedDoorways.Count : 0;
            bool isOnMainPath = fireExitTile.Placement != null && fireExitTile.Placement.IsOnMainPath;
            CameraPick promoted = new CameraPick(
                fireExitTile,
                component: null,
                reason: CameraPickReason.FireExitPromotion,
                degree: degree,
                footprintM2: footprint,
                isOnMainPath: isOnMainPath,
                srcIndex: fireExitSrcIndex);

            List<CameraPick> result = new List<CameraPick>(orderedPicks.Count);
            for (int i = 0; i < orderedPicks.Count; i++)
            {
                if (ReferenceEquals(orderedPicks[i].Tile, evicted.Tile))
                    continue;
                result.Add(orderedPicks[i]);
            }
            result.Add(promoted);
            result.Sort(ComparePicksForInstantiation);

            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] FIREEXIT_PROMOTION evicted=idx{evicted.SrcIndex} name={evicted.Tile?.name} inserted=idx{fireExitSrcIndex} name={fireExitTile.name}");
            return result;
        }

        // Shared eviction-target selection for the promotion passes. Sorts
        // a clone by the active capper-priority comparator and walks from
        // the lowest-priority end, skipping picks that were themselves
        // inserted by a promotion — a guarantee established by one pass
        // must survive every later pass. Returns false when every pick is
        // promoted (tiny caps); the caller skips its promotion to honor
        // the cap budget.
        private bool TryPickEvictionTarget(List<CameraPick> orderedPicks, out CameraPick evicted)
        {
            Comparison<CameraPick> capCmp = CameraBudgetCapper.GetCapPriorityComparison(_config);
            List<CameraPick> byPriority = new List<CameraPick>(orderedPicks);
            byPriority.Sort(capCmp);
            for (int i = byPriority.Count - 1; i >= 0; i--)
            {
                if (IsPromotedPick(byPriority[i].Reason))
                    continue;
                evicted = byPriority[i];
                return true;
            }
            evicted = default;
            return false;
        }

        // ALWAYS-ON warning: a promotion pass reserved this room a camera and
        // the mount pipeline still couldn't place one. Every such miss must be
        // visible without debug logging, because the user-facing symptom
        // ("no camera in the mainframe/entrance/objective room") is otherwise
        // indistinguishable from the promotion never having run.
        private static void WarnPromotedPickUnmounted(CameraPick pick, string reason)
        {
            SurveillanceBootstrap.Log.LogWarning(
                $"[LethalCCTV] PROMOTED_PICK_UNMOUNTED reason={reason} promotion={pick.Reason} tile=idx{pick.SrcIndex} name={pick.Tile?.name}");
        }

        private static bool IsPromotedPick(CameraPickReason reason)
        {
            return reason == CameraPickReason.EntrancePromotion
                || reason == CameraPickReason.ApparatusPromotion
                || reason == CameraPickReason.MainframePromotion
                || reason == CameraPickReason.ObjectivePromotion
                || reason == CameraPickReason.FireExitPromotion;
        }

        private static bool TryFindSrcIndex(IReadOnlyList<Tile> allTiles, Tile tile, out int srcIndex)
        {
            srcIndex = -1;
            if (allTiles == null || tile == null) return false;
            for (int i = 0; i < allTiles.Count; i++)
            {
                if (ReferenceEquals(allTiles[i], tile))
                {
                    srcIndex = i;
                    return true;
                }
            }
            return false;
        }

        private bool TryResolveObjectivePromotionTarget(
            IReadOnlyList<Tile> allTiles,
            out Tile objectiveTile,
            out int objectiveSrcIndex,
            out ContractObjectiveProvider.MarkerSnapshot marker)
        {
            objectiveTile = null;
            objectiveSrcIndex = -1;
            marker = default;

            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> markers = ContractObjectiveProvider.GetMarkers();
            if (markers == null || markers.Count == 0)
                return false;

            for (int i = 0; i < markers.Count; i++)
            {
                ContractObjectiveProvider.MarkerSnapshot current = markers[i];
                if (current.IsComplete)
                    continue;

                if (!TryFindTileForMarker(allTiles, current.Position, out objectiveTile, out objectiveSrcIndex))
                    continue;

                marker = current;
                return true;
            }

            return false;
        }

        // #1283: the positions come from the SpawnSyncedObject markers
        // (CollectSpawnMarkerInputs); the LungProp itself is network-spawned after the
        // dungeon-finished event. A freshly spawned apparatus is never in the ship room or
        // the elevator, so every marker position is a facility apparatus.
        private static bool TryResolveApparatusPromotionTarget(
            IReadOnlyList<Tile> allTiles,
            IReadOnlyList<Vector3> apparatusPositions,
            out Tile apparatusTile,
            out int apparatusSrcIndex,
            out Vector3 apparatusPosition)
        {
            apparatusTile = null;
            apparatusSrcIndex = -1;
            apparatusPosition = Vector3.zero;
            if (allTiles == null || allTiles.Count == 0 || apparatusPositions == null)
                return false;

            for (int i = 0; i < apparatusPositions.Count; i++)
            {
                Vector3 position = apparatusPositions[i];
                if (!TryFindTileForMarker(allTiles, position, out Tile tile, out int srcIndex))
                    continue;
                Vector3 closest = tile.Bounds.ClosestPoint(position);
                if (Vector3.SqrMagnitude(closest - position) > 16f)
                    continue;

                bool better = apparatusTile == null || srcIndex < apparatusSrcIndex;
                if (!better && srcIndex == apparatusSrcIndex)
                {
                    int cmp = QuantizeDm(position.x).CompareTo(QuantizeDm(apparatusPosition.x));
                    if (cmp == 0) cmp = QuantizeDm(position.z).CompareTo(QuantizeDm(apparatusPosition.z));
                    if (cmp == 0) cmp = QuantizeDm(position.y).CompareTo(QuantizeDm(apparatusPosition.y));
                    better = cmp < 0;
                }
                if (!better)
                    continue;

                apparatusTile = tile;
                apparatusSrcIndex = srcIndex;
                apparatusPosition = position;
            }

            return apparatusTile != null;
        }

        private static bool TryFindTileForMarker(IReadOnlyList<Tile> allTiles, Vector3 markerPosition, out Tile tile, out int srcIndex)
        {
            tile = null;
            srcIndex = -1;
            if (allTiles == null || allTiles.Count == 0)
                return false;

            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile candidate = allTiles[i];
                if (candidate == null)
                    continue;
                if (!candidate.Bounds.Contains(markerPosition))
                    continue;

                tile = candidate;
                srcIndex = i;
                return true;
            }

            float bestDistance = float.MaxValue;
            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile candidate = allTiles[i];
                if (candidate == null)
                    continue;

                Vector3 closest = candidate.Bounds.ClosestPoint(markerPosition);
                float distance = Vector3.SqrMagnitude(closest - markerPosition);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                tile = candidate;
                srcIndex = i;
            }

            return tile != null;
        }

    }
}
