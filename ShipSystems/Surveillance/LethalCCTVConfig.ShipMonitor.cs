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
        // The six "83 - CCTV - Ship Monitor" transform entries were promoted to
        // constants on LethalCCTVConfig for 1.0 (#575), and with them went the
        // hot-reload event: there is no longer a runtime value to reload. See
        // MonitorPosition / MonitorRotation in LethalCCTVConfig.Core.cs.

        // === #393 — Standalone store price ===
        // Only consumed when the Contracted plugin (Y4NGZCompany / LGUShipSystems) is
        // absent. With Contracted installed its own requisition shop sells the CCTV
        // terminal and this entry is never read, so the section stays inert there.
        private void BindShipTerminalStore(ConfigFile cfg)
        {
            CctvTerminalPrice = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipTerminalSection, "CCTV Terminal Price"),
                200,
                new ConfigDescription(
                    "Credit cost of the CCTV Terminal upgrade in the vanilla ship store. Host-authoritative. " +
                    "Only used on a standalone LethalCCTV install; when the Y4NGZ Contracted plugin is present the " +
                    "terminal is sold through its requisition shop instead and this value is ignored. The store node " +
                    "is built once at plugin load, so a changed price applies from the next game launch."));
        }

        private void BindShipMonitorHijack(ConfigFile cfg)
        {
            // === Phase 2 — Ship-Side Hijack ===
            CycleSeconds = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Cycle Seconds"),
                8.0f,
                new ConfigDescription("Seconds before the ship monitor advances to the next CCTV camera. Synced across clients so all players see the same camera at the same time."));

            // === #597 — GeneralImprovements "UseBetterMonitors" screen placement ===
            // Deliberately NOT synced. GI's UseBetterMonitors / AddMoreBetterMonitors are
            // per-client settings, so a host that runs 14 screens and a client that runs 9
            // do not even have the same set of indices; forcing one client's choice onto
            // another would point at a screen that does not exist there.
            const string GeneralImprovementsIndexHelp =
                "Screen index on GeneralImprovements' replacement monitor wall, used only when GI is installed with " +
                "UseBetterMonitors=true (otherwise ignored entirely and the vanilla Cube.001 slots are used as before). " +
                "-1 = automatic (see the per-entry note above). " +
                "-2 = GI's MAP screen, the big MIDDLE monitor (MonitorGroup/Monitors/BigMiddle/MScreen) the operator " +
                "chair faces. That screen is not part of GI's numbered set, which is why it has its own value; CCTV " +
                "takes it over the same way it takes over the vanilla map monitor, and while it does so the red bezel " +
                "button stays the CCTV control instead of GI's monitor-power toggle. " +
                "GI's screen order with AddMoreBetterMonitors=true is " +
                "0-1 top-left pair, 2-5 top middle/right, 6-7 second-row left pair, 8-11 second-row middle/right, " +
                "12 = big LEFT screen, 13 = big RIGHT screen (14 total). With AddMoreBetterMonitors=false the whole left " +
                "cluster is gone and there are 9: 0-3 top middle/right, 4-7 second-row middle/right, 8 = big RIGHT screen. " +
                "These are zero-based; GI's own config keys are one-based, so index 12 is GI's ShipMonitor13. " +
                "An explicit index that does not exist in this client's GI layout leaves that feed unbound rather than " +
                "falling back to a screen you did not ask for. " +
                "Per-client; not synced. Read on every bind pass, so a change applies without a restart.";

            GeneralImprovementsFeedScreenIndex = cfg.Bind(
                ShipMonitorSection,
                "GeneralImprovements Feed Screen Index",
                -1,
                new ConfigDescription(
                    "Which GI screen shows the CCTV camera feed (the screen the vanilla lower-LEFT map monitor would " +
                    "have shown). Automatic (-1) is GI's MAP screen — the big MIDDLE monitor — which is vanilla parity: " +
                    "CCTV takes over the map monitor, and that is the screen the operator chair actually faces. " +
                    GeneralImprovementsIndexHelp,
                    new AcceptableValueRange<int>(-2, 13)));

            GeneralImprovementsRadarScreenIndex = cfg.Bind(
                ShipMonitorSection,
                "GeneralImprovements Radar Screen Index",
                -1,
                new ConfigDescription(
                    "Which GI screen shows the CCTV radar (the screen the vanilla lower-RIGHT monitor would have shown). " +
                    "Automatic (-1) claims the big RIGHT screen (index 13, or 8 without AddMoreBetterMonitors), replacing " +
                    "GI's configured content there so the visible radar matches the operator station's Space glance. If " +
                    "that screen is unavailable it falls back to the big LEFT screen and then the top rows, preferring an " +
                    "unassigned fallback. Set to the same value as the feed " +
                    "screen index to leave the radar unbound and show only the camera feed. " + GeneralImprovementsIndexHelp,
                    new AcceptableValueRange<int>(-2, 13)));
        }
    }
}
