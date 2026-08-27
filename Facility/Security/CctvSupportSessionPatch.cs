using HarmonyLib;

namespace Y4NGZCompany.Facility.Security
{
    // Anchors the CCTV support tick driver to session start on every client.
    //
    // CctvSupportTickDriver is the only thing that runs CctvSecurityDirector.Tick,
    // MainframeProtocolDirector.Tick, CctvAlarmSystem.Tick and
    // CctvSpottingAlertHud.Ensure. The detection sweep lives inside the first of those,
    // so the driver is what produces PresentationIsSuspicious — and therefore the warning
    // beam, the detection beep and the spotting alert — on every client, host or not.
    //
    // Before this patch the driver was created only as a side effect of content spawning:
    // CompanyStashController.OnNetworkSpawn registering a runtime stash, a MiniVault
    // HiddenVaultCodes read, or a terminal code lookup through CctvSupportApi. A round
    // with no company stash and no mini-vault therefore left non-host clients with no
    // security presentation at all. That predates the spotting HUD — the beam always had
    // the same dependency — but a missing personal alert reads as a bug in a way a missing
    // beam does not. The real defect is that a session-scoped singleton was owned by
    // whichever consumer happened to spawn first, so it moves to the session lifecycle.
    //
    // StartOfRound.Start is the anchor: it runs once per session on every client, and
    // StartOfRound.Instance is already assigned in Awake, so EnsureInitialized's
    // StartOfRound.Instance null guard is satisfied by the time the postfix runs. Both
    // EnsureInitialized and EnsureTickDriver are idempotent, and ResetRunState clears
    // _initialized while deliberately leaving _tickDriver alive, so one call per session
    // is enough forever and a repeat call is free.
    //
    // Client parity is unaffected even though the director now starts ticking earlier.
    // The rotation seed is mapSeed ^ (bucket * 397) ^ (force ? 0x4C0C : 0) with
    // bucket = floor(now / rotationSeconds), and SecurityTimeSeconds prefers
    // NetworkManager.ServerTime — a pure function of the shared server clock and the
    // shared map seed. Which cameras are security-active is therefore independent of when
    // a client started ticking; starting earlier moves _nextRotationAt scheduling, not the
    // resulting set.
    [HarmonyPatch(typeof(StartOfRound), "Start")]
    internal static class CctvSupportSessionPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            CctvSupportState.EnsureInitialized();
        }
    }
}
