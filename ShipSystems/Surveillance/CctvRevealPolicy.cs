using System;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal enum CctvRevealCategory { Item, Teammate, Enemy, Objective, Device }

    /// <summary>Feed-height units keep aim tolerances independent of resolution/aspect.</summary>
    internal static class CctvRevealPolicy
    {
        internal static float Radius(CctvRevealCategory category, bool acquired)
        {
            float radius = category == CctvRevealCategory.Item ? 0.02f :
                category == CctvRevealCategory.Teammate ? 0.04f :
                category == CctvRevealCategory.Objective ? 0.10f : 0.06f;
            return acquired ? radius * 1.2f : radius;
        }

        internal static bool Eligible(CctvRevealCategory category, bool acquired,
            float aimDistance, bool visible, float zoom, float shortSidePixels)
        {
            if (!visible || float.IsNaN(aimDistance) || aimDistance > Radius(category, acquired)) return false;
            return category != CctvRevealCategory.Item ||
                (zoom >= 1.35f && shortSidePixels >= (acquired ? 24f : 32f));
        }

        internal static float Fade(float alpha, bool reveal, float deltaSeconds)
        {
            float step = Math.Max(0f, deltaSeconds) / (reveal ? 0.12f : 0.18f);
            return reveal ? Math.Min(1f, alpha + step) : Math.Max(0f, alpha - step);
        }
    }
}
