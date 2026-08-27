using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace Y4NGZCompany.Core
{
    /// <summary>
    /// #600. A modpack or a hand-built profile can resolve an OLDER copy of one of our hard
    /// dependencies than we compiled against, because some other mod pins it: LethalPhones,
    /// for example, pins Evaisa-LethalLib-0.16.2 and Rune580-LethalCompany_InputUtils-0.6.3,
    /// and Gale happily resolves those over our 1.2.0 / 0.7.13.
    ///
    /// BepInEx does not check dependency *versions* for us — <c>BepInDependency</c> without a
    /// version range is satisfied by any build carrying the GUID — so the first sign of the
    /// mismatch is a <see cref="TypeLoadException"/> or <see cref="MissingMethodException"/>
    /// deep inside our own code, naming a type the user has never heard of. Worse, the old
    /// dependency itself may simply have failed to load against the current game, in which
    /// case its API calls succeed and quietly do nothing (LethalLib 0.16.2's terminal patches
    /// never run, so a store registration made through it never materialises).
    ///
    /// This audit runs once at plugin Awake and turns that into one loud, readable error line
    /// per outdated package. It changes no behaviour when every dependency is current.
    ///
    /// Caveat worth knowing before adding entries: the version compared here is the plugin's
    /// own <c>BepInPlugin</c> version, which for these five packages matches the Thunderstore
    /// version. A package whose internal version lags its listing would produce a false
    /// positive, so only add dependencies whose numbering is known to line up.
    /// </summary>
    internal static class DependencyVersionAudit
    {
        /// <summary>The one sentence every mismatch ends with. Users read this, not the stack.</summary>
        internal const string UpdateAdvice =
            "Update it in your mod manager; another installed mod may be pinning an old version " +
            "(e.g. LethalPhones pins LethalLib 0.16.2 / InputUtils 0.6.3).";

        internal const string LethalLibGuid = "evaisa.lethallib";
        internal const string InputUtilsGuid = "com.rune580.LethalCompanyInputUtils";
        internal const string CSyncGuid = "com.sigurd.csync";
        internal const string LethalNetworkApiGuid = "LethalNetworkAPI";
        internal const string DawnLibGuid = "com.github.teamxiaolan.dawnlib";

        /// <summary>
        /// LethalCCTV's hard dependencies and the minimum each one is compiled against.
        /// Keep in step with src\LethalCCTV\LethalCCTV.csproj PackageReferences and the
        /// LethalCCTV dependency list in release\stage-all.ps1 — those three are one fact.
        /// The two Y4NGZ-owned hard dependencies (Y4NGZCore, Y4NGZInteractions) are shipped
        /// as one set with this plugin and are deliberately not audited here.
        /// </summary>
        private static readonly Requirement[] LethalCCTVRequirements =
        {
            new Requirement(LethalLibGuid, "LethalLib", "1.2.0",
                "the CCTV Terminal will be missing from the ship store"),
            new Requirement(InputUtilsGuid, "LethalCompanyInputUtils", "0.7.13",
                "the CCTV monitor's keyboard controls will not work"),
            new Requirement(CSyncGuid, "CSync", "5.0.1",
                "host-synced CCTV config values may fail to sync"),
            new Requirement(LethalNetworkApiGuid, "LethalNetworkAPI", "3.4.2",
                "CCTV networking may fail in multiplayer"),
            new Requirement(DawnLibGuid, "DawnLib", "0.9.25",
                "facility CCTV content may fail to register"),
        };

        private static readonly Dictionary<string, string> _outdated =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static bool _ran;

        /// <summary>Runs the LethalCCTV dependency audit. Safe to call more than once.</summary>
        internal static void RunForLethalCCTV(ManualLogSource log)
        {
            Run(log, "LethalCCTV", LethalCCTVRequirements);
        }

        internal static void Run(ManualLogSource log, string pluginName, IReadOnlyList<Requirement> requirements)
        {
            if (_ran || requirements == null)
                return;

            _ran = true;

            for (int i = 0; i < requirements.Count; i++)
            {
                Requirement requirement = requirements[i];
                Version installed = TryGetInstalledVersion(requirement.Guid);
                if (installed == null || installed >= requirement.Minimum)
                    continue;

                _outdated[requirement.Guid] =
                    $"{requirement.PackageName} {installed} is installed, but {pluginName} needs " +
                    $"{requirement.Minimum} or newer";

                log?.LogError(
                    $"[{pluginName}] Outdated dependency: {requirement.PackageName} {installed} is installed, " +
                    $"but {pluginName} needs {requirement.PackageName} {requirement.Minimum} or newer. " +
                    $"Until it is updated, {requirement.Consequence}. {UpdateAdvice}");
            }
        }

        /// <summary>
        /// "&lt;package&gt; x.y.z is installed, but LethalCCTV needs a.b.c or newer", or null when
        /// the dependency is current. For appending to a site-specific failure message.
        /// </summary>
        internal static string DescribeOutdated(string guid)
        {
            if (guid != null && _outdated.TryGetValue(guid, out string description))
                return description;

            return null;
        }

        /// <summary>
        /// True when the plugin is registered but has no live instance — i.e. BepInEx accepted
        /// the assembly and then its own <c>Awake</c> threw, which is exactly how LethalLib
        /// 0.16.2 fails on the current game (TypeLoadException on DunGen.TileSet). A GUID
        /// presence check cannot see this; the null instance can.
        /// </summary>
        internal static bool IsLoadedButDead(string guid)
        {
            if (guid == null || Chainloader.PluginInfos == null)
                return false;

            return Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info) &&
                   info != null &&
                   info.Instance == null;
        }

        private static Version TryGetInstalledVersion(string guid)
        {
            try
            {
                if (Chainloader.PluginInfos != null &&
                    Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info) &&
                    info?.Metadata != null)
                {
                    return info.Metadata.Version;
                }
            }
            catch
            {
                // A dependency we cannot inspect is not a dependency we can accuse.
            }

            return null;
        }

        internal readonly struct Requirement
        {
            internal Requirement(string guid, string packageName, string minimum, string consequence)
            {
                Guid = guid;
                PackageName = packageName;
                Minimum = new Version(minimum);
                Consequence = consequence;
            }

            internal string Guid { get; }

            internal string PackageName { get; }

            internal Version Minimum { get; }

            /// <summary>What the user loses while the old version is installed.</summary>
            internal string Consequence { get; }
        }
    }
}
