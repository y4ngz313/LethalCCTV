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
            cfg.Bind(DiagnosticsSection, "Low Light Revision", 1, "Internal marker for a one-time settings upgrade, set to 1 once it has run; do not edit. Your own setting.");
            cfg.Bind(DiagnosticsSection, "Machine Vision Revision", 1, "Internal marker for a one-time settings upgrade, set to 1 once it has run; do not edit. Your own setting.");
            // === Phase 1.7 — Display ===
            // Display-side night-vision treatment applied to the per-quad clone material's
            // shader. Bool toggle bypasses both grayscale and gain; set false
            // to see raw CCTV colour. Per-client aesthetic; not synced. Hot-reloadable via
            // NightVisionParamsChanged → QuadMonitor.ApplyNightVisionParams.
            NightVisionEnabled = cfg.Bind(
                ShipMonitorSection,
                "Night Vision Enabled",
                true,
                "Lets camera feeds switch to night vision, which brightens dark rooms and turns the picture mostly black and white. On by default; turn it off to see every feed in plain colour with no extra brightening. Your own setting.");

            NightVisionGain = cfg.Bind(
                ShipMonitorSection,
                "Night Vision Gain",
                4.0f,
                "How strongly night vision brightens a dark camera feed: with Night Vision Auto Gain on this is the most it will brighten, with it off every feed is brightened by exactly this much. The default is 4.0, and the mod keeps it between 0.5 and 8. Your own setting.");

            NightVisionAutoGain = cfg.Bind(
                ShipMonitorSection,
                "Night Vision Auto Gain",
                true,
                "Lets each camera feed adjust its own brightness like a real camera, brightening dark rooms up to Night Vision Gain and toning down brightly lit rooms so they do not wash out to white. On by default; turn it off to brighten every feed by exactly Night Vision Gain. Your own setting.");

            NightVisionAutomaticLowLight = cfg.Bind(
                ShipMonitorSection,
                "Automatic Low Light",
                true,
                "Switches a feed into night vision only while the room it shows is dark, fading smoothly in and out, so lit rooms keep their natural colour. On by default; turn it off to keep night vision on in every room. Your own setting.");

            NightVisionFlipY = cfg.Bind(
                ShipMonitorSection,
                "Flip Feed Vertically",
                true,
                "Turns every camera feed upside down before it reaches the ship monitor, in normal view as well as night vision. On by default because feeds otherwise arrive upside down in this game; turn it off only if your feeds look upside down. Your own setting.");

            // === Analog artifacts ===
            // These ten drive the analog-DVR treatment baked into every CCTV feed.
            // The first four existed in the shader from the start but were never
            // written from C#, so they ran on ShaderLab defaults; the last six ship
            // with the 0.0.11 bundle. Setting the five *Strength/Aberration knobs to
            // 0 reproduces the pre-0.0.11 look exactly. All are pushed behind a
            // Material.HasProperty guard, so an older bundle silently ignores any it
            // does not declare.
            FeedColorRetention = cfg.Bind(
                ShipMonitorSection,
                "Feed Color Retention",
                0.08f,
                "How much colour stays in a feed once night vision takes over in a dark room, from 0 for pure black and white to 1 for full colour; it also limits how coloured the fringes from Feed Chromatic Aberration look. The default of 0.08 keeps a little colour, and it has no effect while Night Vision Enabled is off. Your own setting.");

            FeedScanlineStrength = cfg.Bind(
                ShipMonitorSection,
                "Feed Scanline Strength",
                0.018f,
                "How dark the fine horizontal lines across the monitor picture are, like an old tube TV. At the default of 0.018 they are barely visible and 0 removes them; high values can shimmer against Feed Interlace Strength. Your own setting.");

            FeedNoiseStrength = cfg.Bind(
                ShipMonitorSection,
                "Feed Noise Strength",
                0.006f,
                "How much flickering grain covers the monitor picture. At the default of 0.006 the grain is faint, and 0 removes it. Your own setting.");

            FeedVignetteStrength = cfg.Bind(
                ShipMonitorSection,
                "Feed Vignette Strength",
                0f,
                "Darkens the edges of the monitor picture compared with the centre. Off at the default of 0; raise it toward 1 for heavier dark edges. Your own setting.");

            FeedChromaAberration = cfg.Bind(
                ShipMonitorSection,
                "Feed Chromatic Aberration",
                0f,
                "Splits the picture into red, green and blue colour fringes that grow toward the edges, like a cheap lens. Off at the default of 0; a small value such as 0.25 gives a thin fringe on sharp edges, while larger values give an obvious colour ghost. Your own setting.");

            FeedRollBarStrength = cfg.Bind(
                ShipMonitorSection,
                "Feed Roll Bar Strength",
                0f,
                "How bright the soft band is that slowly rolls through the picture, like a badly synced old video signal. Off at the default of 0. Your own setting.");

            FeedRollBarSpeed = cfg.Bind(
                ShipMonitorSection,
                "Feed Roll Bar Speed",
                0.12f,
                "How fast the rolling band from Feed Roll Bar Strength moves; at the default of 0.12 it takes about 8 seconds to cross the picture. Does nothing while Feed Roll Bar Strength is 0. Your own setting.");

            FeedCurvatureStrength = cfg.Bind(
                ShipMonitorSection,
                "Feed Curvature Strength",
                0f,
                "Bulges the picture outward like an old curved TV screen, with the corners falling away to black. Off at the default of 0, which keeps a flat picture that fills the screen; lower it if the text on the monitor ends up over black corners. Your own setting.");

            FeedInterlaceStrength = cfg.Bind(
                ShipMonitorSection,
                "Feed Interlace Strength",
                0f,
                "Makes alternating rows of the picture dim and flicker about 12 times a second, like an old interlaced TV signal. Off at the default of 0. Your own setting.");

            FeedDropoutStrength = cfg.Bind(
                ShipMonitorSection,
                "Feed Dropout Strength",
                0f,
                "Scatters tiny dead or overbright pixel specks that flash across the picture. Off at the default of 0; a value around 0.04 shows a couple of dozen specks at a time. Your own setting.");

            CCTVFillLightEnabled = cfg.Bind(
                ShipMonitorSection,
                "CCTV Fill Light Enabled",
                true,
                "Allows a small light that brightens what a camera sees only while that camera draws its picture, so it never lights the room for players. The light only switches on when CCTV Render Fill Light Enabled is also on, which is off by default, so at the defaults no light is added even though this is on. Your own setting.");

            CCTVRenderFillLightEnabled = cfg.Bind(
                ShipMonitorSection,
                "CCTV Render Fill Light Enabled",
                false,
                "The second switch for the camera fill light: the light is added only when this and CCTV Fill Light Enabled are both on. Off by default because the extra light costs frame rate; turn it on if feeds look too dark and you can spare the performance. Your own setting.");

            CCTVFillLightIntensity = cfg.Bind(
                ShipMonitorSection,
                "CCTV Fill Light Intensity",
                367.0f,
                "How bright the camera fill light is when it is switched on. The default of 367 matches the night-vision light of the OpenBodyCams mod; lower it if feeds turn white, raise it if they stay too dark. Your own setting.");

            CCTVFillLightRange = cfg.Bind(
                ShipMonitorSection,
                "CCTV Fill Light Range",
                14.0f,
                "How far the camera fill light reaches, in metres, when it is switched on. The default of 14 matches the night-vision light of the OpenBodyCams mod. Your own setting.");

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
                CameraPlacementSection,
                "Physical Camera Visuals Enabled",
                true,
                "Shows a security camera body on the wall or ceiling where each camera inside the facility sits; the camera feeds work either way. On by default; turning it off hides the camera bodies for you and also leaves you nothing to hit when trying to break a camera. Your own setting.");

            PhysicalCameraVisualScale = cfg.Bind(
                CameraPlacementSection,
                "Physical Camera Visual Scale",
                1.0f,
                "Size of the camera bodies on walls and ceilings. The default of 1.0 is normal size, and the mod keeps it between 0.05 and 10. Your own setting.");

            SecuritySweepEnabled = cfg.Bind(
                CctvModuleConfig.SecuritySystemsSection,
                "Security Sweep Enabled",
                true,
                "Makes working cameras slowly turn from side to side when nobody is steering them, and turn to follow a player they have spotted, so they watch more of the room. On by default; with it off cameras hold still and only see what is straight ahead of them. Host decides.");

            SecuritySweepYawDegrees = cfg.Bind(
                CctvModuleConfig.SecuritySystemsSection,
                "Security Sweep Degrees",
                44.0f,
                "How far a camera turns to each side during its sweep, in degrees. At the default of 44 it covers 88 degrees in total, and it never turns past the 90 degrees its mount allows. Host decides.");

            SecuritySweepSeconds = cfg.Bind(
                CctvModuleConfig.SecuritySystemsSection,
                "Security Sweep Seconds",
                14.0f,
                "How many seconds one full sweep takes, from one side to the other and back. At the default of 14 cameras turn slowly; lower values sweep faster. Host decides.");

            SecurityTrackDegreesPerSecond = cfg.Bind(
                CctvModuleConfig.SecuritySystemsSection,
                "Security Track Degrees Per Second",
                38.0f,
                "The fastest a camera can turn, in degrees per second, while it follows a player it has spotted; its sweep never goes faster than this either. The default is 38. Host decides.");

            SecurityDetectionIndicatorIntensity = cfg.Bind(
                CctvModuleConfig.SecuritySystemsSection,
                "Security Detection Indicator Intensity",
                1.0f,
                "How bright the beam is that a camera shines while it is spotting a player. The default of 1.0 is normal brightness, and 0 hides the beam. Your own setting.");

            SecurityIdleConeIntensity = cfg.Bind(
                CctvModuleConfig.SecuritySystemsSection,
                "Security Idle Cone Intensity",
                1.0f,
                "How bright the faint cone is that an armed security camera shows before it has spotted anyone, so you can tell where it is looking. The default of 1.0 is normal brightness; 0 hides the cone and leaves only the beam set by Security Detection Indicator Intensity. Your own setting.");

            SecurityLensDotGlow = cfg.Bind(
                CctvModuleConfig.SecuritySystemsSection,
                "Security Lens Dot Glow",
                1.0f,
                "How brightly the small light on each camera's lens glows in every state, from idle red and armed amber to the spotting flash and the alarm, keeping the same difference between states. The default of 1.0 is normal brightness, and 0 leaves the light dark. Your own setting.");
        }
    }
}
