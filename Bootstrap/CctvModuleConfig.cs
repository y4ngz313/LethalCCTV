using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Y4NGZCompany.Bootstrap
{
    /// <summary>
    /// Settings the CCTV systems used to read off <c>ContractsBootstrap</c> while this code
    /// lived inside Y4NGZCompany (#393). <see cref="TryImportLegacyConfig"/> seeds this
    /// plugin's file once from the old shared config, then
    /// <see cref="CctvConfigSurfaceMigration"/> moves those legacy headings and keys onto
    /// the current player-facing config surface before entries bind.
    ///
    /// <see cref="Log"/> stands in for the old <c>ContractsBootstrap.Log</c> so every moved
    /// log line keeps its exact prefix and wording, now on LethalCCTV's own log source.
    /// </summary>
    internal static class CctvModuleConfig
    {
        internal const string SecuritySystemsSection = "Security Systems";
        internal const string MainframeSection = "Mainframe";
        internal const string CompanyStashesSection = "Company Stashes";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> MainframeEnabled;
        internal static ConfigEntry<bool> CompanyStashesEnabled;
        internal static ConfigEntry<int> StashLootRolls;
        internal static ConfigEntry<string> StashLootPool;
        internal static ConfigEntry<int> StashMinimumRiskD;
        internal static ConfigEntry<int> StashMaximumRiskD;
        internal static ConfigEntry<int> StashMinimumRiskC;
        internal static ConfigEntry<int> StashMaximumRiskC;
        internal static ConfigEntry<int> StashMinimumRiskB;
        internal static ConfigEntry<int> StashMaximumRiskB;
        internal static ConfigEntry<int> StashMinimumRiskA;
        internal static ConfigEntry<int> StashMaximumRiskA;
        internal static ConfigEntry<int> StashMinimumRiskS;
        internal static ConfigEntry<int> StashMaximumRiskS;
        internal static ConfigEntry<int> StashMinimumUnknownRisk;
        internal static ConfigEntry<int> StashMaximumUnknownRisk;

        internal static ConfigEntry<bool> CctvSecurityEnabled;
        internal static ConfigEntry<bool> CctvSecurityEnabledRiskD;
        internal static ConfigEntry<bool> CctvSecurityEnabledRiskC;
        internal static ConfigEntry<bool> CctvSecurityEnabledRiskB;
        internal static ConfigEntry<bool> CctvSecurityEnabledRiskA;
        internal static ConfigEntry<bool> CctvSecurityEnabledRiskS;
        internal static ConfigEntry<float> CctvSecurityActiveCameraRatio;
        internal static ConfigEntry<float> CctvSecurityActiveCameraRatioRiskD;
        internal static ConfigEntry<float> CctvSecurityActiveCameraRatioRiskC;
        internal static ConfigEntry<float> CctvSecurityActiveCameraRatioRiskB;
        internal static ConfigEntry<float> CctvSecurityActiveCameraRatioRiskA;
        internal static ConfigEntry<float> CctvSecurityActiveCameraRatioRiskS;
        internal static ConfigEntry<int> CctvSecurityActiveCameraMin;
        internal static ConfigEntry<int> CctvSecurityActiveCameraMax;
        internal static ConfigEntry<float> CctvSecurityRotationSeconds;
        internal static ConfigEntry<float> CctvSecurityDetectionSeconds;
        internal static ConfigEntry<float> CctvSecurityDetectionSecondsRiskD;
        internal static ConfigEntry<float> CctvSecurityDetectionSecondsRiskC;
        internal static ConfigEntry<float> CctvSecurityDetectionSecondsRiskB;
        internal static ConfigEntry<float> CctvSecurityDetectionSecondsRiskA;
        internal static ConfigEntry<float> CctvSecurityDetectionSecondsRiskS;
        internal static ConfigEntry<bool> CctvSecuritySpottingAlertEnabled;
        internal static ConfigEntry<float> CctvSecuritySpottingAlertIntensity;
        internal static ConfigEntry<float> CctvSecurityAlarmDurationSeconds;
        internal static ConfigEntry<bool> CctvSecurityProtocolEventsEnabled;
        internal static ConfigEntry<float> CctvSecurityProtocolBlackoutSeconds;
        internal static ConfigEntry<float> CctvSecurityAlarmBlackoutSeconds;
        internal static ConfigEntry<float> CctvSecurityLockdownDrillCooldownSeconds;
        internal static ConfigEntry<bool> CctvSecurityWallAlarmFixturesEnabled;
        internal static ConfigEntry<float> CctvSecurityAlarmLightIntensity;
        internal static ConfigEntry<float> CctvSecurityAlarmAudioVolume;
        internal static ConfigEntry<float> CctvSecurityAlarmCooldownSeconds;
        internal static ConfigEntry<bool> CctvSecurityAlarmAwarenessPingEnabled;
        internal static ConfigEntry<float> CctvSecurityAlarmAwarenessPingRange;
        internal static ConfigEntry<float> CctvSecurityAlarmAwarenessPingLoudness;
        internal static ConfigEntry<bool> CctvSecurityAlarmAwarenessSecondPingEnabled;
        internal static ConfigEntry<float> CctvSecurityAlarmAwarenessSecondPingDelaySeconds;

        private static bool _bound;

        internal static void Bind(ConfigFile config, ManualLogSource logger)
        {
            Log = logger;
            if (_bound || config == null)
                return;
            _bound = true;

            MainframeEnabled = config.Bind(
                MainframeSection,
                "Enabled",
                true,
                "Places the security mainframe in the facility, which players can hack to shut security down. On by default; when off there is no mainframe, cameras and alarms still work, and the only way to stop a camera is to break it. Host decides.");

            CompanyStashesEnabled = config.Bind(
                CompanyStashesSection,
                "Enabled",
                true,
                "Places code-locked Company Stashes full of loot in the facility. On by default. Host decides.");

            StashLootRolls = config.Bind(
                CompanyStashesSection,
                "Loot Rolls",
                2,
                "How many extra items each Company Stash draws from Loot Pool on top of its guaranteed gold bar. At the default of 2 every stash holds the gold bar plus two draws; 0 leaves only the gold bar. Host decides.");

            StashLootPool = config.Bind(
                CompanyStashesSection,
                "Loot Pool",
                // Refreshed 2026-08-06 (Y4NGZUpgrades release prep). The previous default still named
                // ".357 Rounds" and "Crossbow Bolt", two display names Y4NGZUpgrades retired in #148
                // and #150 — both rolls resolved to nothing. This set is the live Upgrades item names,
                // plus the three renamed firearms as rarer stash finds. Names are resolved by
                // Item.itemName at round time; a missing item is logged and skipped, so the stash
                // still works with Y4NGZUpgrades absent.
                DefaultStashLootPool,
                "Items a Company Stash can draw for Loot Rolls, as entries split by semicolons, each written Name|weight|count: the item's in-game name, how likely it is next to the other entries (bigger is more likely), and how many copies one draw gives. Items that are not in the game are skipped, so items from other mods are safe to list; the default makes ammo and fuel common finds and the three guns rare ones. Host decides.");

            StashMinimumRiskD = BindStashCount(config, "Minimum Stashes - Risk D", 1, true, "moons rated D", "Maximum Stashes - Risk D");
            StashMaximumRiskD = BindStashCount(config, "Maximum Stashes - Risk D", 4, false, "moons rated D", "Minimum Stashes - Risk D");
            StashMinimumRiskC = BindStashCount(config, "Minimum Stashes - Risk C", 1, true, "moons rated C", "Maximum Stashes - Risk C");
            StashMaximumRiskC = BindStashCount(config, "Maximum Stashes - Risk C", 4, false, "moons rated C", "Minimum Stashes - Risk C");
            StashMinimumRiskB = BindStashCount(config, "Minimum Stashes - Risk B", 1, true, "moons rated B", "Maximum Stashes - Risk B");
            StashMaximumRiskB = BindStashCount(config, "Maximum Stashes - Risk B", 4, false, "moons rated B", "Minimum Stashes - Risk B");
            StashMinimumRiskA = BindStashCount(config, "Minimum Stashes - Risk A", 1, true, "moons rated A", "Maximum Stashes - Risk A");
            StashMaximumRiskA = BindStashCount(config, "Maximum Stashes - Risk A", 4, false, "moons rated A", "Minimum Stashes - Risk A");
            StashMinimumRiskS = BindStashCount(config, "Minimum Stashes - Risk S", 1, true, "moons rated S", "Maximum Stashes - Risk S");
            StashMaximumRiskS = BindStashCount(config, "Maximum Stashes - Risk S", 4, false, "moons rated S", "Minimum Stashes - Risk S");
            StashMinimumUnknownRisk = BindStashCount(config, "Minimum Stashes - Unknown Risk", 1, true, "moons whose risk level is not recognised", "Maximum Stashes - Unknown Risk");
            StashMaximumUnknownRisk = BindStashCount(config, "Maximum Stashes - Unknown Risk", 4, false, "moons whose risk level is not recognised", "Minimum Stashes - Unknown Risk");

            CctvSecurityEnabled = config.Bind(
                SecuritySystemsSection,
                "Security Enabled",
                true,
                "Master switch for camera security: watching cameras, alarms, lockdown gates and drills. On by default; each moon also needs its matching Security Enabled - Risk D, C, B, A or S switch on, except moons whose risk level is not recognised, where only this switch counts. Host decides.");

            // #466 per-risk-level security block. The master Security Enabled above still gates
            // everything; these decide whether a landed moon's tier runs hostile security
            // at all. #575: D and C now default on - play testing landed on hostile
            // security running at every risk tier, so a fresh install matches the tuned
            // profile instead of drifting from it.
            CctvSecurityEnabledRiskD = config.Bind(
                SecuritySystemsSection,
                "Security Enabled - Risk D",
                true,
                "Turns camera security (watching cameras, alarms and lockdowns) on for moons rated D. On by default; it only matters while Security Enabled is on. Host decides.");

            CctvSecurityEnabledRiskC = config.Bind(
                SecuritySystemsSection,
                "Security Enabled - Risk C",
                true,
                "Turns camera security (watching cameras, alarms and lockdowns) on for moons rated C. On by default; it only matters while Security Enabled is on. Host decides.");

            CctvSecurityEnabledRiskB = config.Bind(
                SecuritySystemsSection,
                "Security Enabled - Risk B",
                true,
                "Turns camera security (watching cameras, alarms and lockdowns) on for moons rated B. On by default; it only matters while Security Enabled is on. Host decides.");

            CctvSecurityEnabledRiskA = config.Bind(
                SecuritySystemsSection,
                "Security Enabled - Risk A",
                true,
                "Turns camera security (watching cameras, alarms and lockdowns) on for moons rated A. On by default; it only matters while Security Enabled is on. Host decides.");

            CctvSecurityEnabledRiskS = config.Bind(
                SecuritySystemsSection,
                "Security Enabled - Risk S",
                true,
                "Turns camera security (watching cameras, alarms and lockdowns) on for moons rated S. On by default; it only matters while Security Enabled is on. Host decides.");

            CctvSecurityActiveCameraRatio = config.Bind(
                SecuritySystemsSection,
                "Active Camera Share - Unknown Risk",
                0.20f,
                new ConfigDescription(
                    "Share of the working security cameras that actively watch for players on moons whose risk level is not recognised, rounded up and kept between Minimum Active Cameras and Maximum Active Cameras - Unknown Risk. At the default of 0.20 about one camera in five is watching. Host decides.",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            CctvSecurityActiveCameraRatioRiskD = config.Bind(
                SecuritySystemsSection,
                "Active Camera Share - Risk D",
                0.10f,
                new ConfigDescription(
                    "Share of the working security cameras that actively watch for players on moons rated D, rounded up and never below Minimum Active Cameras. At the default of 0.10 about one camera in ten is watching; 0 means no camera watches on these moons. Host decides.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraRatioRiskC = config.Bind(
                SecuritySystemsSection,
                "Active Camera Share - Risk C",
                0.15f,
                new ConfigDescription(
                    "Share of the working security cameras that actively watch for players on moons rated C, rounded up and never below Minimum Active Cameras. At the default of 0.15 about three cameras in twenty are watching; 0 means no camera watches on these moons. Host decides.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraRatioRiskB = config.Bind(
                SecuritySystemsSection,
                "Active Camera Share - Risk B",
                0.25f,
                new ConfigDescription(
                    "Share of the working security cameras that actively watch for players on moons rated B, rounded up and never below Minimum Active Cameras. At the default of 0.25 about one camera in four is watching; 0 means no camera watches on these moons. Host decides.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraRatioRiskA = config.Bind(
                SecuritySystemsSection,
                "Active Camera Share - Risk A",
                0.25f,
                new ConfigDescription(
                    "Share of the working security cameras that actively watch for players on moons rated A, rounded up and never below Minimum Active Cameras. At the default of 0.25 about one camera in four is watching; 0 means no camera watches on these moons. Host decides.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraRatioRiskS = config.Bind(
                SecuritySystemsSection,
                "Active Camera Share - Risk S",
                0.35f,
                new ConfigDescription(
                    "Share of the working security cameras that actively watch for players on moons rated S, rounded up and never below Minimum Active Cameras. At the default of 0.35 about one camera in three is watching; 0 means no camera watches on these moons. Host decides.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraMin = config.Bind(
                SecuritySystemsSection,
                "Minimum Active Cameras",
                1,
                new ConfigDescription(
                    "The fewest cameras that actively watch for players at once, on every moon, as long as there are that many working cameras. At the default of 1 at least one camera is always watching, unless that moon's Active Camera Share is 0. Host decides.",
                    new AcceptableValueRange<int>(0, 12)));

            CctvSecurityActiveCameraMax = config.Bind(
                SecuritySystemsSection,
                "Maximum Active Cameras - Unknown Risk",
                4,
                new ConfigDescription(
                    "The most cameras that actively watch for players at once on moons whose risk level is not recognised; on rated moons the Active Camera Share alone sets the number. At the default of 4 no more than four cameras watch at once there. Host decides.",
                    new AcceptableValueRange<int>(1, 24)));

            CctvSecurityRotationSeconds = config.Bind(
                SecuritySystemsSection,
                "Active Camera Rotation Seconds",
                90f,
                new ConfigDescription(
                    "How often, in seconds, a new set of cameras takes over watching for players. At the default of 90 the watching cameras change every minute and a half. Host decides.",
                    new AcceptableValueRange<float>(5f, 300f)));

            CctvSecurityDetectionSeconds = config.Bind(
                SecuritySystemsSection,
                "Detection Seconds - Unknown Risk",
                1.5f,
                new ConfigDescription(
                    "How long a watching camera must see a player without a break before it raises the alarm, on moons whose risk level is not recognised; the mod adds a quarter on top, so the default of 1.5 takes about 1.9 seconds. The five Detection Seconds - Risk settings start at this value when the file is first created. Host decides.",
                    new AcceptableValueRange<float>(0.25f, 5f)));

            // #466. Each tier's default is the Detection Seconds - Unknown Risk key *as it
            // currently stands in this profile*, not the shipped 1.5 - that key is bound above,
            // so a player who already dialled it in has that value adopted by all five
            // tiers on the first run that writes them, and an untouched config keeps the
            // shipped behaviour. Either way the only change to time-to-trip is the global
            // +25% slowdown CctvSecurityConfig applies afterwards.
            float detectionSecondsDefault = CctvSecurityDetectionSeconds.Value;
            CctvSecurityDetectionSecondsRiskD = config.Bind(
                SecuritySystemsSection,
                "Detection Seconds - Risk D",
                detectionSecondsDefault,
                new ConfigDescription(
                    "How long a watching camera must see a player without a break before it raises the alarm on moons rated D; the mod adds a quarter on top. It starts at the same value as Detection Seconds - Unknown Risk, 1.5 seconds on a new file. Host decides.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecurityDetectionSecondsRiskC = config.Bind(
                SecuritySystemsSection,
                "Detection Seconds - Risk C",
                detectionSecondsDefault,
                new ConfigDescription(
                    "How long a watching camera must see a player without a break before it raises the alarm on moons rated C; the mod adds a quarter on top. It starts at the same value as Detection Seconds - Unknown Risk, 1.5 seconds on a new file. Host decides.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecurityDetectionSecondsRiskB = config.Bind(
                SecuritySystemsSection,
                "Detection Seconds - Risk B",
                detectionSecondsDefault,
                new ConfigDescription(
                    "How long a watching camera must see a player without a break before it raises the alarm on moons rated B; the mod adds a quarter on top. It starts at the same value as Detection Seconds - Unknown Risk, 1.5 seconds on a new file. Host decides.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecurityDetectionSecondsRiskA = config.Bind(
                SecuritySystemsSection,
                "Detection Seconds - Risk A",
                detectionSecondsDefault,
                new ConfigDescription(
                    "How long a watching camera must see a player without a break before it raises the alarm on moons rated A; the mod adds a quarter on top. It starts at the same value as Detection Seconds - Unknown Risk, 1.5 seconds on a new file. Host decides.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecurityDetectionSecondsRiskS = config.Bind(
                SecuritySystemsSection,
                "Detection Seconds - Risk S",
                detectionSecondsDefault,
                new ConfigDescription(
                    "How long a watching camera must see a player without a break before it raises the alarm on moons rated S; the mod adds a quarter on top. It starts at the same value as Detection Seconds - Unknown Risk, 1.5 seconds on a new file. Host decides.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecuritySpottingAlertEnabled = config.Bind(
                SecuritySystemsSection,
                "Spotting Alert Enabled",
                true,
                "Shows an arc at the edge of your screen pointing toward each security camera that is currently spotting you. On by default. Your own setting.");

            CctvSecuritySpottingAlertIntensity = config.Bind(
                SecuritySystemsSection,
                "Spotting Alert Intensity",
                1f,
                new ConfigDescription(
                    "How strongly the spotting alert arcs show, where 0 hides them and 2 is twice as strong. At the default of 1 they show at normal strength; it only matters while Spotting Alert Enabled is on. Your own setting.",
                    new AcceptableValueRange<float>(0f, 2f)));

            CctvSecurityAlarmDurationSeconds = config.Bind(
                SecuritySystemsSection,
                "Alarm Duration Seconds",
                15f,
                new ConfigDescription(
                    "How long an alarm keeps going after a camera last spotted a player. At the default of 15 the alarm ends 15 seconds after the last sighting. Host decides.",
                    new AcceptableValueRange<float>(3f, 120f)));

            // #575: defaults off - protocol events fired often enough to read as
            // background noise, so a fresh install now matches the tuned profile.
            CctvSecurityProtocolEventsEnabled = config.Bind(
                MainframeSection,
                "Security Protocols Enabled",
                false,
                "Lets the security mainframe run surprise lockdown drills: an announcement, then the lights go out and gates close over the main entrance and fire exits for 10 seconds. Off by default. Your own setting.");

            CctvSecurityProtocolBlackoutSeconds = config.Bind(
                MainframeSection,
                "Protocol Blackout Seconds",
                2.5f,
                new ConfigDescription(
                    "The shortest time the facility lights go out when a lockdown drill starts. Drills already keep the lights off for their full 10 seconds, so at the default of 2.5 this changes nothing you can see. Your own setting.",
                    new AcceptableValueRange<float>(0f, 8f)));

            CctvSecurityAlarmBlackoutSeconds = config.Bind(
                SecuritySystemsSection,
                "Alarm Blackout Seconds",
                5f,
                new ConfigDescription(
                    "When an alarm goes off the facility lights stay out for the whole alarm, and for at least this many seconds. At the default of 5 even a very short alarm keeps the lights off for 5 seconds. Your own setting.",
                    new AcceptableValueRange<float>(0f, 8f)));

            CctvSecurityLockdownDrillCooldownSeconds = config.Bind(
                MainframeSection,
                "Lockdown Drill Cooldown Seconds",
                300f,
                new ConfigDescription(
                    "Base wait between lockdown drills; each wait is a random 2.5 to 4.5 times this. At the default of 300 a drill comes roughly every 12 to 22 minutes, while Security Protocols Enabled is on. Your own setting.",
                    new AcceptableValueRange<float>(60f, 1200f)));

            CctvSecurityWallAlarmFixturesEnabled = config.Bind(
                SecuritySystemsSection,
                "Spawn Wall Alarm Fixtures",
                true,
                "Places red wall alarm lights and sirens near the security cameras, and only cameras with a wall alarm in their room can watch for players. On by default; when off no wall alarms appear, but every camera can still spot players and raise alarms. Host decides.");

            CctvSecurityAlarmLightIntensity = config.Bind(
                SecuritySystemsSection,
                "Wall Alarm Light Intensity",
                8f,
                new ConfigDescription(
                    "How bright the red light of the wall alarms is; 0 turns the light off. The default is 8. Your own setting.",
                    new AcceptableValueRange<float>(0f, 40f)));

            CctvSecurityAlarmAudioVolume = config.Bind(
                SecuritySystemsSection,
                "Wall Alarm Volume",
                0.85f,
                new ConfigDescription(
                    "How loud the wall alarm sirens are, from 0 (silent) to 1 (full volume). The default is 0.85. Your own setting.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityAlarmCooldownSeconds = config.Bind(
                SecuritySystemsSection,
                "Alarm Cooldown Seconds",
                60f,
                new ConfigDescription(
                    "After an alarm ends, cameras stop watching and no new alarm can start for this many seconds. At the default of 60 players get a minute of quiet; 0 turns the pause off. Host decides.",
                    new AcceptableValueRange<float>(0f, 600f)));

            CctvSecurityAlarmAwarenessPingEnabled = config.Bind(
                SecuritySystemsSection,
                "Alarm Noise Enabled",
                true,
                "When an alarm starts, it makes one loud noise at the camera that raised it, which monsters within Alarm Noise Range can hear and may come to check out; each monster decides for itself and none is forced to go there. On by default. Host decides.");

            CctvSecurityAlarmAwarenessPingRange = config.Bind(
                SecuritySystemsSection,
                "Alarm Noise Range",
                45f,
                new ConfigDescription(
                    "How far the alarm noise reaches, in metres; monsters further away never hear it. At the default of 45 it carries further than the ship's alarm cord (30) but not as far as the item dropship (60). Host decides.",
                    new AcceptableValueRange<float>(5f, 150f)));

            CctvSecurityAlarmAwarenessPingLoudness = config.Bind(
                SecuritySystemsSection,
                "Alarm Noise Loudness",
                0.9f,
                new ConfigDescription(
                    "How loud the alarm noise is, from 0 (no noise) to 1; some monsters ignore quiet sounds even when they are in range. At the default of 0.9 it is as loud as a boombox. Host decides.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityAlarmAwarenessSecondPingEnabled = config.Bind(
                SecuritySystemsSection,
                "Alarm Second Noise Enabled",
                false,
                "Makes a second, smaller alarm noise a few seconds after the first, to nudge monsters that are already on their way. Off by default, so an alarm makes just one noise. Host decides.");

            CctvSecurityAlarmAwarenessSecondPingDelaySeconds = config.Bind(
                SecuritySystemsSection,
                "Alarm Second Noise Delay Seconds",
                4f,
                new ConfigDescription(
                    "Seconds between the first alarm noise and the second one. At the default of 4 the second noise comes 4 seconds after the first; it only matters while Alarm Second Noise Enabled is on. Host decides.",
                    new AcceptableValueRange<float>(1f, 20f)));
        }

        // Kept verbatim from ContractsBootstrap so companion-mod ammo remains available by
        // default while the surrounding setting is generalized to any stash loot item.
        private const string DefaultStashLootPool =
            "Shotgun Shells|30|1;Small Caliber Rounds|30|1;Heavy Caliber Rounds|30|1;Fuel Tank|15|1;Pistol|10|1;Bolt-Action Rifle|8|1;Assault Rifle|6|1";

        private static ConfigEntry<int> BindStashCount(
            ConfigFile config, string key, int defaultValue, bool isMinimum, string moons, string pairedKey)
        {
            string description = isMinimum
                ? $"The fewest Company Stashes placed on {moons}; at the default of {defaultValue} each round on those moons has at least {defaultValue} stashes. If it is above {pairedKey}, that maximum is raised to match, and setting both to 0 means no stashes on these moons. Host decides."
                : $"The most Company Stashes placed on {moons}; at the default of {defaultValue} each round on those moons has at most {defaultValue} stashes. It is never lower than {pairedKey}, and setting both to 0 means no stashes on these moons. Host decides.";
            return config.Bind(
                CompanyStashesSection,
                key,
                defaultValue,
                new ConfigDescription(
                    description,
                    new AcceptableValueRange<int>(0, 12)));
        }

        /// <summary>
        /// Sections this plugin now owns but which were written into the shared
        /// <c>com.y4ngz.company.cfg</c> before the #393 split. Both LethalCCTVConfig's own
        /// entries (the 8x - CCTV blocks) and the security/stash entries that used to be
        /// bound by ContractsBootstrap are covered.
        /// </summary>
        private static readonly string[] LegacySections =
        {
            "80 - CCTV - Ship Terminal",
            "81 - CCTV - Camera Selection",
            "82 - CCTV - Camera Placement",
            "83 - CCTV - Ship Monitor",
            "84 - CCTV - Rendering",
            "85 - CCTV - Display and Lighting",
            "86 - CCTV - Physical Cameras",
            "87 - CCTV - Operator Controls",
            "88 - CCTV - Exposure",
            "88 - CCTV - Station Props",
            "89 - CCTV - Diagnostics",
            "90 - CCTV - Radar",
            "CCTV Radar",
            "95 - Interior Support",
            "96 - CCTV Security - Shared",
            "97 - CCTV Security - Alarms",
            "98 - CCTV Security - Protocols",
            "StashLoot",
        };

        /// <summary>
        /// One-way, best-effort import of the settings these systems had while they lived
        /// inside Y4NGZCompany. The imported legacy names are normalized by
        /// <see cref="CctvConfigSurfaceMigration"/> immediately afterward.
        ///
        /// A section is imported only when this plugin's own file does not already carry it,
        /// so a value edited here is never overwritten by the stale copy left behind in the
        /// old file. The old file is read as text and never written: Y4NGZCompany still owns
        /// it. Every failure is swallowed - a config that cannot be imported must not stop
        /// the plugin.
        /// </summary>
        internal static void TryImportLegacyConfig(ConfigFile config, ManualLogSource logger)
        {
            const string legacyConfigFileName = "com.y4ngz.company.cfg";

            try
            {
                string targetPath = config?.ConfigFilePath;
                if (string.IsNullOrEmpty(targetPath))
                    return;

                string existing = File.Exists(targetPath) ? File.ReadAllText(targetPath) : string.Empty;

                string legacyPath = Path.Combine(Paths.ConfigPath, legacyConfigFileName);
                if (!File.Exists(legacyPath))
                    return;

                var wanted = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < LegacySections.Length; i++)
                {
                    string header = "[" + LegacySections[i] + "]";
                    if (existing.IndexOf(header, StringComparison.Ordinal) < 0)
                        wanted.Add(LegacySections[i]);
                }

                if (wanted.Count == 0)
                    return;

                var imported = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                string currentSection = null;
                foreach (string rawLine in File.ReadAllLines(legacyPath))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) &&
                        line.EndsWith("]", StringComparison.Ordinal))
                    {
                        string name = line.Substring(1, line.Length - 2);
                        currentSection = wanted.Contains(name) ? name : null;
                        continue;
                    }

                    if (currentSection == null || line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    int split = line.IndexOf('=');
                    if (split <= 0)
                        continue;

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    if (key.Length == 0)
                        continue;

                    if (!imported.TryGetValue(currentSection, out List<string> entries))
                    {
                        entries = new List<string>();
                        imported[currentSection] = entries;
                    }

                    entries.Add(key + " = " + value);
                }

                if (imported.Count == 0)
                    return;

                var text = new StringBuilder();
                if (File.Exists(targetPath))
                {
                    text.Append(existing);
                    text.AppendLine();
                }
                else
                {
                    string directory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                }

                int total = 0;
                foreach (KeyValuePair<string, List<string>> section in imported)
                {
                    text.AppendLine("[" + section.Key + "]");
                    text.AppendLine();
                    for (int i = 0; i < section.Value.Count; i++)
                    {
                        text.AppendLine(section.Value[i]);
                        total++;
                    }

                    text.AppendLine();
                }

                File.WriteAllText(targetPath, text.ToString());
                config.Reload();

                logger?.LogInfo(
                    $"[LethalCCTV] Imported {total} setting(s) across {imported.Count} section(s) from "
                    + $"'{legacyConfigFileName}' into '{Path.GetFileName(targetPath)}'.");
            }
            catch (Exception e)
            {
                logger?.LogWarning(
                    "[LethalCCTV] Legacy config import skipped ("
                    + e.GetType().Name + ": " + e.Message + "); defaults are in force.");
            }
        }
    }
}
