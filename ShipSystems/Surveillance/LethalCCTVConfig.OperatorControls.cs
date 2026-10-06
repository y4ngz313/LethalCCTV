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
            // Radar entries; old "CCTV Radar" and numbered radar headings are migrated by CctvConfigSurfaceMigration.
            UseVanillaRadarFeed = cfg.Bind(
                RadarSection,
                "Use Vanilla Radar Feed",
                true,
                "When on, the radar at the CCTV station shows the game's own map view, centred on the camera you are watching. On by default; turn off to use the mod's simpler drawn floor plan instead, which is also used whenever the map view cannot be set up. Your own setting.");

            ShowRadarItemBlips = cfg.Bind(
                RadarSection,
                "Show Item Blips",
                true,
                "When on, loose scrap inside the building shows as its own dots on the radar. On by default; turn off to clear up a crowded map while keeping the camera, crew, monster and objective markers. Your own setting.");

            RadarBlipScale = cfg.Bind(
                RadarSection,
                "Radar Blip Scale",
                1.0f,
                new ConfigDescription(
                    "Size of every radar marker: cameras, crew, monsters, objectives and scrap. At the default of 1.0 markers are their normal size, and the size difference between marker types stays the same at any setting. Your own setting.",
                    new AcceptableValueRange<float>(0.5f, 2.5f)));

            ShowOperatorControlsOverlay = cfg.Bind(
                ShipMonitorSection,
                "Show Operator Controls Overlay",
                false,
                "When on, a panel listing the CCTV station controls shows in the top-left corner while you use the station, instead of relying on the controls sticky note in the ship. Off by default; the panel shows anyway if the sticky note fails to load, and with Enable Operator Debug Tools on, H turns it on and off. Your own setting.");

            EnableOperatorDebugTools = cfg.Bind(
                ShipMonitorSection,
                "Enable Operator Debug Tools",
                false,
                "When on, the CCTV station's developer keys work: F1 station edit menu, F2 camera placement edit with F4, F5, F6 and Delete, F8 arms toggle and the Alt arms nudge. Off by default; the normal station controls work either way. Your own setting.");

            AllowRadarView = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Allow Radar View"),
                true,
                new ConfigDescription("When off, players at the CCTV station cannot switch to the radar view. On by default. Host decides."));

            AllowCameraPings = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Allow Camera Pings"),
                true,
                new ConfigDescription("When off, players at the CCTV station cannot place pings in the world from a camera. On by default. Host decides."));

            AllowWalkieTalkie = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Allow Walkie Talkie"),
                true,
                new ConfigDescription("When off, players at the CCTV station cannot talk through a walkie-talkie they are holding; the mainframe intercom still works. On by default. Host decides."));

            AllowTargetScanning = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Allow Target Scanning"),
                true,
                new ConfigDescription("When off, players at the CCTV station cannot pick out or give orders to targets seen through a camera. On by default. Host decides."));

            AllowRemoteHacking = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Allow Remote Hacking"),
                true,
                new ConfigDescription("When off, targets seen through a camera cannot be hacked from the CCTV station, though other target actions still work. On by default. Host decides."));
        }

        private void BindFocusMouselook(ConfigFile cfg)
        {
            // === Phase X — Focus Mouselook ===
            MouselookSensitivityMul = cfg.Bind(
                ShipMonitorSection,
                "Mouselook Sensitivity Multiplier",
                1.0f,
                "Scales your normal look sensitivity while you aim a camera at the CCTV station. At the default of 1.0 it matches looking around on foot; lower it for finer aim or raise it for faster sweeps. Your own setting.");
        }
    }
}
