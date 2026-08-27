using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Phase 1.7 — night-vision shader loader. The shader is a custom HDRP-tagged
    /// ShaderLab + HLSL source (Shaders/NightVision.shader). HDRP custom shaders cannot
    /// be compiled at C# runtime; the source must be precompiled in a Unity Editor with
    /// HDRP installed and packaged as <c>lethalcctv-nightvision.shaderbundle</c>, dropped
    /// next to the active plugin DLL in the BepInEx plugins directory.
    ///
    /// If the bundle is missing or fails to load, every quad keeps the vanilla
    /// HDRP/Unlit-cloned material — display still works, no night-vision treatment.
    /// This is by design (graceful fallback per Phase 1.7 SPEC item #2).
    ///
    /// 0.0.7: the shader now exposes <c>_MainTex</c> (renamed from <c>_UnlitColorMap</c>)
    /// so Graphics.Blit's automatic source-as-_MainTex binding feeds the fragment
    /// sampler correctly when the bake pass runs from endCameraRendering. The
    /// wall-side fallback path (when the bundle fails to load) is unaffected because
    /// the wall material reverts to plain HDRP/Unlit, which has its own _UnlitColorMap.
    /// </summary>
    internal static class NightVisionShader
    {
        private const string BUNDLE_FILE_NAME = "lethalcctv-nightvision.shaderbundle";
        private const string SHADER_NAME = "LethalCCTV/NightVision";

        // Property IDs cached at first use. SetFloat with an ID is faster than by-name
        // and the by-name SetFloat in QuadMonitor.ApplyNightVisionParams runs every time
        // the config sliders move.
        internal static readonly int GainPropertyId = Shader.PropertyToID("_Gain");
        internal static readonly int GrayscaleEnabledPropertyId = Shader.PropertyToID("_GrayscaleEnabled");
        internal static readonly int FlipYPropertyId = Shader.PropertyToID("_FlipY");
        internal static readonly int TintPropertyId = Shader.PropertyToID("_Tint");
        internal static readonly int VhsPhasePropertyId = Shader.PropertyToID("_VhsPhase");

        // 0.0.11 — the analog-artifact block. The first four existed in the shader
        // since 0.0.8 but nothing on the C# side ever wrote them, so they ran on
        // their ShaderLab defaults; the last six are new in the 0.0.11 bundle.
        // QuadMonitor.ApplyNightVisionParams guards every write with
        // Material.HasProperty, so this file is safe to ship against an older
        // bundle: the six that do not exist there are simply skipped.
        internal static readonly int ColorRetentionPropertyId = Shader.PropertyToID("_ColorRetention");
        internal static readonly int ScanlineStrengthPropertyId = Shader.PropertyToID("_ScanlineStrength");
        internal static readonly int NoiseStrengthPropertyId = Shader.PropertyToID("_NoiseStrength");
        internal static readonly int VignetteStrengthPropertyId = Shader.PropertyToID("_VignetteStrength");
        internal static readonly int ChromaAberrationPropertyId = Shader.PropertyToID("_ChromaAberration");
        internal static readonly int RollBarStrengthPropertyId = Shader.PropertyToID("_RollBarStrength");
        internal static readonly int RollBarSpeedPropertyId = Shader.PropertyToID("_RollBarSpeed");
        internal static readonly int CurvatureStrengthPropertyId = Shader.PropertyToID("_CurvatureStrength");
        internal static readonly int InterlaceStrengthPropertyId = Shader.PropertyToID("_InterlaceStrength");
        internal static readonly int DropoutStrengthPropertyId = Shader.PropertyToID("_DropoutStrength");

        private static bool _loadAttempted;
        private static Shader _shader;
        private static AssetBundle _bundle;

        /// <summary>
        /// True once a load attempt has succeeded with a non-null shader. False before any
        /// attempt or after a failed attempt (logged with remediation).
        /// </summary>
        internal static bool IsAvailable => _shader != null;

        /// <summary>
        /// One-shot load from disk. Idempotent — repeated calls after a failed first load
        /// do not retry (a missing bundle won't reappear at runtime; retrying is wasted
        /// IO and produces duplicate log spam).
        /// </summary>
        internal static Shader TryLoad()
        {
            if (_loadAttempted) return _shader;
            _loadAttempted = true;

            try
            {
                string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dllDir))
                {
                    SurveillanceBootstrap.Log.LogWarning("[LethalCCTV][NightVision] Cannot resolve plugin directory — skipping shader load. Night-vision will be unavailable; quads keep vanilla HDRP/Unlit.");
                    return null;
                }
                string bundlePath = Path.Combine(dllDir, BUNDLE_FILE_NAME);
                if (!File.Exists(bundlePath))
                {
                    SurveillanceBootstrap.Log.LogWarning(
                        $"[LethalCCTV][NightVision] Shader bundle not found at '{bundlePath}'. " +
                        $"Night-vision treatment will be unavailable; quads keep vanilla HDRP/Unlit. " +
                        $"To enable: compile Shaders/NightVision.shader into '{BUNDLE_FILE_NAME}' " +
                        $"via a Unity Editor with HDRP installed and drop it next to the active plugin DLL.");
                    return null;
                }

                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    SurveillanceBootstrap.Log.LogError(
                        $"[LethalCCTV][NightVision] AssetBundle.LoadFromFile returned null for '{bundlePath}'. " +
                        $"Bundle may be corrupt or built against an incompatible Unity version. " +
                        $"Night-vision treatment will be unavailable.");
                    return null;
                }

                // Unity bundles index assets by lowercased asset path
                // ("assets/lethalcctv/nightvision.shader"), not by the
                // Shader "..." declaration name. Walk LoadAllAssets and
                // match on Shader.name — robust to both build conventions
                // and to any future folder reorganisation.
                Shader loaded = null;
                UnityEngine.Object[] all = _bundle.LoadAllAssets(typeof(Shader));
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        Shader candidate = all[i] as Shader;
                        if (candidate != null && candidate.name == SHADER_NAME)
                        {
                            loaded = candidate;
                            break;
                        }
                    }
                    if (loaded == null && all.Length > 0)
                    {
                        // Lone-shader-in-bundle fallback: if there's exactly
                        // one Shader and its name doesn't match, log what we
                        // actually found so a mis-tagged bundle is diagnosable.
                        Shader only = all[0] as Shader;
                        if (only != null && all.Length == 1)
                        {
                            SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV][NightVision] Bundle contains exactly one Shader but its name '{only.name}' != expected '{SHADER_NAME}'. Accepting it anyway — verify Shaders/NightVision.shader's top-line declaration.");
                            loaded = only;
                        }
                    }
                }
                if (loaded == null)
                {
                    SurveillanceBootstrap.Log.LogError(
                        $"[LethalCCTV][NightVision] Bundle loaded but no Shader matching name '{SHADER_NAME}' found inside. " +
                        $"Bundle assets: [{string.Join(", ", _bundle.GetAllAssetNames())}]. " +
                        $"Night-vision treatment will be unavailable.");
                    return null;
                }

                _shader = loaded;
                SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV][NightVision] Shader '{SHADER_NAME}' loaded from '{bundlePath}'.");
                return _shader;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogError($"[LethalCCTV][NightVision] Load threw: {ex.GetType().Name} — {ex.Message}. Night-vision unavailable.");
                return null;
            }
        }

        // Phase 1.7b removed the display-side shader-swap entry point.
        // Night-vision is now baked at the RT writer side via NightVisionBaker —
        // the wall material stays plain HDRP/Unlit and consumes the pre-filtered
        // display RT. TryLoad above is the sole remaining entry, called by
        // NightVisionBaker.TryStart to acquire the shader for the bake material.
    }
}
