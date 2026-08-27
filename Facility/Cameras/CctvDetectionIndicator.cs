using Y4NGZCompany.Facility.Security;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    internal sealed class CctvDetectionIndicator : MonoBehaviour
    {
        // A bare Light spot renders on surfaces but produces no visible shaft
        // in LC's volumetric interior fog, and its legacy intensity value is
        // not meaningful to HDRP's physical light units — which is why the
        // beam was invisible in game. The light now carries an
        // HDAdditionalLightData configured in lumens (bright-flashlight
        // class) with volumetrics enabled so the cone reads as an actual
        // beam hanging in the air.
        //
        // The cone is now honest about its *angle*. CctvDetectionProbe tests a
        // 35 deg half-angle, so this draws the full 70 deg and a player who
        // reads themselves as outside the cone really is outside detection.
        // The throw stays truncated against the probe's 28 m: the angle is
        // what answers "am I in it?", while the far 12 m is where the
        // volumetric cost lives for almost no legibility gain.
        private const float BeamRangeM = 16f;
        private const float BeamConeDegrees = 70f;
        // Widening 42 -> 70 deg spreads the same lumens over ~2.7x the solid
        // angle, so the detection beam's lumens scale with it to preserve the
        // on-screen brightness that already reads well in game.
        private const float BeamLumens = 5400f;
        // The always-on tier for a security-active camera that has not spotted
        // anyone: same cone, ~8% of the light. Enough to read as a shaft in
        // fog, far too dim to compete with an actual lock-on. Dial it with
        // "Security Idle Cone Intensity"; 0 turns it off entirely.
        private const float IdleLumens = 450f;
        private const float BeamInnerSpotPercent = 55f;
        private const float BeamLingerSeconds = 0.35f;
        // Escalation snaps so being spotted still punches; de-escalation eases
        // back to the idle tier, because a hard cut down after the linger
        // reads as the cone glitching rather than the camera losing interest.
        private const float FalloffResponsePerSecond = 6f;
        private const float MinimumVisibleLumens = 4f;

        private CCTVCamera _camera;
        private Light _beamLight;
        private HDAdditionalLightData _beamData;
        private float _visibleUntil;
        private float _lastProgress01;
        private float _currentLumens;
        private Color _currentColor = CctvDetectionPalette.SecurityActiveColor;

        internal static void AttachTo(CCTVCamera camera)
        {
            if (camera == null || camera.GetComponent<CctvDetectionIndicator>() != null) return;
            var indicator = camera.gameObject.AddComponent<CctvDetectionIndicator>();
            indicator._camera = camera;
        }

        private void LateUpdate()
        {
            if (_camera == null || _camera.IsSecurityBroken)
            {
                Apply(0f, _currentColor, snap: false);
                return;
            }

            CctvSecurityCameraState state = CctvSecurityCameraRegistry.Find(_camera);
            // Mirrors CCTVCameraLensBlinker.ResolveMode's gating rather than
            // inventing a second rule for what counts as a live camera — a
            // broken or inactive camera must never advertise a cone.
            if (state == null || state.IsBroken || !state.IsDetectionLive)
            {
                Apply(0f, _currentColor, snap: false);
                return;
            }

            if (state.IsSuspiciousForPresentation)
            {
                _visibleUntil = Time.unscaledTime + BeamLingerSeconds;
                _lastProgress01 = state.DetectionProgress01(CctvSecurityConfig.Current.DetectionSeconds);
            }

            bool detecting = Time.unscaledTime < _visibleUntil;
            if (detecting)
            {
                // Yellow -> red ramp mirrors the lens strobe's escalation.
                Apply(BeamLumens * Multiplier(SurveillanceBootstrap.Config?.SecurityDetectionIndicatorIntensity?.Value),
                    CctvDetectionPalette.ForProgress(_lastProgress01),
                    snap: true);
                return;
            }

            Apply(IdleLumens * Multiplier(SurveillanceBootstrap.Config?.SecurityIdleConeIntensity?.Value),
                CctvDetectionPalette.SecurityActiveColor,
                snap: false);
        }

        private static float Multiplier(float? configured)
        {
            return Mathf.Max(0f, configured ?? 1f);
        }

        private void Apply(float targetLumens, Color color, bool snap)
        {
            targetLumens = Mathf.Max(0f, targetLumens);
            if (snap && targetLumens > _currentLumens)
            {
                _currentLumens = targetLumens;
            }
            else
            {
                // Frame-rate independent exponential approach, so the ease-down
                // takes the same wall-clock time regardless of framerate.
                float k = 1f - Mathf.Exp(-FalloffResponsePerSecond * Time.unscaledDeltaTime);
                _currentLumens = Mathf.Lerp(_currentLumens, targetLumens, k);
            }

            if (targetLumens > 0f)
                _currentColor = color;

            if (_currentLumens < MinimumVisibleLumens)
            {
                _currentLumens = targetLumens > 0f ? _currentLumens : 0f;
                if (_beamLight != null) _beamLight.enabled = false;
                return;
            }

            Light beam = EnsureBeamLight();
            beam.enabled = true;
            beam.color = _currentColor;
            if (_beamData != null)
                _beamData.SetIntensity(_currentLumens, LightUnit.Lumen);
        }

        private Light EnsureBeamLight()
        {
            if (_beamLight != null) return _beamLight;
            GameObject go = new GameObject("LethalCCTV_DetectionIndicator");
            // Mount the beam on the visible dome head (its +Z is the lens per
            // the split-model contract) so the cone shines from the prop the
            // player sees; fall back to the invisible feed transform when the
            // physical visual has not built.
            Transform head = CCTVCameraVisual.TryGetRotatingHead(_camera);
            go.transform.SetParent(head != null ? head : transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            _beamData = go.AddHDLight(HDLightTypeAndShape.ConeSpot);
            _beamLight = go.GetComponent<Light>();
            _beamLight.shadows = LightShadows.None;
            _beamLight.range = BeamRangeM;
            _beamLight.spotAngle = BeamConeDegrees;
            _beamData.SetIntensity(BeamLumens, LightUnit.Lumen);
            _beamData.innerSpotPercent = BeamInnerSpotPercent;
            _beamData.affectsVolumetric = true;
            _beamData.volumetricDimmer = 1f;
            _beamLight.enabled = false;
            return _beamLight;
        }
    }
}
