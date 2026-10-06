using System;
using System.Reflection;
using HarmonyLib;
using Y4NGZCore.Modules;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Wakes the terminal prefab for vanilla-store purchases (#393) and vanilla save-load
    /// spawns (#1367), including a hidden unlockable restored with the companions installed.
    /// Vanilla's <c>StartOfRound.SpawnUnlockable</c> instantiates the registered prefab
    /// directly. Our template is deliberately kept inactive, and Netcode drops the
    /// NetworkBehaviours of a disabled object at spawn, so the template is woken for the
    /// length of that one call and put back afterwards.
    /// </summary>
    [HarmonyPatch(typeof(StartOfRound), "SpawnUnlockable")]
    internal static class CctvTerminalStoreSpawnPatch
    {
        // SpawnUnlockable is not reentrant - it neither recurses nor yields - so a single
        // static hand-off between the prefix and the postfix is sufficient.
        private static bool _wokeTemplate;

        [HarmonyPrefix]
        private static void Prefix(int unlockableIndex)
        {
            _wokeTemplate = CCTVTerminalUnlockable.BeginVanillaFurnitureSpawn(unlockableIndex);
        }

        [HarmonyPostfix]
        private static void Postfix(int unlockableIndex)
        {
            bool wokeTemplate = _wokeTemplate;
            _wokeTemplate = false;
            CCTVTerminalUnlockable.EndVanillaFurnitureSpawn(unlockableIndex, wokeTemplate);
        }
    }

    /// <summary>
    /// Treats the host's free terminal as installed in the optional companion's canonical
    /// ownership read, so its listing and host purchase guard agree without changing its save.
    /// </summary>
    [HarmonyPatch]
    internal static class CctvTerminalCompanionOwnershipPatch
    {
        private const string CompanionAssemblyName = "Y4NGZTerminalUpgrades";
        private const string ControllerTypeName = "Y4NGZCompany.ShipSystems.Layout.ShipCommandController";
        private const string UpgradeIdTypeName = "Y4NGZCompany.ShipSystems.Layout.ShipUpgradeId";
        private static bool _resolved;
        private static MethodInfo _getUpgradeLevel;
        private static int _cctvTerminalId;

        [HarmonyPrepare]
        private static bool Prepare()
        {
            if (_resolved)
                return _getUpgradeLevel != null;
            _resolved = true;

            try
            {
                // Bootstrap defers this probe until the first StartOfRound.Awake, after
                // BepInEx has chainloaded every plugin. Never load an optional assembly here;
                // an absent companion needs no patch or warning.
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!string.Equals(assembly.GetName().Name, CompanionAssemblyName, StringComparison.Ordinal))
                        continue;

                    Type controller = assembly.GetType(ControllerTypeName, throwOnError: false);
                    Type upgradeId = assembly.GetType(UpgradeIdTypeName, throwOnError: false);
                    if (controller == null || upgradeId == null || !upgradeId.IsEnum ||
                        Enum.GetUnderlyingType(upgradeId) != typeof(int))
                    {
                        WarnUnavailable("controller or int-backed upgrade enum is unavailable");
                        return false;
                    }

                    FieldInfo cctvId = upgradeId.GetField("CCTVTerminal", BindingFlags.Public | BindingFlags.Static);
                    MethodInfo getLevel = controller.GetMethod(
                        "GetUpgradeLevel",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        types: new[] { upgradeId },
                        modifiers: null);
                    if (cctvId == null || !(cctvId.GetRawConstantValue() is int id) ||
                        getLevel == null || getLevel.ReturnType != typeof(int) || getLevel.ContainsGenericParameters)
                    {
                        WarnUnavailable("CCTVTerminal or GetUpgradeLevel signature has changed");
                        return false;
                    }

                    _cctvTerminalId = id;
                    _getUpgradeLevel = getLevel;
                    return true;
                }
            }
            catch (Exception exception)
            {
                WarnUnavailable($"{exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }

        private static void WarnUnavailable(string reason)
        {
            ModuleDiagnostics.WarnOnce(
                "cctv.companionFreeTerminalOwnership",
                $"[LethalCCTV] Free CCTV terminal companion ownership override unavailable: {reason}. " +
                "The companion CCTV terminal remains chargeable; its upgrade ledger has not been changed.");
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() => _getUpgradeLevel;

        [HarmonyPostfix]
        private static void Postfix(int __0, ref int __result)
        {
            // Harmony passes this validated int-backed enum as its underlying stack value.
            // Using object/Enum or ToString here would box/allocate on every ownership read.
            // Never call IsPurchased: its ShipSystems API read comes back through this method.
            if (__0 == _cctvTerminalId && __result < 1 && CCTVTerminalUnlockable.ShipStartsWithTerminal)
                __result = 1;
        }
    }
}
