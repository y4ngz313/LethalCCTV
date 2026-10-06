using System;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // Pure depth-vote arithmetic for SurfaceMount's clutter re-target (#645).
    // Deliberately free of UnityEngine types so the CctvGameplayRegression
    // console harness can compile and exercise it without Unity physics:
    // SurfaceMount performs the raycasts and hands only the resulting hit
    // DISTANCES (metres along the probe ray, or a negative value for a miss)
    // to this function.
    //
    // Deterministic over its inputs — no state, no tolerance derived from
    // anything but the caller's constants — so host and client reach
    // identical verdicts for identical geometry.
    internal static class MountDepthLogic
    {
        // A probe sample that missed is encoded as any negative distance.
        internal const float MissDepth = -1f;

        // Narrow-protrusion vote over lateral probe rays fired past a first
        // hit toward a second (candidate structural) surface. Each sample
        // carries the depth the offset ray would reach on the FIRST hit's
        // plane and on the SECOND hit's plane (negative when the plane is
        // unreachable along that ray). A sample votes "second" when its
        // measured depth agrees with the second plane within toleranceM and
        // is strictly closer to the second plane's depth than to the first's;
        // it votes "first" when it agrees with the first plane. Misses and
        // depths matching neither plane vote for nothing.
        //
        // The first surface is classified as a narrow protrusion — clutter
        // in front of the structural wall — only when a strict MAJORITY of
        // all fired samples votes "second": wide structural geometry (a
        // column face, an alcove wall) answers most offset rays at its own
        // depth and therefore never reaches a majority.
        internal static bool IsNarrowProtrusion(
            float[] sampleDepths,
            float[] expectedFirstDepths,
            float[] expectedSecondDepths,
            float toleranceM)
        {
            if (sampleDepths == null ||
                expectedFirstDepths == null ||
                expectedSecondDepths == null)
                return false;
            int count = sampleDepths.Length;
            if (count == 0 ||
                expectedFirstDepths.Length != count ||
                expectedSecondDepths.Length != count)
                return false;

            int votesSecond = 0;
            for (int i = 0; i < count; i++)
            {
                float d = sampleDepths[i];
                if (d < 0f) continue;

                float e1 = expectedFirstDepths[i];
                float e2 = expectedSecondDepths[i];
                float dev1 = e1 >= 0f ? Math.Abs(d - e1) : float.PositiveInfinity;
                float dev2 = e2 >= 0f ? Math.Abs(d - e2) : float.PositiveInfinity;
                if (dev2 <= toleranceM && dev2 < dev1)
                    votesSecond++;
            }

            return votesSecond * 2 > count;
        }
    }
}
