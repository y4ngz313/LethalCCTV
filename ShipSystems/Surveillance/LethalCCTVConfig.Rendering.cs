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
        private void BindRendering(ConfigFile cfg)
        {
            // === Phase 2 — Render Performance ===
            RenderHzPerMonitor = cfg.Bind(
                PerformanceSection,
                "Render Hz Per Monitor",
                24,
                "Per-monitor render frequency in Hz. Default 24. Tunable 5–30; above 30 risks visible FPS dip on lower-end hardware. Per-client; local entry, not synced.");

            BodycamRenderHz = cfg.Bind(
                PerformanceSection,
                "Bodycam Render Hz",
                15,
                "Render frequency for the selected purchased OpenBodyCams player feed. Every non-selected bodycam camera stays disabled. Tunable 5–30; lower values reduce the extra scene-render cost. Per-client; local entry, not synced.");

            DynamicActiveRendererEnabled = cfg.Bind(
                PerformanceSection,
                "Dynamic Active Renderer Enabled",
                true,
                "When true, only the focused active CCTV pane renders at full Hz. Other bound panes render at Inactive Pane Render Hz. Disable to restore the previous all-four-bound-cameras-at-full-rate behavior. Per-client; local entry, not synced.");

            InactivePaneRenderHz = cfg.Bind(
                PerformanceSection,
                "Inactive Pane Render Hz",
                3,
                "Render frequency for non-active panes while CCTV focus is open and Dynamic Active Renderer is enabled. Set 0 to freeze inactive panes except for one-shot wake/page renders. Per-client; local entry, not synced.");

            UnmannedIdleRenderHz = cfg.Bind(
                PerformanceSection,
                "Unmanned Idle Render Hz",
                3,
                "Render frequency for the one camera left on the ship monitor wall while nobody is operating the station. Only that single feed renders; every other camera stays off. Tunable 0-10; 0 freezes the wall on the last rendered frame (the pre-0.0.x behaviour). Per-client; local entry, not synced.");

            RenderRTWidth = cfg.Bind(
                PerformanceSection,
                "Render RT Width",
                512,
                "RenderTexture width in pixels. Height is derived 4:3. Default 512 (so 512x384). Lower values reduce GPU cost but blur CCTV feeds. Per-client; local entry, not synced.");

            FarClipPlane = cfg.Bind(
                PerformanceSection,
                "Far Clip Plane",
                40.0f,
                "Camera far clip plane in meters. Default 40 m restores the pre-0.0.29 CCTV view distance. Lower values reduce overdraw but can make feeds too short-ranged. Per-client aesthetic; local entry, not synced.");

            FieldOfView = cfg.Bind(
                PerformanceSection,
                "Field Of View",
                75.0f,
                "Camera field-of-view in degrees. Default 75. Per-client aesthetic; local entry, not synced.");

            CCTVShadowMapsEnabled = cfg.Bind(
                PerformanceSection,
                "CCTV Shadow Maps Enabled",
                false,
                "If true, CCTV cameras render HDRP shadow maps. Default false keeps security feeds cheap; the night-vision shader and exposure volume provide legibility without asking every CCTV render to rebuild dungeon shadows. Per-client; local entry, not synced.");
        }
    }
}
