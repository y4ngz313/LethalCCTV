using System;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // One probe position on the mount patch, in metres along the patch's
    // tangent (U) and bitangent (V) axes, measured from the surface hit point.
    internal readonly struct StructuralPatchOffset
    {
        public readonly float U;
        public readonly float V;

        public StructuralPatchOffset(float u, float v)
        {
            U = u;
            V = v;
        }
    }

    // What one patch probe ray found. PlaneOffsetM is the signed distance of
    // the sample's hit point from the mount plane along the mount normal
    // (positive = proud of the surface, toward the room); NormalDot is the dot
    // of the sample's hit normal with the mount normal.
    internal readonly struct StructuralPatchSample
    {
        public readonly bool Hit;
        public readonly float PlaneOffsetM;
        public readonly float NormalDot;
        public readonly bool SameCollider;
        public readonly string ColliderName;

        public StructuralPatchSample(bool hit, float planeOffsetM, float normalDot, bool sameCollider, string colliderName)
        {
            Hit = hit;
            PlaneOffsetM = planeOffsetM;
            NormalDot = normalDot;
            SameCollider = sameCollider;
            ColliderName = colliderName;
        }

        public static StructuralPatchSample Miss => new StructuralPatchSample(false, 0f, 0f, false, null);
    }

    // One hit of the free-standing probe (#1367). FacingDot is the dot of the
    // hit's normal with the mount normal (1 = the hit faces the camera side,
    // the same way as the mount surface); LocalX/Y/Z is the hit point in the
    // owning tile's local frame (placement position and rotation, unit scale).
    internal readonly struct FreeStandingProbeHit
    {
        public readonly float FacingDot;
        public readonly float LocalX;
        public readonly float LocalY;
        public readonly float LocalZ;

        public FreeStandingProbeHit(float facingDot, float localX, float localY, float localZ)
        {
            FacingDot = facingDot;
            LocalX = localX;
            LocalY = localY;
            LocalZ = localZ;
        }
    }

    // Tile-local axis-aligned box the camera spawner places in: the tile's
    // rendered-geometry AABB, or Placement.LocalBounds on a degenerate tile.
    internal readonly struct TileLocalBox
    {
        public readonly float MinX;
        public readonly float MinY;
        public readonly float MinZ;
        public readonly float MaxX;
        public readonly float MaxY;
        public readonly float MaxZ;

        public TileLocalBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
        }
    }

    // Pure structural-patch verdict (#1313). A camera may mount only on a flat
    // structural wall or ceiling: a continuous patch about 1.2 m wide around the
    // hit point must be one plane, and the hit collider itself must be large.
    // It must also not be free-standing (#1367): no room surface facing the
    // same way may sit just behind it inside the tile.
    // Free of UnityEngine types so the CctvGameplayRegression harness compiles
    // and exercises it; SurfaceMount.IsStructuralPatch and
    // SurfaceMount.IsFreeStandingSurface perform the raycasts and feed each
    // probe's result here.
    //
    // Deterministic over its inputs: fixed constants, fixed sample order, no
    // state, so host and client reach identical verdicts for identical geometry.
    internal static class StructuralPatchLogic
    {
        // Largest |plane offset| a sample may have and still count as the same plane.
        internal const float PlaneToleranceM = 0.08f;
        // Smallest dot between a sample's normal and the mount normal.
        internal const float MinNormalDot = 0.95f;
        // A hit collider whose bounds span less than this along BOTH patch axes is
        // a pipe, beam, bracket or trim piece, whatever its neighbours look like.
        internal const float MinColliderTangentExtentM = 1.0f;
        // Grid spacing of the patch probes.
        internal const float SampleSpacingM = 0.3f;
        // Probe rays start this far in front of the mount plane.
        internal const float ProbeStandoffM = 0.3f;
        // Half-size of the camera's mounting-plate footprint (U by V). Its four
        // corners are sampled as well, because the 0.3 m grid steps over them.
        internal const float PlateHalfWidthM = 0.21f;
        internal const float PlateHalfHeightM = 0.15f;

        internal const string PassReason = "pass";

        // ---- free-standing probe (#1367 D2) ----
        // A surface that passes the patch can still be furniture or a partition
        // standing in the room (a bookshelf side panel). The probe casts every
        // hit straight into the mount surface (along -normal), starting
        // FreeStandingProbeStartM behind the hit point, for
        // FreeStandingProbeDistanceM on the mount mask.
        internal const float FreeStandingProbeStartM = 0.02f;
        internal const float FreeStandingProbeDistanceM = 1.5f;
        // Smallest dot between a probe hit's normal and the mount normal for the
        // hit to count as a room surface behind the mount surface.
        internal const float FreeStandingMinFacingDot = 0.9f;
        // A probe hit counts only inside the tile box shrunk by this on every face.
        internal const float FreeStandingTileInsetM = 0.05f;
        internal const string FreeStandingReason = "free-standing";

        // ---- fixture-obstruction veto (#1367 P1) ----
        // Case-insensitive name fragments that mark a collider as furniture, a
        // fixture or ductwork rather than structure ("shelv" catches Shelves and
        // Shelving). The first token in this order that a name contains is the
        // reported token. The CctvSpawnChecks fixture oracle reads this field by
        // reflection, so keep its name and type.
        // blocker/locker are excluded because Factory hallway end walls live under 'HallwayBlocker' prefabs (and 'locker' is a fragment of 'blocker').
        internal static readonly string[] FixtureNameTokens =
        {
            "shelf", "shelv", "bookcase", "bookshelf", "cabinet", "cupboard", "dresser", "wardrobe", "closet", "desk",
            "table", "counter", "couch", "sofa", "chair", "bench", "bed", "piano", "painting", "picture", "frame",
            "crate", "barrel", "pipe", "vent", "duct", "beam", "rack", "furniture",
        };

        // Probe grid over [-halfU, halfU] x [-halfV, halfV] with
        // 2*ceil(half/SampleSpacingM)+1 points per axis, plus the four
        // mounting-plate corners (±PlateHalfWidthM, ±PlateHalfHeightM) when they
        // lie inside the patch and are not already grid points. Ordered grid
        // corners first, then the rest of the border, then interior grid
        // points, then the plate corners, centre last, so the samples most
        // likely to fall off a narrow surface run first and a failing patch
        // exits early.
        internal static StructuralPatchOffset[] SampleOffsets(float halfU, float halfV)
        {
            int nu = AxisCount(halfU);
            int nv = AxisCount(halfV);
            int cu = nu / 2;
            int cv = nv / 2;
            bool plateInside = PlateHalfWidthM <= halfU && PlateHalfHeightM <= halfV &&
                               !(IsGridValue(PlateHalfWidthM, nu, halfU) && IsGridValue(PlateHalfHeightM, nv, halfV));
            var offsets = new StructuralPatchOffset[nu * nv + (plateInside ? 4 : 0)];
            int k = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                if (pass == 3 && plateInside)
                {
                    offsets[k++] = new StructuralPatchOffset(-PlateHalfWidthM, -PlateHalfHeightM);
                    offsets[k++] = new StructuralPatchOffset(PlateHalfWidthM, -PlateHalfHeightM);
                    offsets[k++] = new StructuralPatchOffset(-PlateHalfWidthM, PlateHalfHeightM);
                    offsets[k++] = new StructuralPatchOffset(PlateHalfWidthM, PlateHalfHeightM);
                }
                for (int j = 0; j < nv; j++)
                {
                    for (int i = 0; i < nu; i++)
                    {
                        if (ClassOf(i, j, nu, nv, cu, cv) != pass) continue;
                        offsets[k++] = new StructuralPatchOffset(AxisValue(i, nu, halfU), AxisValue(j, nv, halfV));
                    }
                }
            }
            return offsets;
        }

        // Collider-size rule, applied before any sample: reject when the hit
        // collider's bounds projected on the patch axes are below
        // MinColliderTangentExtentM along BOTH axes.
        internal static bool EvaluateBounds(float centreExtentU, float centreExtentV, out string reason)
        {
            if (centreExtentU < MinColliderTangentExtentM && centreExtentV < MinColliderTangentExtentM)
            {
                reason = FormattableString.Invariant($"narrow-collider u={centreExtentU:F2} v={centreExtentV:F2}");
                return false;
            }
            reason = PassReason;
            return true;
        }

        // Verdict for one probe. A sample off the plane or tilted counts as
        // foreign-collider when it hit a different collider (something proud of,
        // or recessed from, the mount surface), else off-plane / normal-tilt.
        internal static bool EvaluateSample(int index, in StructuralPatchOffset offset, in StructuralPatchSample sample, out string reason)
        {
            if (!sample.Hit)
            {
                reason = FormattableString.Invariant($"sample-miss i={index} u={offset.U:F2} v={offset.V:F2}");
                return false;
            }

            bool offPlane = Math.Abs(sample.PlaneOffsetM) > PlaneToleranceM;
            bool tilted = sample.NormalDot < MinNormalDot;
            if (!offPlane && !tilted)
            {
                reason = PassReason;
                return true;
            }

            if (!sample.SameCollider)
            {
                string name = string.IsNullOrEmpty(sample.ColliderName) ? "none" : sample.ColliderName;
                reason = FormattableString.Invariant(
                    $"foreign-collider i={index} name={name} d={sample.PlaneOffsetM:F3} dot={sample.NormalDot:F3}");
            }
            else if (offPlane)
            {
                reason = FormattableString.Invariant($"off-plane i={index} d={sample.PlaneOffsetM:F3}");
            }
            else
            {
                reason = FormattableString.Invariant($"normal-tilt i={index} dot={sample.NormalDot:F3}");
            }
            return false;
        }

        // Whole-patch verdict over precomputed samples: the bounds rule, then
        // each sample in order; the first failure decides the reason.
        internal static bool Evaluate(
            float centreExtentU,
            float centreExtentV,
            StructuralPatchOffset[] offsets,
            StructuralPatchSample[] samples,
            out string reason)
        {
            if (!EvaluateBounds(centreExtentU, centreExtentV, out reason))
                return false;
            if (offsets == null || samples == null || samples.Length != offsets.Length || samples.Length == 0)
            {
                reason = "sample-miss i=0 u=0.00 v=0.00";
                return false;
            }
            for (int i = 0; i < samples.Length; i++)
            {
                if (!EvaluateSample(i, in offsets[i], in samples[i], out reason))
                    return false;
            }
            reason = PassReason;
            return true;
        }

        // Free-standing verdict (#1367 D2) over the hits of one probe ray. The
        // mount surface is free-standing when ANY hit faces the camera side
        // (FacingDot >= FreeStandingMinFacingDot) and lies inside the tile box
        // shrunk by FreeStandingTileInsetM on every face (inclusive; a shrunk box
        // that is empty on any axis contains nothing). Reads hits[0..count)
        // only, and the verdict does not depend on their order. reason is
        // FreeStandingReason or PassReason.
        internal static bool IsFreeStanding(FreeStandingProbeHit[] hits, int count, in TileLocalBox box, out string reason)
        {
            if (hits != null)
            {
                int n = Math.Min(count, hits.Length);
                for (int i = 0; i < n; i++)
                {
                    if (IsSurfaceBehind(in hits[i], in box))
                    {
                        reason = FreeStandingReason;
                        return true;
                    }
                }
            }
            reason = PassReason;
            return false;
        }

        // First FixtureNameTokens entry (in list order) that name contains,
        // case-insensitive ordinal, or null for a null, empty or token-free name.
        internal static string FixtureToken(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string[] tokens = FixtureNameTokens;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (name.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return tokens[i];
            }
            return null;
        }

        // Fixture token of a hierarchy chain (#1367 P1): checks start, then each
        // parent in turn, and stops before the first node for which isStop is
        // true (the DunGen tile root: it and everything above it are never
        // checked) or when parent returns null. The first node, in walk order,
        // whose name contains a token decides; null when none does.
        internal static string FirstFixtureTokenInChain<TNode>(
            TNode start,
            Func<TNode, TNode> parent,
            Func<TNode, string> name,
            Func<TNode, bool> isStop)
            where TNode : class
        {
            for (TNode node = start; node != null && !isStop(node); node = parent(node))
            {
                string token = FixtureToken(name(node));
                if (token != null) return token;
            }
            return null;
        }

        private static bool IsSurfaceBehind(in FreeStandingProbeHit hit, in TileLocalBox box)
        {
            return hit.FacingDot >= FreeStandingMinFacingDot &&
                   InsideInsetSpan(hit.LocalX, box.MinX, box.MaxX) &&
                   InsideInsetSpan(hit.LocalY, box.MinY, box.MaxY) &&
                   InsideInsetSpan(hit.LocalZ, box.MinZ, box.MaxZ);
        }

        private static bool InsideInsetSpan(float value, float min, float max)
        {
            return min + FreeStandingTileInsetM <= value && value <= max - FreeStandingTileInsetM;
        }

        private static int AxisCount(float half)
        {
            if (!(half > 0f)) return 1;
            // The epsilon keeps an exact multiple of the spacing from rounding up.
            return 2 * (int)Math.Ceiling(half / SampleSpacingM - 1e-4f) + 1;
        }

        private static float AxisValue(int index, int count, float half)
        {
            if (count <= 1) return 0f;
            return -half + index * (2f * half / (count - 1));
        }

        private static bool IsGridValue(float value, int count, float half)
        {
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(AxisValue(i, count, half) - value) < 1e-4f)
                    return true;
            }
            return false;
        }

        // 0 corner, 1 other border point, 2 interior point, 3 centre.
        private static int ClassOf(int i, int j, int nu, int nv, int cu, int cv)
        {
            if (i == cu && j == cv) return 3;
            bool borderU = i == 0 || i == nu - 1;
            bool borderV = j == 0 || j == nv - 1;
            if (borderU && borderV) return 0;
            if (borderU || borderV) return 1;
            return 2;
        }
    }

    // Per-tile tally of structural-patch rejects by category, so a tile that
    // ends with no camera can say why in one SURFACE_SKIP line. The caller owns
    // one instance and resets it per pick; the categories keep a fixed order so
    // the summary is deterministic.
    internal sealed class StructuralPatchRejectTally
    {
        private static readonly string[] s_categories =
        {
            "no-collider", "narrow-collider", "sample-miss", "off-plane", "normal-tilt", "foreign-collider",
            StructuralPatchLogic.FreeStandingReason, "other",
        };

        private readonly int[] _counts = new int[s_categories.Length];

        internal int Total { get; private set; }

        internal void Reset()
        {
            Array.Clear(_counts, 0, _counts.Length);
            Total = 0;
        }

        internal void Add(string reason)
        {
            int index = s_categories.Length - 1;
            if (reason != null)
            {
                for (int i = 0; i < s_categories.Length - 1; i++)
                {
                    string category = s_categories[i];
                    if (reason.StartsWith(category, StringComparison.Ordinal) &&
                        (reason.Length == category.Length || reason[category.Length] == ' '))
                    {
                        index = i;
                        break;
                    }
                }
            }
            _counts[index]++;
            Total++;
        }

        // "narrow-collider:3,off-plane:2", or "none" when nothing was rejected.
        internal string Format()
        {
            if (Total == 0) return "none";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s_categories.Length; i++)
            {
                if (_counts[i] == 0) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(s_categories[i]).Append(':').Append(_counts[i]);
            }
            return sb.ToString();
        }
    }
}
