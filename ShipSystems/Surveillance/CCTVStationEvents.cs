using System;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Animation/flair hook points for the CCTV operator station. Subscribers (future
    /// first-person/third-person animation drivers, SFX layers, network sync) attach
    /// here; the core station code only raises the events and never depends on any
    /// listener being present. All events fire on the local client only.
    /// </summary>
    public static class CCTVStationEvents
    {
        /// <summary>Local player began the sit-down transition into the CCTV chair.</summary>
        public static event Action<PlayerControllerB> ChairEntered;

        /// <summary>Local player stood up / left the CCTV chair.</summary>
        public static event Action<PlayerControllerB> ChairExited;

        /// <summary>Active feed slot changed while seated (arrow keys / paging). Slot is 0..3.</summary>
        public static event Action<int> CameraSelected;

        /// <summary>
        /// Seated control mode changed: true = mouse now drives the selected CCTV
        /// camera (joystick grabbed), false = back to seated free-look.
        /// </summary>
        public static event Action<bool> CameraControlChanged;

        /// <summary>
        /// Cardinal joystick direction while the player is steering a CCTV camera.
        /// Values are quantized to one of left/right/up/down before subscribers see them.
        /// </summary>
        public static event Action<Vector2> JoystickMoved;

        /// <summary>
        /// A station action button was pressed. Known ids: "monitor-mode-toggle",
        /// "camera-page", "station-action", "turret-fire", "stand-up".
        /// </summary>
        public static event Action<string> ActionButtonPressed;

        /// <summary>
        /// Radar glance state changed: true while the operator holds SPACE and the
        /// station camera eases toward the radar monitor, false when it returns to
        /// the CCTV monitor.
        /// </summary>
        public static event Action<bool> RadarViewChanged;

        internal static void RaiseChairEntered(PlayerControllerB player) => SafeRaise(() => ChairEntered?.Invoke(player), "ChairEntered");
        internal static void RaiseChairExited(PlayerControllerB player) => SafeRaise(() => ChairExited?.Invoke(player), "ChairExited");
        internal static void RaiseCameraSelected(int slot) => SafeRaise(() => CameraSelected?.Invoke(slot), "CameraSelected");
        internal static void RaiseCameraControlChanged(bool controllingCamera) => SafeRaise(() => CameraControlChanged?.Invoke(controllingCamera), "CameraControlChanged");
        internal static void RaiseJoystickMoved(Vector2 appliedDeltaDeg)
        {
            Vector2 direction = CCTVJoystickPhaseDriver.QuantizeCardinal(appliedDeltaDeg);
            SafeRaise(() => JoystickMoved?.Invoke(direction), "JoystickMoved");
        }
        internal static void RaiseActionButtonPressed(string actionId) => SafeRaise(() => ActionButtonPressed?.Invoke(actionId), "ActionButtonPressed");
        internal static void RaiseRadarViewChanged(bool lookingAtRadar) => SafeRaise(() => RadarViewChanged?.Invoke(lookingAtRadar), "RadarViewChanged");

        private static void SafeRaise(Action raise, string eventName)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTVStationEvents.{eventName} subscriber threw: {ex.Message}");
            }
        }
    }

    internal sealed class CCTVJoystickPhaseDriver
    {
        internal const float DefaultTransitionSeconds = 0.16f;

        private readonly float _transitionSeconds;
        private Vector2 _value;
        private Vector2 _requested;
        private Vector2 _from;
        private Vector2 _to;
        private Vector2 _queued;
        private float _elapsed;
        private bool _transitioning;

        internal CCTVJoystickPhaseDriver(float transitionSeconds = DefaultTransitionSeconds)
        {
            _transitionSeconds = Mathf.Max(0.01f, transitionSeconds);
        }

        internal Vector2 Requested => _requested;

        internal void Reset()
        {
            _value = Vector2.zero;
            _requested = Vector2.zero;
            _from = Vector2.zero;
            _to = Vector2.zero;
            _queued = Vector2.zero;
            _elapsed = 0f;
            _transitioning = false;
        }

        internal void SetRequested(Vector2 direction)
        {
            direction = QuantizeCardinal(direction);
            if (SameDirection(_requested, direction))
                return;

            _requested = direction;
            ReplanForRequest();
        }

        internal void SetImmediate(Vector2 direction)
        {
            direction = QuantizeCardinal(direction);
            _value = direction;
            _requested = direction;
            _from = direction;
            _to = direction;
            _queued = Vector2.zero;
            _elapsed = 0f;
            _transitioning = false;
        }

        internal Vector2 Tick(float dt)
        {
            if (!_transitioning)
            {
                if (!SameDirection(_value, _requested))
                    StartTransitionForRequest();
                return _value;
            }

            _elapsed += Mathf.Max(0f, dt);
            float t = Mathf.Clamp01(_elapsed / _transitionSeconds);
            float eased = t * t * (3f - 2f * t);
            _value = Vector2.Lerp(_from, _to, eased);

            if (t >= 1f)
            {
                _value = _to;
                _transitioning = false;
                if (!SameDirection(_queued, Vector2.zero) && SameDirection(_queued, _requested))
                {
                    Vector2 queued = _queued;
                    _queued = Vector2.zero;
                    StartTransition(queued);
                }
                else
                {
                    _queued = Vector2.zero;
                    if (!SameDirection(_value, _requested))
                        StartTransitionForRequest();
                }
            }

            return _value;
        }

        private void ReplanForRequest()
        {
            if (!_transitioning)
            {
                StartTransitionForRequest();
                return;
            }

            if (SameDirection(_requested, Vector2.zero))
            {
                _queued = Vector2.zero;
                if (!SameDirection(_to, Vector2.zero))
                    StartTransition(Vector2.zero);
                return;
            }

            if (SameDirection(_requested, _to))
            {
                _queued = Vector2.zero;
                return;
            }

            if (SameDirection(_to, Vector2.zero))
            {
                _queued = _requested;
                return;
            }

            _queued = _requested;
            StartTransition(Vector2.zero);
        }

        private void StartTransitionForRequest()
        {
            if (SameDirection(_value, _requested))
                return;

            if (!SameDirection(_value, Vector2.zero) && !SameDirection(_requested, Vector2.zero))
            {
                _queued = _requested;
                StartTransition(Vector2.zero);
            }
            else
            {
                _queued = Vector2.zero;
                StartTransition(_requested);
            }
        }

        private void StartTransition(Vector2 target)
        {
            target = QuantizeCardinal(target);
            if (SameDirection(_value, target))
            {
                _value = target;
                _from = target;
                _to = target;
                _elapsed = 0f;
                _transitioning = false;
                return;
            }

            _from = _value;
            _to = target;
            _elapsed = 0f;
            _transitioning = true;
        }

        internal static Vector2 QuantizeCardinal(Vector2 input)
        {
            if (input.sqrMagnitude < 1e-8f)
                return Vector2.zero;

            if (Mathf.Abs(input.x) >= Mathf.Abs(input.y))
                return new Vector2(Mathf.Sign(input.x), 0f);

            return new Vector2(0f, Mathf.Sign(input.y));
        }

        private static bool SameDirection(Vector2 a, Vector2 b)
        {
            return Mathf.Abs(a.x - b.x) < 0.001f && Mathf.Abs(a.y - b.y) < 0.001f;
        }
    }
}
