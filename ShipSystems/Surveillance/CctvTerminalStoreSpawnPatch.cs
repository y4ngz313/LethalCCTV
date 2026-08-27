using HarmonyLib;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Standalone-only (#393). Applied by <c>SurveillanceBootstrap</c> exactly when
    /// <see cref="CCTVTerminalUnlockable.SoldInVanillaStore"/> is set, i.e. when the
    /// split Ship Systems API is absent and the terminal is sold as a real vanilla ship
    /// upgrade. With Ship Systems installed this patch is never applied, so the purchase
    /// route it owns cannot alter that configuration.
    ///
    /// Vanilla buys the upgrade and then calls <c>StartOfRound.SpawnUnlockable</c>, which
    /// instantiates the registered prefab directly. Our template is deliberately kept
    /// inactive, and Netcode drops the NetworkBehaviours of a disabled object at spawn, so
    /// the template is woken for the length of that one call and put back afterwards.
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
}
