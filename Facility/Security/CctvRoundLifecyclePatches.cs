using HarmonyLib;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior;

namespace Y4NGZCompany.Facility.Security
{
    /// <summary>
    /// Per-frame tick for the interior support spawner, which used to be step 7 of
    /// Y4NGZCompany's timed RoundManager.Update postfix chain. It is patched onto the same
    /// vanilla method so the work still happens at the same point in the frame. Y4NGZCompany
    /// is a soft dependency and therefore chainloads first when installed, so its postfix
    /// still registers - and runs - ahead of this one, preserving the old relative order.
    /// </summary>
    [HarmonyPatch(typeof(RoundManager), "Update")]
    internal static class CctvInteriorSupportTickPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                InteriorSupportSpawner.UpdateRuntime();
            }
            catch (System.Exception e)
            {
                CctvModuleConfig.Log?.LogError($"[LethalCCTV] Interior support update failed: {e}");
            }
        }
    }

    /// <summary>
    /// Round-boundary reset for every piece of CCTV round state.
    ///
    /// Before #393 this ran as a block inside <c>MoonContractState.ResetRunState</c>: the
    /// contracts plugin owned the round lifecycle and reset the CCTV systems on its way
    /// through. LethalCCTV now ships separately and has to reset itself, so this patches the
    /// same three vanilla moments <c>MoonContractStartOfRoundPatches</c> and
    /// <c>MoonContractEndOfRoundPatch</c> called <c>ResetRunState</c> from - a new orbit,
    /// ship leave, and the end-of-game report - rather than the ship-leave-only set the
    /// blood and finisher systems use. Matching the old trigger set matters: the
    /// <c>SetShipReadyToLand</c> reset is what clears the previous moon's camera registry
    /// before the next dungeon generates, and dropping it would carry stale cameras across.
    ///
    /// <c>GameNetworkManager.Disconnect</c> is added on top, which the old path did not have.
    /// Leaving a lobby mid-round never reached ShipLeave or the end-of-game RPC, so the
    /// registries kept the dead session's cameras until the next landing cleared them.
    ///
    /// Every reset here is idempotent, so overlapping triggers (ShipLeave immediately
    /// followed by the end-of-game RPC) are harmless.
    /// </summary>
    [HarmonyPatch]
    internal static class CctvRoundLifecyclePatches
    {
        [HarmonyPatch(typeof(StartOfRound), "SetShipReadyToLand")]
        [HarmonyPostfix]
        private static void OnSetShipReadyToLand()
        {
            ResetRound();
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        [HarmonyPostfix]
        private static void OnShipLeave()
        {
            ResetRound();
        }

        [HarmonyPatch(typeof(StartOfRound), "EndOfGameClientRpc")]
        [HarmonyPostfix]
        private static void OnEndOfGame()
        {
            ResetRound();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void OnDisconnect()
        {
            ResetRound();
        }

        /// <summary>
        /// Same calls in the same order the contracts plugin used, so a behaviour question
        /// about reset ordering has one answer rather than two.
        /// </summary>
        private static void ResetRound()
        {
            try
            {
                CctvSupportState.ResetRunState();
                CctvAlarmSystem.ResetRunState();
                CctvSecurityDirector.ResetRound();
                CctvSecurityCameraRegistry.ResetRound();
                CctvCameraShutdownSync.ResetRound();
                InteriorAlarmSpawner.ResetRound();
                MainframeProtocolDirector.ResetRound();
                // Was the line immediately after this block in MoonContractState.ResetRunState.
                InteriorSupportSpawner.ResetRunState();
            }
            catch (System.Exception ex)
            {
                // A reset that throws would leave the rest of the systems holding last
                // round's state, which is worse than a logged partial reset.
                CctvModuleConfig.Log?.LogWarning(
                    $"[LethalCCTV] Round reset failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
