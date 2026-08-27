using System;
using System.Collections.Generic;
using Y4NGZCompany.Facility.Cameras;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class QuadMonitor
    {
        private static string DescribeTextureSize(Texture texture)
        {
            return texture != null ? $"{texture.width}x{texture.height}" : "<null>";
        }

        private static void DestroyMaterial(ref Material material)
        {
            if (material == null) return;
            UnityEngine.Object.Destroy(material);
            material = null;
        }

        private static void DestroyFocusChromeMaterials()
        {
            if (_focusChromeMaterials.Count == 0) return;
            for (int i = 0; i < _focusChromeMaterials.Count; i++)
            {
                if (_focusChromeMaterials[i] != null)
                    UnityEngine.Object.Destroy(_focusChromeMaterials[i]);
            }
            _focusChromeMaterials.Clear();
        }

        private static void SetRuntimeMaterialColor(Material mat, Color color)
        {
            if (mat == null) return;
            if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", color);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.color = color;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent == null) return null;
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindChildRecursive(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static TMP_FontAsset ResolveTMPFontAsset()
        {
            // Chain per Phase 1.5a SPEC Q5: TMP_Settings.defaultFontAsset → HUDManager
            // controlTipLines[0].font → null (fall through to TextMesh).
            try
            {
                if (TMP_Settings.defaultFontAsset != null)
                {
                    return TMP_Settings.defaultFontAsset;
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV] TMP_Settings.defaultFontAsset read threw: {ex.Message}");
            }

            try
            {
                var hud = HUDManager.Instance;
                if (hud != null && hud.controlTipLines != null && hud.controlTipLines.Length > 0)
                {
                    var line = hud.controlTipLines[0];
                    if (line != null && line.font != null)
                    {
                        return line.font;
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV] HUDManager.controlTipLines[0].font read threw: {ex.Message}");
            }

            return null;
        }

        private static void EnsureEmptySlotBlackRT()
        {
            if (_emptySlotBlackRT != null) return;
            _emptySlotBlackRT = new RenderTexture(EMPTY_RT_SIZE, EMPTY_RT_SIZE, 0, RenderTextureFormat.ARGB32)
            {
                name = "LethalCCTV_EmptySlot_BlackRT",
                useDynamicScale = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Point,
            };
            _emptySlotBlackRT.Create();

            Texture2D standby = BuildEmptySlotBlackTexture();
            Graphics.Blit(standby, _emptySlotBlackRT);
            UnityEngine.Object.Destroy(standby);
            SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] Empty-slot black RT allocated ({EMPTY_RT_SIZE}x{EMPTY_RT_SIZE} ARGB32).");
        }

        private static Texture2D BuildEmptySlotBlackTexture()
        {
            var texture = new Texture2D(EMPTY_RT_SIZE, EMPTY_RT_SIZE, TextureFormat.RGBA32, mipChain: false)
            {
                name = "LethalCCTV_EmptyBlack",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[EMPTY_RT_SIZE * EMPTY_RT_SIZE];
            Color32 background = new Color32(0, 0, 0, 255);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = background;

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }

        private static Mesh GetQuadMesh()
        {
            if (_cachedQuadMesh != null) return _cachedQuadMesh;

            // Primary: Unity built-in Quad. Documented at runtime; one allocation cost
            // amortised across the retained backing surfaces and round-to-round respawns.
            Mesh m = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            if (m == null)
            {
                // Fallback: CreatePrimitive then strip the auto-attached collider. PLAN
                // flagged this as the "almost-clean" pattern REVIEW would call out — using
                // it here only if Resources path returns null in this Unity/HDRP version.
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] Resources.GetBuiltinResource<Mesh>('Quad.fbx') returned null. Falling back to CreatePrimitive.");
                var temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                var filter = temp.GetComponent<MeshFilter>();
                m = filter != null ? filter.sharedMesh : null;
                UnityEngine.Object.Destroy(temp);
            }
            _cachedQuadMesh = m;
            return _cachedQuadMesh;
        }
    }
}
