using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Y4NGZCompany.Facility.Security
{
    internal static class CctvSecurityLightPulse
    {
        private const float ReassertIntervalSeconds = 0.1f;
        // Natural end-of-alarm restore: animators and enables snap back on the
        // first frame (lights return without the vanilla animator lag reading
        // as "still broken"), while light intensity ramps up over this window
        // so the return reads as power coming back, not a cut.
        private const float RestoreFadeSeconds = 0.75f;

        private static readonly List<LightState> AffectedLights = new List<LightState>();
        private static readonly List<AnimatorState> AffectedAnimators = new List<AnimatorState>();
        private static float _pulseEndsAt;
        private static float _nextReassertAt;
        private static float _restoreStartedAt;
        private static bool _usedRoundPoweredLights;

        internal static bool IsPulsing => Time.unscaledTime < _pulseEndsAt;

        internal static void StartPulse(float seconds)
        {
            float duration = Mathf.Max(0f, seconds);
            if (duration <= 0f) return;

            float now = Time.unscaledTime;
            float newEndsAt = now + duration;
            if (IsPulsing)
            {
                _pulseEndsAt = Mathf.Max(_pulseEndsAt, newEndsAt);
                return;
            }

            Restore();
            _pulseEndsAt = newEndsAt;
            _nextReassertAt = now + ReassertIntervalSeconds;
            if (TryStartRoundPoweredLightPulse())
                return;

            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null || !IsInteriorLight(light)) continue;
                AffectedLights.Add(new LightState(light, light.enabled, ReadIntensity(light)));
                if (light.enabled)
                    light.enabled = false;
            }
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (_restoreStartedAt > 0f)
            {
                TickSmoothRestore(now);
                return;
            }

            if (_pulseEndsAt > 0f && now >= _pulseEndsAt)
            {
                BeginSmoothRestore(now);
                return;
            }

            if (now >= _pulseEndsAt)
                return;

            if (now < _nextReassertAt)
                return;

            _nextReassertAt = now + ReassertIntervalSeconds;
            ReassertBlackout();
        }

        // Natural expiry: bring everything back immediately (enables and
        // animators), then ramp light intensity from black to the captured
        // value so the return is smooth instead of a single-frame snap.
        private static void BeginSmoothRestore(float now)
        {
            if (_usedRoundPoweredLights)
            {
                RoundManager round = RoundManager.Instance;
                if (round != null)
                    round.TurnOnAllLights(on: true);
            }

            for (int i = 0; i < AffectedAnimators.Count; i++)
            {
                AnimatorState state = AffectedAnimators[i];
                if (state.Animator != null)
                    SetAnimatorOn(state.Animator, state.WasOn);
            }

            for (int i = 0; i < AffectedLights.Count; i++)
            {
                LightState state = AffectedLights[i];
                if (state.Light == null || !state.WasEnabled) continue;
                WriteIntensity(state.Light, 0f);
                state.Light.enabled = true;
            }

            _pulseEndsAt = 0f;
            _nextReassertAt = 0f;
            _restoreStartedAt = now;
        }

        private static void TickSmoothRestore(float now)
        {
            float t = Mathf.Clamp01((now - _restoreStartedAt) / RestoreFadeSeconds);
            float eased = t * t * (3f - 2f * t);
            for (int i = 0; i < AffectedLights.Count; i++)
            {
                LightState state = AffectedLights[i];
                if (state.Light == null || !state.WasEnabled) continue;
                WriteIntensity(state.Light, state.Intensity * eased);
            }

            if (t >= 1f)
                FinishRestore();
        }

        // Immediate restore for cancel/reset paths (round teardown, security
        // disable, manual protocol end): no fade, exact captured state back.
        internal static void Restore()
        {
            if (_usedRoundPoweredLights)
            {
                RoundManager round = RoundManager.Instance;
                if (round != null)
                    round.TurnOnAllLights(on: true);
            }

            for (int i = 0; i < AffectedAnimators.Count; i++)
            {
                AnimatorState state = AffectedAnimators[i];
                if (state.Animator != null)
                    SetAnimatorOn(state.Animator, state.WasOn);
            }

            FinishRestore();
        }

        private static void FinishRestore()
        {
            AffectedAnimators.Clear();
            for (int i = 0; i < AffectedLights.Count; i++)
            {
                LightState state = AffectedLights[i];
                if (state.Light != null)
                {
                    WriteIntensity(state.Light, state.Intensity);
                    state.Light.enabled = state.WasEnabled;
                }
            }
            AffectedLights.Clear();
            _pulseEndsAt = 0f;
            _nextReassertAt = 0f;
            _restoreStartedAt = 0f;
            _usedRoundPoweredLights = false;
        }

        // HDRP stores the rendered intensity on HDAdditionalLightData; write
        // both so the ramp works whichever one the fixture's shader path reads.
        private static float ReadIntensity(Light light)
        {
            HDAdditionalLightData hd = light.GetComponent<HDAdditionalLightData>();
            return hd != null ? hd.intensity : light.intensity;
        }

        private static void WriteIntensity(Light light, float value)
        {
            HDAdditionalLightData hd = light.GetComponent<HDAdditionalLightData>();
            if (hd != null) hd.intensity = value;
            else light.intensity = value;
        }

        private static bool TryStartRoundPoweredLightPulse()
        {
            RoundManager round = RoundManager.Instance;
            if (round == null) return false;

            if ((round.allPoweredLights == null || round.allPoweredLights.Count == 0) &&
                (round.allPoweredLightsAnimators == null || round.allPoweredLightsAnimators.Count == 0))
            {
                round.RefreshLightsList();
            }

            bool touched = false;
            if (round.allPoweredLightsAnimators != null)
            {
                foreach (Animator animator in round.allPoweredLightsAnimators)
                {
                    if (animator == null) continue;
                    AffectedAnimators.Add(new AnimatorState(animator, ReadAnimatorOn(animator)));
                    touched = true;
                }
            }

            if (round.allPoweredLights != null)
            {
                foreach (Light light in round.allPoweredLights)
                {
                    if (light == null || IsAlarmLight(light)) continue;
                    AffectedLights.Add(new LightState(light, light.enabled, ReadIntensity(light)));
                    touched = true;
                }
            }

            if (!touched)
                return false;

            _usedRoundPoweredLights = true;
            round.TurnOnAllLights(on: false);
            ReassertBlackout();
            return true;
        }

        private static void ReassertBlackout()
        {
            for (int i = 0; i < AffectedAnimators.Count; i++)
            {
                Animator animator = AffectedAnimators[i].Animator;
                if (animator != null && ReadAnimatorOn(animator))
                    SetAnimatorOn(animator, false);
            }

            for (int i = 0; i < AffectedLights.Count; i++)
            {
                Light light = AffectedLights[i].Light;
                if (light != null && light.enabled)
                    light.enabled = false;
            }
        }

        private static bool IsInteriorLight(Light light)
        {
            if (light == null || light.transform == null) return false;
            if (IsAlarmLight(light)) return false;

            string path = light.transform.GetHierarchyPath().ToLowerInvariant();
            if (path.Contains("lethalcctv")) return false;
            if (path.Contains("ship")) return false;
            return path.Contains("dungeon") ||
                   path.Contains("level") ||
                   path.Contains("factory") ||
                   path.Contains("facility");
        }

        private static bool IsAlarmLight(Light light)
        {
            if (light == null || light.transform == null) return false;
            if (light.GetComponentInParent<InteriorAlarmFixture>() != null) return true;

            string path = light.transform.GetHierarchyPath().ToLowerInvariant();
            return path.Contains("lgu_cctv_wallalarm") ||
                   path.Contains("lethalcctv_wallalarm") ||
                   path.Contains("cctv_wallalarm");
        }

        private static bool ReadAnimatorOn(Animator animator)
        {
            if (animator == null) return true;
            try
            {
                return animator.GetBool("on");
            }
            catch
            {
                return true;
            }
        }

        private static void SetAnimatorOn(Animator animator, bool on)
        {
            if (animator == null) return;
            try
            {
                animator.SetBool("on", on);
            }
            catch
            {
                // Some custom interior fixtures may not expose the standard powered-light bool.
            }
        }

        private readonly struct LightState
        {
            public readonly Light Light;
            public readonly bool WasEnabled;
            public readonly float Intensity;

            public LightState(Light light, bool wasEnabled, float intensity)
            {
                Light = light;
                WasEnabled = wasEnabled;
                Intensity = intensity;
            }
        }

        private readonly struct AnimatorState
        {
            public readonly Animator Animator;
            public readonly bool WasOn;

            public AnimatorState(Animator animator, bool wasOn)
            {
                Animator = animator;
                WasOn = wasOn;
            }
        }
    }
}
