using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Y4NGZCompany.Bootstrap
{
    internal static class CctvConfigSurfaceMigration
    {
        private const string V2BackupSuffix = ".pre-config-v2.bak";
        private const string V3BackupSuffix = ".pre-config-v3.bak";

        // The one-time presentation revision markers live here since config v3. A marker
        // under its v2 section counts as applied too, so the value passes never run twice.
        private const string RevisionMarkerSection = "Diagnostics";

        private static readonly Dictionary<string, string> SectionRenames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["80 - CCTV - Ship Terminal"] = "Ship Terminal",
                ["81 - CCTV - Camera Selection"] = "Camera Placement",
                ["83 - CCTV - Ship Monitor"] = "Ship Monitor",
                ["84 - CCTV - Rendering"] = "Performance",
                ["85 - CCTV - Display and Lighting"] = "Display and Lighting",
                ["86 - CCTV - Physical Cameras"] = "Physical Cameras",
                ["87 - CCTV - Operator Controls"] = "Operator Controls",
                ["89 - CCTV - Diagnostics"] = "Diagnostics",
                ["90 - CCTV - Radar"] = "Radar",
                ["CCTV Radar"] = "Radar",
                ["95 - Interior Support"] = "Interior Support",
                ["96 - CCTV Security - Shared"] = "CCTV Security",
                ["97 - CCTV Security - Alarms"] = "Wall Alarms",
                ["98 - CCTV Security - Protocols"] = "Security Protocols",
                ["StashLoot"] = "Company Stashes",
            };

        private static readonly HashSet<string> RetiredSections =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "82 - CCTV - Camera Placement",
                "88 - CCTV - Exposure",
                "88 - CCTV - Station Props",
            };

        private static readonly HashSet<string> RetiredOperatorKeys =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Use Interactions API Operator Session",
                "Enable Local Authored Operator Animations",
                "Pitch Clamp Degrees",
                "Yaw Clamp Degrees",
            };

        // Config v3: v2 names (old section, old key) -> eight-section names (new section, new key).
        // Frozen history: literal strings, never the bind-site constants, so renaming a
        // setting later cannot silently change what an old file migrates to.
        private static readonly string[,] V3Renames =
            {
                { "Ship Terminal", "CCTV Terminal Price", "General", "CCTV Terminal Price" },
                { "Camera Placement", "Exclude Entrance Tiles", "Camera Placement", "Exclude Main Entrance Rooms" },
                { "Camera Placement", "Exclude Fire Exit Tiles", "Camera Placement", "Exclude Fire Exit Rooms" },
                { "Camera Placement", "Exclude Tiny Tiles", "Camera Placement", "Exclude Tiny Rooms" },
                { "Camera Placement", "Tiny Tile Max Floor Area", "Camera Placement", "Tiny Room Max Floor Area" },
                { "Camera Placement", "Tile Name Exclusion Patterns", "Camera Placement", "Excluded Room Names" },
                { "Camera Placement", "Junction Min Degree", "Camera Placement", "Junction Min Connected Doorways" },
                { "Camera Placement", "Linear Chain Coverage", "Camera Placement", "Coverage Without Junctions" },
                { "Camera Placement", "Leaf Camera Min Area", "Camera Placement", "Extra Room Camera Min Area" },
                { "Camera Placement", "One Camera Per Tile", "Camera Placement", "One Camera Per Room" },
                { "Camera Counts", "Minimum Cameras", "Camera Placement", "Minimum Cameras" },
                { "Camera Counts", "Maximum Cameras", "Camera Placement", "Maximum Cameras" },
                { "Camera Counts", "Camera Budget Priority", "Camera Placement", "Camera Budget Priority" },
                { "Physical Cameras", "Physical Camera Visuals Enabled", "Camera Placement", "Physical Camera Visuals Enabled" },
                { "Physical Cameras", "Physical Camera Visual Scale", "Camera Placement", "Physical Camera Visual Scale" },
                { "CCTV Security", "Enabled", "Security Systems", "Security Enabled" },
                { "CCTV Security", "EnabledRiskD", "Security Systems", "Security Enabled - Risk D" },
                { "CCTV Security", "EnabledRiskC", "Security Systems", "Security Enabled - Risk C" },
                { "CCTV Security", "EnabledRiskB", "Security Systems", "Security Enabled - Risk B" },
                { "CCTV Security", "EnabledRiskA", "Security Systems", "Security Enabled - Risk A" },
                { "CCTV Security", "EnabledRiskS", "Security Systems", "Security Enabled - Risk S" },
                { "CCTV Security", "ActiveCameraRatio", "Security Systems", "Active Camera Share - Unknown Risk" },
                { "CCTV Security", "ActiveCameraRatioRiskD", "Security Systems", "Active Camera Share - Risk D" },
                { "CCTV Security", "ActiveCameraRatioRiskC", "Security Systems", "Active Camera Share - Risk C" },
                { "CCTV Security", "ActiveCameraRatioRiskB", "Security Systems", "Active Camera Share - Risk B" },
                { "CCTV Security", "ActiveCameraRatioRiskA", "Security Systems", "Active Camera Share - Risk A" },
                { "CCTV Security", "ActiveCameraRatioRiskS", "Security Systems", "Active Camera Share - Risk S" },
                { "CCTV Security", "ActiveCameraMin", "Security Systems", "Minimum Active Cameras" },
                { "CCTV Security", "ActiveCameraMax", "Security Systems", "Maximum Active Cameras - Unknown Risk" },
                { "CCTV Security", "RotationSeconds", "Security Systems", "Active Camera Rotation Seconds" },
                { "CCTV Security", "DetectionSeconds", "Security Systems", "Detection Seconds - Unknown Risk" },
                { "CCTV Security", "DetectionSecondsRiskD", "Security Systems", "Detection Seconds - Risk D" },
                { "CCTV Security", "DetectionSecondsRiskC", "Security Systems", "Detection Seconds - Risk C" },
                { "CCTV Security", "DetectionSecondsRiskB", "Security Systems", "Detection Seconds - Risk B" },
                { "CCTV Security", "DetectionSecondsRiskA", "Security Systems", "Detection Seconds - Risk A" },
                { "CCTV Security", "DetectionSecondsRiskS", "Security Systems", "Detection Seconds - Risk S" },
                { "CCTV Security", "SpottingAlertEnabled", "Security Systems", "Spotting Alert Enabled" },
                { "CCTV Security", "SpottingAlertIntensity", "Security Systems", "Spotting Alert Intensity" },
                { "CCTV Security", "AlarmDurationSeconds", "Security Systems", "Alarm Duration Seconds" },
                { "CCTV Security", "Breakable Cameras", "Security Systems", "Breakable Cameras" },
                { "CCTV Security", "Camera Health", "Security Systems", "Camera Health" },
                { "Wall Alarms", "AlarmBlackoutSeconds", "Security Systems", "Alarm Blackout Seconds" },
                { "Wall Alarms", "Spawn Wall Alarm Fixtures", "Security Systems", "Spawn Wall Alarm Fixtures" },
                { "Wall Alarms", "AlarmLightIntensity", "Security Systems", "Wall Alarm Light Intensity" },
                { "Wall Alarms", "AlarmAudioVolume", "Security Systems", "Wall Alarm Volume" },
                { "Wall Alarms", "AlarmCooldownSeconds", "Security Systems", "Alarm Cooldown Seconds" },
                { "Wall Alarms", "AwarenessPingEnabled", "Security Systems", "Alarm Noise Enabled" },
                { "Wall Alarms", "AwarenessPingRange", "Security Systems", "Alarm Noise Range" },
                { "Wall Alarms", "AwarenessPingLoudness", "Security Systems", "Alarm Noise Loudness" },
                { "Wall Alarms", "AwarenessSecondPingEnabled", "Security Systems", "Alarm Second Noise Enabled" },
                { "Wall Alarms", "AwarenessSecondPingDelaySeconds", "Security Systems", "Alarm Second Noise Delay Seconds" },
                { "Wall Alarms", "Close Lockdown Gates During Alarms", "Security Systems", "Close Lockdown Gates During Alarms" },
                { "Physical Cameras", "Security Sweep Enabled", "Security Systems", "Security Sweep Enabled" },
                { "Physical Cameras", "Security Sweep Yaw Degrees", "Security Systems", "Security Sweep Degrees" },
                { "Physical Cameras", "Security Sweep Seconds", "Security Systems", "Security Sweep Seconds" },
                { "Physical Cameras", "Security Track Degrees Per Second", "Security Systems", "Security Track Degrees Per Second" },
                { "Physical Cameras", "Security Detection Indicator Intensity", "Security Systems", "Security Detection Indicator Intensity" },
                { "Physical Cameras", "Security Idle Cone Intensity", "Security Systems", "Security Idle Cone Intensity" },
                { "Physical Cameras", "Security Lens Dot Glow", "Security Systems", "Security Lens Dot Glow" },
                { "Security Protocols", "Enabled", "Mainframe", "Security Protocols Enabled" },
                { "Security Protocols", "ProtocolBlackoutSeconds", "Mainframe", "Protocol Blackout Seconds" },
                { "Security Protocols", "LockdownDrillCooldownSeconds", "Mainframe", "Lockdown Drill Cooldown Seconds" },
                { "Ship Monitor", "Cycle Seconds", "Ship Monitor", "Camera Cycle Seconds" },
                { "Ship Monitor", "GeneralImprovements Feed Screen Index", "Ship Monitor", "General Improvements Feed Screen" },
                { "Ship Monitor", "GeneralImprovements Radar Screen Index", "Ship Monitor", "General Improvements Radar Screen" },
                { "Performance", "Render Hz Per Monitor", "Ship Monitor", "Render Hz Per Monitor" },
                { "Performance", "Bodycam Render Hz", "Ship Monitor", "Bodycam Render Hz" },
                { "Performance", "Dynamic Active Renderer Enabled", "Ship Monitor", "Slow Unfocused Panes" },
                { "Performance", "Inactive Pane Render Hz", "Ship Monitor", "Unfocused Pane Render Hz" },
                { "Performance", "Unmanned Idle Render Hz", "Ship Monitor", "Unmanned Idle Render Hz" },
                { "Performance", "Render RT Width", "Ship Monitor", "Feed Resolution Width" },
                { "Performance", "Far Clip Plane", "Ship Monitor", "View Distance" },
                { "Performance", "Field Of View", "Ship Monitor", "Field Of View" },
                { "Performance", "CCTV Shadow Maps Enabled", "Ship Monitor", "CCTV Shadows Enabled" },
                { "Performance", "CCTV Ambient Occlusion Enabled", "Ship Monitor", "CCTV Ambient Occlusion Enabled" },
                { "Display and Lighting", "Night Vision Enabled", "Ship Monitor", "Night Vision Enabled" },
                { "Display and Lighting", "Night Vision Gain", "Ship Monitor", "Night Vision Gain" },
                { "Display and Lighting", "Night Vision Auto Gain", "Ship Monitor", "Night Vision Auto Gain" },
                { "Display and Lighting", "Automatic Low Light", "Ship Monitor", "Automatic Low Light" },
                { "Display and Lighting", "Night Vision Flip Y", "Ship Monitor", "Flip Feed Vertically" },
                { "Display and Lighting", "Feed Color Retention", "Ship Monitor", "Feed Color Retention" },
                { "Display and Lighting", "Feed Scanline Strength", "Ship Monitor", "Feed Scanline Strength" },
                { "Display and Lighting", "Feed Noise Strength", "Ship Monitor", "Feed Noise Strength" },
                { "Display and Lighting", "Feed Vignette Strength", "Ship Monitor", "Feed Vignette Strength" },
                { "Display and Lighting", "Feed Chromatic Aberration", "Ship Monitor", "Feed Chromatic Aberration" },
                { "Display and Lighting", "Feed Roll Bar Strength", "Ship Monitor", "Feed Roll Bar Strength" },
                { "Display and Lighting", "Feed Roll Bar Speed", "Ship Monitor", "Feed Roll Bar Speed" },
                { "Display and Lighting", "Feed Curvature Strength", "Ship Monitor", "Feed Curvature Strength" },
                { "Display and Lighting", "Feed Interlace Strength", "Ship Monitor", "Feed Interlace Strength" },
                { "Display and Lighting", "Feed Dropout Strength", "Ship Monitor", "Feed Dropout Strength" },
                { "Display and Lighting", "CCTV Fill Light Enabled", "Ship Monitor", "CCTV Fill Light Enabled" },
                { "Display and Lighting", "CCTV Render Fill Light Enabled", "Ship Monitor", "CCTV Render Fill Light Enabled" },
                { "Display and Lighting", "CCTV Fill Light Intensity", "Ship Monitor", "CCTV Fill Light Intensity" },
                { "Display and Lighting", "CCTV Fill Light Range", "Ship Monitor", "CCTV Fill Light Range" },
                { "Tracking Boxes", "Show Main Entrance Tracking Box", "Ship Monitor", "Show Main Entrance Tracking Box" },
                { "Tracking Boxes", "Show Fire Exit Tracking Boxes", "Ship Monitor", "Show Fire Exit Tracking Boxes" },
                { "Tracking Boxes", "Show Apparatus Tracking Box", "Ship Monitor", "Show Apparatus Tracking Box" },
                { "Operator Controls", "Show Operator Controls Overlay", "Ship Monitor", "Show Operator Controls Overlay" },
                { "Operator Controls", "Enable Operator Debug Tools", "Ship Monitor", "Enable Operator Debug Tools" },
                { "Operator Controls", "Mouselook Sensitivity Multiplier", "Ship Monitor", "Mouselook Sensitivity Multiplier" },
                { "Operator Permissions", "Allow Radar View", "Ship Monitor", "Allow Radar View" },
                { "Operator Permissions", "Allow Camera Pings", "Ship Monitor", "Allow Camera Pings" },
                { "Operator Permissions", "Allow Walkie Talkie", "Ship Monitor", "Allow Walkie Talkie" },
                { "Operator Permissions", "Allow Target Scanning", "Ship Monitor", "Allow Target Scanning" },
                { "Operator Permissions", "Allow Remote Hacking", "Ship Monitor", "Allow Remote Hacking" },
                { "Display and Lighting", "Low Light Revision", "Diagnostics", "Low Light Revision" },
                { "Display and Lighting", "Machine Vision Revision", "Diagnostics", "Machine Vision Revision" },
                { "Performance", "Feed Quality Revision", "Diagnostics", "Feed Quality Revision" },
            };

        // Config v3 drops these (section, key) rows. Not LethalCCTV settings: leftovers of an old
        // legacy import of `95 - Interior Support`; Y4NGZCompany owns them in its own file.
        private static readonly string[,] V3Retired =
            {
                { "Interior Support", "DetailedPlacementRejectLogging" },
            };

        private static readonly Dictionary<string, Dictionary<string, KeyValuePair<string, string>>> V3Lookup =
            BuildV3Lookup();

        private sealed class MigratedValue
        {
            internal string Value;
            internal int Priority;
        }

        internal static void Migrate(ConfigFile config, ManualLogSource logger)
        {
            string path = config?.ConfigFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                string[] lines = File.ReadAllLines(path);
                var sections = new Dictionary<string, Dictionary<string, MigratedValue>>(StringComparer.Ordinal);
                var sectionOrder = new List<string>();
                string sourceSection = string.Empty;
                bool v2Changed = false;
                int migrated = 0;
                int retired = 0;
                MigratedValue legacyFixtures = null;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        sourceSection = line.Substring(1, line.Length - 2);
                        continue;
                    }

                    int split = line.IndexOf('=');
                    if (split <= 0 || string.IsNullOrEmpty(sourceSection) || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    string sourceKey = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    if (sourceKey.Length == 0)
                        continue;
                    if ((sourceSection == "Interior Support" || sourceSection == "95 - Interior Support") &&
                        sourceKey == "AutoSpawnFixtures")
                    {
                        int legacyPriority = sourceSection == "Interior Support" ? 2 : 1;
                        if (legacyFixtures == null || legacyPriority >= legacyFixtures.Priority)
                            legacyFixtures = new MigratedValue { Value = value, Priority = legacyPriority };
                        v2Changed = true;
                        retired++;
                        continue;
                    }

                    if (RetiredSections.Contains(sourceSection) ||
                        ((sourceSection == "87 - CCTV - Operator Controls" || sourceSection == "Operator Controls") && RetiredOperatorKeys.Contains(sourceKey)))
                    {
                        v2Changed = true;
                        retired++;
                        continue;
                    }

                    string targetSection = SectionRenames.TryGetValue(sourceSection, out string renamedSection)
                        ? renamedSection
                        : sourceSection;
                    string targetKey = sourceKey;

                    if (sourceSection == "81 - CCTV - Camera Selection" && sourceKey == "Camera Budget Cap")
                    {
                        targetSection = "Camera Counts";
                        targetKey = "Maximum Cameras";
                    }
                    else if (sourceSection == "81 - CCTV - Camera Selection" && sourceKey == "Camera Budget Priority")
                    {
                        targetSection = "Camera Counts";
                    }
                    else if (sourceSection == "StashLoot" && sourceKey == "Extra Ammo Rolls")
                    {
                        targetKey = "Loot Rolls";
                    }
                    else if (sourceSection == "StashLoot" && sourceKey == "Ammo Items")
                    {
                        targetKey = "Loot Pool";
                    }
                    else if (targetSection == "CCTV Security" && sourceKey == "BreakableCamerasEnabled")
                    {
                        targetKey = "Breakable Cameras";
                    }
                    else if (targetSection == "CCTV Security" && sourceKey == "CameraHealth")
                    {
                        targetKey = "Camera Health";
                    }

                    bool isLegacy = targetSection != sourceSection || targetKey != sourceKey;
                    if (isLegacy)
                    {
                        v2Changed = true;
                        migrated++;
                    }

                    Dictionary<string, MigratedValue> entries = GetOrAddSection(sections, sectionOrder, targetSection);

                    int priority = isLegacy ? 1 : 2;
                    if (!entries.TryGetValue(targetKey, out MigratedValue existing) || priority >= existing.Priority)
                    {
                        entries[targetKey] = new MigratedValue { Value = value, Priority = priority };
                    }
                }

                if (legacyFixtures != null &&
                    bool.TryParse(legacyFixtures.Value, out bool fixturesEnabled) && !fixturesEnabled)
                {
                    SetLegacyDisabled(sections, sectionOrder, "Mainframe", "Enabled");
                    SetLegacyDisabled(sections, sectionOrder, "Company Stashes", "Enabled");
                    SetLegacyDisabled(sections, sectionOrder, "Wall Alarms", "Spawn Wall Alarm Fixtures");
                }

                // Apply the new presentation once. Only replace the shipped analog
                // defaults; deliberately customized strengths survive this migration.
                // The value passes act on v2 names; the v3 pass below moves the results.
                Dictionary<string, MigratedValue> display = GetOrAddSection(sections, sectionOrder, "Display and Lighting");
                if (!HasRevisionMarker(sections, "Display and Lighting", "Machine Vision Revision"))
                {
                    MigrateDisplayDefault(display, "Feed Curvature Strength", 0.12f, 0f);
                    MigrateDisplayDefault(display, "Feed Vignette Strength", 0.12f, 0f);
                    MigrateDisplayDefault(display, "Feed Chromatic Aberration", 0.25f, 0f);
                    MigrateDisplayDefault(display, "Feed Roll Bar Strength", 0.10f, 0f);
                    MigrateDisplayDefault(display, "Feed Interlace Strength", 0.06f, 0f);
                    MigrateDisplayDefault(display, "Feed Dropout Strength", 0.04f, 0f);
                    MigrateDisplayDefault(display, "Feed Scanline Strength", 0.055f, 0.018f);
                    MigrateDisplayDefault(display, "Feed Noise Strength", 0.018f, 0.006f);
                    SetRevisionMarker(sections, sectionOrder, "Machine Vision Revision");
                    string displayBackup = path + ".pre-machine-vision.bak";
                    if (!File.Exists(displayBackup)) File.Copy(path, displayBackup);
                    v2Changed = true;
                }

                Dictionary<string, MigratedValue> performance = GetOrAddSection(sections, sectionOrder, "Performance");
                if (!HasRevisionMarker(sections, "Performance", "Feed Quality Revision"))
                {
                    // Only the complete shipped low-quality pair is upgraded. A
                    // deliberately chosen resolution/shadow combination survives.
                    if (performance.TryGetValue("Render RT Width", out var width) && width.Value == "512" &&
                        performance.TryGetValue("CCTV Shadow Maps Enabled", out var shadows) &&
                        string.Equals(shadows.Value, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        width.Value = "768";
                        shadows.Value = "true";
                    }
                    SetRevisionMarker(sections, sectionOrder, "Feed Quality Revision");
                    string qualityBackup = path + ".pre-feed-quality.bak";
                    if (!File.Exists(qualityBackup)) File.Copy(path, qualityBackup);
                    v2Changed = true;
                }

                if (!HasRevisionMarker(sections, "Display and Lighting", "Low Light Revision"))
                {
                    MigrateDisplayDefault(display, "Night Vision Gain", 2f, 4f);
                    MigrateDisplayDefault(display, "Feed Color Retention", 0.35f, 0.08f);
                    MigrateDisplayDefault(performance, "Far Clip Plane", 40f, 120f);
                    SetRevisionMarker(sections, sectionOrder, "Low Light Revision");
                    string lightBackup = path + ".pre-low-light.bak";
                    if (!File.Exists(lightBackup)) File.Copy(path, lightBackup);
                    v2Changed = true;
                }

                // Config v3: move every v2 name onto the eight-section layout. A value
                // already under its v3 name outranks a moved one regardless of file order;
                // among equals the v2 priority decides and the later entry wins ties.
                var current = new Dictionary<string, Dictionary<string, MigratedValue>>(StringComparer.Ordinal);
                var currentOrder = new List<string>();
                int v3Moved = 0;
                int v3Retired = 0;
                for (int sectionIndex = 0; sectionIndex < sectionOrder.Count; sectionIndex++)
                {
                    string section = sectionOrder[sectionIndex];
                    V3Lookup.TryGetValue(section, out Dictionary<string, KeyValuePair<string, string>> renames);
                    foreach (KeyValuePair<string, MigratedValue> entry in sections[section])
                    {
                        if (IsV3Retired(section, entry.Key))
                        {
                            v3Retired++;
                            continue;
                        }

                        string targetSection = section;
                        string targetKey = entry.Key;
                        bool moved = false;
                        if (renames != null && renames.TryGetValue(entry.Key, out KeyValuePair<string, string> target))
                        {
                            targetSection = target.Key;
                            targetKey = target.Value;
                            moved = true;
                            v3Moved++;
                        }

                        Dictionary<string, MigratedValue> entries = GetOrAddSection(current, currentOrder, targetSection);
                        int priority = (moved ? 0 : 2) + entry.Value.Priority;
                        if (!entries.TryGetValue(targetKey, out MigratedValue existing) || priority >= existing.Priority)
                            entries[targetKey] = new MigratedValue { Value = entry.Value.Value, Priority = priority };
                    }
                }

                bool v3Changed = v3Moved > 0 || v3Retired > 0;
                if (!v2Changed && !v3Changed)
                    return;

                // Both backups hold the file as it was on disk when this launch began.
                string v2BackupPath = path + V2BackupSuffix;
                if (v2Changed && !File.Exists(v2BackupPath))
                    File.Copy(path, v2BackupPath, overwrite: false);
                string v3BackupPath = path + V3BackupSuffix;
                if (v3Changed && !File.Exists(v3BackupPath))
                    File.Copy(path, v3BackupPath, overwrite: false);

                var output = new StringBuilder();
                for (int sectionIndex = 0; sectionIndex < currentOrder.Count; sectionIndex++)
                {
                    string section = currentOrder[sectionIndex];
                    Dictionary<string, MigratedValue> entries = current[section];
                    if (entries.Count == 0)
                        continue;

                    output.AppendLine("[" + section + "]");
                    output.AppendLine();
                    foreach (KeyValuePair<string, MigratedValue> entry in entries)
                        output.AppendLine(entry.Key + " = " + entry.Value.Value);
                    output.AppendLine();
                }

                File.WriteAllText(path, output.ToString());
                config.Reload();
                if (v2Changed)
                    logger?.LogInfo(
                        $"[LethalCCTV] Config v2 migration moved {migrated} setting(s), retired {retired} setting(s), and saved backup '{Path.GetFileName(v2BackupPath)}'.");
                if (v3Changed)
                    logger?.LogInfo(
                        $"[LethalCCTV] Config v3 migration moved {v3Moved} and retired {v3Retired} setting(s), and saved backup '{Path.GetFileName(v3BackupPath)}'.");
            }
            catch (Exception exception)
            {
                logger?.LogWarning(
                    $"[LethalCCTV] Config migration skipped ({exception.GetType().Name}: {exception.Message}); existing values were left untouched.");
            }
        }

        private static Dictionary<string, Dictionary<string, KeyValuePair<string, string>>> BuildV3Lookup()
        {
            var lookup = new Dictionary<string, Dictionary<string, KeyValuePair<string, string>>>(StringComparer.Ordinal);
            for (int row = 0; row < V3Renames.GetLength(0); row++)
            {
                if (!lookup.TryGetValue(V3Renames[row, 0], out Dictionary<string, KeyValuePair<string, string>> keys))
                {
                    keys = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
                    lookup[V3Renames[row, 0]] = keys;
                }

                keys.Add(V3Renames[row, 1], new KeyValuePair<string, string>(V3Renames[row, 2], V3Renames[row, 3]));
            }

            return lookup;
        }

        private static bool IsV3Retired(string section, string key)
        {
            for (int row = 0; row < V3Retired.GetLength(0); row++)
            {
                if (V3Retired[row, 0] == section && V3Retired[row, 1] == key)
                    return true;
            }

            return false;
        }

        private static Dictionary<string, MigratedValue> GetOrAddSection(
            Dictionary<string, Dictionary<string, MigratedValue>> sections, List<string> sectionOrder, string section)
        {
            if (!sections.TryGetValue(section, out Dictionary<string, MigratedValue> entries))
            {
                entries = new Dictionary<string, MigratedValue>(StringComparer.Ordinal);
                sections[section] = entries;
                sectionOrder.Add(section);
            }

            return entries;
        }

        private static bool HasRevisionMarker(
            Dictionary<string, Dictionary<string, MigratedValue>> sections, string v2Section, string key) =>
            (sections.TryGetValue(v2Section, out Dictionary<string, MigratedValue> legacy) && legacy.ContainsKey(key)) ||
            (sections.TryGetValue(RevisionMarkerSection, out Dictionary<string, MigratedValue> markers) && markers.ContainsKey(key));

        private static void SetRevisionMarker(
            Dictionary<string, Dictionary<string, MigratedValue>> sections, List<string> sectionOrder, string key) =>
            GetOrAddSection(sections, sectionOrder, RevisionMarkerSection)[key] = new MigratedValue { Value = "1", Priority = 2 };

        private static void SetLegacyDisabled(
            Dictionary<string, Dictionary<string, MigratedValue>> sections, List<string> sectionOrder,
            string section, string key)
        {
            Dictionary<string, MigratedValue> entries = GetOrAddSection(sections, sectionOrder, section);

            // A current-name value wins over the retired gate, regardless of file order.
            if (!entries.TryGetValue(key, out MigratedValue existing) || existing.Priority < 2)
                entries[key] = new MigratedValue { Value = "false", Priority = 1 };
        }

        private static void MigrateDisplayDefault(Dictionary<string, MigratedValue> entries, string key, float oldValue, float newValue)
        {
            if (entries.TryGetValue(key, out var entry) &&
                float.TryParse(entry.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                Math.Abs(value - oldValue) < 0.00001f)
                entry.Value = newValue.ToString(CultureInfo.InvariantCulture);
        }
    }
}
