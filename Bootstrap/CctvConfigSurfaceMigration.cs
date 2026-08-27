using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Y4NGZCompany.Bootstrap
{
    internal static class CctvConfigSurfaceMigration
    {
        private const string BackupSuffix = ".pre-config-v2.bak";

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
                bool changed = false;
                int migrated = 0;
                int retired = 0;

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

                    if (RetiredSections.Contains(sourceSection) ||
                        ((sourceSection == "87 - CCTV - Operator Controls" || sourceSection == "Operator Controls") && RetiredOperatorKeys.Contains(sourceKey)))
                    {
                        changed = true;
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
                        changed = true;
                        migrated++;
                    }

                    if (!sections.TryGetValue(targetSection, out Dictionary<string, MigratedValue> entries))
                    {
                        entries = new Dictionary<string, MigratedValue>(StringComparer.Ordinal);
                        sections[targetSection] = entries;
                        sectionOrder.Add(targetSection);
                    }

                    int priority = isLegacy ? 1 : 2;
                    if (!entries.TryGetValue(targetKey, out MigratedValue existing) || priority >= existing.Priority)
                    {
                        entries[targetKey] = new MigratedValue { Value = value, Priority = priority };
                    }
                }

                if (!changed)
                    return;

                string backupPath = path + BackupSuffix;
                if (!File.Exists(backupPath))
                    File.Copy(path, backupPath, overwrite: false);

                var output = new StringBuilder();
                for (int sectionIndex = 0; sectionIndex < sectionOrder.Count; sectionIndex++)
                {
                    string section = sectionOrder[sectionIndex];
                    Dictionary<string, MigratedValue> entries = sections[section];
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
                logger?.LogInfo(
                    $"[LethalCCTV] Config v2 migration moved {migrated} setting(s), retired {retired} calibration setting(s), and saved backup '{Path.GetFileName(backupPath)}'.");
            }
            catch (Exception exception)
            {
                logger?.LogWarning(
                    $"[LethalCCTV] Config v2 migration skipped ({exception.GetType().Name}: {exception.Message}); existing values were left untouched.");
            }
        }
    }
}
