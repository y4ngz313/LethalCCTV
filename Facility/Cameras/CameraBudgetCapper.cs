using System;
using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Surveillance;

namespace Y4NGZCompany.Facility.Cameras
{
    // Phase 1.8 Placement T4 — budget cap.
    //
    // If picks.Count <= cap: return a copy as-is (no sort here — the
    // instantiation-order composite sort lives at T5).
    // Else: total-ordered sort by the configured priority mode and take
    // the first `cap`. SrcIndex ascending is ALWAYS the final
    // tiebreaker so the same picks-set truncates identically on host
    // and client (SrcIndex is dungeon.AllTiles index → deterministic
    // across clients via DunGen seed). The comparator is total —
    // SrcIndex is unique per Tile in AllTiles, so no two distinct picks
    // ever compare equal on every key. This totality is what the T5
    // low-cap MP test verifies.
    //
    // T4 has no callers — T5 will invoke Apply from the spawner.

    internal static class CameraBudgetCapper
    {
        private const string PriorityMainPathThenDegreeThenArea = "MainPathThenDegreeThenArea";
        private const string PriorityLargestAreaFirst = "LargestAreaFirst";
        private const string PriorityHighestDegreeFirst = "HighestDegreeFirst";

        // Spatial spread for the capped set. A pure priority truncation keeps
        // consecutive main-path junction tiles — a clump of cameras along one
        // corridor while whole wings go dark. When the cap engages, picks are
        // chosen greedily in priority order subject to a minimum horizontal
        // separation between kept tiles; the separation halves whenever no
        // remaining pick clears it, and below the floor the remainder fills
        // in plain priority order. Deterministic: tile centers come from
        // DunGen-seeded placement, iteration order from the total-ordered
        // priority sort.
        private const float SpreadMinSeparationM = 18f;
        private const float SpreadRelaxFloorM = 4.5f;
        // Tiles stacked on different floors are not visually clumped.
        private const float SpreadCrossFloorYM = 5f;

        private static bool _unknownPriorityWarned;

        internal static List<CameraPick> Apply(
            IReadOnlyList<CameraPick> picks,
            LethalCCTVConfig config,
            out bool capEngaged,
            out int droppedCount)
        {
            capEngaged = false;
            droppedCount = 0;
            if (picks == null || picks.Count == 0 || config == null)
            {
                return new List<CameraPick>(0);
            }

            int cap = config.NormalizedMaximumCameraCount;

            if (picks.Count <= cap)
            {
                var asIs = new List<CameraPick>(picks.Count);
                for (int i = 0; i < picks.Count; i++) asIs.Add(picks[i]);
                return asIs;
            }

            string priority = ResolvePriority(config.CameraBudgetPriority.Value);
            var sorted = new List<CameraPick>(picks.Count);
            for (int i = 0; i < picks.Count; i++) sorted.Add(picks[i]);

            // List<T>.Sort is NOT guaranteed stable, but our comparator is
            // total (SrcIndex is unique per AllTiles entry → SrcIndex asc
            // breaks every tie). Stability is irrelevant when the comparator
            // never reports equality on distinct elements.
            switch (priority)
            {
                case PriorityLargestAreaFirst:
                    sorted.Sort(CompareLargestAreaFirst);
                    break;
                case PriorityHighestDegreeFirst:
                    sorted.Sort(CompareHighestDegreeFirst);
                    break;
                case PriorityMainPathThenDegreeThenArea:
                default:
                    sorted.Sort(CompareMainPathThenDegreeThenArea);
                    break;
            }

            capEngaged = true;
            droppedCount = picks.Count - cap;
            return SelectWithSpatialSpread(sorted, cap);
        }

        // Greedy priority-then-spread selection over the priority-sorted
        // list. Always returns exactly `cap` picks (assuming enough input).
        private static List<CameraPick> SelectWithSpatialSpread(List<CameraPick> byPriority, int cap)
        {
            var kept = new List<CameraPick>(cap);
            var remaining = new List<CameraPick>(byPriority);
            if (cap <= 0 || remaining.Count == 0) return kept;

            kept.Add(remaining[0]);
            remaining.RemoveAt(0);

            float minSeparation = SpreadMinSeparationM;
            while (kept.Count < cap && remaining.Count > 0)
            {
                int chosen = -1;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (ClearsSeparation(remaining[i], kept, minSeparation))
                    {
                        chosen = i;
                        break;
                    }
                }

                if (chosen < 0)
                {
                    minSeparation *= 0.5f;
                    if (minSeparation < SpreadRelaxFloorM)
                    {
                        for (int i = 0; i < remaining.Count && kept.Count < cap; i++)
                            kept.Add(remaining[i]);
                        break;
                    }
                    continue;
                }

                kept.Add(remaining[chosen]);
                remaining.RemoveAt(chosen);
            }

            return kept;
        }

        private static bool ClearsSeparation(CameraPick candidate, List<CameraPick> kept, float minSeparation)
        {
            if (candidate.Tile == null) return true;
            Vector3 c = candidate.Tile.Bounds.center;
            for (int i = 0; i < kept.Count; i++)
            {
                if (kept[i].Tile == null) continue;
                Vector3 k = kept[i].Tile.Bounds.center;
                if (Mathf.Abs(c.y - k.y) >= SpreadCrossFloorYM) continue;
                float dx = c.x - k.x;
                float dz = c.z - k.z;
                if (dx * dx + dz * dz < minSeparation * minSeparation)
                    return false;
            }
            return true;
        }

        // P2.0 §6 — exposed for the post-cap entrance promotion layer.
        // Returns the exact Comparison<CameraPick> the capper would use
        // for `config`'s active priority. Promotion sorts orderedPicks
        // by this and evicts the last (lowest-priority) entry — same
        // ordering the capper would have chosen if it were one slot
        // tighter. Host/client identical by construction (the underlying
        // comparators all total-order on SrcIndex).
        internal static Comparison<CameraPick> GetCapPriorityComparison(LethalCCTVConfig config)
        {
            string priority = config != null
                ? ResolvePriority(config.CameraBudgetPriority.Value)
                : PriorityMainPathThenDegreeThenArea;
            switch (priority)
            {
                case PriorityLargestAreaFirst: return CompareLargestAreaFirst;
                case PriorityHighestDegreeFirst: return CompareHighestDegreeFirst;
                default: return CompareMainPathThenDegreeThenArea;
            }
        }

        // (1) IsOnMainPath desc → (2) Degree desc → (3) FootprintM2 desc → (4) SrcIndex asc.
        private static int CompareMainPathThenDegreeThenArea(CameraPick a, CameraPick b)
        {
            int c = CompareBoolDesc(a.IsOnMainPath, b.IsOnMainPath);
            if (c != 0) return c;
            c = b.Degree.CompareTo(a.Degree);
            if (c != 0) return c;
            c = b.FootprintM2.CompareTo(a.FootprintM2);
            if (c != 0) return c;
            return a.SrcIndex.CompareTo(b.SrcIndex);
        }

        // (1) FootprintM2 desc → (2) Degree desc → (3) IsOnMainPath desc → (4) SrcIndex asc.
        private static int CompareLargestAreaFirst(CameraPick a, CameraPick b)
        {
            int c = b.FootprintM2.CompareTo(a.FootprintM2);
            if (c != 0) return c;
            c = b.Degree.CompareTo(a.Degree);
            if (c != 0) return c;
            c = CompareBoolDesc(a.IsOnMainPath, b.IsOnMainPath);
            if (c != 0) return c;
            return a.SrcIndex.CompareTo(b.SrcIndex);
        }

        // (1) Degree desc → (2) IsOnMainPath desc → (3) FootprintM2 desc → (4) SrcIndex asc.
        private static int CompareHighestDegreeFirst(CameraPick a, CameraPick b)
        {
            int c = b.Degree.CompareTo(a.Degree);
            if (c != 0) return c;
            c = CompareBoolDesc(a.IsOnMainPath, b.IsOnMainPath);
            if (c != 0) return c;
            c = b.FootprintM2.CompareTo(a.FootprintM2);
            if (c != 0) return c;
            return a.SrcIndex.CompareTo(b.SrcIndex);
        }

        // Descending: true sorts before false. Returns -1 when a is "greater"
        // (i.e. a=true, b=false), +1 when b is greater, 0 when equal.
        private static int CompareBoolDesc(bool a, bool b)
        {
            if (a == b) return 0;
            return a ? -1 : 1;
        }

        private static string ResolvePriority(string raw)
        {
            if (raw == PriorityMainPathThenDegreeThenArea) return PriorityMainPathThenDegreeThenArea;
            if (raw == PriorityLargestAreaFirst) return PriorityLargestAreaFirst;
            if (raw == PriorityHighestDegreeFirst) return PriorityHighestDegreeFirst;
            if (!_unknownPriorityWarned)
            {
                _unknownPriorityWarned = true;
                SurveillanceBootstrap.Log.LogWarning(
                    $"[LethalCCTV] CameraBudgetCapper: unknown 'Camera Budget Priority' value '{raw}'. " +
                    $"Expected '{PriorityMainPathThenDegreeThenArea}', '{PriorityLargestAreaFirst}', or '{PriorityHighestDegreeFirst}'. " +
                    $"Falling back to '{PriorityMainPathThenDegreeThenArea}'.");
            }
            return PriorityMainPathThenDegreeThenArea;
        }
    }
}
