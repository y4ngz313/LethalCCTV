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
        internal const string SecuritySharedSection = "CCTV Security";
        internal const string SecurityAlarmsSection = "Wall Alarms";
        internal const string SecurityProtocolsSection = "Security Protocols";
        internal const string StashLootSection = "Company Stashes";
        internal const string InteriorSupportSection = "Interior Support";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> InteriorSupportAutoSpawnEnabled;
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

            InteriorSupportAutoSpawnEnabled = config.Bind(
                InteriorSupportSection,
                "AutoSpawnFixtures",
                true,
                "When true, the server spawns alarm box, mainframe, and mini-vaults into the deepest interior rooms of every moon.");

            StashLootRolls = config.Bind(
                StashLootSection,
                "Loot Rolls",
                2,
                "Number of weighted stash-loot rolls after the guaranteed Company Stash Gold bar spawns. Set to 0 to keep only the Gold bar.");

            StashLootPool = config.Bind(
                StashLootSection,
                "Loot Pool",
                // Refreshed 2026-08-06 (Y4NGZUpgrades release prep). The previous default still named
                // ".357 Rounds" and "Crossbow Bolt", two display names Y4NGZUpgrades retired in #148
                // and #150 — both rolls resolved to nothing. This set is the live Upgrades item names,
                // plus the three renamed firearms as rarer stash finds. Names are resolved by
                // Item.itemName at round time; a missing item is logged and skipped, so the stash
                // still works with Y4NGZUpgrades absent.
                DefaultStashLootPool,
                "Weighted Company Stash loot. Any network-spawnable grabbable item can be used, including ammo from companion mods. Format: name|rarity|count; entries are separated by semicolons. Higher rarity values make an entry more likely. Missing items are ignored before rolls are made.");

            StashMinimumRiskD = BindStashCount(config, "Minimum Stashes - Risk D", 1);
            StashMaximumRiskD = BindStashCount(config, "Maximum Stashes - Risk D", 4);
            StashMinimumRiskC = BindStashCount(config, "Minimum Stashes - Risk C", 1);
            StashMaximumRiskC = BindStashCount(config, "Maximum Stashes - Risk C", 4);
            StashMinimumRiskB = BindStashCount(config, "Minimum Stashes - Risk B", 1);
            StashMaximumRiskB = BindStashCount(config, "Maximum Stashes - Risk B", 4);
            StashMinimumRiskA = BindStashCount(config, "Minimum Stashes - Risk A", 1);
            StashMaximumRiskA = BindStashCount(config, "Maximum Stashes - Risk A", 4);
            StashMinimumRiskS = BindStashCount(config, "Minimum Stashes - Risk S", 1);
            StashMaximumRiskS = BindStashCount(config, "Maximum Stashes - Risk S", 4);
            StashMinimumUnknownRisk = BindStashCount(config, "Minimum Stashes - Unknown Risk", 1);
            StashMaximumUnknownRisk = BindStashCount(config, "Maximum Stashes - Unknown Risk", 4);

            CctvSecurityEnabled = config.Bind(
                SecuritySharedSection,
                "Enabled",
                true,
                "When true, the interior mainframe controls rotating CCTV security cameras, wall alarms, and protocol events.");

            // #466 per-risk-level security block. The master Enabled above still gates
            // everything; these decide whether a landed moon's tier runs hostile security
            // at all. #575: D and C now default on - play testing landed on hostile
            // security running at every risk tier, so a fresh install matches the tuned
            // profile instead of drifting from it.
            CctvSecurityEnabledRiskD = config.Bind(
                SecuritySharedSection,
                "EnabledRiskD",
                true,
                "When true, hostile CCTV security (active cameras, detection, alarms) runs on D-risk moons.");

            CctvSecurityEnabledRiskC = config.Bind(
                SecuritySharedSection,
                "EnabledRiskC",
                true,
                "When true, hostile CCTV security (active cameras, detection, alarms) runs on C-risk moons.");

            CctvSecurityEnabledRiskB = config.Bind(
                SecuritySharedSection,
                "EnabledRiskB",
                true,
                "When true, hostile CCTV security (active cameras, detection, alarms) runs on B-risk moons.");

            CctvSecurityEnabledRiskA = config.Bind(
                SecuritySharedSection,
                "EnabledRiskA",
                true,
                "When true, hostile CCTV security (active cameras, detection, alarms) runs on A-risk moons.");

            CctvSecurityEnabledRiskS = config.Bind(
                SecuritySharedSection,
                "EnabledRiskS",
                true,
                "When true, hostile CCTV security (active cameras, detection, alarms) runs on S-risk moons.");

            CctvSecurityActiveCameraRatio = config.Bind(
                SecuritySharedSection,
                "ActiveCameraRatio",
                0.20f,
                new ConfigDescription(
                    "Fallback fraction of unbroken eligible cameras that are security-active when the moon has no recognized C/B/A/S risk tier.",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            CctvSecurityActiveCameraRatioRiskD = config.Bind(
                SecuritySharedSection,
                "ActiveCameraRatioRiskD",
                0.10f,
                new ConfigDescription(
                    "Fraction of unbroken eligible cameras active on D-risk moons. Only reached when EnabledRiskD is turned on.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraRatioRiskC = config.Bind(
                SecuritySharedSection,
                "ActiveCameraRatioRiskC",
                0.15f,
                new ConfigDescription(
                    "Fraction of unbroken eligible cameras active on C-risk moons.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraRatioRiskB = config.Bind(
                SecuritySharedSection,
                "ActiveCameraRatioRiskB",
                0.25f,
                new ConfigDescription(
                    "Fraction of unbroken eligible cameras active on B-risk moons.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraRatioRiskA = config.Bind(
                SecuritySharedSection,
                "ActiveCameraRatioRiskA",
                0.25f,
                new ConfigDescription(
                    "Fraction of unbroken eligible cameras active on A-risk moons.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraRatioRiskS = config.Bind(
                SecuritySharedSection,
                "ActiveCameraRatioRiskS",
                0.35f,
                new ConfigDescription(
                    "Fraction of unbroken eligible cameras active on S-risk moons.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityActiveCameraMin = config.Bind(
                SecuritySharedSection,
                "ActiveCameraMin",
                1,
                new ConfigDescription(
                    "Minimum number of security-active cameras when any eligible camera exists.",
                    new AcceptableValueRange<int>(0, 12)));

            CctvSecurityActiveCameraMax = config.Bind(
                SecuritySharedSection,
                "ActiveCameraMax",
                4,
                new ConfigDescription(
                    "Fallback maximum number of security-active cameras when the moon has no recognized C/B/A/S risk tier.",
                    new AcceptableValueRange<int>(1, 24)));

            CctvSecurityRotationSeconds = config.Bind(
                SecuritySharedSection,
                "RotationSeconds",
                90f,
                new ConfigDescription(
                    "Seconds between rotating the normal security-active camera set.",
                    new AcceptableValueRange<float>(5f, 300f)));

            CctvSecurityDetectionSeconds = config.Bind(
                SecuritySharedSection,
                "DetectionSeconds",
                1.5f,
                new ConfigDescription(
                    "Continuous seconds a player must stay visible to a security-active camera before alarm triggers. Used when the moon has no recognized D/C/B/A/S risk tier; otherwise the per-tier value below applies. A global +25% slowdown is applied on top of whichever value is in force.",
                    new AcceptableValueRange<float>(0.25f, 5f)));

            // #466. Each tier's default is the flat DetectionSeconds *as it currently
            // stands in this profile*, not the shipped 1.5 - the flat key is bound above,
            // so a player who already dialled it in has that value adopted by all five
            // tiers on the first run that writes them, and an untouched config keeps the
            // shipped behaviour. Either way the only change to time-to-trip is the global
            // +25% slowdown CctvSecurityConfig applies afterwards.
            float detectionSecondsDefault = CctvSecurityDetectionSeconds.Value;
            CctvSecurityDetectionSecondsRiskD = config.Bind(
                SecuritySharedSection,
                "DetectionSecondsRiskD",
                detectionSecondsDefault,
                new ConfigDescription(
                    "Continuous seconds a player must stay visible to a security-active camera on D-risk moons.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecurityDetectionSecondsRiskC = config.Bind(
                SecuritySharedSection,
                "DetectionSecondsRiskC",
                detectionSecondsDefault,
                new ConfigDescription(
                    "Continuous seconds a player must stay visible to a security-active camera on C-risk moons.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecurityDetectionSecondsRiskB = config.Bind(
                SecuritySharedSection,
                "DetectionSecondsRiskB",
                detectionSecondsDefault,
                new ConfigDescription(
                    "Continuous seconds a player must stay visible to a security-active camera on B-risk moons.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecurityDetectionSecondsRiskA = config.Bind(
                SecuritySharedSection,
                "DetectionSecondsRiskA",
                detectionSecondsDefault,
                new ConfigDescription(
                    "Continuous seconds a player must stay visible to a security-active camera on A-risk moons.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecurityDetectionSecondsRiskS = config.Bind(
                SecuritySharedSection,
                "DetectionSecondsRiskS",
                detectionSecondsDefault,
                new ConfigDescription(
                    "Continuous seconds a player must stay visible to a security-active camera on S-risk moons.",
                    new AcceptableValueRange<float>(0.25f, 15f)));

            CctvSecuritySpottingAlertEnabled = config.Bind(
                SecuritySharedSection,
                "SpottingAlertEnabled",
                true,
                "When true, a screen-edge arc points toward each security camera currently detecting you. Per-client visual.");

            CctvSecuritySpottingAlertIntensity = config.Bind(
                SecuritySharedSection,
                "SpottingAlertIntensity",
                1f,
                new ConfigDescription(
                    "Opacity multiplier for the spotting alert arcs; 0 hides them. Requires SpottingAlertEnabled. Per-client visual.",
                    new AcceptableValueRange<float>(0f, 2f)));

            CctvSecurityAlarmDurationSeconds = config.Bind(
                SecuritySharedSection,
                "AlarmDurationSeconds",
                15f,
                new ConfigDescription(
                    "Seconds a camera-triggered alarm remains active after the most recent detection.",
                    new AcceptableValueRange<float>(3f, 120f)));

            // #575: defaults off - protocol events fired often enough to read as
            // background noise, so a fresh install now matches the tuned profile.
            CctvSecurityProtocolEventsEnabled = config.Bind(
                SecurityProtocolsSection,
                "Enabled",
                false,
                "When true, the unhacked mainframe schedules fire alarm tests, camera calibration sweeps, and lockdown drills.");

            CctvSecurityProtocolBlackoutSeconds = config.Bind(
                SecurityProtocolsSection,
                "ProtocolBlackoutSeconds",
                2.5f,
                new ConfigDescription(
                    "Seconds interior lights shut off at the start of each protocol event while wall alarms stay red.",
                    new AcceptableValueRange<float>(0f, 8f)));

            CctvSecurityAlarmBlackoutSeconds = config.Bind(
                SecurityAlarmsSection,
                "AlarmBlackoutSeconds",
                5f,
                new ConfigDescription(
                    "Seconds interior lights shut off when a CCTV security alarm first triggers while wall alarms stay red.",
                    new AcceptableValueRange<float>(0f, 8f)));

            CctvSecurityLockdownDrillCooldownSeconds = config.Bind(
                SecurityProtocolsSection,
                "LockdownDrillCooldownSeconds",
                300f,
                new ConfigDescription(
                    "Minimum seconds between lockdown drill protocol attempts.",
                    new AcceptableValueRange<float>(60f, 1200f)));

            CctvSecurityWallAlarmFixturesEnabled = config.Bind(
                SecurityAlarmsSection,
                "Spawn Wall Alarm Fixtures",
                true,
                "When false, wall-mounted red light and siren fixtures are not spawned. CCTV detection and security alarms continue to function.");

            CctvSecurityAlarmLightIntensity = config.Bind(
                SecurityAlarmsSection,
                "AlarmLightIntensity",
                8f,
                new ConfigDescription(
                    "Red light intensity for wall alarm fixtures.",
                    new AcceptableValueRange<float>(0f, 40f)));

            CctvSecurityAlarmAudioVolume = config.Bind(
                SecurityAlarmsSection,
                "AlarmAudioVolume",
                0.85f,
                new ConfigDescription(
                    "Volume multiplier for wall alarm audio sources.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityAlarmCooldownSeconds = config.Bind(
                SecurityAlarmsSection,
                "AlarmCooldownSeconds",
                60f,
                new ConfigDescription(
                    "Seconds after a security alarm ends during which no camera detects and no new alarm can engage. Set to 0 to disable the cooldown.",
                    new AcceptableValueRange<float>(0f, 600f)));

            CctvSecurityAlarmAwarenessPingEnabled = config.Bind(
                SecurityAlarmsSection,
                "AwarenessPingEnabled",
                true,
                "When true, engaging a security alarm emits one vanilla audible-noise event at the tripped camera. Enemies decide for themselves whether to investigate through their own hearing and AI; nothing is forced to path there.");

            CctvSecurityAlarmAwarenessPingRange = config.Bind(
                SecurityAlarmsSection,
                "AwarenessPingRange",
                45f,
                new ConfigDescription(
                    "Radius in metres of the alarm awareness noise. Enemies outside it are never told the alarm happened; inside it each species' own AI decides whether to investigate. For scale, vanilla runs the ship alarm cord at 30, a boombox at 16, and the item dropship - its loudest event - at 60.",
                    new AcceptableValueRange<float>(5f, 150f)));

            CctvSecurityAlarmAwarenessPingLoudness = config.Bind(
                SecurityAlarmsSection,
                "AwarenessPingLoudness",
                0.9f,
                new ConfigDescription(
                    "Loudness of the alarm awareness noise at its source, before distance falloff. Each enemy compares the attenuated value against its own hearing threshold.",
                    new AcceptableValueRange<float>(0f, 1f)));

            CctvSecurityAlarmAwarenessSecondPingEnabled = config.Bind(
                SecurityAlarmsSection,
                "AwarenessSecondPingEnabled",
                false,
                "When true, a single smaller follow-up awareness noise fires a few seconds after the first. Off by default: one ping is an event, a repeating one is a lure.");

            CctvSecurityAlarmAwarenessSecondPingDelaySeconds = config.Bind(
                SecurityAlarmsSection,
                "AwarenessSecondPingDelaySeconds",
                4f,
                new ConfigDescription(
                    "Seconds after the first awareness ping before the follow-up fires. Requires AwarenessSecondPingEnabled.",
                    new AcceptableValueRange<float>(1f, 20f)));
        }

        // Kept verbatim from ContractsBootstrap so companion-mod ammo remains available by
        // default while the surrounding setting is generalized to any stash loot item.
        private const string DefaultStashLootPool =
            "Shotgun Shells|30|1;Small Caliber Rounds|30|1;Heavy Caliber Rounds|30|1;Fuel Tank|15|1;Pistol|10|1;Bolt-Action Rifle|8|1;Assault Rifle|6|1";

        private static ConfigEntry<int> BindStashCount(ConfigFile config, string key, int defaultValue)
        {
            return config.Bind(
                StashLootSection,
                key,
                defaultValue,
                new ConfigDescription(
                    "Risk-specific Company Stash count bound. Values are normalized so the maximum can never be lower than the minimum. Set both bounds to 0 to disable stashes for this risk tier.",
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
