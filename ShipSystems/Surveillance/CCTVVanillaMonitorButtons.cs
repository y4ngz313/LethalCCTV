using System;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Events;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Takes over the two vanilla buttons on the lower-left map monitor bezel
    /// (2026-07-02 spec):
    /// - RED (vanilla power toggle, onInteract -> mapScreen.SwitchScreenButton):
    ///   power toggle removed entirely; becomes the CCTV entry point.
    /// - GREY (vanilla radar target switch, onInteract -> SwitchRadarTargetForward):
    ///   becomes the vanilla&lt;-&gt;CCTV feed cycle; inert while unpurchased or
    ///   while an operator holds the claim. The terminal "switch" command keeps
    ///   its vanilla target-cycling behaviour untouched.
    ///
    /// The buttons are anonymous "Cube (2)" objects on the ship's MonitorWall,
    /// so they are located by scanning that subtree's InteractTriggers for
    /// persistent onInteract calls that target StartOfRound.mapScreen — stable
    /// across game updates that rename or move the objects, without the
    /// scene-wide FindObjectsOfType the first version used (#501).
    /// </summary>
    internal static class CCTVVanillaMonitorButtons
    {
        private const string RedHoverTip = "View cameras : [E]";
        private const string GreyHoverTip = "Switch monitor feed : [E]";
        // #501 — the probe used to be a 1 Hz scene-wide FindObjectsOfType
        // (18.94 ms/pass) that never stopped retrying when a button failed to
        // resolve. It is now scoped to the ship subtree and backs off
        // geometrically on failure, resetting when the ship/mapScreen changes.
        private static readonly float[] ProbeBackoffSeconds = { 1.0f, 5.0f, 15.0f };
        private const float TipRefreshIntervalSeconds = 0.2f;
        private const float PressSfxVolume = 0.9f;

        private sealed class TakenOverButton
        {
            internal InteractTrigger Trigger;
            internal string OriginalHoverTip;
            internal string OriginalDisabledHoverTip;
            internal bool OriginalInteractable;
            internal UnityAction<PlayerControllerB> Listener;
            internal AudioSource Audio;
        }

        private static TakenOverButton _redButton;
        private static TakenOverButton _greyButton;
        private static float _nextProbeAt;
        private static int _probeFailures;
        private static StartOfRound _probeScopeOwner;
        private static ManualCameraRenderer _probeScopeMapScreen;
        private static bool _loggedProbeExhausted;
        private static float _nextTipRefreshAt;
        private static bool _loggedTakeover;
        // #597 — true while the red bezel button has been handed back to vanilla so it can
        // act as GeneralImprovements' monitor-group power switch. See
        // SetRedButtonVanillaPowerMode.
        private static bool _redButtonVanillaPowerMode;
        private static bool _loggedRedButtonVanillaPowerMode;

        internal static Transform RedButtonTransform =>
            _redButton != null && _redButton.Trigger != null ? _redButton.Trigger.transform : null;

        internal static void Tick()
        {
            if (!EnsureButtons())
                return;

            if (Time.unscaledTime >= _nextTipRefreshAt)
            {
                _nextTipRefreshAt = Time.unscaledTime + TipRefreshIntervalSeconds;
                RefreshRedButtonState();
                RefreshGreyButtonState();
            }
        }

        internal static void Shutdown()
        {
            Release(ref _redButton);
            Release(ref _greyButton);
            _redButtonVanillaPowerMode = false;
            _loggedRedButtonVanillaPowerMode = false;
            _loggedTakeover = false;
            _probeScopeOwner = null;
            _probeScopeMapScreen = null;
            ResetProbeBackoff();
        }

        private static bool EnsureButtons()
        {
            bool redAlive = _redButton != null && _redButton.Trigger != null;
            bool greyAlive = _greyButton != null && _greyButton.Trigger != null;
            if (redAlive && greyAlive)
                return true;

            // Scene reload destroyed the triggers; drop stale wrappers.
            if (_redButton != null && !redAlive)
            {
                _redButton = null;
                _redButtonVanillaPowerMode = false;
            }
            if (_greyButton != null && !greyAlive) _greyButton = null;

            // Don't even arm the backoff before the ship exists: a null
            // StartOfRound/mapScreen is the menu, not a failed resolve.
            StartOfRound sor = StartOfRound.Instance;
            ManualCameraRenderer mapScreen = sor != null ? sor.mapScreen : null;
            Transform scope = ResolveProbeScope(sor, mapScreen);
            if (mapScreen == null || scope == null)
                return false;

            // A new StartOfRound/mapScreen means a new ship: the previous
            // failures say nothing about this one, so restart at the fast rate.
            if (!ReferenceEquals(sor, _probeScopeOwner) || !ReferenceEquals(mapScreen, _probeScopeMapScreen))
            {
                _probeScopeOwner = sor;
                _probeScopeMapScreen = mapScreen;
                ResetProbeBackoff();
            }

            if (Time.unscaledTime < _nextProbeAt)
                return _redButton != null || _greyButton != null;

            InteractTrigger[] triggers;
            try { triggers = scope.GetComponentsInChildren<InteractTrigger>(includeInactive: true); }
            catch { triggers = null; }
            if (triggers == null)
            {
                NoteProbeFailure();
                return false;
            }

            for (int i = 0; i < triggers.Length; i++)
            {
                InteractTrigger trigger = triggers[i];
                if (trigger == null || trigger.onInteract == null)
                    continue;

                if (_redButton == null && HasPersistentCall(trigger.onInteract, mapScreen, "SwitchScreenButton"))
                {
                    _redButton = TakeOver(trigger, OnRedPressed, "red/power");
                    _redButtonVanillaPowerMode = false;
                    continue;
                }
                if (_greyButton == null && HasPersistentCall(trigger.onInteract, mapScreen, "SwitchRadarTargetForward"))
                {
                    _greyButton = TakeOver(trigger, OnGreyPressed, "grey/switch");
                }
            }

            bool resolved = _redButton != null && _greyButton != null;
            if (!resolved)
            {
                NoteProbeFailure();
                return false;
            }

            ResetProbeBackoff();
            if (!_loggedTakeover)
            {
                _loggedTakeover = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Vanilla monitor buttons taken over: red={GetPath(_redButton.Trigger.transform)} grey={GetPath(_greyButton.Trigger.transform)}.");
            }

            return true;
        }

        /// <summary>
        /// Smallest subtree that provably contains the bezel buttons: they are
        /// children of the ship's MonitorWall (same wall the CCTV station and
        /// vanilla monitor display resolve through). Falls back to the whole
        /// ship, then to the mapScreen's own root — never to the scene.
        /// </summary>
        private static Transform ResolveProbeScope(StartOfRound sor, ManualCameraRenderer mapScreen)
        {
            Transform ship = sor != null ? sor.elevatorTransform : null;
            if (ship != null)
            {
                Transform monitorWall = ship.Find("ShipModels2b/MonitorWall");
                if (monitorWall != null)
                    return monitorWall;
                return ship;
            }
            return mapScreen != null ? mapScreen.transform.root : null;
        }

        private static void ResetProbeBackoff()
        {
            _probeFailures = 0;
            _nextProbeAt = 0f;
            _loggedProbeExhausted = false;
        }

        private static void NoteProbeFailure()
        {
            int index = Mathf.Min(_probeFailures, ProbeBackoffSeconds.Length - 1);
            _nextProbeAt = Time.unscaledTime + ProbeBackoffSeconds[index];
            if (_probeFailures < ProbeBackoffSeconds.Length)
                _probeFailures++;

            if (_probeFailures >= ProbeBackoffSeconds.Length && !_loggedProbeExhausted)
            {
                _loggedProbeExhausted = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] Vanilla monitor buttons did not resolve in the ship subtree; " +
                    $"retrying at {ProbeBackoffSeconds[ProbeBackoffSeconds.Length - 1]:0}s intervals (logged once).");
            }
        }

        private static bool HasPersistentCall(UnityEventBase unityEvent, ManualCameraRenderer target, string methodName)
        {
            int count = unityEvent.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(unityEvent.GetPersistentTarget(i), target) &&
                    string.Equals(unityEvent.GetPersistentMethodName(i), methodName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static TakenOverButton TakeOver(InteractTrigger trigger, UnityAction<PlayerControllerB> listener, string label)
        {
            var taken = new TakenOverButton
            {
                Trigger = trigger,
                OriginalHoverTip = trigger.hoverTip,
                OriginalDisabledHoverTip = trigger.disabledHoverTip,
                OriginalInteractable = trigger.interactable,
                Listener = listener,
            };

            // Silence every serialized call (the vanilla power/radar handler) but
            // keep the trigger itself alive for our runtime listener.
            int count = trigger.onInteract.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
                trigger.onInteract.SetPersistentListenerState(i, UnityEventCallState.Off);
            trigger.onInteract.AddListener(listener);

            taken.Audio = trigger.GetComponent<AudioSource>();
            if (taken.Audio == null)
            {
                taken.Audio = trigger.gameObject.AddComponent<AudioSource>();
                taken.Audio.playOnAwake = false;
                taken.Audio.spatialBlend = 1f;
                taken.Audio.minDistance = 0.8f;
                taken.Audio.maxDistance = 12f;
            }

            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Monitor button takeover ({label}): {GetPath(trigger.transform)}.");
            return taken;
        }

        private static void Release(ref TakenOverButton button)
        {
            if (button == null)
                return;

            InteractTrigger trigger = button.Trigger;
            if (trigger != null)
            {
                if (button.Listener != null && trigger.onInteract != null)
                    trigger.onInteract.RemoveListener(button.Listener);
                int count = trigger.onInteract != null ? trigger.onInteract.GetPersistentEventCount() : 0;
                for (int i = 0; i < count; i++)
                    trigger.onInteract.SetPersistentListenerState(i, UnityEventCallState.RuntimeOnly);
                trigger.hoverTip = button.OriginalHoverTip;
                trigger.disabledHoverTip = button.OriginalDisabledHoverTip;
                trigger.interactable = button.OriginalInteractable;
            }

            button = null;
        }

        private static void RefreshRedButtonState()
        {
            if (_redButton == null || _redButton.Trigger == null)
                return;

            InteractTrigger trigger = _redButton.Trigger;

            // #597 — with GeneralImprovements' better monitors the red bezel button is no
            // longer "the lower-left map monitor's power": GI turns it into the power switch
            // for its entire replacement monitor group. Give it back to vanilla whenever the
            // physical CCTV access button is alive to own entry, which is the normal state
            // once the upgrade is purchased. If that clone is missing the bezel button stays
            // the CCTV entry fallback, so entry can never become unreachable.
            //
            // #600 — except while CCTV owns GI's MAP screen, which is now the default feed
            // target. GI's power switch repaints exactly that surface
            // (SwitchScreenOn -> Monitors.UpdateMapMaterial), so handing the button back
            // there would wire a press straight into blanking the live feed. The
            // SwitchScreenButton prefix already swallows that press; keeping the button out
            // of vanilla power mode as well means the two mechanisms agree instead of one
            // relying on the other, and the bezel button behaves exactly as it does on the
            // vanilla wall for as long as CCTV owns the map. GI gets the switch back the
            // moment the feed returns to vanilla and the binding releases.
            SetRedButtonVanillaPowerMode(
                CCTVVanillaMonitorDisplay.IsGeneralImprovementsMonitorWallActive() &&
                !CCTVVanillaMonitorDisplay.OwnsGeneralImprovementsMapScreen() &&
                CCTVAccessButton.IsAvailable);
            if (_redButtonVanillaPowerMode)
            {
                trigger.interactable = _redButton.OriginalInteractable;
                trigger.hoverTip = _redButton.OriginalHoverTip;
                trigger.disabledHoverTip = _redButton.OriginalDisabledHoverTip;
                return;
            }

            if (CCTVAccessButton.IsAvailable)
            {
                // The external access button owns entry; the bezel button goes
                // silently inert (same policy as the unpurchased grey button)
                // but stays wired as the fallback if the clone ever dies.
                trigger.interactable = false;
                trigger.disabledHoverTip = string.Empty;
            }
            else if (CCTVMonitorFeedSync.IsEntryAvailable(out string reason))
            {
                trigger.interactable = true;
                trigger.hoverTip = RedHoverTip;
            }
            else
            {
                trigger.interactable = false;
                trigger.disabledHoverTip = reason ?? string.Empty;
            }
        }

        /// <summary>
        /// Flips the red bezel button between the CCTV entry takeover and its serialized
        /// vanilla behaviour (<c>mapScreen.SwitchScreenButton</c>, which under
        /// GeneralImprovements powers the whole replacement monitor group). Idempotent, and
        /// deliberately symmetric with <see cref="TakeOver"/>/<see cref="Release"/>: exactly
        /// one of the persistent call and our runtime listener is ever live, so a press can
        /// never both toggle monitor power and open CCTV.
        /// </summary>
        private static void SetRedButtonVanillaPowerMode(bool vanillaPower)
        {
            TakenOverButton button = _redButton;
            if (button == null || button.Trigger == null || button.Trigger.onInteract == null)
                return;
            if (_redButtonVanillaPowerMode == vanillaPower)
                return;

            _redButtonVanillaPowerMode = vanillaPower;

            var onInteract = button.Trigger.onInteract;
            int count = onInteract.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                onInteract.SetPersistentListenerState(
                    i,
                    vanillaPower ? UnityEventCallState.RuntimeOnly : UnityEventCallState.Off);
            }

            if (button.Listener != null)
            {
                onInteract.RemoveListener(button.Listener);
                if (!vanillaPower)
                    onInteract.AddListener(button.Listener);
            }

            if (vanillaPower && !_loggedRedButtonVanillaPowerMode)
            {
                _loggedRedButtonVanillaPowerMode = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] GeneralImprovements better monitors are active; the red bezel button keeps its " +
                    "vanilla monitor-power behaviour and the physical CCTV access button owns entry.");
            }
        }

        private static void RefreshGreyButtonState()
        {
            if (_greyButton == null || _greyButton.Trigger == null)
                return;

            InteractTrigger trigger = _greyButton.Trigger;
            if (CCTVMonitorFeedSync.IsFeedToggleAvailable())
            {
                trigger.interactable = true;
                trigger.hoverTip = GreyHoverTip;
            }
            else
            {
                // Spec: does nothing while unpurchased / occupied — stay silent.
                trigger.interactable = false;
                trigger.disabledHoverTip = string.Empty;
            }
        }

        private static void OnRedPressed(PlayerControllerB player)
        {
            RequestEntry(player);
        }

        /// <summary>
        /// Shared CCTV entry flow (claim request -> canonical pose settle -> EnterFocus). Called by the
        /// bezel button fallback and by the external access button, which is the
        /// primary entry interaction whenever it is alive.
        /// </summary>
        internal static void RequestEntry(PlayerControllerB player)
        {
            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (local == null)
                return;
            if (player != null && player != local)
                return;

            if (!CCTVMonitorFeedSync.IsEntryAvailable(out string reason))
            {
                if (!string.IsNullOrEmpty(reason))
                    HUDManager.Instance?.DisplayTip("CCTV", reason, isWarning: false);
                return;
            }

            // No click here: the press SFX plays at the enter animation's press
            // contact (MonitorFocus.CompleteStationFeedFlip -> PlayRedButtonPressSfx).
            CCTVMonitorFeedSync.RequestEntry(
                local,
                onConfirmed: () => BeginEntry(local),
                onDenied: message =>
                {
                    if (!string.IsNullOrEmpty(message))
                        HUDManager.Instance?.DisplayTip("CCTV", message, isWarning: true);
                });
        }

        private static void BeginEntry(PlayerControllerB player)
        {
            if (QuadMonitor.MonitorRoot == null || QuadMonitor.QuadRTs == null)
                QuadMonitor.Spawn();

            // MonitorFocus reruns the complete entry preflight before taking
            // ownership of the special-animation flags. It also releases the
            // confirmed feed claim if preflight or the settle later aborts.
            MonitorFocus.BeginEntryPoseSettle(player);
        }

        private static void OnGreyPressed(PlayerControllerB player)
        {
            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (local == null)
                return;
            if (player != null && player != local)
                return;

            if (!CCTVMonitorFeedSync.IsFeedToggleAvailable())
                return;

            PlayPressSfx(_greyButton);
            CCTVMonitorFeedSync.RequestFeedToggle(local);
        }

        /// <summary>Red button click, fired by MonitorFocus at the enter animation's press contact.</summary>
        internal static void PlayRedButtonPressSfx()
        {
            PlayPressSfx(_redButton);
        }

        private static void PlayPressSfx(TakenOverButton button)
        {
            try
            {
                AudioClip clip = CCTVOperatorStation.GetButtonPressClip();
                if (button?.Audio != null && clip != null)
                    button.Audio.PlayOneShot(clip, PressSfxVolume);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Monitor button press sfx failed: {ex.Message}");
            }
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null) return "<null>";
            string path = transform.name;
            Transform current = transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }
    }
}
