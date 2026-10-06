using System.Collections.Generic;
using System.Text;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras.Probe
{
    // Phase 2.0 — Task 1 probe. Pure read; does NOT alter placement.
    //
    // Fires post-SortPicksForInstantiation under the P20ProbeEnabled
    // config flag. For each picked tile, emits 8N candidate lines
    // (4 cornerId anchors × 2 tiers {ceil, wall} × N UsedDoorways), one
    // tile-header line, and one per-dungeon entrance-pick footer. A
    // fail-loud Room-layer assertion fires once at the start: if the
    // "Room" layer is missing OR populated with zero colliders, every
    // CAND line this roll emits lc=INVALID instead of clear/blocked —
    // the bake/faithfulness verdict at Gate 1.3 must not be reasoned off
    // an empty-layer "all-clear" false negative.
    //
    // Per the Gate-1 sign-off:
    //  - 4×2×N full set, NOT just opposite-extent (Task 1 feeds the
    //    scorer's entire input space).
    //  - dFloorY = min(dLocalY) across this tile's UsedDoorways (lowest
    //    doorway = entry-floor candidate). Emitted on the TILE header;
    //    per-doorway dLocalY stays on the CAND line so multi-storey
    //    splits are visible.
    //  - dir_fwd / dir_negfwd / dir_centroid logged side-by-side, XZ
    //    only — Gate 1 picks the inward sign convention from agreement
    //    on a NON-CORRIDOR tile (dir_centroid degenerates on corridors;
    //    qualifier carried into Gate 1 review).
    //  - g13 / g13world always emit `na` here. The faithfulness
    //    check is a separate Task-4-prep pass that needs the real
    //    scorer's pick to be meaningful.
    //  - StartRoom logged BOTH ways: arch_pd0mp = PathDepth==0 &&
    //    IsOnMainPath; arch_name = tile.name contains "StartRoom". Gate 1
    //    decides the canonical derivation from cross-roll agreement.
    //  - Entrance footer applies the unified S4 rule (main-path ∩ prefer
    //    PathDepth==0, else max UsedDoorways.Count, tiebreak min SrcIndex)
    //    and emits ONE line per dungeon.
    internal static class PlacementProbe
    {
        // Vanilla Lethal Company's interior collision layer. If a modded
        // moon renames it, the start-of-probe assertion logs ERROR and
        // every CAND emits lc=INVALID — a misnamed layer reads "all
        // sightlines clear" (nothing to hit), which is the single most
        // dangerous failure mode this probe has.
        private const string ROOM_LAYER_NAME = "Room";

        // Maximum ceil-tier cast length, clamped to keep a degenerately
        // huge tile (mineshaft tunnel slipping past the filter) from
        // firing a 200m raycast. AABB diagonals on real interior rooms
        // top out well under this.
        private const float MAX_CEIL_SIGHTLINE_M = 50f;

        // Multi-storey indicator threshold on the TILE header. If
        // max(dLocalY) - dFloorY > this, the tile is flagged
        // multistorey=YES so §3's "wrong floor of shaft" finding is one
        // read away.
        private const float MULTI_STOREY_THRESHOLD_M = 1.0f;

        // Ceil-tier downward pitch, matched to the fixed 15° ceiling pitch
        // of the Phase 1.9 corner placement (removed in #1313). Kept so the
        // probe's candidate set stays comparable with earlier probe logs.
        private const float CEIL_PITCH_DOWN_DEG = 15.0f;

        internal static void Run(
            IReadOnlyList<CameraPick> orderedPicks,
            in PlacementParams placementParams)
        {
            if (orderedPicks == null || orderedPicks.Count == 0)
            {
                SurveillanceBootstrap.Log.LogInfo("[P20] (no picked tiles — probe skipped)");
                return;
            }

            // --- Line 0: fail-loud Room-layer assertion --------------
            int roomLayerIndex = LayerMask.NameToLayer(ROOM_LAYER_NAME);
            int colliderCount = 0;
            if (roomLayerIndex >= 0)
            {
                Collider[] all = Object.FindObjectsByType<Collider>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].gameObject.layer == roomLayerIndex)
                        colliderCount++;
                }
            }
            bool layerValid = roomLayerIndex >= 0 && colliderCount > 0;
            int roomLayerMask = roomLayerIndex >= 0 ? (1 << roomLayerIndex) : 0;
            string layerStatus = layerValid ? "OK" : "EMPTY_INVALID";
            if (layerValid)
            {
                SurveillanceBootstrap.Log.LogInfo(
                    $"[P20] ROOM_LAYER mask={ROOM_LAYER_NAME} layerIdx={roomLayerIndex} colliders={colliderCount} status={layerStatus}");
            }
            else
            {
                SurveillanceBootstrap.Log.LogError(
                    $"[P20] ROOM_LAYER mask={ROOM_LAYER_NAME} layerIdx={roomLayerIndex} colliders={colliderCount} status={layerStatus} — every CAND lc field this roll emits INVALID; do NOT reason about sightlines off this probe.");
            }

            // --- Per-tile + per-candidate lines ----------------------
            for (int p = 0; p < orderedPicks.Count; p++)
            {
                CameraPick pick = orderedPicks[p];
                Tile t = pick.Tile;
                if (t == null || t.Placement == null) continue;

                EmitTileBlock(t, pick.SrcIndex, in placementParams, layerValid, roomLayerMask);
            }

            // --- Per-dungeon entrance footer -------------------------
            EntrancePick entrance = PickEntrance(orderedPicks);
            string entranceDoorStr = entrance.EntranceDoorIndex >= 0 ? entrance.EntranceDoorIndex.ToString() : "na";
            int entranceSrc = entrance.Tile != null ? entrance.SrcIndex : -1;
            SurveillanceBootstrap.Log.LogInfo(
                $"[P20] ENTRANCE idx={entranceSrc} picked_by={entrance.PickedBy} entranceDoor={entranceDoorStr}");
        }

        private static void EmitTileBlock(
            Tile t,
            int srcIndex,
            in PlacementParams placementParams,
            bool layerValid,
            int roomLayerMask)
        {
            Bounds localAabb = DungeonCameraSpawner.ComputeWorldAabbLocal(t);
            bool degenerate = localAabb.size.sqrMagnitude < 1e-6f;

            Matrix4x4 tileWorldToLocal = Matrix4x4.TRS(
                t.Placement.Position, t.Placement.Rotation, Vector3.one).inverse;

            // Per-doorway tile-local Ys (and directions) — computed once
            // per tile, reused across the 8 anchor×tier combinations.
            List<DoorwayLocal> doorways = new List<DoorwayLocal>();
            float dFloorY = float.PositiveInfinity;
            float maxDLocalY = float.NegativeInfinity;
            if (t.UsedDoorways != null)
            {
                for (int k = 0; k < t.UsedDoorways.Count; k++)
                {
                    var dw = t.UsedDoorways[k];
                    if (dw == null) continue;
                    Vector3 dwLocalPos = tileWorldToLocal.MultiplyPoint3x4(dw.transform.position);
                    Vector3 dwLocalFwd = tileWorldToLocal.MultiplyVector(dw.transform.forward);
                    doorways.Add(new DoorwayLocal(k, dwLocalPos, dwLocalFwd));
                    if (dwLocalPos.y < dFloorY) dFloorY = dwLocalPos.y;
                    if (dwLocalPos.y > maxDLocalY) maxDLocalY = dwLocalPos.y;
                }
            }
            bool noDoorways = doorways.Count == 0;
            if (noDoorways) { dFloorY = float.NaN; maxDLocalY = float.NaN; }
            bool multiStorey = !noDoorways && (maxDLocalY - dFloorY) > MULTI_STOREY_THRESHOLD_M;

            // TILE header.
            int pathDepth = t.Placement.PathDepth;
            bool isOnMainPath = t.Placement.IsOnMainPath;
            int degree = t.UsedDoorways != null ? t.UsedDoorways.Count : 0;
            string tileName = t.name ?? "<unnamed>";
            bool archPd0Mp = isOnMainPath && pathDepth == 0;
            // P2.0 Gate 1 revision (§7) — arch_name was a single hard-coded
            // "StartRoom" check, which missed Facility Entrance(Clone) /
            // GrayEntryRoom(Clone) on facility/manor rolls. Token set per
            // revised §2: { StartRoom, Entrance, EntryRoom }. arch_name_token
            // logs WHICH token matched so the next roll catches a spurious
            // hit on something like "ReEntrancePipe" (sub-string false-
            // positive — LC tile names are clean in practice, but the
            // visibility is cheap insurance).
            bool archName = MatchesEntranceToken(tileName, out string archNameToken);
            float worldYRot = t.Placement.Rotation.eulerAngles.y;

            SurveillanceBootstrap.Log.LogInfo(
                $"[P20] TILE idx={srcIndex} name={tileName} " +
                $"arch_pd0mp={archPd0Mp} arch_name={archName} arch_name_token={archNameToken} " +
                $"mainpath={isOnMainPath} " +
                $"pathdepth={pathDepth} degree={degree} rot={worldYRot:F1} " +
                $"aabbLocal=(c=({localAabb.center.x:F2},{localAabb.center.y:F2},{localAabb.center.z:F2});" +
                $"h=({localAabb.extents.x:F2},{localAabb.extents.y:F2},{localAabb.extents.z:F2})) " +
                $"dFloorY={Fmt(dFloorY)} maxDLocalY={Fmt(maxDLocalY)} multistorey={(multiStorey ? "YES" : "no")}" +
                (degenerate ? " DEGENERATE_AABB" : string.Empty) +
                (noDoorways ? " NO_USED_DOORWAYS" : string.Empty));

            if (degenerate || noDoorways)
            {
                // Nothing meaningful to emit at the candidate level — no
                // box to anchor in or no doorway to aim at. The TILE
                // header has already flagged the cause.
                return;
            }

            // CAND lines: 4 corners × 2 tiers × N doorways.
            Vector3 center = localAabb.center;
            Vector3 ext = localAabb.extents;
            float hFrac = placementParams.HorizontalFraction;
            float vFrac = placementParams.HeightFraction;
            float wallH = placementParams.WallMountHeightM;
            float wallMinRoomY = placementParams.WallMountMinRoomHeightM; // unused in candidate enum — kept for log narrative

            for (int cornerId = 0; cornerId < 4; cornerId++)
            {
                float signX = (cornerId & 1) == 0 ? -1f : 1f;
                float signZ = (cornerId & 2) == 0 ? -1f : 1f;
                float anchorX = center.x + signX * hFrac * ext.x;
                float anchorZ = center.z + signZ * hFrac * ext.z;

                for (int tier = 0; tier < 2; tier++)
                {
                    string tierName = tier == 0 ? "ceil" : "wall";
                    // Ceil tier: anchor near AABB top per the Phase 1.9 corner placement
                    //   localY = min.y + HeightFraction * size.y.
                    // Wall tier: anchor at the P2.0-corrected entry floor
                    //   localY = dFloorY + WallMountHeightM (NOT min.y +
                    //   WallMountHeightM — that's the P1.9b bug this whole
                    //   workstream exists to eliminate). Logging at the
                    //   corrected reference is what makes the probe's CAND
                    //   set the scorer's actual input space.
                    float anchorY = tier == 0
                        ? localAabb.min.y + vFrac * localAabb.size.y
                        : dFloorY + wallH;
                    Vector3 anchorLocal = new Vector3(anchorX, anchorY, anchorZ);

                    for (int d = 0; d < doorways.Count; d++)
                    {
                        DoorwayLocal dw = doorways[d];

                        // Three direction hypotheses (XZ-only, normalized).
                        // Sign convention isn't picked in code — Gate 1
                        // settles it from which one consistently points
                        // INTO the room on non-corridor tiles.
                        Vector3 dirFwd = FlattenXZ(dw.Fwd);
                        Vector3 dirNegFwd = -dirFwd;
                        Vector3 toCenter = new Vector3(center.x - dw.Pos.x, 0f, center.z - dw.Pos.z);
                        Vector3 dirCentroid = FlattenXZ(toCenter);

                        // Aim formulation per tier.
                        // Ceil tier matches the Phase 1.9 corner placement's
                        // ceiling math: yaw toward centroid, fixed 15° pitch
                        // down. We project the ray that far along the floor
                        // with a 15° drop toward the AABB diagonal range
                        // (clamped). Wall tier matches its wall math: aim at
                        // (centroid.x, anchor.y, centroid.z) — same Y by
                        // construction, pitch is 0.
                        Vector3 aimTargetLocal;
                        float pitchDeg;
                        float sightLen;
                        if (tier == 0)
                        {
                            Vector3 yawDir = new Vector3(center.x - anchorX, 0f, center.z - anchorZ);
                            float horizDist = yawDir.magnitude;
                            if (horizDist < 1e-4f) yawDir = Vector3.forward;
                            else yawDir /= horizDist;
                            // Direction vector with 15° downward pitch.
                            float cosP = Mathf.Cos(CEIL_PITCH_DOWN_DEG * Mathf.Deg2Rad);
                            float sinP = Mathf.Sin(CEIL_PITCH_DOWN_DEG * Mathf.Deg2Rad);
                            Vector3 rayDir = new Vector3(yawDir.x * cosP, -sinP, yawDir.z * cosP);
                            // Use the smaller of MAX_CEIL_SIGHTLINE and the
                            // AABB diagonal — keeps the cast bounded but
                            // long enough to clear the room interior.
                            float diag = localAabb.size.magnitude;
                            sightLen = Mathf.Min(MAX_CEIL_SIGHTLINE_M, diag);
                            aimTargetLocal = anchorLocal + rayDir * sightLen;
                            pitchDeg = CEIL_PITCH_DOWN_DEG;
                        }
                        else
                        {
                            aimTargetLocal = new Vector3(center.x, anchorY, center.z);
                            Vector3 delta = aimTargetLocal - anchorLocal;
                            sightLen = delta.magnitude;
                            pitchDeg = 0f;
                        }

                        // Convert to world for the linecast.
                        Vector3 anchorWorld = t.Placement.Position + t.Placement.Rotation * anchorLocal;
                        Vector3 aimWorld = t.Placement.Position + t.Placement.Rotation * aimTargetLocal;

                        string lc;
                        string lcHit;
                        string lcDist;
                        if (!layerValid)
                        {
                            lc = "INVALID";
                            lcHit = "na";
                            lcDist = "na";
                        }
                        else
                        {
                            bool blocked = Physics.Linecast(
                                anchorWorld, aimWorld, out RaycastHit hit,
                                roomLayerMask, QueryTriggerInteraction.Ignore);
                            if (blocked)
                            {
                                lc = "blocked";
                                lcHit = hit.collider != null ? hit.collider.name : "none";
                                lcDist = hit.distance.ToString("F2");
                            }
                            else
                            {
                                lc = "clear";
                                lcHit = "none";
                                lcDist = sightLen.ToString("F2");
                            }
                        }

                        SurveillanceBootstrap.Log.LogInfo(
                            $"[P20] CAND idx={srcIndex} door={dw.DoorIdx} tier={tierName} corner={cornerId} " +
                            $"anchor=({anchorLocal.x:F2},{anchorLocal.y:F2},{anchorLocal.z:F2}) " +
                            $"dLocalY={dw.Pos.y:F2} " +
                            $"dir_fwd=({dirFwd.x:F2},{dirFwd.z:F2}) " +
                            $"dir_negfwd=({dirNegFwd.x:F2},{dirNegFwd.z:F2}) " +
                            $"dir_centroid=({dirCentroid.x:F2},{dirCentroid.z:F2}) " +
                            $"aimTarget=({aimTargetLocal.x:F2},{aimTargetLocal.y:F2},{aimTargetLocal.z:F2}) " +
                            $"pitchDeg={pitchDeg:F2} sightLen={sightLen:F2} " +
                            $"lc={lc} lcHit={lcHit} lcDist={lcDist} " +
                            $"g13=na g13world=na");
                    }
                }
            }
        }

        // Unified S4 rule. Inputs are exactly the columns the TILE header
        // logs — keep the rule a pure function of (IsOnMainPath, PathDepth,
        // UsedDoorways.Count, SrcIndex) so Gate 1 can verify the picker's
        // output against the printed columns without re-deriving anything.
        //   1. main-path tiles only.
        //   2. prefer PathDepth==0; if empty, fall back to max
        //      UsedDoorways.Count on the main path.
        //   3. final tiebreak min SrcIndex (deterministic).
        // Returns Tile=null with PickedBy="none" if no main-path tile
        // exists in the picked set (degenerate; would imply a no-cap roll
        // with selection picking only side-paths — vanishingly rare).
        private static EntrancePick PickEntrance(IReadOnlyList<CameraPick> picks)
        {
            // Stage 1: main-path subset.
            int bestPd0Src = int.MaxValue;
            Tile bestPd0 = null;
            int bestDegree = -1;
            int bestDegreeSrc = int.MaxValue;
            Tile bestDegreeTile = null;
            bool sawMainPath = false;

            for (int i = 0; i < picks.Count; i++)
            {
                CameraPick pk = picks[i];
                if (pk.Tile == null || pk.Tile.Placement == null) continue;
                if (!pk.Tile.Placement.IsOnMainPath) continue;
                sawMainPath = true;
                int degree = pk.Tile.UsedDoorways != null ? pk.Tile.UsedDoorways.Count : 0;
                int src = pk.SrcIndex;

                if (pk.Tile.Placement.PathDepth == 0)
                {
                    if (src < bestPd0Src) { bestPd0Src = src; bestPd0 = pk.Tile; }
                }

                // Track the max-degree fallback in the same pass.
                if (degree > bestDegree || (degree == bestDegree && src < bestDegreeSrc))
                {
                    bestDegree = degree;
                    bestDegreeSrc = src;
                    bestDegreeTile = pk.Tile;
                }
            }

            Tile chosen;
            string pickedBy;
            int chosenSrc;
            if (bestPd0 != null)
            {
                chosen = bestPd0; chosenSrc = bestPd0Src; pickedBy = "pd0mp";
            }
            else if (sawMainPath && bestDegreeTile != null)
            {
                chosen = bestDegreeTile; chosenSrc = bestDegreeSrc; pickedBy = "maxdegree";
            }
            else
            {
                return new EntrancePick(null, -1, "none", -1);
            }

            // Entrance door selection on the chosen tile. Per §2 the
            // entrance picker also returns the doorway the entrance room
            // connects to its main-path origin through — for Task 1 the
            // honest answer is "the doorway connecting to the lower
            // PathDepth peer." We don't have peer PathDepth on Doorway
            // directly, so the probe-time heuristic emits the index of
            // the doorway with the lowest tile-local Y (entry floor)
            // among UsedDoorways with a non-null ConnectedDoorway. This
            // is a Task-1 placeholder; §2 implementation will replace
            // with the structural "connects to lower PathDepth" rule
            // once Doorway connectivity is exercised in Task 2.
            int entranceDoor = -1;
            if (chosen.UsedDoorways != null)
            {
                Matrix4x4 chosenW2L = Matrix4x4.TRS(
                    chosen.Placement.Position, chosen.Placement.Rotation, Vector3.one).inverse;
                float bestY = float.PositiveInfinity;
                for (int k = 0; k < chosen.UsedDoorways.Count; k++)
                {
                    var dw = chosen.UsedDoorways[k];
                    if (dw == null || dw.ConnectedDoorway == null) continue;
                    Vector3 ly = chosenW2L.MultiplyPoint3x4(dw.transform.position);
                    if (ly.y < bestY) { bestY = ly.y; entranceDoor = k; }
                }
            }

            return new EntrancePick(chosen, chosenSrc, pickedBy, entranceDoor);
        }

        // Entrance-archetype token set per revised §2. Case-insensitive
        // Contains is intentionally simple: LC tile names are clean
        // (Facility Entrance(Clone), GrayEntryRoom(Clone), etc.). A
        // false-positive like "ReEntrancePipe" would still register a
        // hit, but arch_name_token surfaces which token matched so the
        // next roll catches it before the entrance picker uses it.
        private static readonly string[] s_entranceTokens = { "StartRoom", "Entrance", "EntryRoom" };

        private static bool MatchesEntranceToken(string tileName, out string token)
        {
            token = "none";
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

        private static Vector3 FlattenXZ(Vector3 v)
        {
            Vector3 flat = new Vector3(v.x, 0f, v.z);
            float m = flat.magnitude;
            if (m < 1e-6f) return Vector3.zero;
            return flat / m;
        }

        private static string Fmt(float f)
        {
            return float.IsNaN(f) ? "na" : f.ToString("F2");
        }

        private readonly struct DoorwayLocal
        {
            public readonly int DoorIdx;
            public readonly Vector3 Pos;
            public readonly Vector3 Fwd;
            public DoorwayLocal(int idx, Vector3 pos, Vector3 fwd)
            {
                DoorIdx = idx; Pos = pos; Fwd = fwd;
            }
        }

        private readonly struct EntrancePick
        {
            public readonly Tile Tile;
            public readonly int SrcIndex;
            public readonly string PickedBy;
            public readonly int EntranceDoorIndex;
            public EntrancePick(Tile tile, int srcIndex, string pickedBy, int entranceDoorIndex)
            {
                Tile = tile; SrcIndex = srcIndex; PickedBy = pickedBy;
                EntranceDoorIndex = entranceDoorIndex;
            }
        }
    }
}
