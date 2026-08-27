using UnityEngine;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVVanillaMonitorDisplay
    {
        // #303/#304 — always-running sweep over the two vanilla cameras that were
        // measured rendering every frame with no visible consumer (36-37% of frame
        // time in the 2026-08-05 evidence session). Runs from the very top of Tick,
        // BEFORE the purchase/mode early returns, so it stays engaged across round
        // transitions and after CCTV releases the monitor wall — the prior guards
        // all lived behind those early returns and disengaged permanently at the
        // round-end handoff.
        private const float VanillaCameraGuardIntervalSeconds = 0.5f;
        private const float VanillaCameraGuardReportIntervalSeconds = 60f;
        private const float HeadMountedLookupIntervalSeconds = 5f;

        private static float _nextVanillaCameraGuardAt;
        private static float _nextVanillaCameraGuardReportAt;
        private static float _nextHeadMountedLookupAt;
        private static Camera _vanillaHeadMountedCamera;
        private static long _headMountedGuardDisables;
        private static long _mapCameraGuardDisables;
        private static bool _headMountedGuardLogged;
        private static bool _mapScreenUnfreezeLogged;

        private static void TickVanillaCameraGuard(float now)
        {
            if (now < _nextVanillaCameraGuardAt)
                return;
            _nextVanillaCameraGuardAt = now + VanillaCameraGuardIntervalSeconds;

            try
            {
                GuardVanillaHeadMountedCamera(now);
                GuardVanillaMapCamera();
            }
            catch { }

            ReportVanillaCameraGuard(now);
        }

        /// <summary>
        /// #303 — the vanilla 'HeadMountedCamera' body-cam rig starts rendering every
        /// frame (~3.3ms) at the first CCTV feed bind and never stops. With
        /// OpenBodyCams installed, OBC owns body-cam rendering and the LGU rule is
        /// that the integration stays inert until the CCTV Terminal is purchased —
        /// the vanilla rig has no visible consumer in this pack, so it is forced off
        /// whenever the terminal is unowned or CCTV owns the monitor wall.
        /// </summary>
        private static void GuardVanillaHeadMountedCamera(float now)
        {
            if (!OpenBodyCamsCompat.IsLoaded)
                return;
            bool mustStayOff = _suppressionCaptured || !OpenBodyCamsCompat.ShouldShowFocusSlot();
            if (!mustStayOff)
                return;

            if (_vanillaHeadMountedCamera == null)
            {
                if (now < _nextHeadMountedLookupAt)
                    return;
                _nextHeadMountedLookupAt = now + HeadMountedLookupIntervalSeconds;
                // Was GameObject.Find("HeadMountedCamera") — an unqualified full
                // scene-hierarchy scan that cost 25-246ms per call late-round and
                // repeated every 5s forever while the rig stayed unresolved
                // (2026-08-06 ExtremeStep evidence: 145 LateUpdate hitches at the
                // exact lookup cadence). Only an ENABLED camera ever needs the
                // guard (a disabled one renders nothing), and Camera.allCameras
                // contains exactly the enabled set — typically a dozen entries.
                _vanillaHeadMountedCamera = FindEnabledCameraByName("HeadMountedCamera");
                if (_vanillaHeadMountedCamera == null)
                    return;
            }

            if (!_vanillaHeadMountedCamera.enabled)
                return;

            _vanillaHeadMountedCamera.enabled = false;
            _headMountedGuardDisables++;
            if (!_headMountedGuardLogged)
            {
                _headMountedGuardLogged = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] Vanilla-camera guard disabled the always-on vanilla HeadMountedCamera " +
                    "(CCTV Terminal unpurchased or CCTV owns the monitors; y4ngz313/Y4NGZCompany#303).");
            }
        }

        // Reused buffer for Camera.GetAllCameras so the 5s lookup allocates nothing
        // beyond occasional growth. Entries are cleared after each scan so the
        // buffer never pins destroyed cameras.
        private static Camera[] _cameraScanBuffer = new Camera[16];

        private static Camera FindEnabledCameraByName(string cameraName)
        {
            int count = Camera.allCamerasCount;
            if (count <= 0)
                return null;
            if (_cameraScanBuffer.Length < count)
                _cameraScanBuffer = new Camera[Mathf.NextPowerOfTwo(count)];

            int written = Camera.GetAllCameras(_cameraScanBuffer);
            Camera found = null;
            for (int i = 0; i < written; i++)
            {
                Camera candidate = _cameraScanBuffer[i];
                if (found == null && candidate != null && candidate.name == cameraName)
                    found = candidate;
                _cameraScanBuffer[i] = null;
            }

            return found;
        }

        /// <summary>
        /// #304 — after CCTV releases the lower monitors, RestoreVanillaLowerMonitorFeeds
        /// puts mapScreen.enabled back to its captured value; if that capture (or any
        /// later teardown) left the ManualCameraRenderer disabled, nothing manages
        /// cam.enabled and whatever state the round-end sequence set freezes for the
        /// whole next round (evidence: round 2 of the 2026-08-05 session, MapCamera
        /// always-on at ~3.6ms/frame). Un-freeze the MCR so vanilla resumes
        /// management, and independently re-apply vanilla's own observability
        /// conditions — SwitchScreenOn-style paths set cam.enabled directly without
        /// consulting MeetsCameraEnabledConditions, bypassing the Harmony guard in
        /// OpenBodyCamsCompat.
        /// </summary>
        private static void GuardVanillaMapCamera()
        {
            // While CCTV suppression owns the wall the Reassert paths keep the map
            // camera off; this guard covers everything outside that window.
            if (_suppressionCaptured)
                return;

            // A cooperating feature (for example the quota unlock screen)
            // owns the same material slot and deliberately suspended this
            // writer. Do not undo that ownership from the recovery guard.
            if (CCTVShipSystemsBridge.IsPrimaryMonitorTakeoverActive())
                return;

            // The quota presentation suspends the same vanilla producers for
            // the ceremony's length (the announcement holds the map screen,
            // the ceremony ribbon holds the interior-camera monitor) without
            // necessarily holding the primary scope. Re-enabling a suspended
            // writer here would put a live feed back over the broadcast; the
            // feeds resume at scope release, when the ceremony restores them.
            if (CCTVShipSystemsBridge.IsQuotaPresentationActive())
                return;

            StartOfRound sor = StartOfRound.Instance;
            ManualCameraRenderer mapScreen = sor != null ? sor.mapScreen : null;
            if (mapScreen == null)
                return;

            if (!mapScreen.enabled)
            {
                mapScreen.enabled = true;
                if (!_mapScreenUnfreezeLogged)
                {
                    _mapScreenUnfreezeLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV] Vanilla-camera guard re-enabled the frozen vanilla mapScreen renderer " +
                        "so vanilla resumes managing its camera (y4ngz313/Y4NGZCompany#304).");
                }
            }

            Camera mapCam = mapScreen.mapCamera != null ? mapScreen.mapCamera : mapScreen.cam;
            if (mapCam == null || !mapCam.enabled)
                return;

            var player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null)
                return;

            // Mirror of the vanilla hard disable/location conditions used by
            // OpenBodyCamsCompat.GateOpenBodyCamsMapCameraPostfix.
            bool disabledByVanilla = mapScreen.currentCameraDisabled;
            if (!sor.inShipPhase)
            {
                disabledByVanilla |= !player.isInHangarShipRoom && !mapScreen.overrideRadarCameraOnAlways;
                disabledByVanilla |= !sor.shipDoorsEnabled
                    && (sor.currentPlanetPrefab == null || !sor.currentPlanetPrefab.activeSelf);
            }

            if (!disabledByVanilla)
                return;

            mapCam.enabled = false;
            _mapCameraGuardDisables++;
        }

        /// <summary>Warning level on purpose, same rationale as the compositor gate
        /// report: Info is filtered off disk in the diagnostic profiles.</summary>
        private static void ReportVanillaCameraGuard(float now)
        {
            if (_nextVanillaCameraGuardReportAt <= 0f)
            {
                _nextVanillaCameraGuardReportAt = now + VanillaCameraGuardReportIntervalSeconds;
                return;
            }

            if (now < _nextVanillaCameraGuardReportAt)
                return;

            _nextVanillaCameraGuardReportAt = now + VanillaCameraGuardReportIntervalSeconds;
            if (_headMountedGuardDisables <= 0L && _mapCameraGuardDisables <= 0L)
                return;

            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV.Timing] vanilla-camera guard re-disabled HeadMountedCamera x{_headMountedGuardDisables}, " +
                $"MapCamera x{_mapCameraGuardDisables} in the last {VanillaCameraGuardReportIntervalSeconds:0}s " +
                "(a repeat-enabler is fighting the guard; see #303/#304).");
            _headMountedGuardDisables = 0L;
            _mapCameraGuardDisables = 0L;
        }
    }
}
