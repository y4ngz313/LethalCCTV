using HarmonyLib;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [HarmonyPatch]
    internal static class CctvTargetRegistryDevicePatches
    {
        [HarmonyPatch(typeof(TerminalAccessibleObject), "Start")]
        [HarmonyPostfix]
        private static void Started(TerminalAccessibleObject __instance) => CctvTargetRegistry.Devices.Add(__instance);

        [HarmonyPatch(typeof(TerminalAccessibleObject), nameof(TerminalAccessibleObject.OnDestroy))]
        [HarmonyPostfix]
        private static void Destroyed(TerminalAccessibleObject __instance) => CctvTargetRegistry.Devices.Remove(__instance);
    }
}
