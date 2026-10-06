using HarmonyLib;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [HarmonyPatch]
    internal static class CctvTargetRegistryEnemyPatches
    {
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.Start))]
        [HarmonyPostfix]
        private static void Started(EnemyAI __instance) => CctvTargetRegistry.Enemies.Add(__instance);

        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.OnDestroy))]
        [HarmonyPostfix]
        private static void Destroyed(EnemyAI __instance) => CctvTargetRegistry.Enemies.Remove(__instance);
    }
}
