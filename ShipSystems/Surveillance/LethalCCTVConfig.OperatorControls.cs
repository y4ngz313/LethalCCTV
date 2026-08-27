using BepInEx.Configuration;
using CSync.Extensions;
using CSync.Lib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    public partial class LethalCCTVConfig
    {
        private void BindOperatorControls(ConfigFile cfg)
        {
            // === Phase 1.8 — Room-Aware Placement ===
            // Replaces Phase 1's corner-based "one camera per non-excluded tile"
            // model with "one camera per room tile, skip corridors." See
            // TileExclusionFilter.IsCorridor for the predicate and
            // DungeonCameraSpawner's Gate A report for the per-tile verdict log.
            // #542 — these shipped under an unnumbered "CCTV Radar" heading, which
            // sorted them away from every other 8x - CCTV block in the file. The
            // section takes the numbered house style (90: 88 belongs to the retired
            // Station Props import and 89 to Diagnostics); both key names are
            // unchanged, and MigrateLegacyRadarSection below lifts an existing
            // file's old values into the renamed section. The old heading only ever
            // existed in THIS plugin's file, so CctvModuleConfig.LegacySections
            // (which imports from the pre-split shared cfg) cannot cover it.
            UseVanillaRadarFeed = cfg.Bind(
                RadarSection,
                "Use Vanilla Radar Feed",
                true,
                "When true, the CCTV focus radar inset uses a cloned vanilla MapCamera/ManualCameraRenderer feed centered on the active CCTV camera instead of the legacy CPU-drawn DunGen tile schematic. If the vanilla feed cannot be constructed, the old schematic is used as a fallback. Per-client; local entry, not synced.");

            ShowRadarItemBlips = cfg.Bind(
                RadarSection,
                "Show Item Blips",
                true,
                "When true, loose scrap inside the facility on the radar's active floor is drawn as its own blip class. A heavily looted interior can put forty blips on the map at once, so turn this off to de-clutter without losing the camera, player, monster and objective markers. Per-client; local entry, not synced.");

            RadarBlipScale = cfg.Bind(
                RadarSection,
                "Radar Blip Scale",
                1.0f,
                new ConfigDescription(
                    "Multiplies the on-screen size of every radar marker — cameras, crew, monsters, objectives and item blips alike. 1.0 is the authored size. The per-class size hierarchy is a set of fixed ratios underneath this, so raising or lowering it can never make one class read as another. Per-client; local entry, not synced.",
                    new AcceptableValueRange<float>(0.5f, 2.5f)));

            ShowOperatorControlsOverlay = cfg.Bind(
                OperatorControlsSection,
                "Show Operator Controls Overlay",
                false,
                "Retired in favour of the CCTV controls sticky note that spawns in the ship (#579); false is the default and the shipped experience. True brings the old top-left tooltip panel back as a fallback for anyone who has lost or stowed the note. This value governs the panel in every mode. The H keybind that flips it mid-session is live only while Enable Operator Debug Tools is on and is inert otherwise; the F1/F2 debug edit menus draw into this same panel, so with debug tools on and this off, H is what brings them back on screen. If the sticky note fails to register at startup the panel is forced on regardless of this setting, so a controls reference is never unavailable. Per-client; local entry, not synced.");

            EnableOperatorDebugTools = cfg.Bind(
                OperatorControlsSection,
                "Enable Operator Debug Tools",
                false,
                "When true, the operator station's developer hotkeys are live: F1 station edit menu, F2 camera placement edit and its F4/F5/F6/Delete actions, F8 first-person arms toggle, and the Alt arms-offset nudge. False (the default) makes all of them inert and hides their panels. Gameplay controls (mouse aim, arrows, SPACE radar, wheel zoom, LMB ping, RMB target, V walkie, E/ESC exit) are unaffected either way. Per-client; local entry, not synced.");

            AllowRadarView = cfg.BindSyncedEntry(
                new ConfigDefinition(OperatorPermissionsSection, "Allow Radar View"),
                true,
                new ConfigDescription("When false, operators cannot hold the radar-view control at the CCTV station. Host authoritative and synced to every client."));

            AllowCameraPings = cfg.BindSyncedEntry(
                new ConfigDefinition(OperatorPermissionsSection, "Allow Camera Pings"),
                true,
                new ConfigDescription("When false, operators cannot publish world pings from CCTV cameras. Host authoritative and synced to every client."));

            AllowWalkieTalkie = cfg.BindSyncedEntry(
                new ConfigDefinition(OperatorPermissionsSection, "Allow Walkie Talkie"),
                true,
                new ConfigDescription("When false, operators cannot transmit through held walkie-talkies from the CCTV station. Mainframe intercom controls remain available. Host authoritative and synced to every client."));

            AllowTargetScanning = cfg.BindSyncedEntry(
                new ConfigDefinition(OperatorPermissionsSection, "Allow Target Scanning"),
                true,
                new ConfigDescription("When false, operators cannot acquire or command context targets with the CCTV target action. Host authoritative and synced to every client."));

            AllowRemoteHacking = cfg.BindSyncedEntry(
                new ConfigDefinition(OperatorPermissionsSection, "Allow Remote Hacking"),
                true,
                new ConfigDescription("When false, CCTV context targets reject remote hack commands while non-hacking target actions remain available. Host authoritative and synced to every client."));
        }

        private void BindFocusMouselook(ConfigFile cfg)
        {
            // === Phase X — Focus Mouselook ===
            MouselookSensitivityMul = cfg.Bind(
                OperatorControlsSection,
                "Mouselook Sensitivity Multiplier",
                1.0f,
                "Multiplier applied to vanilla look sensitivity for the focused CCTV pan. 1.0 matches the player look; lower for finer aim, higher for snappier sweeps. Per-client; not synced.");
        }
    }
}
