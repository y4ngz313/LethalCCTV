using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Y4NGZCompany.Facility.Cameras.Placement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Shared;

namespace Y4NGZCompany.Facility.Cameras
{
    internal sealed class CCTVCameraLensBlinker : MonoBehaviour
    {
        private const string BlinkAudioDirectory = "Audio";
        private const string BlinkAudioFileName = "freesoundsxx-button-press-beep-269718.mp3";
        private const string ActivePulseAudioFileName = "analog_computer_beep_1.ogg";
        private const string ActiveLoopAudioFileName = "machine_11_loop.ogg";
        private const string RotationAudioFileName = "button_mx_3.ogg";
        private const float ActivePulseIntervalSeconds = 12f;
        private const float ActiveLoopVolume = 0.35f;
        private const float ActiveLoopMinDistanceM = 2f;
        private const float ActiveLoopMaxDistanceM = 12f;
        private const float RotationSfxVolume = 0.5f;
        private const float RotationSfxMaxDistanceM = 14f;
        private const float BlinkMinIntervalSeconds = 30f;
        private const float BlinkMaxIntervalSeconds = 60f;
        // Each beep now pumps a real red spot light from the lens dot that
        // decays back to dark, instead of the old brief blackout of the dot
        // mesh (which read as the camera turning OFF at the beep). Peak
        // intensity is calibrated against the interior alarm fixture's room
        // light (config 8 x 2.25 multiplier) so it registers in HDRP without
        // washing the room red.
        private const float BlinkGlowSeconds = 0.55f;
        private const float BlinkGlowPeakIntensity = 6f;
        private const float BlinkGlowRangeM = 3.5f;
        private const float SteadyGlowIntensity = 1.25f;
        private const float SteadyGlowRangeM = 2.5f;
        private const float TipLightSpotAngleDegrees = 150f;
        private const float BlinkSfxVolume = 0.36f;
        private const float BlinkSfxMaxDistance = 9f;
        // Detection state grammar (turret-style): amber strobe accelerating
        // with detection progress while a hostile camera is watching a player,
        // solid-strobing red while the security alarm rings. Inactive idle is
        // steady red with a silent occasional blink; security-active idle is
        // steady orange with a fixed 12-second beep + matching glow pulse.
        private const float DetectStrobeSlowSeconds = 0.5f;
        private const float DetectStrobeFastSeconds = 0.12f;
        private const float AlarmStrobePeriodSeconds = 0.18f;
        // The detect/alarm strobes must read across a room, unlike the subtle
        // idle beep pulse. Playtest feedback: the alarm-fixture-matched 18
        // still went unnoticed mid-run, so the strobe deliberately overshoots
        // that calibration and washes the area amber/red. The warning ticks
        // likewise run much louder and farther than the idle beep so a player
        // who cannot see the lens still hears that they are being detected.
        private const float StrobeGlowPeakIntensity = 32f;
        private const float StrobeGlowRangeM = 10f;
        // An AudioSource caps volume at 1.0, so "2x louder" ticks come from
        // stacking two simultaneous one-shots (+6 dB doubles the amplitude)
        // plus a wide min-distance plateau so the tick stays at full volume
        // for the first few meters instead of rolling off immediately.
        private const float DetectTickVolume = 1f;
        private const float DetectTickMaxDistanceM = 30f;
        private const float DetectTickMinDistanceM = 4f;
        private const int DetectTickLayers = 2;
        private const float BlinkSfxMinDistance = 0.75f;
        // #563 impact feedback. A registered hit overrides every other lens state for a
        // fraction of a second with a hard white-hot flicker, which is the one signal that
        // reads instantly at melee range regardless of what the camera was doing. Fast
        // enough to be a glitch rather than a new state, and it ends by re-deriving the
        // real state rather than assuming one.
        private const float DamageFlashSeconds = 0.32f;
        private const float DamageFlashPeriodSeconds = 0.07f;
        // Cumulative damage darkens the idle lens (see ApplyIdleLens), so a camera that
        // has been beaten on but not yet destroyed reads visibly wounded from across a
        // room without a new asset or material.
        private const float DamagedIdleEmissiveFloor = 0.3f;

        // HDRP reads ONLY _EmissiveColor. Its own shader source annotates the
        // other two: "Used only to serialize the LDR and HDR emissive color in
        // the material UI, in the shader only the _EmissiveColor should be
        // used." The material inspector multiplies _EmissiveColorLDR by
        // _EmissiveIntensity and bakes the result into _EmissiveColor at
        // authoring time; at runtime nothing reads either one.
        //
        // This class used to drive the glow through _EmissiveIntensity, so
        // every intensity it set was silently discarded and _EmissiveColor was
        // left holding near-black values. The dot has therefore never actually
        // glowed in ANY state, red or amber - which is why the only amber
        // reaching the player was tip-light spill onto nearby geometry:
        // inconsistent (needs a wall close enough to catch it) and dark-only
        // (loses to ambient light).
        //
        // Intensity is now baked into the emissive colour, in HDR magnitudes
        // large enough to clear the bloom threshold so a 6 cm sphere reads as a
        // lit indicator instead of a flat-shaded bead. The ordering is what
        // keeps the states legible: inactive < active < blink peak < strobe.
        // Scale the whole ladder in-game with "Security Lens Dot Glow".
        private const float InactiveIdleEmissive = 6f;
        private const float InactiveIdlePeakEmissive = 26f;
        private const float ActiveIdleEmissive = 22f;
        private const float ActiveIdlePeakEmissive = 46f;
        private const float StrobeEmissive = 90f;

        private static readonly Color IdleGlowColor = new Color(1f, 0.08f, 0.05f, 1f);
        private static readonly Color DetectAmberColor = new Color(1f, 0.55f, 0.08f, 1f);
        private static readonly Color AlarmRedColor = new Color(1f, 0.02f, 0.02f, 1f);

        private enum LensMode { InactiveIdle, ActiveIdle, Detecting, Alarm }

        // Base colour under the glow. The dot material is HDRP/Unlit, which
        // shows this directly, so it is what the dot reads as where the
        // emissive is dimmest.
        private static readonly Color LensLitColor = new Color(0.48f, 0.012f, 0.006f, 1f);
        private static readonly Color ActiveLensLitColor = new Color(0.92f, 0.40f, 0.04f, 1f);
        // Strobe-off frames used to drop the dot back to the inactive red, so a
        // camera mid-detection flickered "hostile / not hostile". Both strobes
        // now keep a hot lit surface underneath the flash.
        private static readonly Color AlarmLensLitColor = new Color(0.85f, 0.04f, 0.03f, 1f);
        // Deliberately off-palette: neither the red idle nor the amber detect grammar, so
        // an impact flicker cannot be mistaken for a security state change.
        private static readonly Color DamageFlashColor = new Color(1f, 0.9f, 0.7f, 1f);
        private static readonly Color DamageLensLitColor = new Color(0.9f, 0.8f, 0.6f, 1f);
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int UnlitColorId = Shader.PropertyToID("_UnlitColor");
        private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
        // Built-in/URP spelling, for the non-HDRP fallback shaders in
        // CreateMaterial's Shader.Find chain. HDRP ignores it harmlessly.
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private static AudioClip _blinkClip;
        private static bool _blinkClipLoadStarted;
        private static bool _blinkClipLoadFailed;

        private Renderer _renderer;
        private MaterialPropertyBlock _properties;
        private Light _tipLight;
        private AudioSource _audioSource;
        private AudioSource _activeLoopSource;
        private AudioClip _activePulseClip;
        private AudioClip _activeLoopClip;
        private AudioClip _rotationClip;
        private CCTVCamera _holder;
        private float _nextBlinkAt;
        private float _glowUntil;
        private LensMode _lastMode = LensMode.InactiveIdle;
        private bool _strobeWasOn;
        private bool _disabledLensApplied;
        private bool _rotationSubscribed;
        private int _lastActivePulseBucket = int.MinValue;
        private float _damageFlashUntil;
        private float _damage01;

        internal static void Attach(GameObject lens, CCTVCamera holder)
        {
            if (lens == null) return;
            CCTVCameraLensBlinker blinker = lens.GetComponent<CCTVCameraLensBlinker>();
            if (blinker == null)
                blinker = lens.AddComponent<CCTVCameraLensBlinker>();
            blinker._holder = holder;
        }

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _properties = new MaterialPropertyBlock();
            EnsureTipLight();
            EnsureAudioSource();
            ApplyIdleLens(LensMode.InactiveIdle, pulseStrength: 0f);
            ScheduleNextBlink();
        }

        private void Start()
        {
            EnsureBlinkClipLoading();
            FacilityAudioClipLoader.Request(this, ActivePulseAudioFileName, clip => _activePulseClip = clip);
            FacilityAudioClipLoader.Request(this, ActiveLoopAudioFileName, clip => _activeLoopClip = clip);
            FacilityAudioClipLoader.Request(this, RotationAudioFileName, clip => _rotationClip = clip);
        }

        private void OnEnable()
        {
            SubscribeToRotationRefresh();
            if (_nextBlinkAt <= 0f)
                ScheduleNextBlink();

            LensMode mode = ResolveMode(out _);
            _lastMode = mode;
            _strobeWasOn = false;
            if (mode == LensMode.ActiveIdle)
                _lastActivePulseBucket = CurrentActivePulseBucket();
            if (_glowUntil <= 0f && IsIdleMode(mode))
                ApplyIdleLens(mode, pulseStrength: 0f);
        }

        private void OnDisable()
        {
            UnsubscribeFromRotationRefresh();
            _glowUntil = 0f;
            if (_tipLight != null)
                _tipLight.enabled = false;
            if (_activeLoopSource != null)
                _activeLoopSource.Stop();
        }

        private void OnDestroy()
        {
            UnsubscribeFromRotationRefresh();
        }

        private void Update()
        {
            UpdateActiveLoop();
            if (_holder != null && (_holder.IsSecurityBroken || _holder.SecurityRemotelyDisabled))
            {
                ApplyDisabledLens();
                return;
            }

            // #563: an impact flicker outranks every security state for its duration, then
            // hands the lens back by re-deriving whatever state the camera is actually in
            // (the flash overwrote the property block, so an idle mode has to be re-applied
            // explicitly — the mode-change branch below would not fire if nothing changed).
            if (_damageFlashUntil > 0f)
            {
                if (Time.unscaledTime < _damageFlashUntil)
                {
                    TickDamageFlash();
                    return;
                }

                _damageFlashUntil = 0f;
                _strobeWasOn = false;
                _glowUntil = 0f;
                LensMode resumed = ResolveMode(out _);
                if (IsIdleMode(resumed))
                    ApplyIdleLens(resumed, pulseStrength: 0f);
            }

            LensMode mode = ResolveMode(out float progress01);
            if (mode != _lastMode)
            {
                _strobeWasOn = false;
                _glowUntil = 0f;
                if (IsIdleMode(mode))
                {
                    ApplyIdleLens(mode, pulseStrength: 0f);
                    if (mode == LensMode.ActiveIdle)
                        _lastActivePulseBucket = CurrentActivePulseBucket();
                    else
                        ScheduleNextBlink();
                }
                _lastMode = mode;
            }

            switch (mode)
            {
                case LensMode.Detecting:
                    TickDetecting(progress01);
                    return;
                case LensMode.Alarm:
                    TickAlarm();
                    return;
                default:
                    TickIdle(mode);
                    return;
            }
        }

        private LensMode ResolveMode(out float progress01)
        {
            progress01 = 0f;
            if (_holder == null || _holder.IsSecurityBroken)
                return LensMode.InactiveIdle;

            Y4NGZCompany.Facility.Security.CctvSecurityCameraState state =
                Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry.Find(_holder);
            // #563: IsDetectionLive, not IsSecurityActive. A camera whose detection sweep
            // is suppressed (Containment Breach, post-alarm cooldown) is still in the
            // rotation and still sweeping, but it cannot detect anyone — so its lens must
            // read inactive red rather than the amber "I am watching you" idle, and it
            // must fall through before the alarm branch below.
            if (state == null || state.IsBroken || !state.IsDetectionLive)
                return LensMode.InactiveIdle;

            if (Y4NGZCompany.Facility.Security.CctvSecurityDirector.IsTimedAlarmActive ||
                Y4NGZCompany.Facility.Security.MainframeProtocolDirector.IsProtocolAlarmVisualActive)
                return LensMode.Alarm;

            if (state.IsSuspiciousForPresentation)
            {
                progress01 = state.DetectionProgress01(
                    Y4NGZCompany.Facility.Security.CctvSecurityConfig.Current.DetectionSeconds);
                return LensMode.Detecting;
            }

            return LensMode.ActiveIdle;
        }

        private void TickIdle(LensMode mode)
        {
            float now = Time.unscaledTime;
            if (_glowUntil > 0f)
            {
                float remaining = _glowUntil - now;
                if (remaining <= 0f)
                {
                    _glowUntil = 0f;
                    ApplyIdleLens(mode, pulseStrength: 0f);
                    if (mode == LensMode.InactiveIdle)
                        ScheduleNextBlink();
                }
                else
                {
                    ApplyIdleLens(mode, Mathf.Clamp01(remaining / BlinkGlowSeconds));
                }

                return;
            }

            if (mode == LensMode.ActiveIdle)
            {
                int pulseBucket = CurrentActivePulseBucket();
                if (_lastActivePulseBucket == int.MinValue)
                    _lastActivePulseBucket = pulseBucket;
                else if (pulseBucket != _lastActivePulseBucket)
                {
                    _lastActivePulseBucket = pulseBucket;
                    PulseActiveLens(now);
                }
                return;
            }

            if (now >= _nextBlinkAt)
                BlinkInactive(now);
        }

        // Amber warning strobe that accelerates as detection progress rises,
        // with tick beeps pitching up on each flash — the player-readable
        // "you are being detected, break line of sight" window.
        private void TickDetecting(float progress01)
        {
            float period = Mathf.Lerp(DetectStrobeSlowSeconds, DetectStrobeFastSeconds, progress01);
            bool on = Mathf.Repeat(Time.unscaledTime, period) < period * 0.5f;
            SetLens(DetectAmberColor, on ? 1f : 0f, StrobeGlowPeakIntensity, StrobeGlowRangeM, ActiveLensLitColor);
            if (on && !_strobeWasOn)
                PlayBlinkSfx(Mathf.Lerp(1.15f, 1.6f, progress01), 1f, DetectTickVolume, DetectTickMaxDistanceM, DetectTickMinDistanceM, DetectTickLayers);
            _strobeWasOn = on;
        }

        // Fast red strobe for the alarm duration; the siren fixtures carry the
        // audio so the lens stays silent here.
        private void TickAlarm()
        {
            bool on = Mathf.Repeat(Time.unscaledTime, AlarmStrobePeriodSeconds) < AlarmStrobePeriodSeconds * 0.5f;
            SetLens(AlarmRedColor, on ? 1f : 0.2f, StrobeGlowPeakIntensity, StrobeGlowRangeM, AlarmLensLitColor);
            _strobeWasOn = on;
        }

        /// <summary>
        /// #563: called by <see cref="CctvBreakableCamera"/> on every registered hit.
        /// <paramref name="damage01"/> is cumulative damage as a 0..1 fraction of the
        /// camera's health, which persists as a darkened lens after the flicker ends.
        /// </summary>
        internal void FlashDamage(float damage01)
        {
            _damageFlashUntil = Time.unscaledTime + DamageFlashSeconds;
            _damage01 = Mathf.Clamp01(Mathf.Max(_damage01, damage01));
        }

        // Hard, fast, off-palette flicker. Deliberately reuses SetLens so the dot mesh
        // and the spot light move together exactly as they do for the security strobes.
        private void TickDamageFlash()
        {
            bool on = Mathf.Repeat(Time.unscaledTime, DamageFlashPeriodSeconds)
                < DamageFlashPeriodSeconds * 0.5f;
            SetLens(
                DamageFlashColor,
                on ? 1f : 0.05f,
                StrobeGlowPeakIntensity,
                StrobeGlowRangeM,
                on ? DamageLensLitColor : LensLitColor);
            _strobeWasOn = on;
        }

        private void BlinkInactive(float now)
        {
            // Inactive cameras retain their subtle visual idle blink but are silent.
            _glowUntil = now + BlinkGlowSeconds;
            ApplyIdleLens(LensMode.InactiveIdle, pulseStrength: 1f);
        }

        private void PulseActiveLens(float now)
        {
            _glowUntil = now + BlinkGlowSeconds;
            ApplyIdleLens(LensMode.ActiveIdle, pulseStrength: 1f);
            PlayActivePulseSfx();
        }

        private static int CurrentActivePulseBucket()
        {
            return Mathf.FloorToInt(
                Y4NGZCompany.Facility.Security.CctvSecurityDirector.SecurityTimeSeconds()
                / ActivePulseIntervalSeconds);
        }

        private static bool IsIdleMode(LensMode mode)
        {
            return mode == LensMode.InactiveIdle || mode == LensMode.ActiveIdle;
        }

        // Broken and remotely disabled cameras keep their prop visible, but the
        // lens dot and tip light read dead — no blink, beep, glow, or machine loop.
        private void ApplyDisabledLens()
        {
            if (_disabledLensApplied) return;
            _disabledLensApplied = true;
            _glowUntil = 0f;

            if (_tipLight != null)
                _tipLight.enabled = false;

            if (_renderer == null)
                return;

            _renderer.GetPropertyBlock(_properties);
            _properties.SetColor(ColorId, Color.black);
            _properties.SetColor(BaseColorId, Color.black);
            _properties.SetColor(UnlitColorId, Color.black);
            _properties.SetColor(EmissiveColorId, Color.black);
            _properties.SetColor(EmissionColorId, Color.black);
            _renderer.SetPropertyBlock(_properties);
        }

        private void ApplyIdleLens(LensMode mode, float pulseStrength)
        {
            bool securityActive = mode == LensMode.ActiveIdle;
            Color color = securityActive ? CctvDetectionPalette.SecurityActiveColor : IdleGlowColor;
            float pulse = Mathf.Clamp01(pulseStrength);
            // #563: accumulated damage dims the steady lens. Applied only to the idle
            // states so an alarm or a detection strobe still reads at full strength - a
            // wounded camera is still dangerous, it just looks it.
            float damageDim = Mathf.Lerp(1f, DamagedIdleEmissiveFloor, _damage01);

            if (_tipLight != null)
            {
                _tipLight.color = color;
                _tipLight.intensity = damageDim * Mathf.Lerp(SteadyGlowIntensity, BlinkGlowPeakIntensity, pulse);
                _tipLight.range = Mathf.Lerp(SteadyGlowRangeM, BlinkGlowRangeM, pulse);
                _tipLight.enabled = true;
            }

            if (_renderer == null)
                return;

            Color surface = securityActive ? ActiveLensLitColor : LensLitColor;
            float emissive = securityActive
                ? Mathf.Lerp(ActiveIdleEmissive, ActiveIdlePeakEmissive, pulse)
                : Mathf.Lerp(InactiveIdleEmissive, InactiveIdlePeakEmissive, pulse);
            Color glow = color * (emissive * damageDim * GlowMultiplier());
            _renderer.GetPropertyBlock(_properties);
            _properties.SetColor(ColorId, surface);
            _properties.SetColor(BaseColorId, surface);
            _properties.SetColor(UnlitColorId, surface);
            _properties.SetColor(EmissiveColorId, glow);
            _properties.SetColor(EmissionColorId, glow);
            _renderer.SetPropertyBlock(_properties);
        }

        // strength 1 -> 0: real light pulse from the dot plus a matching
        // emissive flare on the dot mesh itself. surface is the lit colour the
        // mesh falls back to between flashes, so a strobing camera never reads
        // as an inactive one on the dark half of the cycle.
        private void SetLens(Color color, float strength, float peakIntensity, float rangeM, Color surface)
        {
            if (_tipLight != null)
            {
                _tipLight.color = color;
                _tipLight.intensity = peakIntensity * strength;
                _tipLight.range = rangeM;
                _tipLight.enabled = strength > 0.001f;
            }

            if (_renderer == null)
                return;

            // Emissive flare on the dot mesh scales with the strobe so the dot
            // itself carries the escalation, not just the spill light. The
            // floor keeps the dark half of a strobe cycle reading as a hot
            // camera rather than an inactive one.
            float emissive = StrobeEmissive * Mathf.Max(0.05f, strength);
            Color glow = color * (emissive * GlowMultiplier());
            _renderer.GetPropertyBlock(_properties);
            _properties.SetColor(ColorId, surface);
            _properties.SetColor(BaseColorId, surface);
            _properties.SetColor(UnlitColorId, surface);
            _properties.SetColor(EmissiveColorId, glow);
            _properties.SetColor(EmissionColorId, glow);
            _renderer.SetPropertyBlock(_properties);
        }

        private static float GlowMultiplier()
        {
            return Mathf.Max(0f, SurveillanceBootstrap.Config?.SecurityLensDotGlow?.Value ?? 1f);
        }

        private void ScheduleNextBlink()
        {
            _nextBlinkAt = Time.unscaledTime + UnityEngine.Random.Range(BlinkMinIntervalSeconds, BlinkMaxIntervalSeconds);
        }

        private void SubscribeToRotationRefresh()
        {
            if (_rotationSubscribed)
                return;
            Y4NGZCompany.Facility.Security.CctvSecurityDirector.RotationRefreshed += OnRotationRefreshed;
            _rotationSubscribed = true;
        }

        private void UnsubscribeFromRotationRefresh()
        {
            if (!_rotationSubscribed)
                return;
            Y4NGZCompany.Facility.Security.CctvSecurityDirector.RotationRefreshed -= OnRotationRefreshed;
            _rotationSubscribed = false;
        }

        private void OnRotationRefreshed()
        {
            if (_holder == null || _holder.IsSecurityBroken || _holder.SecurityRemotelyDisabled)
                return;
            if (_rotationClip == null || _audioSource == null)
                return;

            ConfigureOneShotSource(RotationSfxVolume, BlinkSfxMinDistance, RotationSfxMaxDistanceM, 1f);
            _audioSource.PlayOneShot(_rotationClip, 1f);
        }

        private void UpdateActiveLoop()
        {
            // #563: the machine hum is the audible half of the amber active-idle lens, so
            // it follows the same live-detection gate.
            bool shouldPlay = _holder != null
                && _holder.IsDetectionLive
                && !_holder.IsSecurityBroken
                && !_holder.SecurityRemotelyDisabled;
            if (!shouldPlay || _activeLoopClip == null)
            {
                if (_activeLoopSource != null && _activeLoopSource.isPlaying)
                    _activeLoopSource.Stop();
                return;
            }

            EnsureActiveLoopSource();
            if (_activeLoopSource.clip != _activeLoopClip)
                _activeLoopSource.clip = _activeLoopClip;
            if (!_activeLoopSource.isPlaying)
                _activeLoopSource.Play();
        }

        private void EnsureActiveLoopSource()
        {
            if (_activeLoopSource != null)
                return;

            GameObject loopObject = new GameObject("SecurityActiveMachineLoop");
            loopObject.transform.SetParent(transform, worldPositionStays: false);
            _activeLoopSource = loopObject.AddComponent<AudioSource>();
            _activeLoopSource.playOnAwake = false;
            _activeLoopSource.loop = true;
            _activeLoopSource.spatialBlend = 1f;
            _activeLoopSource.volume = ActiveLoopVolume;
            _activeLoopSource.rolloffMode = AudioRolloffMode.Linear;
            _activeLoopSource.minDistance = ActiveLoopMinDistanceM;
            _activeLoopSource.maxDistance = ActiveLoopMaxDistanceM;
            _activeLoopSource.dopplerLevel = 0f;
            _activeLoopSource.priority = 165;
        }

        private void PlayActivePulseSfx()
        {
            if (_activePulseClip == null || _audioSource == null)
                return;

            ConfigureOneShotSource(BlinkSfxVolume, BlinkSfxMinDistance, BlinkSfxMaxDistance, 1f);
            _audioSource.PlayOneShot(_activePulseClip, 1f);
        }

        private void ConfigureOneShotSource(float volume, float minDistance, float maxDistance, float pitch)
        {
            _audioSource.volume = volume;
            _audioSource.minDistance = minDistance;
            _audioSource.maxDistance = maxDistance;
            _audioSource.pitch = pitch;
        }
        private void EnsureTipLight()
        {
            if (_tipLight == null)
                _tipLight = GetComponent<Light>();
            if (_tipLight == null)
            {
                // The old code only looked for a pre-existing Light (there never
                // was one on the bundled dot) and zeroed its cullingMask, so the
                // beep had no light source at all. Create a real one.
                GameObject lightGo = new GameObject("LensBlinkLight");
                lightGo.transform.SetParent(transform, worldPositionStays: false);
                lightGo.transform.localPosition = Vector3.zero;
                lightGo.transform.localRotation = Quaternion.identity;
                _tipLight = lightGo.AddComponent<Light>();
            }

            // Both lens-dot attachment paths make the dot's +Z point out of
            // the camera head (the bundled path uses LookRotation(forward));
            // the identity child rotation therefore keeps all status light
            // in front of the housing instead of leaking onto the mount wall.
            _tipLight.type = LightType.Spot;
            _tipLight.spotAngle = TipLightSpotAngleDegrees;
            _tipLight.color = new Color(1f, 0.08f, 0.05f, 1f);
            _tipLight.intensity = 0f;
            _tipLight.range = BlinkGlowRangeM;
            _tipLight.shadows = LightShadows.None;
            _tipLight.enabled = false;
        }

        private void EnsureAudioSource()
        {
            if (_audioSource == null)
                _audioSource = GetComponent<AudioSource>();
            if (_audioSource == null)
                _audioSource = gameObject.AddComponent<AudioSource>();

            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.spatialBlend = 1f;
            _audioSource.volume = BlinkSfxVolume;
            _audioSource.minDistance = 0.75f;
            _audioSource.maxDistance = BlinkSfxMaxDistance;
            _audioSource.rolloffMode = AudioRolloffMode.Linear;
            _audioSource.dopplerLevel = 0f;
            _audioSource.priority = 160;
        }

        private void PlayBlinkSfx(float pitch, float volumeScale)
        {
            PlayBlinkSfx(pitch, volumeScale, BlinkSfxVolume, BlinkSfxMaxDistance, BlinkSfxMinDistance, 1);
        }

        private void PlayBlinkSfx(float pitch, float volumeScale, float sourceVolume, float maxDistanceM, float minDistanceM, int layers)
        {
            EnsureBlinkClipLoading();
            if (_audioSource == null || _blinkClip == null)
                return;

            _audioSource.volume = sourceVolume;
            _audioSource.maxDistance = maxDistanceM;
            _audioSource.minDistance = minDistanceM;
            _audioSource.pitch = pitch * UnityEngine.Random.Range(0.97f, 1.03f);
            for (int i = 0; i < Mathf.Max(1, layers); i++)
                _audioSource.PlayOneShot(_blinkClip, volumeScale);
        }

        private void EnsureBlinkClipLoading()
        {
            if (_blinkClip != null || _blinkClipLoadStarted || _blinkClipLoadFailed)
                return;

            string path = ResolveBlinkAudioPath();
            if (string.IsNullOrEmpty(path))
            {
                _blinkClipLoadFailed = true;
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][CameraVisual] Blink audio missing: {BlinkAudioFileName}");
                return;
            }

            _blinkClipLoadStarted = true;
            StartCoroutine(LoadBlinkClip(path));
        }

        private IEnumerator LoadBlinkClip(string path)
        {
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _blinkClipLoadFailed = true;
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][CameraVisual] Failed loading blink audio '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    _blinkClipLoadFailed = true;
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][CameraVisual] Blink audio decoded null: {path}");
                    yield break;
                }

                clip.name = "LethalCCTV_CameraTipBlink";
                _blinkClip = clip;
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][CameraVisual] Loaded camera blink audio from '{path}'.");
            }
        }

        private static string ResolveBlinkAudioPath()
        {
            string pluginDir = null;
            try
            {
                pluginDir = Path.GetDirectoryName(typeof(SurveillanceBootstrap).Assembly.Location);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][CameraVisual] Blink audio plugin-dir resolve failed: {ex.Message}");
            }

            if (string.IsNullOrEmpty(pluginDir)) return null;

            string nested = Path.Combine(pluginDir, BlinkAudioDirectory, BlinkAudioFileName);
            if (File.Exists(nested)) return nested;

            string flat = Path.Combine(pluginDir, BlinkAudioFileName);
            return File.Exists(flat) ? flat : null;
        }
    }
}
