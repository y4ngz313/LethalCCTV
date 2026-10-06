using HarmonyLib;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [HarmonyPatch]
    internal static class CctvTargetRegistryItemPatches
    {
        [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.Start))]
        [HarmonyPostfix]
        private static void Started(GrabbableObject __instance) => CctvTargetRegistry.Items.Add(__instance);

        [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.OnDestroy))]
        [HarmonyPostfix]
        private static void Destroyed(GrabbableObject __instance) => CctvTargetRegistry.Items.Remove(__instance);
    }
}
