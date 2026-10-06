using HarmonyLib;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [HarmonyPatch(typeof(EntranceTeleport), "Awake")]
    internal static class CctvTargetRegistryEntrancePatch
    {
        [HarmonyPostfix]
        private static void Postfix(EntranceTeleport __instance) => CctvTargetRegistry.Entrances.Add(__instance);
    }
}
