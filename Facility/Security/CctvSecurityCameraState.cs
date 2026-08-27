using UnityEngine;

namespace Y4NGZCompany.Facility.Security
{
    public sealed class CctvSecurityCameraState
    {
        public readonly int CameraIndex;
        public readonly Component CameraComponent;
        public readonly Transform Transform;
        public readonly string Label;

        public bool HasAlarmFixture;
        public bool IsSecurityActive;
        public bool IsBroken;
        public bool IsSuspicious;
        public float DetectionProgressSeconds;
        public float LastDetectionAt;
        public float LastSelectedAt;
        public GameNetcodeStuff.PlayerControllerB TrackedPlayer;
        public bool PresentationIsSuspicious;
        public GameNetcodeStuff.PlayerControllerB PresentationTrackedPlayer;
        public float PresentationSuspicionStartedAt;

        // Whether this client's sweep saw the *local* player in the cone, which is
        // not the same question as "is the local player the tracked player":
        // PresentationTrackedPlayer is only the nearest visible crew member, while
        // the alarm this camera is filling is per-camera, not per-player. A personal
        // alert keyed on the tracked player alone goes dark for the second person
        // standing in the same cone, who is just as detected.
        public bool PresentationSeesLocalPlayer;

        public bool Eligible => CameraComponent != null && Transform != null && !IsBroken && HasAlarmFixture;

        /// <summary>
        /// The presentation-facing "is this camera hunting me right now" flag (#563).
        ///
        /// IsSecurityActive alone answers only "is this camera in the rotation", and the
        /// director has suppression states — Containment Breach, the #466 post-alarm
        /// cooldown — that stop the detection sweep while deliberately leaving the active
        /// set rotating. A lens, cone or HUD arc keyed on IsSecurityActive therefore keeps
        /// advertising a threat that cannot fire. Everything that *presents* detection
        /// reads this; the rotation and sweep motion itself still reads IsSecurityActive,
        /// because suppressed cameras are supposed to keep sweeping.
        /// </summary>
        public bool IsDetectionLive => IsSecurityActive && !CctvSecurityDirector.IsDetectionSuppressed;
        public bool IsSuspiciousForPresentation => PresentationIsSuspicious || IsSuspicious;
        public GameNetcodeStuff.PlayerControllerB TrackedPlayerForPresentation => PresentationTrackedPlayer ?? TrackedPlayer;
        public bool IsSpottingLocalPlayerForPresentation => PresentationSeesLocalPlayer && IsSuspiciousForPresentation;

        public CctvSecurityCameraState(int cameraIndex, Component cameraComponent, Transform transform, string label)
        {
            CameraIndex = cameraIndex;
            CameraComponent = cameraComponent;
            Transform = transform;
            Label = string.IsNullOrWhiteSpace(label) ? $"CAM_{cameraIndex:D2}" : label;
        }

        internal void ClearSuspicion()
        {
            ClearAuthoritativeSuspicion();
            ClearPresentationSuspicion();
        }

        internal void ClearAuthoritativeSuspicion()
        {
            IsSuspicious = false;
            DetectionProgressSeconds = 0f;
            TrackedPlayer = null;
        }

        internal void ClearPresentationSuspicion()
        {
            PresentationIsSuspicious = false;
            PresentationTrackedPlayer = null;
            PresentationSuspicionStartedAt = 0f;
            PresentationSeesLocalPlayer = false;
        }

        internal void SetPresentationSuspicion(
            GameNetcodeStuff.PlayerControllerB player,
            bool seesLocalPlayer)
        {
            if (player != null && !PresentationIsSuspicious)
                PresentationSuspicionStartedAt = Time.unscaledTime;
            PresentationIsSuspicious = player != null;
            PresentationTrackedPlayer = player;
            PresentationSeesLocalPlayer = player != null && seesLocalPlayer;
        }

        // 0..1 fraction of the detection window for presentation (lens strobe,
        // warning beam). The server reports its authoritative accumulator;
        // clients approximate with a local timer from suspicion onset — close
        // enough for a visual ramp, and the alarm state takes over at 1.
        public float DetectionProgress01(float detectionSeconds)
        {
            float window = Mathf.Max(0.1f, detectionSeconds);
            if (IsSuspicious && DetectionProgressSeconds > 0f)
                return Mathf.Clamp01(DetectionProgressSeconds / window);
            if (PresentationIsSuspicious && PresentationSuspicionStartedAt > 0f)
            {
                // The authoritative accumulator above is already stretched by the
                // per-player multiplier; only the client-local approximation timer
                // needs the window scaled to match.
                if (PresentationSeesLocalPlayer)
                    window *= CctvSupportApi.ResolveLocalDetectionTimeMultiplier();
                return Mathf.Clamp01((Time.unscaledTime - PresentationSuspicionStartedAt) / window);
            }
            return 0f;
        }

        internal void ClearActive()
        {
            IsSecurityActive = false;
            ClearSuspicion();
        }

        internal void MarkBroken()
        {
            IsBroken = true;
            ClearActive();
        }
    }
}
