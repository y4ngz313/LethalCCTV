using System.Collections.Generic;
using System.Text;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;
using Y4NGZCompany.Facility.Stash;
using Y4NGZCompany.Facility.Mainframe;
namespace Y4NGZCompany.Facility.Security
{
    public static class CctvSupportState
    {
        public const int StashCodeDigits = 4;
        public const int VaultCodeDigits = StashCodeDigits;
        public const int MaxMainframeAttempts = 4;
        public const float MainframeLockoutSeconds = 60f;
        public const float AlarmTickIntervalSeconds = 0.4f;

        private static bool _initialized;
        private static bool _vaultCodesConfigured;
        private static int _configuredVaultCount;
        private static int _configuredSeed;
        private static int[] _vaultCodes = System.Array.Empty<int>();
        private static int[] _assignedVaultCodes = System.Array.Empty<int>();
        private static VaultCodeDebugEntry[] _debugVaultCodes = System.Array.Empty<VaultCodeDebugEntry>();
        private static readonly HashSet<int> RuntimeStashIds = new HashSet<int>();

        private static bool StashesDisabledByHost =>
            CctvNetworkRole.IsServer() && !(CctvModuleConfig.CompanyStashesEnabled?.Value ?? true);

        public static int[] StashCodes
        {
            get
            {
                EnsureInitialized();
                EnsureCodesConfiguredFromRuntimeStashes();
                if (!IsMainframeHacked) return System.Array.Empty<int>();
                return _vaultCodes ?? System.Array.Empty<int>();
            }
        }

        public static int[] VaultCodes => StashCodes;

        internal static int[] HiddenStashCodes
        {
            get
            {
                EnsureInitialized();
                EnsureCodesConfiguredFromRuntimeStashes();
                return _vaultCodes ?? System.Array.Empty<int>();
            }
        }

        internal static int[] HiddenVaultCodes => HiddenStashCodes;

        public static bool IsMainframeHacked =>
            MainframeSupport.Active != null && MainframeSupport.Active.IsHacked;

        public static void ResetRunState()
        {
            _initialized = false;
            _vaultCodesConfigured = false;
            _configuredVaultCount = 0;
            _configuredSeed = 0;
            _vaultCodes = System.Array.Empty<int>();
            _assignedVaultCodes = System.Array.Empty<int>();
            _debugVaultCodes = System.Array.Empty<VaultCodeDebugEntry>();
            RuntimeStashIds.Clear();
        }

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            if (StartOfRound.Instance == null) return;
            _initialized = true;
            EnsureTickDriver();
        }

        internal static void ConfigureStashCodesForRound(int spawnedStashCount)
        {
            ConfigureStashCodesForRound(spawnedStashCount, BuildVaultCodeSeed());
        }

        internal static void ConfigureStashCodesForRound(int spawnedStashCount, int seed)
        {
            EnsureInitialized();

            int vaultCount = StashesDisabledByHost ? 0 : Mathf.Max(0, spawnedStashCount);
            _configuredVaultCount = vaultCount;
            _configuredSeed = seed;
            _vaultCodesConfigured = true;

            if (vaultCount == 0)
            {
                _vaultCodes = System.Array.Empty<int>();
                _assignedVaultCodes = System.Array.Empty<int>();
                _debugVaultCodes = System.Array.Empty<VaultCodeDebugEntry>();
                return;
            }

            int totalCodeCount = Mathf.Max(vaultCount + 1, Mathf.CeilToInt(vaultCount * 1.5f));
            var rng = new System.Random(seed ^ 0x5A17C0DE);
            var used = new HashSet<int>();
            var assigned = new int[vaultCount];
            var allCodes = new List<int>(totalCodeCount);

            for (int i = 0; i < vaultCount; i++)
            {
                int code = NextUniqueCode(rng, used);
                assigned[i] = code;
                allCodes.Add(code);
            }

            while (allCodes.Count < totalCodeCount)
                allCodes.Add(NextUniqueCode(rng, used));

            Shuffle(allCodes, rng);

            _assignedVaultCodes = assigned;
            _vaultCodes = allCodes.ToArray();
            _debugVaultCodes = BuildDebugRows(_assignedVaultCodes, _vaultCodes);
        }

        internal static void FinalizeStashCodesForSpawnedCount(int spawnedStashCount)
        {
            if (StashesDisabledByHost)
            {
                ConfigureStashCodesForRound(0);
                return;
            }

            EnsureInitialized();
            EnsureCodesConfiguredFromRuntimeStashes();

            // Spawned stashes already hold assigned codes, so finalization must only
            // update metadata/debug labels. Regenerating here would desync the menu.
            int actualCount = Mathf.Max(0, spawnedStashCount);
            if (!_vaultCodesConfigured)
            {
                ConfigureStashCodesForRound(actualCount);
                return;
            }

            if (actualCount > _configuredVaultCount)
            {
                ExpandConfiguredCodes(actualCount);
                return;
            }

            _configuredVaultCount = actualCount;
            if (_assignedVaultCodes == null || actualCount == 0)
            {
                _assignedVaultCodes = System.Array.Empty<int>();
            }
            else if (_assignedVaultCodes.Length != actualCount)
            {
                int keep = Mathf.Min(actualCount, _assignedVaultCodes.Length);
                var assigned = new int[actualCount];
                for (int i = 0; i < keep; i++)
                    assigned[i] = _assignedVaultCodes[i];
                for (int i = keep; i < assigned.Length; i++)
                    assigned[i] = -1;
                _assignedVaultCodes = assigned;
            }

            _debugVaultCodes = BuildDebugRows(_assignedVaultCodes, _vaultCodes);
        }

        internal static void RegisterRuntimeStash(CompanyStashController stash)
        {
            if (stash == null)
                return;

            EnsureInitialized();
            if (StashesDisabledByHost)
            {
                ConfigureStashCodesForRound(0);
                return;
            }

            if (!RuntimeStashIds.Add(stash.GetInstanceID()))
                return;

            if (!_vaultCodesConfigured || RuntimeStashIds.Count > _configuredVaultCount)
                ConfigureStashCodesForRound(RuntimeStashIds.Count, BuildVaultCodeSeed());
        }

        internal static void UnregisterRuntimeStash(CompanyStashController stash)
        {
            if (stash == null)
                return;

            RuntimeStashIds.Remove(stash.GetInstanceID());
        }

        internal static bool TryGetAssignedStashCode(int stashIndex, out int code)
        {
            EnsureInitialized();
            EnsureCodesConfiguredFromRuntimeStashes();
            code = -1;
            if (_assignedVaultCodes == null || stashIndex < 0 || stashIndex >= _assignedVaultCodes.Length)
                return false;

            code = _assignedVaultCodes[stashIndex];
            return code >= 0;
        }

        public static string BuildDebugStashCodesReport()
        {
            EnsureInitialized();
            EnsureCodesConfiguredFromRuntimeStashes();

            var sb = new StringBuilder();
            sb.Append("Company Stash Codes\n");
            sb.Append("moon=").Append(StartOfRound.Instance?.currentLevel?.PlanetName ?? "<none>")
              .Append(" seed=").Append(StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed.ToString() : "na")
              .Append(" codeSeed=").Append(_configuredSeed)
              .Append('\n');
            sb.Append("flow=").Append(ResolveCurrentDungeonFlowName())
              .Append(" hacked=").Append(IsMainframeHacked ? "yes" : "no")
              .Append('\n');
            sb.Append("stashes=").Append(_configuredVaultCount)
              .Append(" codes=").Append(_vaultCodes != null ? _vaultCodes.Length : 0)
              .Append(" configured=").Append(_vaultCodesConfigured ? "yes" : "no")
              .Append("\n\n");

            if (_debugVaultCodes == null || _debugVaultCodes.Length == 0)
            {
                sb.Append("No Company Stash codes configured for this generation.");
                return sb.ToString();
            }

            for (int i = 0; i < _debugVaultCodes.Length; i++)
            {
                VaultCodeDebugEntry row = _debugVaultCodes[i];
                sb.Append(FormatStashCode(row.Code));
                if (row.IsAssigned)
                    sb.Append("  ->  ").Append(row.StashLabel);
                else
                    sb.Append("  ->  decoy");
                sb.Append('\n');
            }

            return sb.ToString();
        }

        public static string BuildDebugVaultCodesReport()
        {
            return BuildDebugStashCodesReport();
        }

        private static void EnsureCodesConfiguredFromRuntimeStashes()
        {
            if (StashesDisabledByHost)
            {
                if (!_vaultCodesConfigured || _configuredVaultCount != 0)
                    ConfigureStashCodesForRound(0);
                return;
            }

            if (_vaultCodesConfigured)
                return;

            if (RuntimeStashIds.Count > 0)
                ConfigureStashCodesForRound(RuntimeStashIds.Count, BuildVaultCodeSeed());
        }

        private static CctvSupportTickDriver _tickDriver;
        private static void EnsureTickDriver()
        {
            if (_tickDriver != null) return;
            GameObject go = new GameObject("CctvSupportTickDriver");
            Object.DontDestroyOnLoad(go);
            _tickDriver = go.AddComponent<CctvSupportTickDriver>();
        }

        private static int NextUniqueCode(System.Random rng, HashSet<int> used)
        {
            while (true)
            {
                int code = rng.Next(0, 10000);
                if (used.Add(code))
                    return code;
            }
        }

        private static void Shuffle(List<int> values, System.Random rng)
        {
            if (values == null || rng == null)
                return;

            for (int i = values.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int tmp = values[i];
                values[i] = values[j];
                values[j] = tmp;
            }
        }

        private static VaultCodeDebugEntry[] BuildDebugRows(int[] assignedCodes, int[] allCodes)
        {
            if (allCodes == null || allCodes.Length == 0)
                return System.Array.Empty<VaultCodeDebugEntry>();

            int[] assigned = assignedCodes ?? System.Array.Empty<int>();
            var debugRows = new VaultCodeDebugEntry[allCodes.Length];
            for (int i = 0; i < allCodes.Length; i++)
            {
                int code = allCodes[i];
                int assignedIndex = System.Array.IndexOf(assigned, code);
                debugRows[i] = new VaultCodeDebugEntry
                {
                    Code = code,
                    IsAssigned = assignedIndex >= 0,
                    StashIndex = assignedIndex,
                    StashLabel = assignedIndex >= 0 ? $"Company Stash {assignedIndex + 1}" : "decoy"
                };
            }

            return debugRows;
        }

        private static void ExpandConfiguredCodes(int actualCount)
        {
            int targetCount = Mathf.Max(0, actualCount);
            var used = new HashSet<int>();
            var allCodes = new List<int>();
            if (_vaultCodes != null)
            {
                for (int i = 0; i < _vaultCodes.Length; i++)
                {
                    if (used.Add(_vaultCodes[i]))
                        allCodes.Add(_vaultCodes[i]);
                }
            }

            int[] previousAssigned = _assignedVaultCodes ?? System.Array.Empty<int>();
            var assigned = new int[targetCount];
            int keep = Mathf.Min(previousAssigned.Length, assigned.Length);
            for (int i = 0; i < keep; i++)
                assigned[i] = previousAssigned[i];

            var rng = new System.Random(_configuredSeed ^ 0x5A17C0DE);
            for (int i = keep; i < assigned.Length; i++)
            {
                int code = NextUniqueCode(rng, used);
                assigned[i] = code;
                allCodes.Add(code);
            }

            int totalCodeCount = Mathf.Max(targetCount + 1, Mathf.CeilToInt(targetCount * 1.5f));
            while (allCodes.Count < totalCodeCount)
                allCodes.Add(NextUniqueCode(rng, used));

            _configuredVaultCount = targetCount;
            _assignedVaultCodes = assigned;
            _vaultCodes = allCodes.ToArray();
            _debugVaultCodes = BuildDebugRows(_assignedVaultCodes, _vaultCodes);
        }

        public static string FormatStashCode(int code)
        {
            return code.ToString("D" + StashCodeDigits);
        }

        public static string FormatVaultCode(int code)
        {
            return FormatStashCode(code);
        }

        private static string ResolveCurrentDungeonFlowName()
        {
            try
            {
                var flow = RoundManager.Instance?.dungeonGenerator?.Generator?.DungeonFlow;
                return flow != null && !string.IsNullOrWhiteSpace(flow.name) ? flow.name : "unknown-flow";
            }
            catch
            {
                return "unknown-flow";
            }
        }

        private static int BuildVaultCodeSeed()
        {
            unchecked
            {
                int seed = StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : System.Environment.TickCount;
                SelectableLevel level = StartOfRound.Instance?.currentLevel ?? RoundManager.Instance?.currentLevel;
                seed = (seed * 397) ^ StableHash("company-stash-codes");
                seed = (seed * 397) ^ (level != null ? level.levelID : 0);
                seed = (seed * 397) ^ StableHash(level != null ? level.PlanetName : string.Empty);
                seed = (seed * 397) ^ StableHash(ResolveCurrentDungeonFlowName());
                return seed;
            }
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = (int)2166136261;
                if (!string.IsNullOrEmpty(value))
                {
                    for (int i = 0; i < value.Length; i++)
                        hash = (hash ^ value[i]) * 16777619;
                }
                return hash;
            }
        }

        public sealed class VaultCodeDebugEntry
        {
            public int Code;
            public bool IsAssigned;
            public int StashIndex = -1;
            public string StashLabel = "";
        }
    }
}
