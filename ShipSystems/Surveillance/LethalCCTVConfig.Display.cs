using System;
using BepInEx.Configuration;
using CSync.Extensions;
using CSync.Lib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    public partial class LethalCCTVConfig
    {
        private void BindDisplay(ConfigFile cfg)
        {
            // === Phase 1.7 — Display ===
            // Display-side night-vision treatment applied to the per-quad clone material's
            // shader. Bool toggle bypasses the grayscale step (gain still applies); set false
            // to see raw CCTV colour. Per-client aesthetic; not synced. Hot-reloadable via
            // NightVisionParamsChanged → QuadMonitor.ApplyNightVisionParams.
            NightVisionEnabled = cfg.Bind(
                DisplaySection,
                "Night Vision Enabled",
                true,
                "If true, CCTV feeds are rendered grayscale (Rec.601 luminance) with the configured brightness gain — the security-camera 'night vision' aesthetic, and the practical fix for dim dungeon interiors that would otherwise display as near-black. If false, only the gain multiplier applies (colour preserved). Per-client; not synced. Hot-reloadable.");

            NightVisionGain = cfg.Bind(
                DisplaySection,
                "Night Vision Gain",
                2.0f,
                "Brightness multiplier applied after the (optional) grayscale step, saturated to [0,1] after multiply. Default 2.0 brightens dim interiors without crushing the highlights of already-lit feeds. Range: 0.5–8.0. Per-client; not synced. Hot-reloadable.");

            NightVisionAutoGain = cfg.Bind(
                DisplaySection,
                "Night Vision Auto Gain",
                true,
                "If true, each CCTV feed measures its own average brightness and scales the night-vision gain like a real camera's AGC: dim interiors are boosted up to the configured Night Vision Gain ceiling, while brightly lit interiors are attenuated so they no longer clip to solid white (the fixed 2.0x gain white-out on lit vanilla interiors, #569). If false, the configured gain applies unconditionally (the pre-1.0 behavior). Per-client; not synced. Hot-reloadable.");

            NightVisionFlipY = cfg.Bind(
                DisplaySection,
                "Night Vision Flip Y",
                true,
                "If true, vertically flips the baked CCTV feed before it reaches the monitor. Direct3D/HDRP camera target textures can arrive upside-down through the Graphics.Blit bake path; leave enabled unless the feed appears vertically inverted. Per-client; not synced. Hot-reloadable.");

            // === Analog artifacts ===
            // These ten drive the analog-DVR treatment baked into every CCTV feed.
            // The first four existed in the shader from the start but were never
            // written from C#, so they ran on ShaderLab defaults; the last six ship
            // with the 0.0.11 bundle. Setting the five *Strength/Aberration knobs to
            // 0 reproduces the pre-0.0.11 look exactly. All are pushed behind a
            // Material.HasProperty guard, so an older bundle silently ignores any it
            // does not declare.
            FeedColorRetention = cfg.Bind(
                DisplaySection,
                "Feed Color Retention",
                0.35f,
                "How much of the original colour survives the grayscale step, 0 = fully monochrome, 1 = untouched colour. Only applies when Night Vision Enabled is true. Also sets the ceiling on the chromatic-aberration fringe: the RGB split is composed from three independently desaturated taps, so at 0 retention the fringe comes purely from the three taps' differing luminance and at higher retention it also carries real hue. Range: 0.0–1.0. Per-client; not synced. Hot-reloadable.");

            FeedScanlineStrength = cfg.Bind(
                DisplaySection,
                "Feed Scanline Strength",
                0.055f,
                "Depth of the CRT scanline darkening — a fine sine ripple plus slow-crawling coarse bands. 0 disables scanlines. Raise carefully: scanlines and Feed Interlace Strength both key off the 432 px feed height and can moiré against each other. Range: 0.0–1.0. Per-client; not synced. Hot-reloadable.");

            FeedNoiseStrength = cfg.Bind(
                DisplaySection,
                "Feed Noise Strength",
                0.018f,
                "Amplitude of the per-pixel analog grain, re-rolled 24 times a second. 0 disables grain. Range: 0.0–1.0. Per-client; not synced. Hot-reloadable.");

            FeedVignetteStrength = cfg.Bind(
                DisplaySection,
                "Feed Vignette Strength",
                0.12f,
                "Radial darkening toward the edges of the feed. 0 disables the vignette. Range: 0.0–1.0. Per-client; not synced. Hot-reloadable.");

            FeedChromaAberration = cfg.Bind(
                DisplaySection,
                "Feed Chromatic Aberration",
                0.25f,
                "Radial RGB split, scaling with distance from the centre of the feed. At the default 0.25 the split at the frame corner is about 1.5 px on a 768x432 feed — a fringe on high-contrast edges rather than an obvious colour ghost. 0 disables it. Range: 0.0–4.0. Per-client; not synced. Hot-reloadable.");

            FeedRollBarStrength = cfg.Bind(
                DisplaySection,
                "Feed Roll Bar Strength",
                0.10f,
                "Brightness of the soft band that drifts through the frame, the way an unsynced analog capture rolls. 0 disables the band. Range: 0.0–1.0. Per-client; not synced. Hot-reloadable.");

            FeedRollBarSpeed = cfg.Bind(
                DisplaySection,
                "Feed Roll Bar Speed",
                0.12f,
                "How fast the roll bar drifts, in frame-heights per second — the default 0.12 takes roughly 8 seconds to cross the feed. Has no effect when Feed Roll Bar Strength is 0. Range: 0.0–2.0. Per-client; not synced. Hot-reloadable.");

            FeedCurvatureStrength = cfg.Bind(
                DisplaySection,
                "Feed Curvature Strength",
                0.12f,
                "CRT barrel warp. The image bulges outward and the corners fall off the tube, so they render black instead of smearing. 0 disables the warp and restores a flat, full-bleed frame. Lower this if the overlay text stack ends up sitting over blacked-out corners. Range: 0.0–1.0. Per-client; not synced. Hot-reloadable.");

            FeedInterlaceStrength = cfg.Bind(
                DisplaySection,
                "Feed Interlace Strength",
                0.06f,
                "Depth of the odd/even field flicker — alternating single-pixel rows dim, and which field is dimmed toggles about 12 times a second. 0 disables the flicker. Range: 0.0–1.0. Per-client; not synced. Hot-reloadable.");

            FeedDropoutStrength = cfg.Bind(
                DisplaySection,
                "Feed Dropout Strength",
                0.04f,
                "Density of sparse dead/hot pixel specks, re-rolled 8 times a second. At the default 0.04 roughly 25 four-pixel specks are on screen at any moment. 0 disables them. Range: 0.0–1.0. Per-client; not synced. Hot-reloadable.");

            CCTVFillLightEnabled = cfg.Bind(
                DisplaySection,
                "CCTV Fill Light Enabled",
                true,
                "If true, enables a scoped night-vision spot light only while an individual CCTV camera is rendering. This brightens geometry for the CCTV feed without leaving a persistent light in the dungeon for the player camera. Per-client; not synced. Hot-reloadable.");

            CCTVRenderFillLightEnabled = cfg.Bind(
                DisplaySection,
                "CCTV Render Fill Light Enabled",
                false,
                "Second-stage opt-in for the real scoped CCTV spot light. Default false keeps CCTV renders shader/exposure-only for performance; set true together with CCTV Fill Light Enabled if feeds are too dark and the extra lighting cost is acceptable. Per-client; not synced. Hot-reloadable.");

            CCTVFillLightIntensity = cfg.Bind(
                DisplaySection,
                "CCTV Fill Light Intensity",
                367.0f,
                "Intensity for the scoped CCTV fill light. Default mirrors the OpenBodyCams night-vision light baseline. Lower if feeds clip white; raise if they remain too dark. Per-client; not synced. Hot-reloadable.");

            CCTVFillLightRange = cfg.Bind(
                DisplaySection,
                "CCTV Fill Light Range",
                12.0f,
                "Range in meters for the scoped CCTV fill light. Default mirrors the OpenBodyCams night-vision range baseline and matches the existing short indoor CCTV far-clip target. Per-client; not synced. Hot-reloadable.");

            void RaiseNightVisionChanged(object _, EventArgs __) => NightVisionParamsChanged?.Invoke();
            NightVisionEnabled.SettingChanged += RaiseNightVisionChanged;
            NightVisionGain.SettingChanged += RaiseNightVisionChanged;
            NightVisionFlipY.SettingChanged += RaiseNightVisionChanged;
            FeedColorRetention.SettingChanged += RaiseNightVisionChanged;
            FeedScanlineStrength.SettingChanged += RaiseNightVisionChanged;
            FeedNoiseStrength.SettingChanged += RaiseNightVisionChanged;
            FeedVignetteStrength.SettingChanged += RaiseNightVisionChanged;
            FeedChromaAberration.SettingChanged += RaiseNightVisionChanged;
            FeedRollBarStrength.SettingChanged += RaiseNightVisionChanged;
            FeedRollBarSpeed.SettingChanged += RaiseNightVisionChanged;
            FeedCurvatureStrength.SettingChanged += RaiseNightVisionChanged;
            FeedInterlaceStrength.SettingChanged += RaiseNightVisionChanged;
            FeedDropoutStrength.SettingChanged += RaiseNightVisionChanged;
            CCTVFillLightEnabled.SettingChanged += RaiseNightVisionChanged;
            CCTVRenderFillLightEnabled.SettingChanged += RaiseNightVisionChanged;
            CCTVFillLightIntensity.SettingChanged += RaiseNightVisionChanged;
            CCTVFillLightRange.SettingChanged += RaiseNightVisionChanged;
        }

        private void BindPhysicalCameras(ConfigFile cfg)
        {
            PhysicalCameraVisualsEnabled = cfg.Bind(
                PhysicalCamerasSection,
                "Physical Camera Visuals Enabled",
                true,
                "If true, spawns a visible wall/ceiling security-camera prop near each interior CCTV render camera. The prop is visual-only; the actual CCTV feed camera stays at its existing view position. Per-client; not synced.");

            PhysicalCameraVisualScale = cfg.Bind(
                PhysicalCamerasSection,
                "Physical Camera Visual Scale",
                1.0f,
                "Scale multiplier for the visible security-camera prop. Applies to the optional bundled prefab and the generated fallback visual. Per-client; not synced.");

            SecuritySweepEnabled = cfg.Bind(
                PhysicalCamerasSection,
                "Security Sweep Enabled",
                true,
                "If true, unbroken CCTV cameras slowly sweep left-to-right when not under manual player control. Per-client visual; server owns detection state.");

            SecuritySweepYawDegrees = cfg.Bind(
                PhysicalCamerasSection,
                "Security Sweep Yaw Degrees",
                42.0f,
                "Maximum yaw offset in either direction for automatic camera sweeps. Per-client visual; server owns detection state.");

            SecuritySweepSeconds = cfg.Bind(
                PhysicalCamerasSection,
                "Security Sweep Seconds",
                12.0f,
                "Seconds for one full left-to-right-to-left camera sweep cycle. Per-client visual; server owns detection state.");

            SecurityTrackDegreesPerSecond = cfg.Bind(
                PhysicalCamerasSection,
                "Security Track Degrees Per Second",
                38.0f,
                "Maximum degrees per second a security-active camera can turn while following a visible player. Per-client visual; server owns detection state.");

            SecurityDetectionIndicatorIntensity = cfg.Bind(
                PhysicalCamerasSection,
                "Security Detection Indicator Intensity",
                1.0f,
                "Intensity multiplier for suspicion-only camera detection indicators. Per-client visual.");

            SecurityIdleConeIntensity = cfg.Bind(
                PhysicalCamerasSection,
                "Security Idle Cone Intensity",
                1.0f,
                "Intensity multiplier for the faint always-on visibility cone a security-active camera casts before it has spotted anyone, so you can read what it is looking at. 0 disables the idle cone and leaves only the detection beam. Separate from Security Detection Indicator Intensity so 'subtle' can be dialled without touching the detection beam. Per-client visual.");

            SecurityLensDotGlow = cfg.Bind(
                PhysicalCamerasSection,
                "Security Lens Dot Glow",
                1.0f,
                "Multiplier for how brightly the camera's lens dot itself glows, across every state (inactive red, active amber, detection strobe, alarm). Scales the whole ladder at once, so the relative brightness between states is preserved. 0 leaves the dot unlit. Per-client visual.");
        }
    }
}
