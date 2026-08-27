using Y4NGZCompany.ShipSystems.Surveillance;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// Per-camera framerate throttle. UnityEngine.Camera has no equivalent of vanilla LC's
    /// ManualCameraRenderer.renderAtLowerFramerate / fps fields, so we implement the same
    /// "toggle Camera.enabled based on elapsed time" pattern as a sibling MonoBehaviour.
    /// The assigned FPS comes from LethalCCTV's local render-performance config.
    /// </summary>
    internal sealed class CCTVCameraThrottle : MonoBehaviour
    {
        private const float SeatedStationInteractiveFpsMultiplier = 1.8f;
        private const float SeatedStationInteractiveMaxFps = 24f;

        private Camera _cam;
        private float _activeInterval;
        private float _inactiveInterval;
        private float _idleInterval;
        private int _slot = -1;
        private float _elapsed;
        private bool _pendingWake;
        private float _pendingWakeAt;

        internal void Configure(Camera cam, int slot, float activeFps, float inactiveFps, float idleFps)
        {
            _cam = cam;
            _slot = slot;
            _activeInterval = activeFps > 0f ? (1f / activeFps) : 0f;
            _inactiveInterval = inactiveFps > 0f ? (1f / inactiveFps) : 0f;
            _idleInterval = idleFps > 0f ? (1f / idleFps) : 0f;
            _elapsed = 0f;
            _pendingWake = false;
            _pendingWakeAt = 0f;
            if (_cam != null)
            {
                _cam.enabled = false;
            }
        }

        /// <summary>
        /// Mark this throttle for a wake on the next Update.
        /// </summary>
        internal void RequestWake(float delaySeconds = 0f)
        {
            _pendingWake = true;
            _pendingWakeAt = Time.unscaledTime + Mathf.Max(0f, delaySeconds);
        }

        private void Update()
        {
            if (_cam == null)
            {
                return;
            }

            // The physical wall is intentionally blank outside focus mode, unless
            // we're an observer watching the operator's replicated CCTV feed on a
            // locally-visible monitor wall (y4ngz313/Y4NGZCompany#533 — observers
            // used to see a black monitor because their facility cameras never
            // woke up).
            bool observingRemoteFeed = CCTVVanillaMonitorDisplay.IsCctvModeActive &&
                                        CCTVVanillaMonitorDisplay.IsMonitorLocallyVisible;
            if (!MonitorFocus.IsFocused && !observingRemoteFeed)
            {
                if (_cam.enabled) _cam.enabled = false;
                _pendingWake = false;
                _pendingWakeAt = 0f;
                _elapsed = 0f;
                return;
            }

            float interval = ResolveCurrentInterval();
            if (interval <= 0f)
            {
                if (_cam.enabled) _cam.enabled = false;
                _pendingWake = false;
                _pendingWakeAt = 0f;
                _elapsed = 0f;
                return;
            }

            if (_pendingWake)
            {
                if (Time.unscaledTime < _pendingWakeAt)
                {
                    if (_cam.enabled) _cam.enabled = false;
                    return;
                }

                _pendingWake = false;
                _pendingWakeAt = 0f;
                _cam.enabled = true;
                _elapsed = 0f;
                return;
            }

            _elapsed += Time.deltaTime;
            if (_elapsed >= interval)
            {
                _cam.enabled = true;
                _elapsed -= interval;
            }
            else
            {
                _cam.enabled = false;
            }
        }

        private float ResolveCurrentInterval()
        {
            if (MonitorFocus.IsFocused && !MonitorFocus.IsFacilityFeedActive)
                return 0f;

            // Local operators drive their own MonitorFocus.ActiveSlot; observers
            // have no local focus, so key off the operator's replicated slot
            // instead (see CCTVMonitorFeedSync.RemoteActiveSlot).
            int effectiveActiveSlot = MonitorFocus.IsFocused
                ? MonitorFocus.ActiveSlot
                : CCTVMonitorFeedSync.RemoteActiveSlot;
            bool activeSlot = _slot == effectiveActiveSlot;
            bool seatedStation = CCTVVanillaMonitorDisplay.IsCctvModeActive;
            if (seatedStation)
            {
                // #562 — nobody is seated anywhere (no local focus, no replicated
                // operator claim), but the CCTV feed is still up on the wall. Keep
                // exactly the one camera that feeds the wall ticking at the idle
                // rate so the monitor shows a live picture instead of the frozen
                // last frame. "Unmanned" is derived from operator state, not from
                // the slot: the slot is deliberately retained across a release
                // (see CCTVMonitorFeedSync.RemoteActiveSlot).
                bool unmanned = !MonitorFocus.IsFocused && !CCTVMonitorFeedSync.HasOperator;
                if (unmanned)
                {
                    // Fall back to slot 0 when no slot was ever reported (fresh
                    // round, late joiner) — that is the slot the wall binds first.
                    int idleSlot = effectiveActiveSlot >= 0 ? effectiveActiveSlot : 0;
                    return _slot == idleSlot ? _idleInterval : 0f;
                }

                if (!activeSlot)
                    return 0f;
                return ResolveSeatedStationActiveInterval();
            }

            if (SurveillanceBootstrap.Config == null || !SurveillanceBootstrap.Config.DynamicActiveRendererEnabled.Value)
            {
                return _activeInterval;
            }

            float interval = activeSlot ? _activeInterval : _inactiveInterval;
            if (interval <= 0f)
                return interval;

            return interval;
        }

        private float ResolveSeatedStationActiveInterval()
        {
            if (_activeInterval <= 0f)
                return 0f;

            if (!MonitorFocus.IsStationCameraFeedInteractionActive)
                return _activeInterval;

            float configuredFps = 1f / _activeInterval;
            float boostedFps = Mathf.Min(
                SeatedStationInteractiveMaxFps,
                Mathf.Max(configuredFps, configuredFps * SeatedStationInteractiveFpsMultiplier));
            return boostedFps > 0f ? 1f / boostedFps : _activeInterval;
        }

        private void OnDisable()
        {
            if (_cam != null)
            {
                _cam.enabled = false;
            }
        }
    }
}
