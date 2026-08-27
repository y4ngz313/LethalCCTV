using BepInEx.Configuration;
using CSync.Extensions;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    public partial class LethalCCTVConfig
    {
        private void BindSecurityAndTracking(ConfigFile cfg)
        {
            BreakableCameras = cfg.BindSyncedEntry(
                new ConfigDefinition(CctvModuleConfig.SecuritySharedSection, "Breakable Cameras"),
                true,
                new ConfigDescription(
                    "When true, physical CCTV cameras can be damaged and broken by melee, vanilla shotguns, and supported Y4NGZ weapons. Host authoritative and synced to every client."));

            CameraHealth = cfg.BindSyncedEntry(
                new ConfigDefinition(CctvModuleConfig.SecuritySharedSection, "Camera Health"),
                6f,
                new ConfigDescription(
                    "Damage threshold before a physical CCTV camera breaks. A shovel swing deals 1; firearms use their own enemy-force falloff. Host authoritative and synced to every client.",
                    new AcceptableValueRange<float>(1f, 10f)));

            AlarmLockdownGates = cfg.BindSyncedEntry(
                new ConfigDefinition(CctvModuleConfig.SecurityAlarmsSection, "Close Lockdown Gates During Alarms"),
                true,
                new ConfigDescription(
                    "When true, CCTV security alarms close physical gates over facility entrances and fire exits for the alarm duration. LethalCCTV supplies the gates in standalone installs and shares Contracted's gates when available. Host authoritative and synced to every client."));

            ShowMainEntranceTrackingBox = cfg.BindSyncedEntry(
                new ConfigDefinition(TrackingBoxesSection, "Show Main Entrance Tracking Box"),
                true,
                new ConfigDescription(
                    "When true, CCTV feeds draw an objective tracking box around the main entrance. Host authoritative and synced to every client."));

            ShowFireExitTrackingBoxes = cfg.BindSyncedEntry(
                new ConfigDefinition(TrackingBoxesSection, "Show Fire Exit Tracking Boxes"),
                true,
                new ConfigDescription(
                    "When true, CCTV feeds draw objective tracking boxes around fire exits. Host authoritative and synced to every client."));

            ShowApparatusTrackingBox = cfg.BindSyncedEntry(
                new ConfigDefinition(TrackingBoxesSection, "Show Apparatus Tracking Box"),
                true,
                new ConfigDescription(
                    "When true, CCTV feeds draw an objective tracking box around the facility apparatus while it exists. Host authoritative and synced to every client."));
        }
    }
}
