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

        [SyncedEntryField] public SyncedEntry<bool> ShipStartsWithCctvTerminal;

        // Seller registration is identical on every peer, regardless of this host setting.
        private void BindShipTerminalStore(ConfigFile cfg)
        {
            ShipStartsWithCctvTerminal = cfg.BindSyncedEntry(
                new ConfigDefinition(GeneralSection, "Ship Starts With CCTV Terminal"),
                true,
                new ConfigDescription(
                    "On by default: the ship already has the CCTV terminal, so nobody has to buy it. Turn off to sell it in the store for CCTV Terminal Price. Host decides."));

            CctvTerminalPrice = cfg.BindSyncedEntry(
                new ConfigDefinition(GeneralSection, "CCTV Terminal Price"),
                200,
                new ConfigDescription(
                    "Credits the CCTV terminal costs in the ship store when Ship Starts With CCTV Terminal is off; the default is 200, and a change takes effect the next time you start the game. If both Y4NGZ Ship Systems and the Y4NGZ terminal shop are installed, their shop uses its own price instead. Host decides."));
        }

        private void BindShipMonitorHijack(ConfigFile cfg)
        {
            // === Phase 2 — Ship-Side Hijack ===
            CycleSeconds = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Camera Cycle Seconds"),
                8.0f,
                new ConfigDescription("Seconds before the ship monitor moves on to the next camera; everyone on the ship sees the same camera at the same time. The default is 8. Host decides."));

            // === #597 — GeneralImprovements "UseBetterMonitors" screen placement ===
            // Deliberately NOT synced. GI's UseBetterMonitors / AddMoreBetterMonitors are
            // per-client settings, so a host that runs 14 screens and a client that runs 9
            // do not even have the same set of indices; forcing one client's choice onto
            // another would point at a screen that does not exist there.
            GeneralImprovementsFeedScreenIndex = cfg.Bind(
                ShipMonitorSection,
                "General Improvements Feed Screen",
                -1,
                new ConfigDescription(
                    "Picks which General Improvements screen shows the camera feed, counted in General Improvements' own order: 0 is its first monitor (ShipMonitor1), 12 and 13 are the big left and right screens when its extra monitors are on, and -2 is its big middle map screen. At the default of -1 the mod chooses, which puts the feed on the big middle map screen; only used when General Improvements replaces the monitor wall. Your own setting.",
                    new AcceptableValueRange<int>(-2, 13)));

            GeneralImprovementsRadarScreenIndex = cfg.Bind(
                ShipMonitorSection,
                "General Improvements Radar Screen",
                -1,
                new ConfigDescription(
                    "Picks which General Improvements screen shows the radar, using the same numbers as General Improvements Feed Screen: 0 is its first monitor (ShipMonitor1), 12 and 13 are the big left and right screens when its extra monitors are on, and -2 is its big middle map screen. At the default of -1 the mod chooses, which puts the radar on the big right screen; picking the same screen as the feed leaves the radar off, and this is only used when General Improvements replaces the monitor wall. Your own setting.",
                    new AcceptableValueRange<int>(-2, 13)));
        }
    }
}
