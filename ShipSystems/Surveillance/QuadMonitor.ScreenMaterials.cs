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
        private static Material CreateScreenSurfaceMaterial(string name, Texture texture, Color color)
        {
            Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Texture");
            Material mat = shader != null
                ? new Material(shader)
                : UnityEngine.Object.Instantiate(VanillaShaderInfo.SourceMaterial);
            mat.name = name;
            BindDisplayTexture(mat, texture);
            if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", color);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", Color.black);
            if (mat.HasProperty("_EmissiveColorLDR")) mat.SetColor("_EmissiveColorLDR", Color.black);
            if (mat.HasProperty("_SurfaceType")) mat.SetFloat("_SurfaceType", 0f);
            if (mat.HasProperty("_BlendMode")) mat.SetFloat("_BlendMode", 0f);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 1f);
            if (mat.HasProperty("_TransparentZWrite")) mat.SetFloat("_TransparentZWrite", 0f);
            if (mat.HasProperty("_AlphaCutoffEnable")) mat.SetFloat("_AlphaCutoffEnable", 0f);
            if (mat.HasProperty("_DoubleSidedEnable")) mat.SetFloat("_DoubleSidedEnable", 1f);
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Off);
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_BLENDMODE_ALPHA");
            mat.DisableKeyword("_EMISSION");
            mat.DisableKeyword("_EMISSIVE_COLOR_MAP");
            mat.EnableKeyword("_DOUBLESIDED_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            ValidateMaterial(mat);
            return mat;
        }

        private static void BindDisplayTexture(Material material, Texture texture)
        {
            if (material == null) return;
            Texture resolved = texture != null ? texture : Texture2D.blackTexture;

            if (!string.IsNullOrEmpty(VanillaShaderInfo.MainTexturePropertyName))
                SetTextureIfExists(material, VanillaShaderInfo.MainTexturePropertyName, resolved);

            for (int i = 0; i < DisplayTexturePropertyNames.Length; i++)
            {
                SetTextureIfExists(material, DisplayTexturePropertyNames[i], resolved);
            }

            for (int i = 0; i < ClearTexturePropertyNames.Length; i++)
            {
                SetTextureIfExists(material, ClearTexturePropertyNames[i], null);
            }
        }

        private static bool SetTextureIfExists(Material material, string propertyName, Texture texture)
        {
            if (material == null || string.IsNullOrEmpty(propertyName) || !material.HasProperty(propertyName))
                return false;

            material.SetTexture(propertyName, texture);
            return true;
        }

        private static void ValidateMaterial(Material material)
        {
            if (material == null) return;
            try
            {
                HDMaterial.ValidateMaterial(material);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Material validation skipped for '{material.name}': {ex.Message}");
            }
        }

    }
}
