using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Phase 1.5a Item 1 — ESC interception so pressing Escape while focus mode is active
    /// closes the overlay without also opening the vanilla pause menu.
    ///
    /// Vanilla LC routes the pause-open through a single chokepoint:
    /// QuickMenuManager.OpenQuickMenu() (dump line 77331). The only external caller is
    /// PlayerControllerB.OpenMenu_performed (dump line 121020), which routes through this
    /// method — patching the method itself catches the only known open path.
    /// </summary>
    [HarmonyPatch(typeof(QuickMenuManager), nameof(QuickMenuManager.OpenQuickMenu))]
    internal static class QuickMenuPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            // Phase 1.5a Round 2 — dual check (IsFocused OR WasActiveThisFrame) closes the
            // same-frame race where our ExitFocus handler may have already cleared IsFocused
            // before vanilla's OpenMenu_performed fires on the same ESC press. Diagnostic
            // log is essential: if it does not appear when Lawson presses ESC in focus mode,
            // the patch isn't binding at all (different failure mode → flowchart branch (a)).
            bool mainframeActive = MainframeInteractionSession.IsAnyLocalSessionActive || MainframeInteractionSession.WasActiveThisFrame;
            bool shouldSuppress = MonitorFocus.IsFocused || MonitorFocus.WasActiveThisFrame || mainframeActive;
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] QuickMenu prefix fired, IsFocused={MonitorFocus.IsFocused} WasActiveThisFrame={MonitorFocus.WasActiveThisFrame} MainframeActive={MainframeInteractionSession.IsAnyLocalSessionActive} MainframeWasActiveThisFrame={MainframeInteractionSession.WasActiveThisFrame} suppress={shouldSuppress}");
            return !shouldSuppress;
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "OpenMenu_performed")]
    internal static class CameraOperatorOpenMenuPerformedPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            bool mainframeActive = MainframeInteractionSession.IsAnyLocalSessionActive || MainframeInteractionSession.WasActiveThisFrame;
            bool shouldSuppress = MonitorFocus.IsFocused || MonitorFocus.WasActiveThisFrame || mainframeActive;
            if (shouldSuppress)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] PlayerControllerB.OpenMenu_performed suppressed during CCTV focus/mainframe session, IsFocused={MonitorFocus.IsFocused} WasActiveThisFrame={MonitorFocus.WasActiveThisFrame} MainframeActive={MainframeInteractionSession.IsAnyLocalSessionActive} MainframeWasActiveThisFrame={MainframeInteractionSession.WasActiveThisFrame}.");
            }
            return !shouldSuppress;
        }
    }
}
