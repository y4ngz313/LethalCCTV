using HarmonyLib;
using Unity.Netcode;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    // Netcode reaches this shared boundary even when a mod overrides Start without calling base.
    [HarmonyPatch(typeof(NetworkObject), "InvokeBehaviourNetworkSpawn")]
    internal static class CctvTargetRegistryNetworkSpawnPatch
    {
        [HarmonyPostfix]
        private static void Postfix(NetworkObject __instance) => CctvTargetRegistry.RegisterNetworkObject(__instance, spawned: true);
    }
}
