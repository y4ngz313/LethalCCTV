using HarmonyLib;
using Unity.Netcode;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [HarmonyPatch(typeof(NetworkObject), "InvokeBehaviourNetworkDespawn")]
    internal static class CctvTargetRegistryNetworkDespawnPatch
    {
        [HarmonyPostfix]
        private static void Postfix(NetworkObject __instance) => CctvTargetRegistry.RegisterNetworkObject(__instance, spawned: false);
    }
}
