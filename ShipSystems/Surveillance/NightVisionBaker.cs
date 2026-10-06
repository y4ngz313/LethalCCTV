using System;
using System.Collections.Generic;
using Y4NGZCompany.Facility.Cameras;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Rendering;
using Y4NGZCore.Diagnostics;

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
        private const float AgcSampleIntervalSeconds = 0.5f;
        private const int AgcProbeSize = 16;

        /// <summary>Source renders of a feed, after an overlay effect starts on it, that FeedGuard meters.</summary>
        internal const int FeedGuardSourceRenders = 2;

        /// <summary>A composited feed whose meter falls below this fraction of the expected level has collapsed…</summary>
        internal const float FeedGuardCollapseFraction = 0.25f;

        /// <summary>…provided it is also below this absolute linear level, so a genuinely dark room never trips.</summary>
        internal const float FeedGuardAbsoluteCeiling = 0.02f;

        private sealed class AgcState
        {
            public readonly CctvExposure Exposure = new CctvExposure();
            public float NextSampleAt;
            public float LastBakeAt;
            public bool RequestPending;
            public bool HasMeterSample;
            public readonly float[] Luminance = new float[AgcProbeSize * AgcProbeSize];
            public readonly Action<AsyncGPUReadbackRequest> Completed;

            // FeedGuard (#1219 G3): the overlay start this slot last saw, the lease it armed on, the
            // source renders left to meter, the level the composite should read, and one sample per
            // metered render so both can be in flight at once.
            public int ObservedOverlayGeneration;
            public CameraRenderLease GuardLease;
            public int GuardRendersRemaining;
            public float GuardExpected;
            public readonly List<GuardSample> GuardSamples = new List<GuardSample>(2) { new GuardSample(), new GuardSample() };

            public AgcState()
            {
                Completed = request => CompleteAgcSample(this, request);
            }
        }

        /// <summary>
        /// One FeedGuard readback of a composited feed. The lease, source frame and render index are
        /// captured when the sample is requested, so a readback that completes frames later is judged
        /// against the frame it measured, not the frame it arrived on.
        /// </summary>
        private sealed class GuardSample
        {
            public readonly CctvExposure Meter = new CctvExposure();
            public readonly float[] Luminance = new float[AgcProbeSize * AgcProbeSize];
            public readonly Action<AsyncGPUReadbackRequest> Completed;
            public bool Pending;
            public CameraRenderLease Lease;
            public int SourceFrame;
            public int SourceRender;
            public float Expected;

            public GuardSample()
            {
                Completed = request => CompleteGuardSample(this, request);
            }
        }

        private static bool _subscribed;
        private static Material _bakeMat;
        private static readonly Dictionary<Camera, int> _camToSlot = new Dictionary<Camera, int>(4);
        private static readonly Dictionary<int, AgcState> _agcBySlot = new Dictionary<int, AgcState>(4);
        private static readonly HashSet<string> _feedGuardLogged = new HashSet<string>(StringComparer.Ordinal);
        private static RenderTexture _agcProbeRT;
        private static CCTVCamera _activeFillLightHolder;

        internal static bool IsActive => _bakeMat != null;
        internal static Material BakeMaterial => _bakeMat;

        /// <summary>
        /// One-shot start. Loads the shader bundle (via NightVisionShader.TryLoad),
        /// creates the shared bake Material, and subscribes the handler to
        /// begin/endCameraRendering. Returns true when the bake is available.
        /// Without the shader, the handler copies the raw source to the separate
        /// display target before compositing overlays and running FeedGuard.
        /// </summary>
        internal static bool TryStart()
        {
            if (_subscribed) return _bakeMat != null;

            Shader sh = NightVisionShader.TryLoad();
            if (sh != null)
            {
                _bakeMat = new Material(sh)
                {
                    name = "LethalCCTV_NightVisionBakeMat",
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            // else: TryLoad already logged the remediation; feeds take the raw path.

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            _subscribed = true;
            SurveillanceBootstrap.Log.LogInfo(_bakeMat != null
                ? "[LethalCCTV][NightVisionBaker] Subscribed to RenderPipelineManager begin/end camera rendering; bake material allocated."
                : "[LethalCCTV][NightVisionBaker] Subscribed without a bake material; raw source copies and overlays use separate display targets.");
            return _bakeMat != null;
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
            // The fill light belongs to the night-vision bake; the raw path never lit it.
            if (cam == null || _bakeMat == null) return;
            if (!_camToSlot.ContainsKey(cam)) return;
            CCTVCamera holder = cam.GetComponent<CCTVCamera>();
            if (holder == null) return;
            holder.SetNightVisionFillLightActive(true);
            _activeFillLightHolder = holder;
        }

        /// <summary>
        /// Register a CCTV camera under its slot index. Called from BindCameraToSlot
        /// immediately after cam.targetTexture is set so the next endCameraRendering
        /// fire bakes (or, on the raw path, overlays) the correct slot. Silent no-op
        /// before TryStart, so callers do not need to guard.
        /// </summary>
        internal static void Register(Camera cam, int slot)
        {
            if (!_subscribed || cam == null) return;
            if (!_camToSlot.TryGetValue(cam, out int previous) || previous != slot)
                _agcBySlot.Remove(slot);
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

                RenderTexture[] disp = QuadMonitor.QuadRTs;
                if (disp == null || slot < 0 || slot >= disp.Length) return;
                RenderTexture dst = disp[slot];
                if (dst == null) return;

                RenderTexture[] raw = QuadMonitor.RawRTs;
                RenderTexture src = raw != null && slot < raw.Length ? raw[slot] : null;
                if (src == null) return;
                if (_bakeMat == null)
                {
                    // The display must not also be HDRP's camera target: its final
                    // write can land after this callback and erase the overlay.
                    AgcState rawState = ResolveExposure(slot, src);
                    Graphics.Blit(src, dst);
                    OutlineEffect.OutlineEffect.CompositeFeedOverlay(cam, dst);
                    GuardFeed(cam, rawState, dst, 1f);
                    return;
                }

                // Graphics.Blit auto-binds `src` to the material's _MainTex and drives
                // blit UVs against it — the shader samples _MainTex.
                // Same HasProperty discipline as QuadMonitor.ApplyNightVisionParams:
                // both knobs are shader-bundle-versioned, so against an older
                // bundle the write is skipped rather than logged or thrown. The
                // gain is resolved unconditionally so the AGC readback keeps
                // ticking regardless of which bundle is loaded.
                AgcState state = ResolveExposure(slot, src);
                float gain = state.Exposure.Gain;
                LethalCCTVConfig cfg = SurveillanceBootstrap.Config;
                bool nightVision = cfg == null || cfg.NightVisionEnabled.Value;
                float lowLight = nightVision ? state.Exposure.NightBlend : 0f;
                float retention = Mathf.Lerp(1f, cfg?.FeedColorRetention.Value ?? 0.08f, lowLight);
                if (_bakeMat.HasProperty(NightVisionShader.NightVisionBlendPropertyId))
                    _bakeMat.SetFloat(NightVisionShader.NightVisionBlendPropertyId, lowLight);
                if (_bakeMat.HasProperty(NightVisionShader.GrayscaleEnabledPropertyId))
                    _bakeMat.SetFloat(NightVisionShader.GrayscaleEnabledPropertyId, nightVision ? 1f : 0f);
                if (_bakeMat.HasProperty(NightVisionShader.ColorRetentionPropertyId))
                    _bakeMat.SetFloat(NightVisionShader.ColorRetentionPropertyId, retention);
                if (_bakeMat.HasProperty(NightVisionShader.VhsPhasePropertyId))
                    _bakeMat.SetFloat(NightVisionShader.VhsPhasePropertyId, Time.unscaledTime);
                if (_bakeMat.HasProperty(NightVisionShader.GainPropertyId))
                    _bakeMat.SetFloat(NightVisionShader.GainPropertyId, gain);
                Graphics.Blit(src, dst, _bakeMat, 0);

                // #1219 G1: the squad-ping outline is an overlay on the baked image, drawn with the
                // feed camera's matrices outside HDRP. The raw render and its frame settings are
                // never touched, so a ping cannot black out the background.
                OutlineEffect.OutlineEffect.CompositeFeedOverlay(cam, dst);
                GuardFeed(cam, state, dst, gain);
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

        private static AgcState ResolveExposure(int slot, RenderTexture src)
        {
            LethalCCTVConfig cfg = SurveillanceBootstrap.Config;
            if (!_agcBySlot.TryGetValue(slot, out AgcState state))
            {
                state = new AgcState { LastBakeAt = Time.unscaledTime };
                _agcBySlot[slot] = state;
            }
            if (!state.RequestPending && Time.unscaledTime >= state.NextSampleAt &&
                SystemInfo.supportsAsyncGPUReadback)
            {
                state.RequestPending = true;
                state.NextSampleAt = Time.unscaledTime + AgcSampleIntervalSeconds;
                SampleLuminance(src, state);
            }
            float now = Time.unscaledTime;
            state.Exposure.Tick(now - state.LastBakeAt, cfg?.NightVisionGain.Value ?? 4f,
                cfg == null || cfg.NightVisionEnabled.Value,
                SystemInfo.supportsAsyncGPUReadback && (cfg == null || cfg.NightVisionAutomaticLowLight.Value),
                SystemInfo.supportsAsyncGPUReadback && (cfg == null || cfg.NightVisionAutoGain.Value));
            state.LastBakeAt = now;
            return state;
        }

        private static RenderTexture EnsureProbe()
        {
            if (_agcProbeRT == null)
            {
                var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                    ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                _agcProbeRT = new RenderTexture(AgcProbeSize, AgcProbeSize, 0, format, RenderTextureReadWrite.Linear)
                {
                    name = "LethalCCTV_NightVisionAgcProbe",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _agcProbeRT.Create();
            }
            return _agcProbeRT;
        }

        private static void SampleLuminance(RenderTexture src, AgcState state)
        {
            try
            {
                // Explicitly linear probe and float readback: no guessed sRGB decode. A readback
                // captures the probe at request time, so AGC and guard samples can share it.
                RenderTexture probe = EnsureProbe();
                Graphics.Blit(src, probe);
                AsyncGPUReadback.Request(probe, 0, TextureFormat.RGBAFloat, state.Completed);
            }
            catch { state.RequestPending = false; }
        }

        private static void CompleteAgcSample(AgcState state, AsyncGPUReadbackRequest request)
        {
            state.RequestPending = false;
            if (!ReadLuminance(request, state.Luminance)) return;
            state.Exposure.Measure(state.Luminance);
            state.HasMeterSample = true;
        }

        private static bool ReadLuminance(AsyncGPUReadbackRequest request, float[] luminance)
        {
            if (request.hasError) return false;
            var pixels = request.GetData<Color>();
            if (pixels.Length != luminance.Length) return false;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color p = pixels[i];
                luminance[i] = Mathf.Max(0f, 0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b);
            }
            return true;
        }

        /// <summary>
        /// FeedGuard (#1219 G3). Feed cameras refuse settings leases, so every effect on a feed is an
        /// overlay lease, and each one that starts is seen here as a new
        /// <see cref="CameraRenderProfile.OverlayGeneration"/>. The next
        /// <see cref="FeedGuardSourceRenders"/> renders of that feed are metered after the composite
        /// with the AGC meter; a collapse revokes the lease (suppressing the effect on this feed until
        /// it is rebound) and logs once per effect.
        /// </summary>
        private static void GuardFeed(Camera cam, AgcState state, RenderTexture composited, float gain)
        {
            int generation = CameraRenderProfile.OverlayGeneration(cam);
            if (generation != state.ObservedOverlayGeneration)
            {
                state.ObservedOverlayGeneration = generation;
                CameraRenderLease started = CameraRenderProfile.NewestOverlay(cam);
                // The exposure initializer is not an observed pre-effect baseline. On a new
                // binding, a naturally dark first frame must not permanently suppress the effect.
                if (started != null && state.HasMeterSample)
                {
                    state.GuardLease = started;
                    state.GuardRendersRemaining = FeedGuardSourceRenders;
                    // What the baked feed reads without the effect: the raw meter through the gain.
                    state.GuardExpected = state.Exposure.Meter * gain;
                }
            }

            if (state.GuardRendersRemaining <= 0) return;
            CameraRenderLease lease = state.GuardLease;
            if (lease == null || !lease.IsActive || !SystemInfo.supportsAsyncGPUReadback)
            {
                state.GuardRendersRemaining = 0;
                state.GuardLease = null;
                return;
            }

            int render = FeedGuardSourceRenders - state.GuardRendersRemaining + 1;
            state.GuardRendersRemaining--;
            if (state.GuardRendersRemaining == 0) state.GuardLease = null;

            GuardSample sample = null;
            for (int i = 0; i < state.GuardSamples.Count; i++)
            {
                if (state.GuardSamples[i].Pending) continue;
                sample = state.GuardSamples[i];
                break;
            }
            if (sample == null)
            {
                // Grow only for a new in-flight peak; retain every context so
                // completed captures can serve later overlapping generations.
                sample = new GuardSample();
                state.GuardSamples.Add(sample);
            }
            try
            {
                sample.Pending = true;
                sample.Lease = lease;
                sample.SourceFrame = Time.frameCount;
                sample.SourceRender = render;
                sample.Expected = state.GuardExpected;
                RenderTexture probe = EnsureProbe();
                Graphics.Blit(composited, probe);
                AsyncGPUReadback.Request(probe, 0, TextureFormat.RGBAFloat, sample.Completed);
            }
            catch
            {
                sample.Pending = false;
                sample.Lease = null;
            }
        }

        private static void CompleteGuardSample(GuardSample sample, AsyncGPUReadbackRequest request)
        {
            sample.Pending = false;
            CameraRenderLease lease = sample.Lease;
            sample.Lease = null;
            if (lease == null || !lease.IsActive || !ReadLuminance(request, sample.Luminance)) return;

            sample.Meter.Measure(sample.Luminance);
            float meter = sample.Meter.Meter;
            if (!IsFeedCollapsed(meter, sample.Expected) || !CameraRenderProfile.Revoke(lease)) return;
            if (!_feedGuardLogged.Add(lease.Effect)) return;

            string cameraName = lease.Camera != null ? lease.Camera.name : "<destroyed>";
            ModuleLog.ShipSystems.LogWarning(
                $"[LethalCCTV][FeedGuard] effect={lease.Effect} meter={meter:F4} expected={sample.Expected:F4} " +
                $"camera={cameraName} sampleFrame={sample.SourceFrame} render={sample.SourceRender}/{FeedGuardSourceRenders} " +
                $"startFrame={lease.StartFrame}; effect revoked on this feed until it is rebound.");
        }

        /// <summary>
        /// True when a composited feed's meter has collapsed: below
        /// <see cref="FeedGuardCollapseFraction"/> of <paramref name="expectedMeter"/> and below
        /// <see cref="FeedGuardAbsoluteCeiling"/>.
        /// </summary>
        internal static bool IsFeedCollapsed(float compositeMeter, float expectedMeter)
        {
            return compositeMeter < FeedGuardAbsoluteCeiling && compositeMeter < expectedMeter * FeedGuardCollapseFraction;
        }
    }
}
