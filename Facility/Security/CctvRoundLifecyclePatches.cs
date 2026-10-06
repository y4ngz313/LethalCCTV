using HarmonyLib;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Stash;

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
        ///
        /// #716 G4: internal so <c>CctvSupportApi.ResetRound</c> can delegate here. That API
        /// used to reset four of the eight systems below, which meant an external caller
        /// asking for a round reset silently kept the alarm system, alarm fixtures, mainframe
        /// protocol state and interior support state from the previous round.
        /// </summary>
        internal static void ResetRound()
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
                // #716 G4: the keypad overlay is a static singleton holding a canvas parented to
                // the local player plus the vault it was opened against, both of which belong to
                // the round that just ended. It had no caller at all, so the stale instance and
                // its canvas survived into the next round.
                VaultKeypadOverlay.ResetInstance();
                // Was the line immediately after this block in MoonContractState.ResetRunState.
                InteriorSupportSpawner.ResetRunState();
                // #1271: the host's queued camera reservations belong to the registry that
                // reset just cleared, so they go with it; the next round starts uncommitted.
                Y4NGZCompany.Facility.Cameras.CameraReservationQueue.ResetRound();
                // #716 A6: the support-injection gate token. Only the CamerasReady follow-up
                // coroutine ever raises it, so a placement pass that threw before raising
                // CamerasReady - the spawner swallows that exception - or a follow-up stopped
                // mid-window would leave it closed and make every later support-injection pass
                // burn its full frame wait. The round boundary is the natural restore point:
                // the next round's SurveillanceBootstrap.RunCamerasReadyFollowUps closes it and
                // reopens it around the registry rebuild inside the dungeon-finished event.
                SurveillanceBootstrap.SecurityRegistryReadyForRound = true;
                // #1271: drop the per-dungeon placement caches with the round that owned them.
                // Both caches also key themselves to the dungeon, so this only releases the
                // references early; a missed reset cannot serve last round's values.
                Y4NGZCompany.Facility.Cameras.DungeonCameraSpawner.ResetTileAabbCache();
                Y4NGZCompany.Facility.Cameras.Placement.PlacementMask.ResetRoundCache();
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
