using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    // Per-frame mouselook on the active focus pane. Reads vanilla's "Look"
    // InputAction (the same one PlayerControllerB.PlayerLookInput consumes)
    // so sensitivity/flip/invert honor the player's settings automatically.
    // PlayerControllerB.disableLookInput gates the PLAYER's consumption of
    // that action, not the action's input pipeline, so the read here still
    // produces deltas while disableLookInput=true. VERIFY-ON-FIRST-RUN per
    // PLAN: if pans are dead in-game, fall back to Mouse.current.delta.
    //
    // Also owns the once-per-LateUpdate isPlayerDead poll that closes the
    // death-while-focused stuck-look bug. The check runs BEFORE the
    // empty-slot guard so a death coinciding with an unbound active slot
    // still force-exits — explicit ordering per PLAN T5 confirmation.
    internal static class FocusMouselook
    {
        private const string LOOK_ACTION_NAME = "Look";
        private const float MAX_ROTATION_DEG_PER_SECOND = 125f;
        private const float RAW_LOOK_DEADZONE = 0.08f;
        private const float SCALED_LOOK_DEADZONE_DEG = 0.004f;

        private static InputAction _lookAction;
        private static bool _lookActionResolved;

        internal static void Initialize()
        {
            ResolveLookAction();
        }

        internal static void Shutdown()
        {
            _lookAction = null;
            _lookActionResolved = false;
        }

        internal static void Tick()
        {
            // 1. Focus guard. Outside focus, nothing in this method should run.
            if (!MonitorFocus.IsFocused) return;
            if (MonitorFocus.IsReviewMenuOpen) return;
            if (MonitorFocus.IsStationEditMenuOpen && !MonitorFocus.IsStationEditThirdPersonDebugActive) return;
            if (Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive) return;
            if (MonitorFocus.HackingOverlay != null && MonitorFocus.HackingOverlay.IsOpen) return;
            if (MonitorFocus.MainframeOverlay != null && MonitorFocus.MainframeOverlay.IsOpen) return;

            // 2. Death check — BEFORE the empty-slot return so a death that
            //    coincides with an unbound active slot still force-exits.
            //    Single chokepoint: MonitorFocus.ForceExit → ExitFocus →
            //    disableLookInput stash-restore. Same restore path as E/ESC
            //    and the regen hook in SurveillanceBootstrap.OnCamerasReady.
            //
            //    Null and dead are SPLIT: a transient null
            //    (scene-transition / reference-rebind frame) is not a death
            //    signal and must not tear down focus — skip the frame and
            //    retry next LateUpdate. isPlayerDead is the actual death
            //    signal and routes to ForceExit.
            PlayerControllerB lp = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (lp == null) return;
            if (lp.isPlayerDead)
            {
                MonitorFocus.ForceExit("player-death");
                return;
            }
            if (CctvDeviceCommandLine.ConsumesInput) return;

            // F1/F2 station viewpoint tuning uses WASD/arrows and mouse hand motion.
            // While that editor is active, the active CCTV camera must stay still so
            // the user can tune the viewpoint without also steering the feed.
            if (CCTVOperatorStation.IsDebugPlacementActive) return;

            if (MonitorFocus.IsStationThirdPersonDebugViewActive)
            {
                if (TryReadLookDelta(out Vector2 orbitDelta))
                {
                    MonitorFocus.ApplyStationThirdPersonOrbitDelta(orbitDelta);
                    MonitorFocus.NotifyCameraRotated(orbitDelta.magnitude);
                }
                return;
            }

            if (!MonitorFocus.IsStationCameraControlActive) return;

            if (MonitorFocus.IsTurretPageActive)
            {
                if (TryReadRawLook(out Vector2 turretRaw))
                    ShipTurretController.ApplyLookDelta(turretRaw);
                return;
            }

            // 3. Only facility feeds accept mounted-camera mouselook.
            if (MonitorFocus.IsBodycamFeedActive) return;

            CCTVCamera holder = QuadCameraAssignment.GetBoundCamera(MonitorFocus.ActiveSlot);
            if (holder == null || holder.Cam == null) return;
            if (!holder.AllowsOperatorRotation) return;

            // 4. Read mouse delta from the vanilla "Look" action. One-shot
            //    resolve (cached _lookAction) — if it failed at Initialize
            //    time, ResolveLookAction returns the cached null and Tick
            //    no-ops on this frame. We re-resolve once per Tick when
            //    not yet resolved, in case the action asset was not ready
            //    at plugin Awake.
            if (!TryReadLookDelta(out Vector2 d)) return;
            // Zero-delta early-return — skips the offset update and the
            // ApplyOffsets transform write on idle frames. ASSUMES this is
            // the sole writer of CCTVCamera.transform.localRotation after
            // spawn (verified: spawner writes once, then never touches it).
            // If a future feature adds a competing writer, drop this guard
            // so ApplyOffsets re-asserts our state every frame.
            

            // 5. Apply vanilla sensitivity + the per-client multiplier. The
            //    0.008f constant + lookSensitivity scaling matches
            //    PlayerControllerB.PlayerLookInput (decompile line 121176) so
            //    the CCTV pan feel matches the player look feel. flipCamera
            //    and invertYAxis are honored for the same reason.
            MonitorFocus.NotifyCameraRotated(d.magnitude);
            CctvCameraSweepMotor.NotifyManualControl(holder);

            // 6. Update absolute offsets (NOT cumulative quaternion mul — see
            //    CCTVCamera.ApplyOffsets for the world-axes composition).
            //    Pitch clamp lives in ApplyOffsets so it operates on the
            //    summed (basePitch + offset) result, NOT on the offset in
            //    isolation. Tick accumulates raw; ApplyOffsets enforces.
            holder.PitchOffsetDeg -= d.y;

            holder.YawOffsetDeg += d.x;
            // Every spawned camera is a mounted interior camera, so the yaw arc
            // is always clamped to the mount (the free-spin branch only ever
            // applied to the deleted exterior page).
            float yawClamp = SurveillanceBootstrap.Config.YawClampDeg;
            holder.YawOffsetDeg = Mathf.Clamp(holder.YawOffsetDeg, -yawClamp, +yawClamp);

            // 7. Apply to transform. Cheap quaternion product; one writer.
            holder.ApplyOffsets();

            // Animation hook: per-frame joystick deflection while the operator is
            // steering a CCTV camera from the station seat.
            if (MonitorFocus.IsStationCameraControlActive)
                CCTVStationEvents.RaiseJoystickMoved(d);
        }

        private static bool TryReadRawLook(out Vector2 raw)
        {
            raw = Vector2.zero;
            if (_lookAction == null && !_lookActionResolved) ResolveLookAction();
            if (_lookAction == null) return false;

            raw = _lookAction.ReadValue<Vector2>();
            return raw.sqrMagnitude >= RAW_LOOK_DEADZONE * RAW_LOOK_DEADZONE;
        }

        private static bool TryReadLookDelta(out Vector2 delta)
        {
            delta = Vector2.zero;
            if (!TryReadRawLook(out Vector2 raw))
                return false;

            float sensitivity = 1f;
            if (IngamePlayerSettings.Instance != null && IngamePlayerSettings.Instance.settings != null)
                sensitivity = IngamePlayerSettings.Instance.settings.lookSensitivity;
            float multiplier = SurveillanceBootstrap.Config?.MouselookSensitivityMul != null
                ? SurveillanceBootstrap.Config.MouselookSensitivityMul.Value
                : 1f;

            if (IngamePlayerSettings.Instance != null)
            {
                if (IngamePlayerSettings.Instance.flipCamera) raw.x = -raw.x;
                if (IngamePlayerSettings.Instance.settings != null && IngamePlayerSettings.Instance.settings.invertYAxis)
                    raw.y *= -1f;
            }

            delta = raw * sensitivity * 0.008f * multiplier;
            float maxDelta = MAX_ROTATION_DEG_PER_SECOND * Mathf.Max(Time.unscaledDeltaTime, 1f / 120f);
            if (delta.magnitude > maxDelta)
                delta = delta.normalized * maxDelta;
            return delta.sqrMagnitude >= SCALED_LOOK_DEADZONE_DEG * SCALED_LOOK_DEADZONE_DEG;
        }

        private static void ResolveLookAction()
        {
            _lookActionResolved = true;
            try
            {
                _lookAction = InputSystem.actions != null
                    ? InputSystem.actions.FindAction(LOOK_ACTION_NAME)
                    : null;
            }
            catch
            {
                _lookAction = null;
            }
            if (_lookAction == null)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] FocusMouselook: vanilla 'Look' InputAction not resolvable — focus-mode mouselook will be inert until next session. Fallback to Mouse.current.delta not yet wired (PLAN verify-on-first-run path).");
            }
            else
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] FocusMouselook: 'Look' action resolved.");
            }
        }
    }
}
