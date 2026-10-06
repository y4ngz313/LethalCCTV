using System.Collections.Generic;
using UnityEngine;

using Y4NGZCompany.Facility.Mainframe;
namespace Y4NGZCompany.Facility.Security
{
    public static class CctvSupportApi
    {
        /// <summary>
        /// Optional per-player camera-detection-time scale supplied by companion mods
        /// (Y4NGZUpgrades' Chameleon). Returns how many times LONGER cameras take to
        /// detect the given player; null or 1.0 means vanilla speed.
        /// </summary>
        public static System.Func<GameNetcodeStuff.PlayerControllerB, float> DetectionTimeMultiplierProvider { get; set; }

        internal static float ResolveDetectionTimeMultiplier(GameNetcodeStuff.PlayerControllerB player)
        {
            System.Func<GameNetcodeStuff.PlayerControllerB, float> provider = DetectionTimeMultiplierProvider;
            if (provider == null || player == null)
                return 1f;

            try
            {
                return Mathf.Max(0.01f, provider(player));
            }
            catch
            {
                return 1f;
            }
        }

        internal static float ResolveLocalDetectionTimeMultiplier()
        {
            return ResolveDetectionTimeMultiplier(StartOfRound.Instance?.localPlayerController);
        }

        public static bool IsMainframeHacked => CctvSupportState.IsMainframeHacked;
        public static bool IsAlarmActive => MainframeSupport.Active != null && MainframeSupport.Active.IsAlarmOn;
        public static bool IsCctvSecurityDisabled => CctvSecurityDirector.IsSecurityDisabled;
        public static bool IsTimedSecurityAlarmActive => CctvSecurityDirector.IsTimedAlarmActive;
        public static float SecurityAlarmRemaining => CctvSecurityDirector.AlarmRemainingSeconds;
        public static string SecurityAlarmReason => CctvSecurityDirector.AlarmReason;
        public static bool IsMainframeLockedOut => MainframeSupport.Active != null && MainframeSupport.Active.IsLockedOut;
        public static int MainframeAttemptsRemaining => IsMainframeLockedOut ? 0 : CctvSupportState.MaxMainframeAttempts;
        public static float MainframeLockoutRemaining => MainframeSupport.Active != null ? MainframeSupport.Active.LockoutRemaining : 0f;

        public static IReadOnlyList<int> RevealedStashCodes
        {
            get
            {
                int[] codes = CctvSupportState.StashCodes;
                if (codes == null || codes.Length == 0)
                    return System.Array.Empty<int>();
                return codes;
            }
        }

        public static IReadOnlyList<int> RevealedVaultCodes => RevealedStashCodes;

        public static IReadOnlyList<int> MainframeControlStashCodes
        {
            get
            {
                int[] codes = CctvSupportState.HiddenStashCodes;
                if (codes == null || codes.Length == 0)
                    return System.Array.Empty<int>();
                return codes;
            }
        }

        public static string FormatStashCode(int code)
        {
            return CctvSupportState.FormatStashCode(code);
        }

        public static string FormatCode(int code)
        {
            return FormatStashCode(code);
        }

        public static bool ValidateStashCode(int code)
        {
            int[] codes = CctvSupportState.StashCodes;
            if (codes == null) return false;
            for (int i = 0; i < codes.Length; i++)
            {
                if (codes[i] == code) return true;
            }
            return false;
        }

        public static bool ValidateVaultCode(int code)
        {
            return ValidateStashCode(code);
        }

        /// <summary>
        /// Server-authoritative per-camera shutdown for companion mods
        /// (Y4NGZUpgrades' Field Mechanic). cameraId is the camera's CameraIndex —
        /// the registry's stable per-round identifier, deterministic across clients
        /// because cameras spawn from the shared map seed. Host-only: returns false
        /// on clients and for unknown ids. The camera stays down for the rest of
        /// the round (detection sweeps stop, monitor feed reads OFFLINE, lens light
        /// goes dark) and the state replicates to every client.
        /// </summary>
        public static bool TryDisableCamera(int cameraId)
        {
            return CctvCameraShutdownSync.TryDisableCamera(cameraId);
        }

        public static bool IsCameraDisabled(int cameraId)
        {
            return CctvCameraShutdownSync.IsCameraDisabled(cameraId);
        }

        /// <summary>
        /// Ids of every camera currently registered with the security system,
        /// for companion-mod UIs (Y4NGZUpgrades' Field Operations tablet). Each
        /// id is the camera's CameraIndex — the same stable per-round identifier
        /// TryDisableCamera/IsCameraDisabled take. Snapshot copy: safe to hold
        /// across frames while the registry churns.
        /// </summary>
        public static IReadOnlyList<int> GetCameraIds()
        {
            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            if (cameras == null || cameras.Count == 0)
                return System.Array.Empty<int>();

            var ids = new List<int>(cameras.Count);
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i] != null)
                    ids.Add(cameras[i].CameraIndex);
            }
            return ids;
        }

        /// <summary>
        /// Display label for a registered camera (the monitor feed's label,
        /// e.g. "CAM_03" or an authored zone name), or null for unknown ids.
        /// </summary>
        public static string GetCameraLabel(int cameraId)
        {
            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i] != null && cameras[i].CameraIndex == cameraId)
                    return cameras[i].Label;
            }
            return null;
        }

        private static bool _lockdownApiWarned;

        /// <summary>
        /// Whether the facility lockdown gates are currently held down by any
        /// holder (manual/scheduled lockdown drill, CCTV security alarm, or the
        /// Containment Breach contract).
        /// </summary>
        public static bool IsLockdownActive =>
            CctvLockdownGateService.LockdownActive;

        /// <summary>
        /// Begins a facility lockdown drill for companion mods (Y4NGZUpgrades'
        /// Field Operations tablet) — the same MainframeProtocolDirector manual
        /// LockdownDrill event the ship mainframe OS "Lockdown" menu drives:
        /// announcer tip, security light pulse, and the containment gates slam
        /// shut. Host-only: returns false on clients or if the machinery throws.
        /// </summary>
        public static bool TryBeginLockdown()
        {
            return TryInvokeLockdown(begin: true);
        }

        /// <summary>
        /// Ends the manual lockdown drill and opens the shared gates, unless
        /// another holder (security alarm, Containment Breach contract) is
        /// still holding them. Host-only like TryBeginLockdown.
        /// </summary>
        public static bool TryEndLockdown()
        {
            return TryInvokeLockdown(begin: false);
        }

        private static bool TryInvokeLockdown(bool begin)
        {
            Unity.Netcode.NetworkManager nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return false;

            try
            {
                return begin
                    ? MainframeProtocolDirector.BeginManualEvent(MainframeProtocolEvent.LockdownDrill)
                    : MainframeProtocolDirector.EndManualEvent(MainframeProtocolEvent.LockdownDrill);
            }
            catch (System.Exception ex)
            {
                if (!_lockdownApiWarned)
                {
                    _lockdownApiWarned = true;
                    Y4NGZCompany.Bootstrap.CctvModuleConfig.Log?.LogWarning(
                        $"[MoonContracts.CctvSecurity] Lockdown API {(begin ? "begin" : "end")} failed: {ex.Message}");
                }
                return false;
            }
        }

        public static string GetStashCodeDebugReport()
        {
            return CctvSupportState.BuildDebugStashCodesReport();
        }

        public static string GetVaultCodeDebugReport()
        {
            return GetStashCodeDebugReport();
        }

        /// <summary>#716 G4. Delegates to the round-lifecycle reset so an external caller gets
        /// the same eight-system reset the vanilla round boundaries run. It previously reset
        /// only support state, the security director, the camera registry and the shutdown
        /// sync, leaving the alarm system, spawned alarm fixtures, mainframe protocol state
        /// and interior support state holding the previous round.</summary>
        public static void ResetRound()
        {
            CctvRoundLifecyclePatches.ResetRound();
        }
    }
}
