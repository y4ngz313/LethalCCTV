using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Y4NGZCompany.Facility.Cameras.Placement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Shared;

namespace Y4NGZCompany.Facility.Cameras
{
    internal sealed partial class CCTVCameraVisual
    {
        // #716 (audit A6) — Shader.Find is a string lookup through the whole
        // loaded shader set and used to run once per material chain, per
        // material slot, per camera. The result is process-stable, so each
        // chain resolves once and is then reused. Unity can null out a Shader
        // reference across a domain reload, so every accessor re-checks.
        private static Shader s_importedShader;
        private static Shader s_litShader;
        private static Shader s_unlitShader;

        // #716 — the runtime replacement for a bundled material is a pure
        // function of (source material, lens slot): every colour/texture value
        // is read off the source, nothing per-camera is written afterwards, and
        // per-camera state (the lens blink) goes through a MaterialPropertyBlock
        // in CCTVCameraLensBlinker rather than the material. So one shared
        // material per distinct source is correct, and it collapses N cameras ×
        // M slots material allocations down to M.
        private static readonly Dictionary<int, Material> s_importedMaterialCache =
            new Dictionary<int, Material>();

        private static void RepairImportedMaterials(GameObject instance)
        {
            if (instance == null) return;

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;

                Material[] materials = renderer.sharedMaterials;
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    bool lensSlot = IsLensLikeRenderer(renderer) ||
                                    materialIndex > 0 && HasPathSegment(renderer, "camera");
                    materials[materialIndex] = CreateRuntimeImportedMaterial(materials[materialIndex], lensSlot);
                }
                renderer.sharedMaterials = materials;
            }
        }

        private static Material CreateRuntimeImportedMaterial(Material source, bool lens)
        {
            // Key on the source material identity plus the lens flag; the same
            // source can legitimately resolve to a lens and a non-lens variant.
            int key = (source != null ? source.GetInstanceID() : 0) * 2 + (lens ? 1 : 0);
            if (s_importedMaterialCache.TryGetValue(key, out Material cached))
            {
                if (cached != null)
                    return cached;
                s_importedMaterialCache.Remove(key);
            }

            Color fallback = lens ? new Color(0.03f, 0.01f, 0.01f, 1f) : new Color(0.38f, 0.40f, 0.42f, 1f);
            Color color = lens ? fallback : ReadMaterialColor(source, fallback);
            Shader shader = s_importedShader != null
                ? s_importedShader
                : (s_importedShader = FindFirstSupportedShader("HDRP/Lit", "Standard", "Unlit/Texture", "Sprites/Default"));
            Material material = new Material(shader) { name = $"{source?.name ?? "Camera"}_Runtime" };
            Texture texture = lens ? null : ReadMaterialTexture(source);
            if (texture != null)
            {
                SetTextureIfPresent(material, "_BaseColorMap", texture);
                SetTextureIfPresent(material, "_MainTex", texture);
                SetTextureIfPresent(material, "_BaseMap", texture);
                SetTextureIfPresent(material, "_UnlitColorMap", texture);
            }

            SetColorIfPresent(material, "_BaseColor", color);
            SetColorIfPresent(material, "_Color", color);
            SetColorIfPresent(material, "_UnlitColor", color);
            SetFloatIfPresent(material, "_Metallic", 0f);
            SetFloatIfPresent(material, "_Smoothness", lens ? 0.65f : 0.22f);
            if (!lens && source != null && (shader.name == "HDRP/Lit" || shader.name == "Standard"))
            {
                bool hdrp = shader.name == "HDRP/Lit";
                Texture normal = source.HasProperty("_BumpMap") ? source.GetTexture("_BumpMap") : null;
                if (normal != null)
                {
                    SetTextureIfPresent(material, hdrp ? "_NormalMap" : "_BumpMap", normal);
                    material.EnableKeyword("_NORMALMAP");
                    SetFloatIfPresent(material, hdrp ? "_NormalScale" : "_BumpScale",
                        hdrp || !source.HasProperty("_BumpScale") ? 1f : source.GetFloat("_BumpScale"));
                }

                Texture mask = source.HasProperty("_MetallicGlossMap") ? source.GetTexture("_MetallicGlossMap") : null;
                if (mask != null)
                {
                    SetTextureIfPresent(material, hdrp ? "_MaskMap" : "_MetallicGlossMap", mask);
                    material.EnableKeyword(hdrp ? "_MASKMAP" : "_METALLICGLOSSMAP");
                    if (hdrp)
                    {
                        SetFloatIfPresent(material, "_Metallic", 1f);
                        SetFloatIfPresent(material, "_Smoothness", 1f);
                    }
                    else if (source.HasProperty("_GlossMapScale"))
                    {
                        SetFloatIfPresent(material, "_GlossMapScale", source.GetFloat("_GlossMapScale"));
                    }
                }
            }
            s_importedMaterialCache[key] = material;
            return material;
        }

        private static Texture ReadMaterialTexture(Material source)
        {
            if (source == null) return null;
            string[] properties = { "_BaseColorMap", "_MainTex", "_BaseMap", "_UnlitColorMap" };
            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                if (source.HasProperty(property))
                {
                    Texture texture = source.GetTexture(property);
                    if (texture != null)
                        return texture;
                }
            }

            return source.mainTexture;
        }

        private static Color ReadMaterialColor(Material source, Color fallback)
        {
            if (source == null) return fallback;
            string[] properties = { "_BaseColor", "_Color", "_UnlitColor" };
            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                if (source.HasProperty(property))
                    return source.GetColor(property);
            }

            return fallback;
        }

        private static bool IsLensLikeRenderer(Renderer renderer)
        {
            return ContainsAny(BuildDescriptor(renderer), "lens", "glass");
        }


        private static Shader FindFirstSupportedShader(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Shader shader = Shader.Find(names[i]);
                if (shader != null)
                    return shader;
            }

            return Shader.Find("Standard");
        }

        private static void DisableShadows(GameObject go)
        {
            if (go == null) return;
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private void DestroyVisual()
        {
            UnregisterPhysicalCameraRenderers();
            if (_visualRoot != null)
            {
                Object.Destroy(_visualRoot);
                _visualRoot = null;
                _aimPivot = null;
                _bundledYoke = null;
                _bundledRotatingHead = null;
                if (_holder != null)
                    s_rotatingHeadsByHolder.Remove(_holder);
            }
            _built = false;
        }

        private static Material EnsureBodyMaterial()
        {
            if (_fallbackBodyMaterial != null) return _fallbackBodyMaterial;
            _fallbackBodyMaterial = CreateMaterial("LethalCCTV_PhysicalCameraBody", new Color(0.34f, 0.36f, 0.34f, 1f), false);
            return _fallbackBodyMaterial;
        }

        private static Material EnsureDarkMaterial()
        {
            if (_fallbackDarkMaterial != null) return _fallbackDarkMaterial;
            _fallbackDarkMaterial = CreateMaterial("LethalCCTV_PhysicalCameraDark", new Color(0.055f, 0.06f, 0.06f, 1f), false);
            return _fallbackDarkMaterial;
        }

        private static Material EnsureLensMaterial()
        {
            if (_fallbackLensMaterial != null) return _fallbackLensMaterial;
            _fallbackLensMaterial = CreateMaterial("LethalCCTV_PhysicalCameraLens", new Color(0.85f, 0.04f, 0.02f, 1f), true);
            return _fallbackLensMaterial;
        }

        private static Material CreateMaterial(string name, Color color, bool emissive)
        {
            // Resolved once per process per chain (#716). The emissive chain is
            // HDRP/Unlit-first and the opaque chain HDRP/Lit-first; both fall
            // back through the same tail, so two cached fields cover them.
            Shader shader;
            if (emissive)
            {
                if (s_unlitShader == null)
                    s_unlitShader = FindFirstSupportedShader("HDRP/Unlit", "Unlit/Color", "Standard");
                shader = s_unlitShader;
            }
            else
            {
                if (s_litShader == null)
                    s_litShader = FindFirstSupportedShader("HDRP/Lit", "HDRP/Unlit", "Unlit/Color", "Standard");
                shader = s_litShader;
            }
            Material material = new Material(shader) { name = name, color = color };
            SetColorIfPresent(material, "_BaseColor", color);
            SetColorIfPresent(material, "_UnlitColor", color);
            SetColorIfPresent(material, "_Color", color);
            if (emissive)
            {
                // Only _EmissiveColor is read by the HDRP shader - see the note
                // on CCTVCameraLensBlinker's emissive constants. The keyword,
                // the white emissive map and the built-in-RP _EmissionColor
                // name mirror what InteriorAlarmFixture and CCTVScanGlowManager
                // already do successfully in this repo; _EmissionColor also
                // covers the non-HDRP fallback shaders in the Shader.Find chain
                // above. Keywords cannot be set from a MaterialPropertyBlock,
                // so they have to live on the shared material here.
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissiveColorMap"))
                    material.SetTexture("_EmissiveColorMap", Texture2D.whiteTexture);
                SetColorIfPresent(material, "_EmissiveColor", color * 6f);
                SetColorIfPresent(material, "_EmissionColor", color * 6f);
            }
            return material;
        }

        private static void SetColorIfPresent(Material material, string property, Color color)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, color);
        }

        private static void SetFloatIfPresent(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void SetTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static float ResolveScale()
        {
            float value = SurveillanceBootstrap.Config?.PhysicalCameraVisualScale?.Value ?? 1f;
            return Mathf.Clamp(value, 0.05f, 10f);
        }

        private static float ResolveMaxSnapOffset()
        {
            float value = SurveillanceBootstrap.Config?.PhysicalCameraVisualMaxSnapOffsetM ?? 0.9f;
            return Mathf.Clamp(value, MinSnapOffsetM, MaxSnapOffsetCeilingM);
        }

        // Shared verbose gate for the camera-visual family (visual build, breakable
        // hitbox). Null-safe on the entries as well as the config: this is called from
        // inside IHittable.Hit, where a throw would escape into vanilla melee code.
        internal static bool ShouldLogVerbose()
        {
            return SurveillanceBootstrap.Config?.PlacementDebugLoggingEnabled?.Value == true
                || SurveillanceBootstrap.Config?.ReconLoggingEnabled?.Value == true;
        }

        private static string Describe(CCTVCamera holder)
        {
            if (holder == null) return "<null>";
            return $"{holder.ResolvedLabel}#{holder.CameraIndex} tile='{holder.OwningTile?.name ?? "none"}'";
        }

    }
}
