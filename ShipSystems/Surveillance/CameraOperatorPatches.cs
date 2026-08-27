using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [HarmonyPatch(typeof(HUDManager), "PingScan_performed")]
    internal static class CameraOperatorPingScanPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref InputAction.CallbackContext context)
        {
            if (!MonitorFocus.IsFocused) return true;
            if (Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive) return false;
            if (context.performed)
            {
                MonitorFocus.TriggerCameraContextOrScan();
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(HUDManager), "UpdateScanNodes")]
    internal static class CameraOperatorScanNodesPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !MonitorFocus.IsFocused;
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "ActivateItem_performed")]
    internal static class CameraOperatorActivateItemPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !MonitorFocus.IsFocused;
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "ActivateItem_canceled")]
    internal static class CameraOperatorActivateItemCancelPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return !MonitorFocus.IsFocused;
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "Jump_performed")]
    internal static class CameraOperatorJumpPerformedPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerControllerB __instance)
        {
            return !MonitorFocus.ShouldSuppressPlayerInput(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "Update")]
    internal static class CameraOperatorPlayerUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerControllerB __instance)
        {
            if (!MonitorFocus.ShouldSuppressPlayerInput(__instance)) return;
            MonitorFocus.ApplyFocusInputLock(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "PlayerLookInput")]
    internal static class CameraOperatorPlayerLookInputPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerControllerB __instance)
        {
            return !MonitorFocus.ShouldSuppressPlayerInput(__instance);
        }
    }
}
