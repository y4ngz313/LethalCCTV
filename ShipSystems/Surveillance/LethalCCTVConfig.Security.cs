using BepInEx.Configuration;
using CSync.Extensions;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    public partial class LethalCCTVConfig
    {
        private void BindSecurityAndTracking(ConfigFile cfg)
        {
            AllCamerasPassive = cfg.BindSyncedEntry(
                new ConfigDefinition(CctvModuleConfig.SecuritySystemsSection, "All Cameras Passive"),
                false,
                new ConfigDescription(
                    "When on, every camera stays passive on every moon: none watches for players and no camera alarm can go off, though the cameras still show on the ship monitor. Off by default. Host decides."));

            BreakableCameras = cfg.BindSyncedEntry(
                new ConfigDefinition(CctvModuleConfig.SecuritySystemsSection, "Breakable Cameras"),
                true,
                new ConfigDescription(
                    "When on, cameras inside the building can be damaged and broken by melee hits, the shotgun and supported Y4NGZ weapons. On by default. Host decides."));

            CameraHealth = cfg.BindSyncedEntry(
                new ConfigDefinition(CctvModuleConfig.SecuritySystemsSection, "Camera Health"),
                6f,
                new ConfigDescription(
                    "How much damage a camera takes before it breaks; a shovel swing deals 1, so at the default of 6 it takes six swings. Guns deal their own amount of damage. Host decides.",
                    new AcceptableValueRange<float>(1f, 10f)));

            AlarmLockdownGates = cfg.BindSyncedEntry(
                new ConfigDefinition(CctvModuleConfig.SecuritySystemsSection, "Close Lockdown Gates During Alarms"),
                true,
                new ConfigDescription(
                    "When on, a security alarm closes gates over the main entrance and fire exits for as long as the alarm lasts. On by default; the mod brings its own gates, or uses Contracted's gates when that mod is installed. Host decides."));

            ShowMainEntranceTrackingBox = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Show Main Entrance Tracking Box"),
                true,
                new ConfigDescription(
                    "When on, camera feeds draw a tracking box around the main entrance. On by default. Host decides."));

            ShowFireExitTrackingBoxes = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Show Fire Exit Tracking Boxes"),
                true,
                new ConfigDescription(
                    "When on, camera feeds draw a tracking box around each fire exit. On by default. Host decides."));

            ShowApparatusTrackingBox = cfg.BindSyncedEntry(
                new ConfigDefinition(ShipMonitorSection, "Show Apparatus Tracking Box"),
                true,
                new ConfigDescription(
                    "When on, camera feeds draw a tracking box around the building's apparatus for as long as it exists. On by default. Host decides."));
        }
    }
}
