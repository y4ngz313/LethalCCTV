using System;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // Layer NAMES a camera may mount on, per placement tier (#1313). Free of
    // UnityEngine types so the CctvGameplayRegression harness can check the
    // tier lists directly; PlacementMask turns them into bit masks.
    //
    // Only structural layers belong here. Railing is deliberately absent:
    // guard rails, catwalk rails and stair banisters are thin rods, never a
    // flat wall or ceiling. It stays in PlacementMask's solid and body-overlap
    // masks, which decide what a camera may not be embedded in or overlap.
    internal static class PlacementMaskLayers
    {
        internal readonly struct MountTier
        {
            public readonly string Name;
            public readonly string[] LayerNames;

            public MountTier(string name, string[] layerNames)
            {
                Name = name;
                LayerNames = layerNames ?? Array.Empty<string>();
            }
        }

        // Baseline mount layers, shared by the strict and relaxed passes. "Room"
        // first: it is the interior collision layer vanilla LC walks on.
        internal static readonly string[] BaselineMount =
            { "Room", "Colliders", "MiscLevelGeometry" };

        // Expanded mount layers for sparse/custom interiors that put their
        // structural shell on Terrain or Default. Every candidate still passes
        // SurfaceMount.IsStructuralPatch, so a prop-sized collider on these
        // layers is rejected by the patch test. Props, hazards and foliage are
        // never mountable.
        internal static readonly string[] ExpandedMount =
            { "Room", "Colliders", "MiscLevelGeometry", "Terrain", "Default" };

        // Every mount tier (strict and relaxed share the baseline).
        internal static readonly MountTier[] MountTiers =
        {
            new MountTier("baseline", BaselineMount),
            new MountTier("expanded", ExpandedMount),
        };
    }
}
