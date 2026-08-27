using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Mainframe;

namespace Y4NGZCompany.Facility.Interior
{
    /// <summary>
    /// Bundle access for the interior assets LethalCCTV owns: the networked CCTV mainframe
    /// support and Company Stash, plus the local-only standalone alarm lockdown gate.
    ///
    /// Before #393 this went through Y4NGZCompany's <c>ContractAssetLoader</c>, which opens
    /// <c>lgucontracts.bundle</c>. That bundle belongs to the contracts plugin and cannot be a
    /// dependency of this one, so the source is resolved through a chain that works whether
    /// this plugin ships alone or alongside Y4NGZCompany:
    ///
    /// 1. <c>lethalcctv-fixtures.bundle</c> beside this assembly. This is where the fixtures
    ///    now come from; it carries the two network prefabs, the lockdown-door visual, and
    ///    the keypad clips. A missing file still
    ///    logs one line and falls through rather than failing, so an older profile keeps
    ///    working off the contracts bundle.
    /// 2. An already-loaded bundle that carries the fixture assets. When Y4NGZCompany is
    ///    installed it has usually opened <c>lgucontracts.bundle</c> before we get here, and
    ///    Unity refuses a second <c>LoadFromFile</c> of the same file - so reusing the open
    ///    handle is required, not merely an optimization.
    /// 3. <c>lgucontracts.bundle</c> loaded from the contracts plugin's package directory.
    ///    Covers load-order cases where Y4NGZCompany has not opened it yet.
    ///
    /// If none of those resolve, the callers fall back to their generated primitive visuals,
    /// exactly as they always did when the asset was missing from the bundle.
    /// </summary>
    internal static class CctvFixtureAssets
    {
        internal const string CctvFixtureBundleFileName = "lethalcctv-fixtures.bundle";
        internal const string ContractsBundleFileName = "lgucontracts.bundle";

        /// <summary>Asset used to recognize an already-open bundle as one that carries our fixtures.</summary>
        private const string ProbeAssetName = "CCTVMainframeSupport";

        // The MD5 seed for GlobalObjectIdHash. Spelled out rather than taken from this
        // plugin's own GUID: the hash has to match what Y4NGZCompany produced before #393,
        // or a mid-upgrade session would disagree with itself about prefab identity.
        private const string NetworkHashSeedGuid = "com.y4ngz.company";

        private static readonly System.Collections.Generic.HashSet<string> PreparedPrefabs =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _resolveAttempted;
        private static AssetBundle _bundle;

        /// <summary>The resolved fixture bundle, or null when none of the sources produced one.</summary>
        internal static AssetBundle Bundle
        {
            get
            {
                Resolve();
                return _bundle;
            }
        }

        /// <summary>
        /// Loads a fixture prefab and prepares it for networking exactly once per asset name.
        /// Mirrors <c>ContractAssetLoader.GetPreparedInteriorSupportPrefab</c>.
        /// </summary>
        internal static GameObject GetPreparedInteriorSupportPrefab(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName) || Bundle == null)
                return null;

            GameObject prefab = LoadAssetByName<GameObject>(assetName);
            if (prefab == null)
                return null;

            if (PreparedPrefabs.Add(assetName))
                PrepareInteriorSupportPrefab(prefab, assetName);

            return prefab;
        }

        /// <summary>
        /// Prepares a prefab this plugin built at runtime rather than loaded from a bundle.
        /// Mirrors <c>ContractAssetLoader.PrepareRuntimeInteriorSupportPrefab</c>.
        /// </summary>
        internal static void PrepareRuntimeInteriorSupportPrefab(GameObject prefab, string assetName)
        {
            if (prefab == null || string.IsNullOrWhiteSpace(assetName))
                return;

            if (PreparedPrefabs.Add(assetName))
                PrepareInteriorSupportPrefab(prefab, assetName);
        }

        /// <summary>Loads a non-networked fixture asset, such as the standalone alarm gate.</summary>
        internal static T GetBundledAsset<T>(string assetName) where T : UnityEngine.Object
        {
            return LoadAssetByName<T>(assetName);
        }

        /// <summary>
        /// Resolves an audio clip by base name across the bundle, trying the bare name and
        /// then the three extensions the bundle uses. Feeds the shared keypad's clip provider.
        /// </summary>
        internal static AudioClip GetBundledAudioClip(params string[] names)
        {
            if (names == null)
                return null;

            for (int i = 0; i < names.Length; i++)
            {
                string shortName = names[i];
                if (string.IsNullOrWhiteSpace(shortName))
                    continue;

                AudioClip clip = LoadAssetByName<AudioClip>(shortName)
                                 ?? LoadAssetByName<AudioClip>(shortName + ".wav")
                                 ?? LoadAssetByName<AudioClip>(shortName + ".ogg")
                                 ?? LoadAssetByName<AudioClip>(shortName + ".mp3");
                if (clip != null)
                    return clip;
            }

            return null;
        }

        /// <summary>
        /// Deterministic <c>GlobalObjectIdHash</c> so every client agrees on a code-built
        /// prefab's identity. Byte-identical to Y4NGZCompany's implementation, seed included.
        /// </summary>
        internal static void AssignStableNetworkHash(NetworkObject networkObject, string scope, string hashKey)
        {
            if (networkObject == null)
                return;

            try
            {
                uint hash;
                using (MD5 md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(NetworkHashSeedGuid + "." + scope + "." + hashKey));
                    hash = BitConverter.ToUInt32(bytes, 0);
                }

                Type type = typeof(NetworkObject);
                PropertyInfo prop = type.GetProperty("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(networkObject, hash);
                    CctvModuleConfig.Log?.LogInfo($"[MoonContracts] {scope} NetworkObject hash set for {hashKey}: {hash}.");
                    return;
                }

                FieldInfo field = type.GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? type.GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(networkObject, hash);
                    CctvModuleConfig.Log?.LogInfo($"[MoonContracts] {scope} NetworkObject hash set for {hashKey}: {hash}.");
                    return;
                }

                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Could not locate NetworkObject.GlobalObjectIdHash for {scope} prefab '{hashKey}'.");
            }
            catch (Exception e)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] {scope} NetworkObject hash assignment failed for '{hashKey}': {e.Message}");
            }
        }

        private static void PrepareInteriorSupportPrefab(GameObject prefab, string assetName)
        {
            if (prefab == null)
                return;

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
                networkObject = prefab.AddComponent<NetworkObject>();
            AssignStableNetworkHash(networkObject, "InteriorSupport", assetName);
            networkObject.SynchronizeTransform = true;

            if (prefab.GetComponent<NetworkTransform>() == null)
                prefab.AddComponent<NetworkTransform>();

            try
            {
                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
                CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Registered interior support network prefab '{assetName}'.");
            }
            catch (Exception e)
            {
                CctvModuleConfig.Log?.LogDebug($"[MoonContracts] Interior support prefab '{assetName}' registration skipped: {e.Message}");
            }
        }

        private static T LoadAssetByName<T>(string assetName) where T : UnityEngine.Object
        {
            AssetBundle bundle = Bundle;
            if (bundle == null || string.IsNullOrWhiteSpace(assetName))
                return null;

            T direct = bundle.LoadAsset<T>(assetName);
            if (direct != null)
                return direct;

            foreach (string bundledName in bundle.GetAllAssetNames())
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(bundledName), assetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                T asset = bundle.LoadAsset<T>(bundledName);
                if (asset != null)
                    return asset;
            }

            return null;
        }

        private static void Resolve()
        {
            if (_resolveAttempted)
                return;
            _resolveAttempted = true;

            try
            {
                if (TryLoadOwnFixtureBundle())
                    return;
                if (TryAdoptLoadedContractsBundle())
                    return;
                TryLoadContractsBundleFromDisk();
            }
            catch (Exception e)
            {
                CctvModuleConfig.Log?.LogWarning(
                    $"[LethalCCTV] Fixture bundle resolution failed ({e.GetType().Name}: {e.Message}); "
                    + "mainframe and stash will use their generated fallback visuals.");
            }
        }

        private static bool TryLoadOwnFixtureBundle()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(CctvFixtureAssets).Assembly.Location) ?? string.Empty;
            string path = Path.Combine(assemblyDir, CctvFixtureBundleFileName);
            if (!File.Exists(path))
            {
                // The bundle ships as of #393, so this is an incomplete install or an older
                // profile; the contracts bundle still covers it. One line, info level.
                CctvModuleConfig.Log?.LogInfo(
                    $"[LethalCCTV] {CctvFixtureBundleFileName} not present beside the assembly; "
                    + $"falling back to {ContractsBundleFileName}.");
                return false;
            }

            MainframeSpawnDiagnostics.LogBundleMetadata(path);
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null)
            {
                CctvModuleConfig.Log?.LogWarning($"[LethalCCTV] AssetBundle.LoadFromFile returned null for {path}.");
                return false;
            }

            CctvModuleConfig.Log?.LogInfo($"[LethalCCTV] Fixture bundle loaded from {path}.");
            return true;
        }

        private static bool TryAdoptLoadedContractsBundle()
        {
            foreach (AssetBundle candidate in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (candidate == null)
                    continue;

                try
                {
                    if (!BundleCarriesFixtures(candidate))
                        continue;
                }
                catch
                {
                    continue;
                }

                _bundle = candidate;
                CctvModuleConfig.Log?.LogInfo(
                    $"[LethalCCTV] Reusing already-loaded bundle '{candidate.name}' for interior fixtures.");
                return true;
            }

            return false;
        }

        private static bool BundleCarriesFixtures(AssetBundle bundle)
        {
            foreach (string assetName in bundle.GetAllAssetNames())
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(assetName), ProbeAssetName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static void TryLoadContractsBundleFromDisk()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(CctvFixtureAssets).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, ContractsBundleFileName),
                // Gale names a locally imported package's directory after the package, so a
                // re-imported profile holds `Y4NGZCompany` while older ones hold the
                // hand-made `y4ngz-Y4NGZCompany`. Probe both, newest layout first.
                Path.Combine(Paths.PluginPath, "Y4NGZCompany", ContractsBundleFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", ContractsBundleFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", ContractsBundleFileName),
                Path.Combine(Paths.PluginPath, ContractsBundleFileName),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                if (!File.Exists(candidates[i]))
                    continue;

                MainframeSpawnDiagnostics.LogBundleMetadata(candidates[i]);
                _bundle = AssetBundle.LoadFromFile(candidates[i]);
                if (_bundle != null)
                {
                    CctvModuleConfig.Log?.LogInfo($"[LethalCCTV] Interior fixture assets loaded from {candidates[i]}.");
                    return;
                }

                CctvModuleConfig.Log?.LogWarning(
                    $"[LethalCCTV] AssetBundle.LoadFromFile returned null for {candidates[i]}.");
            }

            CctvModuleConfig.Log?.LogWarning(
                $"[LethalCCTV] Neither {CctvFixtureBundleFileName} nor {ContractsBundleFileName} could be opened; "
                + "mainframe and stash will use their generated fallback visuals.");
        }
    }
}
