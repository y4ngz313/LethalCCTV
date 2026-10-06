using HarmonyLib;
using Y4NGZCompany.Facility.Stash;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [HarmonyPatch(typeof(CompanyStashController), "Awake")]
    internal static class CctvTargetRegistryStashPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CompanyStashController __instance) => CctvTargetRegistry.Stashes.Add(__instance);
    }
}
