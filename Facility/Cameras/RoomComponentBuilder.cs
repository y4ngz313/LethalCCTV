using System.Collections.Generic;
using DunGen;

namespace Y4NGZCompany.Facility.Cameras
{
    // Phase 1.8 Placement T2 — room-only subgraph builder.
    //
    // The settled design (SPEC + PLAN, both signed off):
    //   Nodes = tiles classified ROOM by TileExclusionFilter.IsCorridor
    //           AND not otherwise excluded by ShouldInclude (mineshaft /
    //           tiny / name-pattern / entrance / fire-exit). Composing
    //           IsCorridor with ShouldInclude here keeps the placement
    //           subgraph in lockstep with the Phase 1 filter walk —
    //           anything ShouldInclude drops is not a placement node.
    //   Edges = direct room↔room only. For room A, each dw in
    //           A.UsedDoorways: if dw.ConnectedDoorway.Tile is in the
    //           eligible (room) set, edge A↔B. Corridors are dividers,
    //           not edges — because the eligible set excludes corridors,
    //           a doorway leading into a corridor naturally produces
    //           no edge and the corridor severs the graph. The hangar's
    //           directly-touching room cells form one component; manor
    //           rooms separated by hallway corridors stay separate.
    //
    // T2 has no callers — T5 will invoke Build from the spawner. Compiles
    // and is wired through the config knobs from T1 but does not run on
    // any normal dungeon load. The associated types (RoomComponent,
    // CameraPick, CameraPickReason) are co-located in this file because
    // they're tightly coupled to the builder + consumed only by T3/T4.

    internal sealed class RoomComponent
    {
        public List<Tile> Tiles;
        public List<int> Degrees;     // parallel to Tiles; each entry is the
                                      // full UsedDoorways.Count of that tile,
                                      // NOT the in-component peer count.
        public List<int> SrcIndices;  // parallel to Tiles; the tile's index in
                                      // dungeon.AllTiles. CRITICAL: this is the
                                      // host/client-deterministic key (DunGen
                                      // generates AllTiles in seed-deterministic
                                      // order, so the same physical tile has the
                                      // same SrcIndex on every client). Used
                                      // for stable iteration within a component
                                      // AND as the final tiebreaker for the
                                      // budget capper's truncation sort.
                                      // Tile.GetInstanceID() is NOT a substitute:
                                      // it's assigned at local Instantiate time
                                      // and differs between clients.
        public int RootSrcIndex;
        public float FootprintM2;
    }

    internal enum CameraPickReason
    {
        SingleTileComponent,
        Junction,
        // D4 supersedes D1's "LargeLeaf". This pass now covers any non-
        // junction room (subgraph degree <= 2) above the area threshold,
        // not just degree-1 leaves — runtime exposed large degree-2
        // rooms falling through every branch. See CameraSelector for
        // the rule.
        LargeUncovered,
        LinearChainEndpoint,
        RingFallbackLargestTile,
        Isolated,
        // P2.0 §6 — entrance tile promoted post-cap when EntranceRoom
        // identifies a tile that didn't survive the selector + cap
        // pipeline (this roll's Facility Entrance is the canonical case).
        // Surfaced as its own reason so the promotion is auditable in
        // the placement diagnostic — a tile with this reason was NOT
        // chosen by the selector, it was inserted by the additive layer.
        EntrancePromotion,
        // Post-cap apparatus-room coverage. The vanilla LungProp is resolved from
        // the generated scene and mapped back to its DunGen tile; the same target is
        // also carried into semantic aiming so the promoted camera watches it.
        ApparatusPromotion,
        // Post-cap mainframe-room coverage guarantee. The mainframe is an
        // authored injected tile (MainframeAuthoredTileMarker); the CCTV wall
        // is pointless without eyes on it, so it gets its own promotion pass
        // ranked just below the entrance.
        MainframePromotion,
        // Post-cap guaranteed contract coverage. A tile with this reason was
        // inserted specifically so at least one camera lands in the same room
        // as the current active contract objective marker.
        ObjectivePromotion,
        // Post-cap fire-exit coverage guarantee. Fire-exit tiles are excluded
        // by TileExclusionFilter, so without this pass no camera can ever land
        // on one; the promotion inserts the lowest-srcIndex fire-exit tile when
        // none of the capped picks already cover a fire exit.
        FireExitPromotion,
        // Sparse-interior fallback. When the room-only selection yields too
        // few interior cameras to keep the wall useful, the spawner can
        // deterministically top the set up from tiles excluded solely by the
        // corridor rule. This preserves the room-first behavior on normal
        // interiors while avoiding near-empty CCTV pages on corridor-heavy
        // modded layouts.
        CorridorFallback,
    }

    internal readonly struct CameraPick
    {
        public readonly Tile Tile;
        public readonly RoomComponent Component;
        public readonly CameraPickReason Reason;
        public readonly int Degree;
        public readonly float FootprintM2;
        public readonly bool IsOnMainPath;
        public readonly int SrcIndex;

        public CameraPick(
            Tile tile, RoomComponent component, CameraPickReason reason,
            int degree, float footprintM2, bool isOnMainPath, int srcIndex)
        {
            Tile = tile;
            Component = component;
            Reason = reason;
            Degree = degree;
            FootprintM2 = footprintM2;
            IsOnMainPath = isOnMainPath;
            SrcIndex = srcIndex;
        }
    }

    internal static class RoomComponentBuilder
    {
        internal static List<RoomComponent> Build(
            IReadOnlyList<Tile> allTiles,
            TileExclusionFilter filter,
            ISet<Tile> entranceTiles,
            ISet<Tile> fireExitTiles)
        {
            var components = new List<RoomComponent>();
            if (allTiles == null || allTiles.Count == 0 || filter == null) return components;

            int n = allTiles.Count;

            // Eligibility pass. eligibleIndex[i] is the srcIndex of tile i
            // if eligible; -1 otherwise. tileToSrcIndex maps Tile back to
            // its srcIndex for the adjacency lookup (peer tile → eligible?).
            // We DO NOT use a plain HashSet<Tile> alone; we want both
            // membership AND ordered iteration by srcIndex for deterministic
            // BFS-root selection.
            var eligibleSrcIndices = new List<int>(n);
            var tileToSrcIndex = new Dictionary<Tile, int>(n);
            for (int i = 0; i < n; i++)
            {
                Tile t = allTiles[i];
                if (t == null) continue;
                if (!filter.ShouldInclude(t, out _, entranceTiles, fireExitTiles)) continue;
                if (filter.IsCorridor(t)) continue;
                eligibleSrcIndices.Add(i);
                tileToSrcIndex[t] = i;
            }
            if (eligibleSrcIndices.Count == 0) return components;

            // Adjacency pass. adjacency[srcIndex] = list of peer srcIndices
            // reachable via a direct room↔room doorway. We key by srcIndex
            // (not Tile) so the BFS works on stable integer keys and the
            // iteration order inside the per-tile adjacency list is fixed
            // by srcIndex ascending — important for determinism if a tile
            // has multiple eligible peers.
            var adjacency = new Dictionary<int, List<int>>(eligibleSrcIndices.Count);
            for (int k = 0; k < eligibleSrcIndices.Count; k++)
            {
                int srcIdx = eligibleSrcIndices[k];
                Tile a = allTiles[srcIdx];
                var aDoorways = a.UsedDoorways;
                // Null UsedDoorways: tile remains in the eligible set
                // (classifier biases such tiles toward ROOM) but has no
                // adjacency, so BFS leaves it as its own one-tile component.
                if (aDoorways == null) continue;

                List<int> nbrs = null;
                for (int d = 0; d < aDoorways.Count; d++)
                {
                    Doorway dw = aDoorways[d];
                    if (dw == null) continue;
                    Doorway peerDw = dw.ConnectedDoorway;
                    if (peerDw == null) continue;
                    Tile peerTile = peerDw.Tile;
                    if (peerTile == null) continue;
                    if (!tileToSrcIndex.TryGetValue(peerTile, out int peerSrcIdx)) continue;
                    if (peerSrcIdx == srcIdx) continue; // defensive: self-loop
                    if (nbrs == null) nbrs = new List<int>(4);
                    // De-dup: two doorways from A to the same B yield one edge.
                    if (!nbrs.Contains(peerSrcIdx)) nbrs.Add(peerSrcIdx);
                }
                if (nbrs != null)
                {
                    nbrs.Sort();
                    adjacency[srcIdx] = nbrs;
                }
            }

            // BFS pass. Outer loop iterates eligible tiles in srcIndex
            // ascending order. The first unvisited eligible tile in that
            // order is the BFS root; its srcIndex is the component's
            // RootSrcIndex (the spec's stable identity for determinism).
            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            for (int k = 0; k < eligibleSrcIndices.Count; k++)
            {
                int rootSrcIdx = eligibleSrcIndices[k];
                if (visited.Contains(rootSrcIdx)) continue;

                var compTiles = new List<Tile>();
                var compDegrees = new List<int>();
                var compSrcIndices = new List<int>();
                float footprint = 0f;

                queue.Clear();
                queue.Enqueue(rootSrcIdx);
                visited.Add(rootSrcIdx);

                while (queue.Count > 0)
                {
                    int srcIdx = queue.Dequeue();
                    Tile t = allTiles[srcIdx];
                    int deg = t.UsedDoorways != null ? t.UsedDoorways.Count : 0;
                    compTiles.Add(t);
                    compDegrees.Add(deg);
                    compSrcIndices.Add(srcIdx);
                    var size = t.Bounds.size;
                    footprint += size.x * size.z;

                    if (adjacency.TryGetValue(srcIdx, out List<int> nbrs))
                    {
                        for (int j = 0; j < nbrs.Count; j++)
                        {
                            int peerSrcIdx = nbrs[j];
                            if (visited.Add(peerSrcIdx))
                            {
                                queue.Enqueue(peerSrcIdx);
                            }
                        }
                    }
                }

                components.Add(new RoomComponent
                {
                    Tiles = compTiles,
                    Degrees = compDegrees,
                    SrcIndices = compSrcIndices,
                    RootSrcIndex = rootSrcIdx,
                    FootprintM2 = footprint,
                });
            }

            // Components already appear in RootSrcIndex ascending order
            // because the outer loop walked srcIndices ascending; no sort
            // needed.
            return components;
        }
    }
}
