using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // 0.0.36 surface-mount pass — PLACEMENT physics mask.
    //
    // This is DELIBERATELY a separate mask from the CCTV render culling
    // mask (QuadCameraAssignment.ResolveCullingMask / CCTV_CULLING_MASK).
    // The render mask answers "what should appear in the feed" (enemies,
    // props, player, hazards, level geometry minus UI/visor). The
    // placement mask answers "what solid static surface can a camera be
    // physically mounted ON, and what counts as in-world geometry for the
    // skybox/void test." Conflating them would let cameras mount on an
    // enemy or a grabbable item, or treat a hostile silhouette as a
    // mounting surface. Do NOT merge these two masks. See
    // LethalCCTV.md → "Display And Rendering" load-bearing rule.
    //
    // Two masks are produced:
    //   MountMask  — structural surfaces a camera may ATTACH to. Static
    //                interior collision only: Room (the primary interior
    //                collider layer placement linecasts against),
    //                plus Colliders / MiscLevelGeometry /
    //                Railing for structural variety. Intentionally EXCLUDES
    //                Default: too many exterior shells/terrain helpers live
    //                there, and accepting them let interior cameras mount where
    //                they could see the skybox/outside-world shell.
    //                Intentionally EXCLUDES grabbable "Props"/"PhysicsProp"
    //                (they move — a camera
    //                bolted to a carried scrap item would drift) and
    //                "MapHazards" (landmines/turrets are not mount points).
    //   SolidMask  — anything that means "the world is here, not open sky."
    //                Used for the not-skybox / embedding-adjacent tests:
    //                MountMask plus Terrain / Default / MapHazards / Foliage.
    //                A frustum ray that hits ANY of these has not escaped
    //                the world; a ray that hits none out to the void probe
    //                distance is a candidate skybox exposure.
    //
    // Mask resolution is by NAME (LayerMask.NameToLayer), the same idiom
    // QuadCameraAssignment.LayerBit uses, so a modded moon that renames a
    // layer degrades gracefully (the name simply resolves to nothing and is
    // skipped) rather than hard-coding a possibly-wrong bit index.
    //
    // Determinism: pure function of the loaded scene's layer table and
    // collider set; no Time, no randomness, no instance IDs. Host and
    // client run identical builds against identical (DunGen-seeded)
    // geometry, so they resolve identical masks and validity.
    internal readonly struct PlacementMasks
    {
        public readonly int MountMask;
        public readonly int SolidMask;
        public readonly int BodyOverlapMask;
        // Valid iff at least one collider currently lives on a MountMask
        // layer. False means "Room" (and friends) are absent/unpopulated —
        // physics isn't ready or a moon renamed everything. Callers must
        // skip placement rather than fall back to the legacy AABB path,
        // because this pass's hard requirement is "no unsupported cameras."
        public readonly bool Valid;
        public readonly int MountColliderCount;

        public PlacementMasks(int mountMask, int solidMask, int bodyOverlapMask, bool valid, int mountColliderCount)
        {
            MountMask = mountMask;
            SolidMask = solidMask;
            BodyOverlapMask = bodyOverlapMask;
            Valid = valid;
            MountColliderCount = mountColliderCount;
        }
    }

    internal static class PlacementMask
    {
        // Structural, static, mountable surfaces. "Room" first — it is the
        // interior collision layer vanilla LC walks on and the one the
        // existing placement linecasts already prove is populated at
        // OnFinishedGeneratingDungeon time.
        private static readonly string[] s_mountLayerNames =
            { "Room", "Colliders", "MiscLevelGeometry", "Railing" };

        // Broad "is there world here" set for the not-skybox test. Superset
        // of the mount layers plus outdoor/general geometry. A ray hitting
        // any of these has NOT exited to sky.
        private static readonly string[] s_solidLayerNames =
        {
            "Room", "Colliders", "MiscLevelGeometry", "Railing",
            "Terrain", "Default", "MapHazards", "Foliage",
        };

        private static readonly string[] s_bodyOverlapLayerNames =
        {
            "Room", "Colliders", "MiscLevelGeometry", "Railing",
            "Terrain", "Default", "MapHazards", "Foliage",
            "Props", "PhysicsProp", "InteractableObject", "PlaceableShipObjects",
        };

        private static readonly string[] s_expandedMountLayerNames =
        {
            // Sparse/custom interiors may put their structural shell on Terrain or
            // Default. Props, hazards and foliage are deliberately excluded: mounting
            // on a pipe or movable prop is worse than skipping the tile.
            "Room", "Colliders", "MiscLevelGeometry", "Railing",
            "Terrain", "Default",
        };

        private static bool _resolvedLogEmitted;
        private static bool _expandedFallbackLogEmitted;

        internal static PlacementMasks Resolve(bool verboseLog)
        {
            int mountMask = MaskFromNames(s_mountLayerNames, out string mountDesc);
            int solidMask = MaskFromNames(s_solidLayerNames, out string solidDesc);
            int bodyOverlapMask = solidMask | MaskFromNames(s_bodyOverlapLayerNames, out string bodyDesc);

            int mountColliders = CountCollidersOnMask(mountMask);
            bool valid = mountMask != 0 && mountColliders > 0;

            // One-shot summary so a misconfigured moon (renamed Room layer,
            // physics not ready) is one grep away — mirrors the existing
            // ROOM_LAYER fail-loud line.
            if (!_resolvedLogEmitted || verboseLog)
            {
                _resolvedLogEmitted = true;
                if (valid)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] PLACEMENT_MASK mount=0x{mountMask:X8} [{mountDesc}] colliders={mountColliders} " +
                        $"solid=0x{solidMask:X8} [{solidDesc}] body=0x{bodyOverlapMask:X8} [{bodyDesc}] status=OK");
                }
                else
                {
                    SurveillanceBootstrap.Log.LogWarning(
                        $"[LethalCCTV] PLACEMENT_MASK mount=0x{mountMask:X8} [{mountDesc}] colliders={mountColliders} " +
                        $"body=0x{bodyOverlapMask:X8} [{bodyDesc}] status=INVALID — no mountable surface colliders found; no CCTV cameras will spawn this dungeon " +
                        "rather than falling back to unsupported AABB placement.");
                }
            }

            return new PlacementMasks(mountMask, solidMask, bodyOverlapMask, valid, mountColliders);
        }

        internal static PlacementMasks ResolveExpandedMountFallback(PlacementMasks baseline, bool verboseLog)
        {
            int mountMask = MaskFromNames(s_expandedMountLayerNames, out string mountDesc);
            int solidMask = baseline.SolidMask | mountMask;
            int bodyOverlapMask = baseline.BodyOverlapMask | mountMask;
            int mountColliders = CountCollidersOnMask(mountMask);
            bool valid = mountMask != 0 && mountColliders > 0;

            if (!_expandedFallbackLogEmitted || verboseLog)
            {
                _expandedFallbackLogEmitted = true;
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] PLACEMENT_FALLBACK_MASK mount=0x{mountMask:X8} [{mountDesc}] " +
                    $"colliders={mountColliders} solid=0x{solidMask:X8} body=0x{bodyOverlapMask:X8} status={(valid ? "OK" : "INVALID")}");
            }

            return new PlacementMasks(mountMask, solidMask, bodyOverlapMask, valid, mountColliders);
        }

        private static int MaskFromNames(string[] names, out string resolvedDesc)
        {
            int mask = 0;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < names.Length; i++)
            {
                int layer = LayerMask.NameToLayer(names[i]);
                if (layer < 0) continue;
                mask |= 1 << layer;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(names[i]);
            }
            resolvedDesc = sb.Length > 0 ? sb.ToString() : "<none>";
            return mask;
        }

        private static int CountCollidersOnMask(int mask)
        {
            if (mask == 0) return 0;
            Collider[] all = Object.FindObjectsByType<Collider>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Collider c = all[i];
                if (c == null) continue;
                if ((mask & (1 << c.gameObject.layer)) != 0) n++;
            }
            return n;
        }
    }
}
