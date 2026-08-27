using HarmonyLib;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// The vanilla monitor power toggle is removed entirely (2026-07-02 spec):
    /// the red bezel button is the CCTV entry point and there is no reason to
    /// ever power the map monitor off. The button's serialized onInteract call
    /// is disabled by CCTVVanillaMonitorButtons; this prefix is defense in
    /// depth for anything else that invokes SwitchScreenButton on the map
    /// screen (other mods, the window before the takeover probe runs).
    /// SwitchScreenOn(true) is untouched, so vanilla code paths that force the
    /// screen back on (radar target switch) keep working.
    ///
    /// #597 scoped this cancellation to the vanilla monitor wall. With
    /// GeneralImprovements' UseBetterMonitors the map-screen power toggle is no
    /// longer "the lower-left monitor's power": GI's SwitchScreenOn prefix turns
    /// it into the power switch for the entire replacement monitor group
    /// (SyncExtraMonitorsPower, on by default) and repaints its own visible
    /// BigMiddle map surface from it. While CCTV lived on unrelated GI screens,
    /// swallowing the call would have stranded every GI screen in whatever power
    /// state it happened to be in.
    ///
    /// #600 changes the ownership picture: the CCTV feed now takes GI's MAP
    /// surface (Monitors/BigMiddle/MScreen) by default, which is exactly what
    /// GI's SwitchScreenOn prefix repaints through Monitors.UpdateMapMaterial.
    /// Letting the press through there would blank our live feed to GI's
    /// offScreenMat (and re-blank it on every subsequent press) until a reassert
    /// pass clawed it back — a visible flicker at best. So the semantics are
    /// vanilla parity: while CCTV owns the map screen the red bezel button is the
    /// CCTV control and the vanilla/GI power toggle is swallowed; when it does
    /// not (feed left on the vanilla wall, or a configured GI index that is not
    /// the map screen), GI keeps full control of monitor power.
    ///
    /// Priority note: GI patches ManualCameraRenderer.SwitchScreenOn, not
    /// SwitchScreenButton, so cancelling here already means SwitchScreenOn — and
    /// therefore UpdateMapMaterial — is never reached from the button. High
    /// priority is set anyway so that any other mod prefixing this same method at
    /// default priority cannot run its own power handling ahead of the decision.
    /// </summary>
    [HarmonyPatch(typeof(ManualCameraRenderer), nameof(ManualCameraRenderer.SwitchScreenButton))]
    internal static class CCTVMonitorModeButtonPatch
    {
        [HarmonyPriority(Priority.High)]
        private static bool Prefix(ManualCameraRenderer __instance)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || __instance != sor.mapScreen)
                return true;

            if (CCTVVanillaMonitorDisplay.IsGeneralImprovementsMonitorWallActive() &&
                !CCTVVanillaMonitorDisplay.OwnsGeneralImprovementsMapScreen())
            {
                return true; // GI owns monitor power on its own wall
            }

            return false; // power toggle removed
        }
    }

    /// <summary>
    /// Leaving the moon resets the shared feed state: monitor returns to the
    /// vanilla display, the operator claim clears, and any active CCTV session
    /// closes. These StartOfRound hooks run on every client, so the reset is
    /// deterministic without a network broadcast.
    /// </summary>
    [HarmonyPatch]
    internal static class CCTVMonitorModeShipLeavePatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ShipLeave))]
        private static void OnShipLeave()
        {
            ResetForOrbit("ship-leave");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ShipLeaveAutomatically))]
        private static void OnShipLeaveAutomatically()
        {
            ResetForOrbit("ship-leave-auto");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.Disconnect))]
        private static void OnDisconnect()
        {
            ResetForOrbit("disconnect");
        }

        private static void ResetForOrbit(string reason)
        {
            // Exit first so the focus cleanup (input locks, controller swap,
            // ChairExited) runs while the session state is still coherent.
            MonitorFocus.ForceExit(reason);
            CCTVMonitorFeedSync.ResetForOrbit(reason);
        }
    }

    [HarmonyPatch(typeof(ManualCameraRenderer), "Update")]
    internal static class CCTVMonitorOwnershipManualCameraRendererUpdatePatch
    {
        private static bool Prefix(ManualCameraRenderer __instance)
        {
            if (!CCTVVanillaMonitorDisplay.ShouldSuppressVanillaMonitorRendererUpdate(__instance))
                return true;

            CCTVVanillaMonitorDisplay.ReassertCctvScreenOwnershipAfterVanilla();
            return false;
        }

        private static void Postfix()
        {
            CCTVVanillaMonitorDisplay.ReassertCctvScreenOwnershipAfterVanilla();
        }
    }

    [HarmonyPatch(typeof(StartOfRound), "LateUpdate")]
    internal static class CCTVMonitorOwnershipStartOfRoundLateUpdatePatch
    {
        private static void Postfix()
        {
            CCTVVanillaMonitorDisplay.ReassertCctvScreenOwnershipAfterVanilla();
        }
    }
}
