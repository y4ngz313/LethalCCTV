using System.Collections.Generic;
using DunGen;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Surveillance;

namespace Y4NGZCompany.Facility.Cameras
{
    // Phase 1.8 Placement T3 — per-component camera selection.
    //
    // Consumes the components produced by RoomComponentBuilder (T2) and
    // emits a flat list of CameraPicks. Branches:
    //   single-tile component                  → 1 pick (Isolated if
    //                                            UsedDoorways was null at
    //                                            T2 time, else Single-
    //                                            TileComponent)
    //   multi-tile, ≥1 tile deg >= JunctionMinDegree
    //                                          → 1 pick per junction
    //   multi-tile, no junctions,
    //     LinearChainCoverage=DegreeOneEndpoints
    //     ≥1 tile deg == 1                     → 1 pick per degree-1 endpoint
    //   multi-tile, no junctions, no degree-1
    //     (a ring)                             → 1 pick at largest-footprint
    //                                            tile, tiebreak lowest
    //                                            srcIndex
    //   multi-tile, no junctions,
    //     LinearChainCoverage=LargestTileOnly  → same largest-tile rule
    //   unknown LinearChainCoverage string     → warn-once, treat as
    //                                            DegreeOneEndpoints
    //
    // T3 has no callers — T5 will invoke Select from the spawner. Iterating
    // component tiles in srcIndex ascending (which T2 already guarantees
    // by the BFS construction) is what makes the junction/endpoint paths
    // deterministic across host/client.

    internal static class CameraSelector
    {
        private const string LinearChainEndpoints = "DegreeOneEndpoints";
        private const string LinearChainLargest = "LargestTileOnly";

        private static bool _unknownLinearChainWarned;

        internal static List<CameraPick> Select(
            IReadOnlyList<RoomComponent> components,
            LethalCCTVConfig config)
        {
            var picks = new List<CameraPick>();
            if (components == null || components.Count == 0 || config == null) return picks;

            int junctionMinDeg = config.JunctionMinDegree.Value;
            string linearChainPolicy = ResolveLinearChainPolicy(config.LinearChainCoverage.Value);
            bool oneCameraPerTile = config.OneCameraPerTile.Value;

            for (int c = 0; c < components.Count; c++)
            {
                RoomComponent comp = components[c];
                if (comp == null || comp.Tiles == null || comp.Tiles.Count == 0) continue;

                // T2 BFS produces Tiles in BFS-visit order, which is not
                // necessarily SrcIndices ascending. Build a stable iteration
                // order here by sorting positions-in-component by their
                // SrcIndex (dungeon.AllTiles index — the host/client-
                // deterministic key). This makes the junction/endpoint
                // emission order, and the largest-tile tiebreaker, identical
                // on every client given the same seed.
                int count = comp.Tiles.Count;
                var orderedIndices = new int[count];
                for (int i = 0; i < count; i++) orderedIndices[i] = i;
                SortByTileSrcKey(orderedIndices, comp);

                if (oneCameraPerTile)
                {
                    // "1 camera per tile" mode — guarantees the CCTV
                    // operator has coverage of every room in the dungeon.
                    // This is the new default and replaces the strict
                    // junction-only selection, which was yielding 3-4
                    // cameras on Facility rolls. The surface-mount pass
                    // in the spawner still filters tiles that can't
                    // physically host a camera.
                    for (int i = 0; i < count; i++)
                    {
                        int idx = orderedIndices[i];
                        Tile t = comp.Tiles[idx];
                        if (t == null) continue;
                        picks.Add(MakePick(t, comp, CameraPickReason.SingleTileComponent, comp.Degrees[idx]));
                    }
                    continue;
                }

                if (count == 1)
                {
                    int only = orderedIndices[0];
                    Tile t = comp.Tiles[only];
                    int deg = comp.Degrees[only];
                    var reason = (t.UsedDoorways == null)
                        ? CameraPickReason.Isolated
                        : CameraPickReason.SingleTileComponent;
                    picks.Add(MakePick(t, comp, reason, deg));
                    continue;
                }

                // Multi-tile: count junctions in deterministic order.
                bool hasJunction = false;
                for (int i = 0; i < count; i++)
                {
                    if (comp.Degrees[orderedIndices[i]] >= junctionMinDeg) { hasJunction = true; break; }
                }
                if (hasJunction)
                {
                    // Junction picks. Track chosen junctions by srcIndex (the
                    // host/client-deterministic identity also used as the
                    // capper's final tiebreaker) so the leaf-pass de-dup
                    // guard below stays correct regardless of how these
                    // loops are later restructured. Using srcIndex rather
                    // than position-in-component removes a latent trap if
                    // the iteration arrays ever diverge.
                    var chosenJunctionSrcIndices = new HashSet<int>();
                    for (int i = 0; i < count; i++)
                    {
                        int idx = orderedIndices[i];
                        if (comp.Degrees[idx] >= junctionMinDeg)
                        {
                            picks.Add(MakePick(comp.Tiles[idx], comp, CameraPickReason.Junction, comp.Degrees[idx]));
                            chosenJunctionSrcIndices.Add(comp.SrcIndices[idx]);
                        }
                    }

                    // D1 + D4 — large uncovered-room coverage. After junction
                    // emit, additionally pick any non-junction room (subgraph
                    // degree <= 2: either a degree-1 leaf OR a degree-2
                    // through-room) whose footprint clears the threshold.
                    // D1 was degree-1 only; D4 widened it to <=2 after the
                    // runtime check found a 1280m² degree-2 room (one peer
                    // doorway, one corridor doorway elsewhere) falling
                    // through every selection branch. The principled
                    // long-term fix (cover any large room no junction
                    // sightline reaches) is the D4-deferred reframing —
                    // this remains the narrow patch. Strictly inert when
                    // the knob is <= 0: the early-out preserves byte-
                    // identical output to the pre-D1 behaviour the test
                    // matrix relies on.
                    float leafMinArea = config.LeafCameraMinAreaM2.Value;
                    if (leafMinArea > 0f)
                    {
                        // RoomComponent.Degrees is the RAW UsedDoorways.Count
                        // (intended for the junction-degree rule on the line
                        // above), NOT the in-component peer count. The
                        // uncovered-room rule needs subgraph degree: a big
                        // through-room with one doorway into a peer room
                        // AND one doorway into a corridor reads as raw-
                        // degree 2 but is also a subgraph-degree-2 through-
                        // room — and is exactly the case D4 exists to
                        // cover. Build a member set once per component and
                        // compute subgraph degree on demand by walking
                        // UsedDoorways and tallying peer Tiles in the set,
                        // matching T2's adjacency-pass semantics (including
                        // its multi-doorway-to-same-peer de-dup).
                        var subgraphMembers = new HashSet<Tile>(comp.Tiles);

                        for (int i = 0; i < count; i++)
                        {
                            int idx = orderedIndices[i];
                            Tile candidate = comp.Tiles[idx];
                            if (candidate == null) continue;

                            // De-dup guard. The junction branch above
                            // selects on RAW UsedDoorways.Count >=
                            // JunctionMinDegree (default 3); this pass
                            // selects on SUBGRAPH degree <= 2. The two
                            // predicates use different degree notions, so
                            // they are NOT cleanly disjoint — a tile with
                            // raw degree 3 (one doorway into a corridor +
                            // two into peer rooms) qualifies as a junction
                            // above AND has subgraph degree 2 here. The
                            // guard catches that overlap by srcIndex (R1:
                            // the stable host/client-deterministic identity,
                            // also the capper's final tiebreaker). On the
                            // D4-deferred reframing that unifies the two
                            // predicates this becomes belt-and-suspenders;
                            // today it does real work.
                            int candidateSrcIdx = comp.SrcIndices[idx];
                            if (chosenJunctionSrcIndices.Contains(candidateSrcIdx)) continue;

                            // Subgraph-degree check (D4): accept degree 1
                            // (D1's leaf case) OR degree 2 (D4's through-
                            // room case); skip junctions (deg >= 3) and
                            // the degenerate deg-0 case. Short-circuited:
                            // track up to two distinct peers; bail when a
                            // third appears. Allocation-free, and matches
                            // T2's edge de-dup (two doorways A→B count as
                            // one edge — incrementing only on a new peer).
                            Tile peer0 = null, peer1 = null;
                            int subgraphDeg = 0;
                            bool exceeded = false;
                            var dws = candidate.UsedDoorways;
                            if (dws != null)
                            {
                                for (int d = 0; d < dws.Count; d++)
                                {
                                    Doorway dw = dws[d];
                                    if (dw == null) continue;
                                    Doorway peerDw = dw.ConnectedDoorway;
                                    if (peerDw == null) continue;
                                    Tile peerTile = peerDw.Tile;
                                    if (peerTile == null) continue;
                                    if (!subgraphMembers.Contains(peerTile)) continue;
                                    if (peer0 == null)
                                    {
                                        peer0 = peerTile;
                                        subgraphDeg = 1;
                                    }
                                    else if (ReferenceEquals(peer0, peerTile))
                                    {
                                        // duplicate edge to peer0
                                    }
                                    else if (peer1 == null)
                                    {
                                        peer1 = peerTile;
                                        subgraphDeg = 2;
                                    }
                                    else if (!ReferenceEquals(peer1, peerTile))
                                    {
                                        exceeded = true;
                                        break;
                                    }
                                    // else: duplicate edge to peer1
                                }
                            }
                            if (exceeded || subgraphDeg < 1 || subgraphDeg > 2) continue;

                            // Same area measure Gate A and the ring branch
                            // use (Bounds.size.x * size.z via TileFootprint),
                            // so eligibility area and Gate A area are the
                            // same number.
                            if (TileFootprint(candidate) < leafMinArea) continue;

                            // Iteration is orderedIndices ascending → picks
                            // emitted in srcIndex ascending order,
                            // deterministic across host/client. The Degree
                            // field on CameraPick mirrors the raw count
                            // (consistent with the junction picks above; the
                            // subgraph degree is a selection criterion, not
                            // a sort key).
                            picks.Add(MakePick(candidate, comp, CameraPickReason.LargeUncovered, comp.Degrees[idx]));
                        }
                    }

                    continue;
                }

                // No junctions. Endpoint / largest-tile branch.
                if (linearChainPolicy == LinearChainEndpoints)
                {
                    bool hasEndpoint = false;
                    for (int i = 0; i < count; i++)
                    {
                        if (comp.Degrees[orderedIndices[i]] == 1) { hasEndpoint = true; break; }
                    }
                    if (hasEndpoint)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            int idx = orderedIndices[i];
                            if (comp.Degrees[idx] == 1)
                            {
                                picks.Add(MakePick(comp.Tiles[idx], comp, CameraPickReason.LinearChainEndpoint, comp.Degrees[idx]));
                            }
                        }
                        continue;
                    }
                    // Fall through to ring/largest-tile fallback.
                }

                // Either policy=LargestTileOnly, or DegreeOneEndpoints with no
                // degree-1 tile (a ring). Single largest-footprint tile;
                // tiebreak by SrcIndex ascending (orderedIndices iterates
                // tiles in SrcIndex order, so an "> only" comparison keeps
                // the first occurrence among ties — i.e. lowest SrcIndex).
                int bestIdx = orderedIndices[0];
                float bestFootprint = TileFootprint(comp.Tiles[bestIdx]);
                for (int i = 1; i < count; i++)
                {
                    int idx = orderedIndices[i];
                    float fp = TileFootprint(comp.Tiles[idx]);
                    if (fp > bestFootprint)
                    {
                        bestFootprint = fp;
                        bestIdx = idx;
                    }
                }
                picks.Add(MakePick(comp.Tiles[bestIdx], comp, CameraPickReason.RingFallbackLargestTile, comp.Degrees[bestIdx]));
            }

            return picks;
        }

        private static CameraPick MakePick(Tile t, RoomComponent comp, CameraPickReason reason, int degree)
        {
            float fp = TileFootprint(t);
            bool mainPath = false;
            // Tile.Placement is a TilePlacementData with IsOnMainPath.
            // Defensive read in case Placement is null on a partially-
            // initialized tile.
            try { mainPath = t.Placement != null && t.Placement.IsOnMainPath; }
            catch { mainPath = false; }
            int srcIdx = SrcIndexOf(t, comp);
            return new CameraPick(t, comp, reason, degree, fp, mainPath, srcIdx);
        }

        // Find the tile's SrcIndex by walking the parallel SrcIndices list.
        // Components are small (≤ tens of tiles typically), so linear scan
        // is fine. Returns int.MaxValue if not found (defensive — should
        // never happen since MakePick is called with a tile from the
        // component's own Tiles list).
        private static int SrcIndexOf(Tile t, RoomComponent comp)
        {
            if (comp == null || comp.Tiles == null || comp.SrcIndices == null) return int.MaxValue;
            for (int i = 0; i < comp.Tiles.Count; i++)
            {
                if (ReferenceEquals(comp.Tiles[i], t)) return comp.SrcIndices[i];
            }
            return int.MaxValue;
        }

        private static float TileFootprint(Tile t)
        {
            if (t == null) return 0f;
            var size = t.Bounds.size;
            return size.x * size.z;
        }

        private static void SortByTileSrcKey(int[] indices, RoomComponent comp)
        {
            // Sort the position-in-component index array by the underlying
            // tile's SrcIndex (= dungeon.AllTiles index, host/client-
            // deterministic). Small N per component; insertion sort, no LINQ.
            for (int i = 1; i < indices.Length; i++)
            {
                int key = indices[i];
                int keySrc = comp.SrcIndices[key];
                int j = i - 1;
                while (j >= 0 && comp.SrcIndices[indices[j]] > keySrc)
                {
                    indices[j + 1] = indices[j];
                    j--;
                }
                indices[j + 1] = key;
            }
        }

        private static string ResolveLinearChainPolicy(string raw)
        {
            if (raw == LinearChainEndpoints) return LinearChainEndpoints;
            if (raw == LinearChainLargest) return LinearChainLargest;
            if (!_unknownLinearChainWarned)
            {
                _unknownLinearChainWarned = true;
                SurveillanceBootstrap.Log.LogWarning(
                    $"[LethalCCTV] CameraSelector: unknown 'Linear Chain Coverage' value '{raw}'. " +
                    $"Expected '{LinearChainEndpoints}' or '{LinearChainLargest}'. Falling back to '{LinearChainEndpoints}'.");
            }
            return LinearChainEndpoints;
        }
    }
}
