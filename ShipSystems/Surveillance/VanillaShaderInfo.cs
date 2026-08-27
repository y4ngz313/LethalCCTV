using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Phase 1.5 probe. Reads the vanilla on-wall monitor's material at first construction
    /// time and caches the shader + texture-property name to use when cloning for our Quad
    /// MeshRenderers. The pivot replaces UI RawImage display with HDRP/Unlit mesh display;
    /// this class knows what to bind on the clone.
    /// </summary>
    internal static class VanillaShaderInfo
    {
        internal static bool ProbeSucceeded { get; private set; }
        internal static Material SourceMaterial { get; private set; }
        internal static string ShaderName { get; private set; }
        internal static string MainTexturePropertyName { get; private set; }

        private static bool _probed;

        internal static void EnsureProbed()
        {
            if (_probed) return;
            _probed = true;

            Material vanillaMat = TryResolveVanillaScreenMaterial();
            if (vanillaMat == null || vanillaMat.shader == null)
            {
                SurveillanceBootstrap.Log.LogError(
                    "[LethalCCTV][VanillaProbe] ERROR — could not resolve vanilla mapScreen material. " +
                    "Aborting QuadMonitor construction. Cameras remain spawned; focus mode unavailable. " +
                    "See Phase 1.5 retrospective.");
                ProbeSucceeded = false;
                return;
            }

            var textureProps = new List<string>();
            int count = vanillaMat.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (vanillaMat.shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                {
                    textureProps.Add(vanillaMat.shader.GetPropertyName(i));
                }
            }

            if (textureProps.Count == 0)
            {
                SurveillanceBootstrap.Log.LogError(
                    "[LethalCCTV][VanillaProbe] ERROR — vanilla material shader exposes no texture properties. " +
                    "Aborting QuadMonitor construction. Cameras remain spawned; focus mode unavailable. " +
                    "See Phase 1.5 retrospective.");
                ProbeSucceeded = false;
                return;
            }

            // Selection precedence: HDRP/Unlit's canonical _UnlitColorMap, then HDRP/Lit's
            // _BaseColorMap, then first texture property by shader-declared order. If we
            // end up on the index-0 fallback the WARNING flags that for Round 2 attention.
            string chosen;
            if (textureProps.Contains("_UnlitColorMap"))
            {
                chosen = "_UnlitColorMap";
            }
            else if (textureProps.Contains("_BaseColorMap"))
            {
                chosen = "_BaseColorMap";
            }
            else
            {
                chosen = textureProps[0];
                SurveillanceBootstrap.Log.LogWarning(
                    $"[LethalCCTV][VanillaProbe] Neither _UnlitColorMap nor _BaseColorMap present. " +
                    $"Falling back to shader-order index 0 ('{chosen}'). Verify in Round 2 if display is empty.");
            }

            SourceMaterial = vanillaMat;
            ShaderName = vanillaMat.shader.name;
            MainTexturePropertyName = chosen;
            ProbeSucceeded = true;
        }

        private static Material TryResolveVanillaScreenMaterial()
        {
            // Primary path — the wall mesh holding the on-wall monitor material, proven in
            // the Phase 1.2/1.3 diagnostic (QuadCameraAssignment.LogVanillaWallMaterials).
            try
            {
                const string monitorWallPath = "Environment/HangarShip/ShipModels2b/MonitorWall/Cube.001";
                GameObject wallGO = GameObject.Find(monitorWallPath);
                if (wallGO != null)
                {
                    var renderer = wallGO.GetComponent<MeshRenderer>();
                    if (renderer != null)
                    {
                        Material picked = PickScreenMaterial(renderer.sharedMaterials);
                        if (picked != null) return picked;
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV][VanillaProbe] Primary path threw: {ex.Message}");
            }

            // Secondary path — broad scene-search for any GameObject named Cube.001 with a
            // MeshRenderer. Survives a future LC patch that re-parents the wall.
            try
            {
                var allRenderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
                for (int i = 0; i < allRenderers.Length; i++)
                {
                    var r = allRenderers[i];
                    if (r != null && r.gameObject.name == "Cube.001")
                    {
                        Material picked = PickScreenMaterial(r.sharedMaterials);
                        if (picked != null) return picked;
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV][VanillaProbe] Secondary path threw: {ex.Message}");
            }

            return null;
        }

        private static Material PickScreenMaterial(Material[] mats)
        {
            if (mats == null) return null;
            // Phase 1.2/1.3 diagnostic confirmed index 1 is the screen material on Cube.001.
            if (mats.Length > 1 && mats[1] != null && mats[1].shader != null)
            {
                return mats[1];
            }
            // Defensive fallback if the slot count changes — first non-null material.
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] != null && mats[i].shader != null) return mats[i];
            }
            return null;
        }
    }
}
