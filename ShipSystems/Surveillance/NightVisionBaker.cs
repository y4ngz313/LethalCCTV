using System.Collections.Generic;
using Y4NGZCompany.Facility.Cameras;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Phase 1.7b — RT-writer-side night-vision bake. Subscribes to
    /// RenderPipelineManager.endCameraRendering and, for each registered CCTV
    /// camera, blits its raw render output through the night-vision material
    /// into the slot's display RT. Wall and focus overlay then both consume
    /// the pre-filtered display RT — single filter path, no per-consumer shader.
    ///
    /// Lifetime is a strict chokepoint: subscribe exactly once via TryStart,
    /// unsubscribe via Shutdown from every teardown site (Despawn,
    /// CleanupPartialConstruction, SurveillanceBootstrap.OnDestroy). Shutdown is idempotent
    /// and unconditional — delegate -= for an unsubscribed handler is a CLR
    /// no-op, so calling Shutdown twice or on a never-started baker is safe.
    /// </summary>
    internal static class NightVisionBaker
    {
        // AGC (#569): the CCTV cameras render with ExposureControl and Postprocess
        // stripped, so a lit vanilla interior arrives near-clipped in the raw RT
        // and a fixed 2.0x gain turns it solid white. Each slot therefore measures
        // its raw feed's average luminance on a slow cadence (async GPU readback of
        // an 8x8 downsample) and scales the gain toward a mid-gray target, clamped
        // to [MIN, configured gain]: dim interiors keep the full configured boost,
        // lit interiors attenuate below 1.0 instead of clipping.
        private const float AgcSampleIntervalSeconds = 0.5f;
        private const float AgcTargetLinearLuma = 0.18f;
        private const float AgcMinGain = 0.35f;
        private const float AgcSmoothing = 0.35f;
        private const int AgcProbeSize = 8;

        private sealed class AgcState
        {
            public float Gain = 1f;
            public float NextSampleAt;
            public bool RequestPending;
        }

        private static bool _subscribed;
        private static Material _bakeMat;
        private static readonly Dictionary<Camera, int> _camToSlot = new Dictionary<Camera, int>(4);
        private static readonly Dictionary<int, AgcState> _agcBySlot = new Dictionary<int, AgcState>(4);
        private static RenderTexture _agcProbeRT;
        private static CCTVCamera _activeFillLightHolder;

        internal static bool IsActive => _bakeMat != null;
        internal static Material BakeMaterial => _bakeMat;

        /// <summary>
        /// One-shot start. Loads the shader bundle (via NightVisionShader.TryLoad),
        /// creates the shared bake Material, and subscribes the bake handler to
        /// endCameraRendering. Returns true on success; returns false when the
        /// bundle is missing or the shader fails to load (caller must leave
        /// RawRTs null and route cameras directly to the display RTs — graceful
        /// fallback).
        /// </summary>
        internal static bool TryStart()
        {
            if (_subscribed) return _bakeMat != null;

            Shader sh = NightVisionShader.TryLoad();
            if (sh == null)
            {
                // TryLoad already logged the remediation. Caller falls through to raw path.
                return false;
            }

            _bakeMat = new Material(sh)
            {
                name = "LethalCCTV_NightVisionBakeMat",
                hideFlags = HideFlags.HideAndDontSave,
            };

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            _subscribed = true;
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV][NightVisionBaker] Subscribed to RenderPipelineManager begin/end camera rendering; bake material allocated.");
            return true;
        }

        /// <summary>
        /// Unconditional, idempotent teardown chokepoint. Called from every
        /// destruction path (QuadMonitor.Despawn, CleanupPartialConstruction,
        /// SurveillanceBootstrap.OnDestroy). The -= runs regardless of _subscribed state — the
        /// CLR treats delegate -= for a non-registered handler as a no-op, which
        /// closes the "subscribe threw before flag flipped" window.
        /// </summary>
        internal static void Shutdown()
        {
            DisableActiveFillLight();
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            _subscribed = false;
            _camToSlot.Clear();
            _agcBySlot.Clear();
            if (_agcProbeRT != null)
            {
                _agcProbeRT.Release();
                UnityEngine.Object.Destroy(_agcProbeRT);
                _agcProbeRT = null;
            }
            if (_bakeMat != null)
            {
                UnityEngine.Object.Destroy(_bakeMat);
                _bakeMat = null;
            }
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext _, Camera cam)
        {
            DisableActiveFillLight();
            if (cam == null) return;
            if (!_camToSlot.ContainsKey(cam)) return;

            CCTVCamera holder = cam.GetComponent<CCTVCamera>();
            if (holder == null) return;
            holder.SetNightVisionFillLightActive(true);
            _activeFillLightHolder = holder;
        }

        /// <summary>
        /// Register a CCTV camera under its slot index. Called from BindCameraToSlot
        /// immediately after cam.targetTexture is set so the next endCameraRendering
        /// fire bakes the correct slot. Silent no-op when the baker is inactive
        /// (bundle missing), so callers do not need to guard.
        /// </summary>
        internal static void Register(Camera cam, int slot)
        {
            if (!IsActive || cam == null) return;
            _camToSlot[cam] = slot;
        }

        /// <summary>
        /// Remove a camera from the bake map. Called from TearDownSlot BEFORE the
        /// camera's targetTexture is nulled, so the bake handler can never fire on
        /// a half-torn-down camera. Silent no-op for un-mapped or null cameras.
        /// </summary>
        internal static void Unregister(Camera cam)
        {
            if (cam == null) return;
            _camToSlot.Remove(cam);
        }

        /// <summary>
        /// Clear every dictionary entry. Called from QuadCameraAssignment.Assign /
        /// Unassign as a structural leak-prevention sweep — round-to-round the
        /// CCTV camera GameObjects are destroyed with the dungeon and replaced
        /// wholesale, so stale Unity-object keys would accumulate without this.
        /// </summary>
        internal static void ClearRegistrations()
        {
            _camToSlot.Clear();
            // Slots rebind to different cameras/interiors wholesale; drop the
            // measured gains so a bright slot's history never dims a dark one.
            _agcBySlot.Clear();
        }

        // endCameraRendering is process-wide: fires for the player camera, sky
        // probes, reflection probes, OBC's bodycam, and our CCTV cameras. Every
        // entry checks the dictionary; misses (~all of them) early-return after
        // one Dictionary.TryGetValue. Every Unity reference is null-checked —
        // a destroyed-but-not-yet-unregistered camera is the exact deref window
        // teardown-ordering bugs would produce.
        private static void OnEndCameraRendering(ScriptableRenderContext _, Camera cam)
        {
            try
            {
                if (cam == null) return;
                if (!_camToSlot.TryGetValue(cam, out int slot)) return;
                if (_bakeMat == null) return;

                RenderTexture[] raw = QuadMonitor.RawRTs;
                RenderTexture[] disp = QuadMonitor.QuadRTs;
                if (raw == null || disp == null) return;
                if (slot < 0 || slot >= raw.Length || slot >= disp.Length) return;

                RenderTexture src = raw[slot];
                RenderTexture dst = disp[slot];
                if (src == null || dst == null) return;

                // Graphics.Blit auto-binds `src` to the material's _MainTex and drives
                // blit UVs against it — the shader samples _MainTex.
                // Same HasProperty discipline as QuadMonitor.ApplyNightVisionParams:
                // both knobs are shader-bundle-versioned, so against an older
                // bundle the write is skipped rather than logged or thrown. The
                // gain is resolved unconditionally so the AGC readback keeps
                // ticking regardless of which bundle is loaded.
                float gain = ResolveGain(slot, src);
                if (_bakeMat.HasProperty(NightVisionShader.VhsPhasePropertyId))
                    _bakeMat.SetFloat(NightVisionShader.VhsPhasePropertyId, Time.unscaledTime);
                if (_bakeMat.HasProperty(NightVisionShader.GainPropertyId))
                    _bakeMat.SetFloat(NightVisionShader.GainPropertyId, gain);
                Graphics.Blit(src, dst, _bakeMat, 0);
            }
            finally
            {
                DisableActiveFillLight();
            }
        }

        private static void DisableActiveFillLight()
        {
            if (_activeFillLightHolder != null)
            {
                _activeFillLightHolder.SetNightVisionFillLightActive(false);
                _activeFillLightHolder = null;
            }
        }

        /// <summary>
        /// Per-slot effective gain for this bake. With auto-gain off (or async GPU
        /// readback unsupported) this is the configured gain, i.e. the pre-1.0
        /// behavior. With it on, the configured gain becomes the CEILING and the
        /// slot's measured average luminance steers the actual value.
        /// </summary>
        private static float ResolveGain(int slot, RenderTexture src)
        {
            LethalCCTVConfig cfg = SurveillanceBootstrap.Config;
            float configGain = cfg != null ? cfg.NightVisionGain.Value : 2.0f;
            bool auto = cfg == null || cfg.NightVisionAutoGain.Value;
            if (!auto || !SystemInfo.supportsAsyncGPUReadback)
                return configGain;

            if (!_agcBySlot.TryGetValue(slot, out AgcState state))
            {
                state = new AgcState { Gain = Mathf.Min(configGain, 1f) };
                _agcBySlot[slot] = state;
            }

            if (!state.RequestPending && Time.unscaledTime >= state.NextSampleAt)
            {
                state.RequestPending = true;
                state.NextSampleAt = Time.unscaledTime + AgcSampleIntervalSeconds;
                SampleAverageLuma(src, state, configGain);
            }

            return Mathf.Clamp(state.Gain, AgcMinGain, configGain);
        }

        private static void SampleAverageLuma(RenderTexture src, AgcState state, float configGain)
        {
            try
            {
                if (_agcProbeRT == null)
                {
                    _agcProbeRT = new RenderTexture(AgcProbeSize, AgcProbeSize, 0, RenderTextureFormat.ARGB32)
                    {
                        name = "LethalCCTV_NightVisionAgcProbe",
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    _agcProbeRT.Create();
                }

                // Plain blit (bilinear downsample); the readback copies the probe's
                // contents at request time, so back-to-back slots sharing the probe
                // cannot race each other's data.
                Graphics.Blit(src, _agcProbeRT);
                UnityEngine.Rendering.AsyncGPUReadback.Request(_agcProbeRT, 0, TextureFormat.RGBA32, request =>
                {
                    state.RequestPending = false;
                    if (request.hasError) return;
                    var pixels = request.GetData<Color32>();
                    if (pixels.Length == 0) return;

                    float sum = 0f;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Color32 p = pixels[i];
                        float encoded = (0.299f * p.r + 0.587f * p.g + 0.114f * p.b) / 255f;
                        // Approximate sRGB decode; the gain multiply happens in
                        // linear space in the shader, so steer in linear too.
                        sum += Mathf.Pow(encoded, 2.2f);
                    }
                    float averageLinear = sum / pixels.Length;
                    float desired = Mathf.Clamp(
                        AgcTargetLinearLuma / Mathf.Max(averageLinear, 0.001f),
                        AgcMinGain,
                        configGain);
                    state.Gain = Mathf.Lerp(state.Gain, desired, AgcSmoothing);
                });
            }
            catch
            {
                state.RequestPending = false;
            }
        }
    }
}
