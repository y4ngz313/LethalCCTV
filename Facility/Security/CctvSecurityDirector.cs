using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Facility.Mainframe;
namespace Y4NGZCompany.Facility.Security
{
    public static class CctvSecurityDirector
    {
        private const float DetectionSweepIntervalSeconds = 0.1f;

        private static readonly List<CctvSecurityCameraState> Cameras = new List<CctvSecurityCameraState>();
        private static readonly List<CctvSecurityCameraState> LastActiveSet = new List<CctvSecurityCameraState>();
        private static float _nextRotationAt;
        private static bool? _lastAllCamerasPassive;
        private static float _lastDetectionSweepAt = -1f;
        private static float _alarmEndsAt;
        private static string _alarmReason;
        private static bool _securityDisabledByHack;
        private static bool _securityDisabledByMeltdown;
        private static bool _securityAlarmOwnsMainframeAlarm;

        // #466 post-alarm cooldown. Tracked on every machine off the replicated alarm
        // state rather than pushed from the host, so a client's lens strobe and spotting
        // arc go quiet on the same edge the host stops detecting on, with no new netcode.
        private static bool _alarmEpisodeWasActive;
        private static float _alarmCooldownUntil;
        private static bool _alarmCooldownAnnounced;

        // Per-frame memo for IsDetectionSuppressed (#563). Every lens, cone and HUD arc
        // asks the same question every frame, and one of its terms is a reflection probe.
        private static int _detectionSuppressedFrame = -1;
        private static bool _detectionSuppressedCached;

        internal static event Action RotationRefreshed;

        // Named for the effect rather than one cause: a mainframe hack and a facility
        // meltdown suppress detection-driven security identically, and everything
        // downstream (the beam, the beep, the spotting arc, the alarm fixtures) reads
        // IsSecurityActive / IsSuspiciousForPresentation and so falls out of this gate.
        public static bool IsSecurityDisabled =>
            _securityDisabledByHack || _securityDisabledByMeltdown || CctvSupportState.IsMainframeHacked;
        public static bool IsTimedAlarmActive
        {
            get
            {
                MainframeSupport active = MainframeSupport.Active;
                return active != null ? active.IsSecurityAlarmActive : SecurityTimeSeconds() < _alarmEndsAt;
            }
        }

        public static float AlarmRemainingSeconds
        {
            get
            {
                MainframeSupport active = MainframeSupport.Active;
                return active != null ? active.SecurityAlarmRemaining : Mathf.Max(0f, _alarmEndsAt - SecurityTimeSeconds());
            }
        }

        /// <summary>
        /// True while the post-alarm cooldown is running (#466): no camera detects, no
        /// detection presentation shows, and no alarm can engage or refresh. Evaluated
        /// locally on every machine from the replicated alarm state.
        /// </summary>
        public static bool IsAlarmCooldownActive =>
            _alarmCooldownUntil > 0f && SecurityTimeSeconds() < _alarmCooldownUntil;

        public static float AlarmCooldownRemainingSeconds =>
            _alarmCooldownUntil <= 0f ? 0f : Mathf.Max(0f, _alarmCooldownUntil - SecurityTimeSeconds());

        /// <summary>
        /// True whenever <see cref="Tick"/> will NOT reach <see cref="TickDetection"/> this
        /// frame — the single answer to "is any camera actually detecting right now".
        ///
        /// The conditions are exactly the early returns in Tick: the feature and passive
        /// switches, hack/meltdown disable, Containment Breach suppression and post-alarm
        /// cooldown. The last two leave the active set intact (cameras keep rotating and
        /// rendering), so presentation also needs this separate detection gate.
        ///
        /// Cached per frame because presentation reads it from several MonoBehaviour
        /// Update/LateUpdate loops per camera and the Containment Breach probe is a
        /// reflection call through ContractsBridge.
        /// </summary>
        public static bool IsDetectionSuppressed
        {
            get
            {
                int frame = Time.frameCount;
                if (_detectionSuppressedFrame == frame)
                    return _detectionSuppressedCached;

                _detectionSuppressedFrame = frame;
                CctvSecurityConfig config = CctvSecurityConfig.Current;
                _detectionSuppressedCached =
                    !config.Enabled
                    || config.AllCamerasPassive
                    || IsSecurityDisabled
                    || IsContainmentBreachContractActive()
                    || IsAlarmCooldownActive;
                return _detectionSuppressedCached;
            }
        }

        public static string AlarmReason
        {
            get
            {
                MainframeSupport active = MainframeSupport.Active;
                return active != null ? active.SecurityAlarmReason : _alarmReason ?? string.Empty;
            }
        }

        internal static void ResetRound()
        {
            Cameras.Clear();
            LastActiveSet.Clear();
            _nextRotationAt = 0f;
            _lastAllCamerasPassive = null;
            ResetDetectionSweepClock();
            _securityDisabledByHack = false;
            _securityDisabledByMeltdown = false;
            ClearAlarmCooldown();
            _alarmEpisodeWasActive = false;
            _detectionSuppressedFrame = -1;
            _detectionSuppressedCached = false;
            CctvAlarmAwarenessPing.ResetRound();
            CctvMainEntranceTileRule.ResetRound();
            ClearSecurityAlarmTimer(turnOffMainframeIfOwned: true);
        }

        internal static void OnCamerasRegistered(IReadOnlyList<CctvSecurityCameraState> cameras)
        {
            Cameras.Clear();
            if (cameras != null)
            {
                for (int i = 0; i < cameras.Count; i++)
                    if (cameras[i] != null)
                        Cameras.Add(cameras[i]);
            }

            LastActiveSet.Clear();
            for (int i = 0; i < Cameras.Count; i++)
                if (Cameras[i] != null && Cameras[i].IsSecurityActive)
                    LastActiveSet.Add(Cameras[i]);

            if (Cameras.Count == 0)
            {
                _nextRotationAt = 0f;
                ResetDetectionSweepClock();
                return;
            }

            if (_nextRotationAt <= 0f)
                RotateActiveSet(force: true, now: SecurityTimeSeconds());
        }

        internal static void Tick()
        {
            CctvSecurityConfig config = CctvSecurityConfig.Current;
            float now = SecurityTimeSeconds();
            if (_lastAllCamerasPassive != config.AllCamerasPassive)
            {
                _detectionSuppressedFrame = -1;
                RotateActiveSet(force: true, now: now);
                ResetDetectionSweepClock();
            }

            if (!config.Enabled)
            {
                ClearAllHostileCameraState();
                ClearSecurityAlarmTimer(turnOffMainframeIfOwned: true);
                ResetDetectionSweepClock();
                ClearAlarmCooldown();
                _alarmEpisodeWasActive = false;
                return;
            }

            if (IsSecurityDisabled)
            {
                ClearAllHostileCameraState();
                ClearSecurityAlarmTimer(turnOffMainframeIfOwned: true);
                ResetDetectionSweepClock();
                ClearAlarmCooldown();
                _alarmEpisodeWasActive = false;
                return;
            }

            if (config.AllCamerasPassive)
            {
                ClearAllHostileCameraState();
                ClearSecurityAlarmTimer(turnOffMainframeIfOwned: true);
                ResetDetectionSweepClock();
                ClearAlarmCooldown();
                _alarmEpisodeWasActive = false;
                return;
            }

            if (IsServerRuntime() && _alarmEndsAt > 0f && now >= _alarmEndsAt)
                ClearSecurityAlarmTimer(turnOffMainframeIfOwned: true);

            if (IsServerRuntime() && _alarmEndsAt > now && !_securityAlarmOwnsMainframeAlarm && MainframeSupport.Active != null && !MainframeSupport.Active.IsAlarmOn)
            {
                MainframeSupport.Active.SetAlarmServerSide(true);
                _securityAlarmOwnsMainframeAlarm = true;
            }

            TrackAlarmBlackoutState(config);
            TrackAlarmEpisodeEdges(config, now);

            if (IsServerRuntime())
                CctvAlarmAwarenessPing.Tick(now);

            if (now >= _nextRotationAt)
                RotateActiveSet(force: false, now: now);

            // Containment Breach suppresses hostile CCTV security at the source:
            // cameras keep rotating and rendering (and the wave-pulse strobe still
            // reads on active lenses), but the detection sweep never runs and no
            // suspicion survives, so nothing can raise a detection alarm. Runs
            // identically on every machine off the shared contract state.
            if (IsContainmentBreachContractActive())
            {
                ClearAllSuspicionState();
                ResetDetectionSweepClock();
                return;
            }

            // #466 post-alarm cooldown. Same shape as the Containment Breach suppression
            // above and for the same reason: cameras keep rotating and rendering, but the
            // detection sweep never runs, no suspicion survives, and RefreshAlarmTimer
            // refuses to re-engage. Every machine takes this branch off the same replicated
            // alarm edge, so the lens ramp and the spotting arc go quiet with it.
            if (IsAlarmCooldownActive)
            {
                ClearAllSuspicionState();
                ResetDetectionSweepClock();
                return;
            }

            TickDetection(Time.unscaledTime);
        }

        /// <summary>
        /// Watches the alarm's own rising and falling edges (#466) and starts the post-alarm
        /// cooldown on the fall. Runs on every machine: <see cref="IsTimedAlarmActive"/> is
        /// replicated through MainframeSupport, so host and clients see the same edge and
        /// reach the same cooldown window without a dedicated message.
        /// </summary>
        private static void TrackAlarmEpisodeEdges(CctvSecurityConfig config, float now)
        {
            if (IsTimedAlarmActive)
            {
                _alarmEpisodeWasActive = true;
                return;
            }

            if (_alarmEpisodeWasActive)
            {
                _alarmEpisodeWasActive = false;
                if (config.AlarmCooldownSeconds > 0f)
                {
                    _alarmCooldownUntil = now + config.AlarmCooldownSeconds;
                    _alarmCooldownAnnounced = true;
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts.CctvSecurity] ALARM_COOLDOWN engaged for {config.AlarmCooldownSeconds:0.0}s "
                        + "(cameras stop detecting; no alarm can re-engage).");
                }
                else
                {
                    ClearAlarmCooldown();
                }

                return;
            }

            if (_alarmCooldownUntil <= 0f)
                return;

            // Deadlock guard: the cooldown deadline is a network-time stamp, and a host
            // migration or a fresh round can move that clock backwards under us. A deadline
            // further out than the cooldown itself could ever place it is stale, not pending.
            if (_alarmCooldownUntil - now > config.AlarmCooldownSeconds + 5f)
            {
                ClearAlarmCooldown();
                return;
            }

            if (now >= _alarmCooldownUntil)
            {
                bool announce = _alarmCooldownAnnounced;
                ClearAlarmCooldown();
                if (announce)
                {
                    CctvModuleConfig.Log?.LogInfo(
                        "[MoonContracts.CctvSecurity] ALARM_COOLDOWN released (cameras detecting again).");
                }
            }
        }

        private static void ClearAlarmCooldown()
        {
            _alarmCooldownUntil = 0f;
            _alarmCooldownAnnounced = false;
        }

        private static bool IsContainmentBreachContractActive()
        {
            return ContractsBridge.IsContainmentBreachContractActive();
        }

        // Clears detection/suspicion on every camera without touching the active
        // set, so lenses drop any detection presentation but keep sweeping.
        private static void ClearAllSuspicionState()
        {
            bool isServer = IsServerRuntime();
            for (int i = 0; i < Cameras.Count; i++)
            {
                CctvSecurityCameraState state = Cameras[i];
                if (state == null) continue;
                state.ClearPresentationSuspicion();
                if (isServer)
                    state.ClearAuthoritativeSuspicion();
            }
        }

        internal static int CalculateActiveCameraCount(int eligibleCount, CctvSecurityConfig config)
        {
            if (eligibleCount <= 0 || !config.Enabled || config.AllCamerasPassive) return 0;

            char riskTier = ContractsBridge.GetCurrentRiskTier();
            if (TryResolveTierRatio(config, riskTier, out float tierRatio))
            {
                // A ratio of zero is the operator saying "no hostile cameras on this
                // tier", not "as few as possible". The ActiveCameraMin floor below would
                // otherwise turn it back into one live camera and there would be no way
                // to configure a tier off. Checked before the floor, both here and on the
                // legacy path.
                if (tierRatio <= 0f) return 0;

                int tierCount = Mathf.Max(
                    config.ActiveCameraMin,
                    Mathf.CeilToInt(eligibleCount * tierRatio));
                // Risk-tier balance is a percentage of the full pool. The legacy
                // ActiveCameraMax cap applies only to the unknown-tier fallback.
                return Mathf.Clamp(tierCount, 0, eligibleCount);
            }

            if (config.ActiveCameraRatio <= 0f) return 0;

            int byRatio = Mathf.CeilToInt(eligibleCount * config.ActiveCameraRatio);
            int withMin = Mathf.Max(config.ActiveCameraMin, byRatio);
            int withMax = Mathf.Min(config.ActiveCameraMax, withMin);
            return Mathf.Clamp(withMax, 0, eligibleCount);
        }

        private static bool TryResolveTierRatio(CctvSecurityConfig config, char riskTier, out float ratio)
        {
            switch (char.ToUpperInvariant(riskTier))
            {
                // D only reaches here when Security Enabled - Risk D is turned on: with it off,
                // config.Enabled is already false and CalculateActiveCameraCount
                // returns 0 before asking for a ratio.
                case 'D': ratio = config.ActiveCameraRatioRiskD; return true;
                case 'C': ratio = config.ActiveCameraRatioRiskC; return true;
                case 'B': ratio = config.ActiveCameraRatioRiskB; return true;
                case 'A': ratio = config.ActiveCameraRatioRiskA; return true;
                case 'S': ratio = config.ActiveCameraRatioRiskS; return true;
                default: ratio = config.ActiveCameraRatio; return false;
            }
        }

        internal static void RefreshAlarmTimer(Component camera, string reason)
        {
            if (!IsServerRuntime()) return;
            // Containment Breach: the security alarm system is inactive for the
            // contract's duration — no camera-raised alarm (detection or sabotage)
            // may start or refresh the timed alarm. The wave-start pulse is purely
            // presentational and never routes through here.
            if (IsContainmentBreachContractActive()) return;

            // #466: nothing raises or refreshes the alarm during the post-alarm cooldown -
            // detection sabotage included. This is the "cannot re-engage for 60s" half of
            // the rule; the sweep gate above is the "cameras do not detect" half.
            if (IsAlarmCooldownActive) return;

            CctvSecurityConfig config = CctvSecurityConfig.Current;
            if (config.AllCamerasPassive) return;

            // Engage edge, evaluated before the timer is written: a refresh from a camera
            // that is still watching must not re-ping. One ping per alarm episode.
            bool engaging = !IsTimedAlarmActive;
            _alarmEndsAt = SecurityTimeSeconds() + config.AlarmDurationSeconds;
            _alarmReason = string.IsNullOrWhiteSpace(reason) ? "SECURITY CAMERA" : reason;
            MainframeSupport activeMainframe = MainframeSupport.Active;
            if (activeMainframe != null)
            {
                activeMainframe.SetSecurityAlarmServerSide(_alarmEndsAt, _alarmReason);
                bool alreadyOwned = _securityAlarmOwnsMainframeAlarm;
                bool alreadyOn = activeMainframe.IsAlarmOn;
                if (alreadyOwned || !alreadyOn)
                {
                    activeMainframe.SetAlarmServerSide(true);
                    _securityAlarmOwnsMainframeAlarm = true;
                }
                else
                {
                    _securityAlarmOwnsMainframeAlarm = false;
                }
            }
            else
            {
                _securityAlarmOwnsMainframeAlarm = false;
            }
            StartAlarmBlackoutIfNeeded(config);
            _alarmEpisodeWasActive = true;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts.CctvSecurity] Alarm refreshed reason='{_alarmReason}' camera='{DescribeCamera(camera)}' duration={config.AlarmDurationSeconds:0.0}s.");

            if (engaging && TryResolveCameraPosition(camera, out Vector3 pingPosition))
                CctvAlarmAwarenessPing.EmitForAlarmEngage(pingPosition, DescribeCamera(camera), config);
        }

        private static bool TryResolveCameraPosition(Component camera, out Vector3 position)
        {
            CctvSecurityCameraState state = CctvSecurityCameraRegistry.Find(camera);
            Transform transform = state?.Transform ?? (camera != null ? camera.transform : null);
            if (transform == null)
            {
                position = Vector3.zero;
                return false;
            }

            position = transform.position;
            return true;
        }

        internal static void SilenceSecurityAlarm()
        {
            if (!IsServerRuntime()) return;
            ClearSecurityAlarmTimer(turnOffMainframeIfOwned: true);
        }

        internal static void ReleaseMainframeAlarmOwnership()
        {
            _securityAlarmOwnsMainframeAlarm = false;
        }

        internal static void OnMainframeHacked()
        {
            _securityDisabledByHack = true;
            ClearSecurityAlarmTimer(turnOffMainframeIfOwned: true);
            ClearAllHostileCameraState();
            MainframeProtocolDirector.CancelForSecurityDisable();
            CctvModuleConfig.Log?.LogInfo("[MoonContracts.CctvSecurity] Mainframe hacked; hostile CCTV security disabled.");
        }

        // FacilityMeltdown's apparatus pull, via FacilityMeltdownCompat. Meltdown raises
        // its listener from inside its own ClientRpc, so this runs on every client and
        // each machine gates its own presentation — no netcode of ours involved.
        //
        // Safe to run everywhere: ClearSecurityAlarmTimer keeps its mainframe writes
        // behind IsServerRuntime, ClearAllHostileCameraState is local camera state, and
        // CancelForSecurityDisable only tears down local event state and restores the
        // local light pulse.
        //
        // The alarm *fixtures* are deliberately not silenced here. They stop being
        // security indicators and become meltdown warning lights instead, which
        // InteriorAlarmFixture handles on its own meltdown branch.
        internal static void OnFacilityMeltdownStarted()
        {
            if (_securityDisabledByMeltdown) return;

            _securityDisabledByMeltdown = true;
            ClearSecurityAlarmTimer(turnOffMainframeIfOwned: true);
            ClearAllHostileCameraState();
            MainframeProtocolDirector.CancelForSecurityDisable();
            CctvModuleConfig.Log?.LogInfo("[MoonContracts.CctvSecurity] Facility meltdown started; hostile CCTV security disabled for the round.");
        }

        internal static void OnCameraBroken(CctvSecurityCameraState state)
        {
            if (state == null || state.IsBroken) return;
            bool wasHostile = state.IsSecurityActive || state.IsSuspicious;
            if (wasHostile)
                RefreshAlarmTimer(state.CameraComponent, "CAMERA SABOTAGE");
            state.MarkBroken();
            OnCameraEligibilityChanged(state);
        }

        // Remote shutdown via CctvSupportApi.TryDisableCamera (Field Mechanic hack).
        // Same terminal state as sabotage minus the "CAMERA SABOTAGE" alarm: a hack
        // takes the camera down quietly.
        internal static void OnCameraRemotelyDisabled(CctvSecurityCameraState state)
        {
            if (state == null || state.IsBroken) return;
            state.MarkBroken();
            OnCameraEligibilityChanged(state);
        }

        internal static void OnCameraEligibilityChanged(CctvSecurityCameraState state)
        {
            if (state == null) return;
            if (!state.Eligible)
                state.ClearActive();

            CctvSecurityConfig config = CctvSecurityConfig.Current;
            if (!config.Enabled || IsSecurityDisabled)
            {
                ClearAllHostileCameraState();
                return;
            }

            RotateActiveSet(force: true, now: SecurityTimeSeconds());
        }

        private static void RotateActiveSet(bool force, float now)
        {
            CctvSecurityConfig config = CctvSecurityConfig.Current;
            _lastAllCamerasPassive = config.AllCamerasPassive;
            _nextRotationAt = now + config.RotationSeconds;
            if (!config.Enabled || config.AllCamerasPassive || IsSecurityDisabled)
            {
                ClearAllHostileCameraState();
                return;
            }

            // #735: the main-entrance tile's camera never joins the rotation. Applied here, on
            // every rotation, because the dungeon and supplementary cameras can both arrive
            // after registration; the rule caches per round so this is a no-op after the first
            // successful pass.
            CctvMainEntranceTileRule.Apply(Cameras);

            var eligible = new List<CctvSecurityCameraState>();
            for (int i = 0; i < Cameras.Count; i++)
                if (Cameras[i] != null && Cameras[i].Eligible)
                    eligible.Add(Cameras[i]);

            int count = CalculateActiveCameraCount(eligible.Count, config);
            for (int i = 0; i < Cameras.Count; i++)
                if (Cameras[i] != null)
                    Cameras[i].ClearActive();

            LastActiveSet.Clear();
            if (count > 0)
            {
                int seed = BuildRotationSeed(force, now);
                eligible.Sort((a, b) => ScoreForRotation(a, seed, now).CompareTo(ScoreForRotation(b, seed, now)));
                for (int i = 0; i < count && i < eligible.Count; i++)
                {
                    eligible[i].IsSecurityActive = true;
                    eligible[i].LastSelectedAt = now;
                    LastActiveSet.Add(eligible[i]);
                }
            }

            if (!force)
                RotationRefreshed?.Invoke();
        }

        private static int ScoreForRotation(CctvSecurityCameraState state, int seed, float now)
        {
            unchecked
            {
                int value = state.CameraIndex * 73856093 ^ seed * 19349663;
                float recentSelectionWindow = CctvSecurityConfig.Current.RotationSeconds * (2f / 3f);
                if (state.LastSelectedAt > 0f && now - state.LastSelectedAt < recentSelectionWindow)
                    value += 100000000;
                return value;
            }
        }

        private static int BuildRotationSeed(bool force, float now)
        {
            int mapSeed = StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : 0;
            float rotationSeconds = Mathf.Max(1f, CctvSecurityConfig.Current.RotationSeconds);
            int bucket = Mathf.FloorToInt(now / rotationSeconds);
            return mapSeed ^ (bucket * 397) ^ (force ? 0x4C0C : 0);
        }

        private static void TickDetection(float now)
        {
            if (_lastDetectionSweepAt < 0f || now < _lastDetectionSweepAt)
            {
                _lastDetectionSweepAt = now;
                RunDetectionSweep(0f, now);
                return;
            }

            float elapsed = Mathf.Max(0f, now - _lastDetectionSweepAt);
            if (elapsed < DetectionSweepIntervalSeconds)
                return;

            _lastDetectionSweepAt = now;
            RunDetectionSweep(elapsed, now);
        }

        private static void RunDetectionSweep(float elapsed, float now)
        {
            CctvSecurityConfig config = CctvSecurityConfig.Current;
            bool isServer = IsServerRuntime();
            for (int i = 0; i < Cameras.Count; i++)
            {
                CctvSecurityCameraState state = Cameras[i];
                if (state == null || !state.IsSecurityActive || !state.Eligible)
                {
                    state?.ClearPresentationSuspicion();
                    if (isServer)
                        state?.ClearAuthoritativeSuspicion();
                    continue;
                }

                if (!CctvDetectionProbe.TryFindVisiblePlayer(
                        state, out GameNetcodeStuff.PlayerControllerB player, out bool sawLocalPlayer))
                {
                    state.ClearPresentationSuspicion();
                    if (isServer)
                        state.ClearAuthoritativeSuspicion();
                    continue;
                }

                state.SetPresentationSuspicion(player, sawLocalPlayer);
                if (!isServer) continue;

                state.TrackedPlayer = player;
                state.IsSuspicious = true;
                // Chameleon-style companion upgrades stretch the detection window per player.
                state.DetectionProgressSeconds += elapsed / CctvSupportApi.ResolveDetectionTimeMultiplier(player);
                state.LastDetectionAt = now;

                if (state.DetectionProgressSeconds >= config.DetectionSeconds)
                {
                    RefreshAlarmTimer(state.CameraComponent, "SECURITY CAMERA");
                    state.DetectionProgressSeconds = 0f;
                }
            }
        }

        private static void ClearSecurityAlarmTimer(bool turnOffMainframeIfOwned)
        {
            bool ownedMainframeAlarm = _securityAlarmOwnsMainframeAlarm;
            _alarmEndsAt = 0f;
            _alarmReason = null;
            _securityAlarmOwnsMainframeAlarm = false;

            MainframeSupport activeMainframe = MainframeSupport.Active;
            if (activeMainframe != null && IsServerRuntime())
            {
                activeMainframe.ClearSecurityAlarmServerSide();
                if (turnOffMainframeIfOwned && ownedMainframeAlarm)
                    activeMainframe.SetAlarmServerSide(false);
            }
        }

        private static void ClearAllHostileCameraState()
        {
            for (int i = 0; i < Cameras.Count; i++)
                if (Cameras[i] != null)
                    Cameras[i].ClearActive();
            LastActiveSet.Clear();
        }

        private static void ResetDetectionSweepClock()
        {
            _lastDetectionSweepAt = -1f;
        }

        private static void TrackAlarmBlackoutState(CctvSecurityConfig config)
        {
            if (IsTimedAlarmActive)
            {
                StartAlarmBlackoutIfNeeded(config);
            }
            else
            {
            }
        }

        private static void StartAlarmBlackoutIfNeeded(CctvSecurityConfig config)
        {
            if (!IsTimedAlarmActive) return;
            CctvSecurityLightPulse.StartPulse(Mathf.Max(config.AlarmBlackoutSeconds, AlarmRemainingSeconds));
        }

        private static string DescribeCamera(Component camera)
        {
            CctvSecurityCameraState state = CctvSecurityCameraRegistry.Find(camera);
            return state != null ? state.Label : "<unknown>";
        }

        internal static float SecurityTimeSeconds()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening)
                return (float)manager.ServerTime.Time;
            return Time.unscaledTime;
        }

        /// <summary>
        /// #716 G5: delegates to <see cref="CctvNetworkRole.IsServer"/>. This used to hold its
        /// own copy of the role check whose last line returned <c>true</c> when
        /// <see cref="StartOfRound"/> was null, so a client ran the server-only branches below
        /// until the singletons resolved.
        /// </summary>
        private static bool IsServerRuntime()
        {
            return CctvNetworkRole.IsServer();
        }
    }
}
