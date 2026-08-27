using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;
namespace Y4NGZCompany.Facility.Security
{
    internal static class MainframeProtocolDirector
    {
        // Protocol events should read as rare facility color, not a cycle:
        // the old deterministic scheduling re-fired each event every cooldown
        // (3-5 min), stacking several drills into a single round. Each
        // occurrence now waits a randomized multiple of the configured
        // cooldown. The fire alarm test and camera calibration sweep were
        // removed, so the lockdown drill is the only protocol event left and
        // the old cross-event push-out has nothing left to push.
        private const float IntervalJitterMinMultiplier = 2.5f;
        private const float IntervalJitterMaxMultiplier = 4.5f;

        private static MainframeProtocolEvent _activeEvent;
        private static float _activeUntil;
        private static float _nextLockdownAt;
        // True while the CCTV security alarm is holding the lockdown gates.
        // Engaged on the alarm's rising edge, released on its falling edge —
        // the alarm timer itself (refreshed by detection) is the duration.
        private static bool _alarmLockdownEngaged;
        // Containment Breach wave-start pulse: a fixed-length alarm *presentation*
        // window (facility blackout, fixture strobe + siren, camera lens strobe)
        // with none of the security semantics — no detection, no lockdown, no
        // alarm-timer refresh. Driven from MoonContractState.SpawnContainmentWave,
        // which runs deterministically on every machine (same local path as the
        // wave voice lines), so the window needs no networking of its own.
        private static float _wavePulseVisualUntil;

        internal static bool IsProtocolAlarmVisualActive =>
            (_activeEvent != MainframeProtocolEvent.None && Time.unscaledTime < _activeUntil)
            || Time.unscaledTime < _wavePulseVisualUntil;

        internal static void ResetRound()
        {
            _activeEvent = MainframeProtocolEvent.None;
            _activeUntil = 0f;
            _nextLockdownAt = 0f;
            _alarmLockdownEngaged = false;
            _wavePulseVisualUntil = 0f;
            CctvLockdownGateService.ResetRound();
            CctvSecurityLightPulse.Restore();
        }

        // Time-boxed alarm pulse for a Containment Breach wave start: the normal
        // alarm look for exactly `seconds` (blackout via CctvSecurityLightPulse,
        // fixture/lens strobe via IsProtocolAlarmVisualActive), ended by the
        // pulse's existing smooth restore.
        internal static void BeginContainmentWavePulse(float seconds)
        {
            float duration = Mathf.Max(0f, seconds);
            if (duration <= 0f) return;
            _wavePulseVisualUntil = Time.unscaledTime + duration;
            CctvSecurityLightPulse.StartPulse(duration);
        }

        internal static void Tick()
        {
            CctvSecurityLightPulse.Tick();
            CctvSecurityConfig config = CctvSecurityConfig.Current;
            if (!config.Enabled)
                return;

            UpdateAlarmLockdown();

            if (!config.ProtocolEventsEnabled || CctvSecurityDirector.IsSecurityDisabled)
                return;

            if (_activeEvent != MainframeProtocolEvent.None && Time.unscaledTime >= _activeUntil)
                EndActiveEvent();

            if (_activeEvent != MainframeProtocolEvent.None)
                return;

            if (_nextLockdownAt <= 0f)
            {
                _nextLockdownAt = NextOccurrence(config.LockdownDrillCooldownSeconds);
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] PROTOCOL_SCHEDULE lockdown=+{_nextLockdownAt - Time.unscaledTime:0}s");
            }

            if (Time.unscaledTime >= _nextLockdownAt)
                BeginEvent(MainframeProtocolEvent.LockdownDrill, 10f, config.LockdownDrillCooldownSeconds);
        }

        // Security containment: while the CCTV alarm runs, the lockdown gates
        // stay closed for the alarm's full (detection-refreshed) duration.
        // The alarm state is network-replicated, so every machine takes the
        // same edges. The gates are shared with the drill and the Containment
        // Breach contract; never release while another holder is live.
        private static void UpdateAlarmLockdown()
        {
            bool alarmGatesEnabled = SurveillanceBootstrap.Config?.AlarmLockdownGates?.Value ?? true;
            bool alarmActive = alarmGatesEnabled && CctvSecurityDirector.IsTimedAlarmActive;
            if (alarmActive)
            {
                bool firstEdge = !_alarmLockdownEngaged;
                _alarmLockdownEngaged = true;
                // Also covers a mid-alarm re-close: if another holder (contract
                // completion) opened the shared gates while the alarm still
                // runs, the doors come back down.
                if (!CctvLockdownGateService.LockdownActive)
                {
                    CctvLockdownGateService.PrepareLockdownGates();
                    CctvLockdownGateService.BeginLockdown();
                    CctvLockdownGateService.PlayLockdownSlam();
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts.CctvSecurity] ALARM_LOCKDOWN {(firstEdge ? "engaged" : "re-engaged")} (security alarm).");
                }
            }
            else if (_alarmLockdownEngaged)
            {
                _alarmLockdownEngaged = false;
                if (_activeEvent != MainframeProtocolEvent.LockdownDrill && CanReleaseSharedGates())
                {
                    CctvLockdownGateService.EndLockdown();
                    CctvModuleConfig.Log?.LogInfo("[MoonContracts.CctvSecurity] ALARM_LOCKDOWN released (alarm ended).");
                }
            }
        }

        internal static void CancelForSecurityDisable()
        {
            bool holdingGates = _activeEvent == MainframeProtocolEvent.LockdownDrill || _alarmLockdownEngaged;
            _alarmLockdownEngaged = false;
            _wavePulseVisualUntil = 0f;
            if (holdingGates && CanReleaseSharedGates())
                CctvLockdownGateService.EndLockdown();

            _activeEvent = MainframeProtocolEvent.None;
            _activeUntil = 0f;
            CctvSecurityLightPulse.Restore();
        }

        internal static bool BeginManualEvent(MainframeProtocolEvent evt)
        {
            CctvSecurityConfig config = CctvSecurityConfig.Current;
            switch (evt)
            {
                case MainframeProtocolEvent.LockdownDrill:
                    BeginEvent(MainframeProtocolEvent.LockdownDrill, 10f, config.LockdownDrillCooldownSeconds);
                    return true;
                default:
                    return false;
            }
        }

        internal static bool EndManualEvent(MainframeProtocolEvent evt)
        {
            if (evt == MainframeProtocolEvent.LockdownDrill && CanReleaseSharedGates())
                CctvLockdownGateService.EndLockdown();

            if (_activeEvent == evt || evt == MainframeProtocolEvent.None)
            {
                _activeEvent = MainframeProtocolEvent.None;
                _activeUntil = 0f;
                CctvSecurityLightPulse.Restore();
                return true;
            }

            return false;
        }
        private static void BeginEvent(MainframeProtocolEvent evt, float duration, float cooldown)
        {
            _activeEvent = evt;
            _activeUntil = Time.unscaledTime + duration;
            CctvSecurityLightPulse.StartPulse(Mathf.Max(CctvSecurityConfig.Current.ProtocolBlackoutSeconds, duration));
            Announce(evt);

            switch (evt)
            {
                case MainframeProtocolEvent.LockdownDrill:
                    _nextLockdownAt = NextOccurrence(cooldown);
                    CctvLockdownGateService.PrepareLockdownGates();
                    CctvLockdownGateService.BeginLockdown();
                    CctvLockdownGateService.PlayLockdownSlam();
                    break;
            }
        }

        private static float NextOccurrence(float cooldownSeconds)
        {
            float multiplier = Random.Range(IntervalJitterMinMultiplier, IntervalJitterMaxMultiplier);
            return Time.unscaledTime + Mathf.Max(5f, cooldownSeconds) * multiplier;
        }

        private static void EndActiveEvent()
        {
            if (_activeEvent == MainframeProtocolEvent.LockdownDrill && CanReleaseSharedGates())
                CctvLockdownGateService.EndLockdown();

            _activeEvent = MainframeProtocolEvent.None;
            _activeUntil = 0f;
        }

        // The lockdown gates are shared: the drill, the CCTV alarm, and the
        // Containment Breach contract can all be holding them. A holder that
        // finishes may only open the doors when no other holder is live.
        private static bool CanReleaseSharedGates()
        {
            return !_alarmLockdownEngaged
                && !ContractsBridge.ContainmentLockdownHeld;
        }

        private static void Announce(MainframeProtocolEvent evt)
        {
            string text = evt switch
            {
                MainframeProtocolEvent.LockdownDrill => "LOCKDOWN DRILL",
                _ => "MAINFRAME PROTOCOL"
            };

            HUDManager.Instance?.DisplayTip("FACILITY ANNOUNCER", text, false, false, "LC_Tip1");
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts.CctvSecurity] ANNOUNCER {text}");
        }
    }
}

