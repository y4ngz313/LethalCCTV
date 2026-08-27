using System.Collections.Generic;
using DunGen;
using UnityEngine;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // Phase 2.0 Task 2 — entrance-room identifier.
    //
    // PURE function over allTiles (NOT orderedPicks). Returns the tile
    // we want surveilled as the entrance + the doorway that connects
    // outward. The post-cap promotion layer (DungeonCameraSpawner) is
    // what reconciles this with the budget cap — EntranceRoom has no
    // knowledge of cap or orderedPicks, so the identifier and the
    // eviction logic stay cleanly separated.
    //
    // Rule per revised §2 (Gate 1 disproved the graph signals on two
    // rolls; both rolls had PathDepth >= 1 for every tile, so PathDepth==0
    // never fires; max-degree picks junction closets, not entrance rooms):
    //   1. Main-path tiles with non-degenerate worldAABB only.
    //   2. Prefer name match against entrance-archetype token set
    //      { StartRoom, Entrance, EntryRoom } (case-insensitive Contains).
    //      Multi-match tiebreak: max floorArea (the watchable room beats
    //      the airlock stub), then min SrcIndex.
    //   3. Else PathDepth ascending.
    //   4. Else max UsedDoorways.Count, tiebreak min SrcIndex.
    //
    // Entrance doorway (returned alongside the tile): on the chosen
    // tile, the doorway with the LOWEST tile-local Y among UsedDoorways
    // with a non-null ConnectedDoorway. This is the §2-handoff
    // placeholder — "lowest connected doorway = entry floor" — kept
    // here because the structural "doorway whose peer has lower
    // PathDepth" rule needs Doorway-peer-Tile mapping that DunGen
    // doesn't expose cleanly. Adequate for surveillance framing
    // (the camera looks toward the doorway, doesn't navigate through it).
    internal static class EntranceRoom
    {
        // Mirror of PlacementProbe's token set, kept here so EntranceRoom
        // has no compile dependency on the probe. Drift risk is low: both
        // are short, well-commented, and any change goes through the
        // revised §2 review anyway.
        private static readonly string[] s_entranceTokens = { "StartRoom", "Entrance", "EntryRoom" };

        internal readonly struct Result
        {
            public readonly Tile Tile;
            public readonly int SrcIndex;
            public readonly string PickedBy;       // "name" | "pathdepth" | "maxdegree" | "none"
            public readonly string MatchedToken;   // token that hit on the "name" branch, else "none"
            public readonly int EntranceDoorIdx;   // index into UsedDoorways, -1 if no usable doorway

            public Result(Tile tile, int srcIndex, string pickedBy, string matchedToken, int entranceDoorIdx)
            {
                Tile = tile;
                SrcIndex = srcIndex;
                PickedBy = pickedBy;
                MatchedToken = matchedToken;
                EntranceDoorIdx = entranceDoorIdx;
            }

            public bool Found => Tile != null;
        }

        // allTiles is dungeon.AllTiles — the order is SrcIndex by
        // construction. We track SrcIndex through enumerator i instead
        // of recomputing, which keeps tiebreaks deterministic without
        // calling into RoomComponent's separate index list.
        internal static Result Pick(IReadOnlyList<Tile> allTiles)
        {
            if (allTiles == null || allTiles.Count == 0)
                return new Result(null, -1, "none", "none", -1);

            // Stage 1 — name-token match, with max-floorArea / min-SrcIndex
            // tiebreak. Tracked alongside Stages 2/3 in the same pass to
            // avoid three-pass scanning.
            Tile bestNameTile = null;
            int bestNameSrc = int.MaxValue;
            float bestNameArea = float.NegativeInfinity;
            string bestNameToken = "none";

            // Stage 2 — lowest PathDepth on the main path.
            Tile bestPdTile = null;
            int bestPdSrc = int.MaxValue;
            int bestPd = int.MaxValue;

            // Stage 3 — max degree on the main path, min-SrcIndex tiebreak.
            Tile bestDegTile = null;
            int bestDegSrc = int.MaxValue;
            int bestDeg = -1;

            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile t = allTiles[i];
                if (t == null || t.Placement == null) continue;
                if (!t.Placement.IsOnMainPath) continue;

                // Non-degenerate worldAABB guard. Skips the airlock-stub
                // entrance pad (FacilitySilly idx 0 / GrayEntrance) — these
                // are degenerate-worldAABB tiles per the P1.9b probe, and
                // they are NOT the watchable entrance room.
                Bounds aabb = DungeonCameraSpawner.ComputeWorldAabbLocal(t);
                if (aabb.size.sqrMagnitude < 1e-6f) continue;

                int srcIdx = i;
                int pathDepth = t.Placement.PathDepth;
                int degree = t.UsedDoorways != null ? t.UsedDoorways.Count : 0;

                // Stage 1 candidate. The token set is small; in practice
                // a tile name matches zero or one token, so the FIRST
                // hit is the only one (no inner tiebreak across tokens
                // for the same tile).
                string matchedToken = null;
                if (TryMatchToken(t.name, out string tok))
                {
                    matchedToken = tok;
                    // floor area = aabb size.x * size.z (a reasonable proxy
                    // independent of selection's FootprintM2 calculation).
                    float area = aabb.size.x * aabb.size.z;
                    bool better;
                    if (bestNameTile == null) better = true;
                    else if (area > bestNameArea) better = true;
                    else if (area < bestNameArea) better = false;
                    else better = srcIdx < bestNameSrc;
                    if (better)
                    {
                        bestNameTile = t;
                        bestNameSrc = srcIdx;
                        bestNameArea = area;
                        bestNameToken = matchedToken;
                    }
                }

                // Stage 2 candidate (only used if Stage 1 finds nothing).
                if (pathDepth < bestPd || (pathDepth == bestPd && srcIdx < bestPdSrc))
                {
                    bestPd = pathDepth;
                    bestPdSrc = srcIdx;
                    bestPdTile = t;
                }

                // Stage 3 candidate (only used if Stages 1 and 2 are both empty).
                if (degree > bestDeg || (degree == bestDeg && srcIdx < bestDegSrc))
                {
                    bestDeg = degree;
                    bestDegSrc = srcIdx;
                    bestDegTile = t;
                }
            }

            Tile chosen = null;
            int chosenSrc = -1;
            string pickedBy = "none";
            string chosenToken = "none";

            if (bestNameTile != null)
            {
                chosen = bestNameTile;
                chosenSrc = bestNameSrc;
                pickedBy = "name";
                chosenToken = bestNameToken;
            }
            else if (bestPdTile != null)
            {
                chosen = bestPdTile;
                chosenSrc = bestPdSrc;
                pickedBy = "pathdepth";
            }
            else if (bestDegTile != null)
            {
                chosen = bestDegTile;
                chosenSrc = bestDegSrc;
                pickedBy = "maxdegree";
            }
            else
            {
                return new Result(null, -1, "none", "none", -1);
            }

            int entranceDoor = PickEntranceDoor(chosen);
            return new Result(chosen, chosenSrc, pickedBy, chosenToken, entranceDoor);
        }

        // Lowest tile-local Y among connected UsedDoorways. "Lowest" =
        // entry floor (consistent with PlacementProbe's dFloorY derivation).
        // Placement-matrix inverse, NOT tile.transform.worldToLocalMatrix
        // (per the P1.9 Factory finding).
        private static int PickEntranceDoor(Tile chosen)
        {
            if (chosen?.UsedDoorways == null || chosen.Placement == null) return -1;

            Matrix4x4 worldToLocal = Matrix4x4.TRS(
                chosen.Placement.Position, chosen.Placement.Rotation, Vector3.one).inverse;

            int best = -1;
            float bestY = float.PositiveInfinity;
            for (int k = 0; k < chosen.UsedDoorways.Count; k++)
            {
                var dw = chosen.UsedDoorways[k];
                if (dw == null || dw.ConnectedDoorway == null) continue;
                Vector3 local = worldToLocal.MultiplyPoint3x4(dw.transform.position);
                if (local.y < bestY)
                {
                    bestY = local.y;
                    best = k;
                }
            }
            return best;
        }

        private static bool TryMatchToken(string tileName, out string token)
        {
            token = null;
            if (string.IsNullOrEmpty(tileName)) return false;
            for (int i = 0; i < s_entranceTokens.Length; i++)
            {
                if (tileName.IndexOf(s_entranceTokens[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    token = s_entranceTokens[i];
                    return true;
                }
            }
            return false;
        }
    }
}
