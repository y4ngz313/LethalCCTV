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
            cfg.Bind(DiagnosticsSection, "Feed Quality Revision", 1, "Internal marker for a one-time settings upgrade, set to 1 once it has run; do not edit. Your own setting.");
            // === Phase 2 — Render Performance ===
            RenderHzPerMonitor = cfg.Bind(
                ShipMonitorSection,
                "Render Hz Per Monitor",
                24,
                "How many times a second the camera feed you are watching updates. At the default of 24 the picture moves smoothly; lower values save frame rate, the mod allows 1 to 30, and anything above 24 is capped while you sit at the CCTV station. Your own setting.");

            BodycamRenderHz = cfg.Bind(
                ShipMonitorSection,
                "Bodycam Render Hz",
                15,
                "How many times a second the body camera feed you have selected updates when the OpenBodyCams mod is installed; every other body camera stays off. The default is 15 and the mod keeps it between 5 and 30; lower values save frame rate. Your own setting.");

            DynamicActiveRendererEnabled = cfg.Bind(
                ShipMonitorSection,
                "Slow Unfocused Panes",
                true,
                "Updates only the camera pane you are focused on at full speed and the other panes at Unfocused Pane Render Hz, which saves frame rate. On by default; turn it off to update all four panes at full speed. Your own setting.");

            InactivePaneRenderHz = cfg.Bind(
                ShipMonitorSection,
                "Unfocused Pane Render Hz",
                3,
                "How many times a second the panes you are not focused on update while Slow Unfocused Panes is on. At the default of 3 they move in slow steps, and 0 freezes them apart from brief refreshes when they wake or change page. Your own setting.");

            UnmannedIdleRenderHz = cfg.Bind(
                ShipMonitorSection,
                "Unmanned Idle Render Hz",
                3,
                "How many times a second the one camera feed left on the ship monitor updates while nobody is using the CCTV station; every other camera stays off. At the default of 3 it moves in slow steps, 0 freezes it on its last picture, and the mod caps it at 10. Your own setting.");

            RenderRTWidth = cfg.Bind(
                ShipMonitorSection,
                "Feed Resolution Width",
                768,
                "How many pixels wide each camera feed is, from 64 to 1024, with the height following at 4:3, so the default of 768 gives 768 by 576. Higher values make feeds sharper and cost more performance, lower values make them blurrier and cheaper; restart the game after changing it. Your own setting.");

            FarClipPlane = cfg.Bind(
                ShipMonitorSection,
                "View Distance",
                120.0f,
                "How far a camera draws, in metres, from 4 to 250. The default of 120 reaches the far end of large rooms; lower values save some performance but show a blank background where the view is cut off. Your own setting.");

            FieldOfView = cfg.Bind(
                ShipMonitorSection,
                "Field Of View",
                75.0f,
                "How wide the camera view is, in degrees. The default of 75 is also the widest allowed, so this setting can only narrow the view. Your own setting.");

            CCTVShadowMapsEnabled = cfg.Bind(
                ShipMonitorSection,
                "CCTV Shadows Enabled",
                true,
                "Draws shadows in camera feeds, which gives rooms depth. On by default; turn it off for better performance on slower graphics cards, and it applies the next time a camera is put on the monitor. Your own setting.");

            CCTVAmbientOcclusionEnabled = cfg.Bind(
                ShipMonitorSection,
                "CCTV Ambient Occlusion Enabled",
                true,
                "Draws ambient occlusion in camera feeds, the soft darkening where walls, floors and objects meet, on maps that support it. On by default; turn it off for better performance, and it applies the next time a camera is put on the monitor. Your own setting.");
        }
    }
}
