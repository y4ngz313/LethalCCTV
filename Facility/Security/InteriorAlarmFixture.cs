using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;
namespace Y4NGZCompany.Facility.Security
{
    internal sealed class InteriorAlarmFixture : MonoBehaviour
    {
        private const string AlarmAudioFileName = "cctv-security-alarm.mp3";
        private const int LoudnessEnvelopeFramesPerSecond = 60;
        private const float FallbackPulseSpeed = 12f;
        private const float AlarmRoomLightIntensityMultiplier = 2.25f;
        private const float AlarmEmissiveColorMultiplier = 7.5f;
        private const float AlarmEmissiveIntensityMultiplier = 4.5f;
        // Native-emissive mode (cloned vanilla fire-exit light bar): rather
        // than force approximated pure-red onto the mesh, scale each
        // material's own captured emissive so the lamp idles at a calm dim
        // glow and visibly throbs bright during the alarm.
        // Idle sits well below the authored vanilla level: an inactive alarm
        // bar should read as a dormant fixture, not a lit fire-exit sign, and
        // the dim idle is what makes the alarm strobe read as the lamp
        // lighting up rather than the asset merely getting brighter.
        private const float NativeEmissiveIdleFactor = 0.3f;
        private const float NativeEmissiveAlarmFloor = 0.9f;
        private const float NativeEmissiveAlarmPeak = 3.4f;
        private const float NativeLightIdleFactor = 0.25f;
        private const float NativeLightAlarmFloor = 0.7f;
        private const float NativeLightAlarmPeak = 2.6f;
        // Materials whose captured emissive is at or below this are housing/
        // frame surfaces: the alarm pulse must never touch them, only the
        // genuinely emissive lamp material(s).
        private const float NativeEmissiveLampThreshold = 0.001f;
        // Must match CCTVCameraLensBlinker.AlarmStrobePeriodSeconds: both run on
        // global Time.unscaledTime so every camera lens and every light bar
        // blinks on the same clock/frequency during an alarm.
        private const float AlarmStrobePeriodSeconds = 0.18f;
        // Applied on top of the config volume: the siren was judged ~15% too
        // loud in play, and lowering the multiplier (not the config default)
        // reaches existing installs whose profile already stores the old value.
        private const float AlarmVolumeScale = 0.85f;
        // #466: a further -20% on top of the above, for the same reason and by the same
        // route - the siren was still overbearing once alarms became frequent, and a
        // constant reaches profiles that already store a hand-set Wall Alarm Volume.
        private const float AlarmVolumeRebalance = 0.8f;
        // End-of-alarm ramp. The stop is allowed early at a loudness valley
        // (between beeps) once most of the ramp has elapsed, so the tail
        // never cuts mid-beep; the hard cap keeps the tail bounded when the
        // envelope never dips (missing envelope → sine fallback).
        private const float AlarmFadeOutSeconds = 1.1f;
        private const float FadeOutValleyLoudness = 0.22f;
        private const float FadeOutMinFractionForValleyStop = 0.35f;

        private static bool _loadStarted;
        private static bool _loadFailed;
        private static AudioClip _alarmClip;
        private static float[] _loudnessEnvelope = Array.Empty<float>();
        private static Material _fallbackAlarmRedMaterial;
        private static Material _fallbackAlarmDarkMaterial;

        private Light _redLight;
        private AudioSource _audioSource;
        private Renderer[] _alarmRenderers = Array.Empty<Renderer>();
        private Material[] _alarmMaterials = Array.Empty<Material>();
        private float _lastAppliedPulse = -1f;
        private bool _scaleNativeEmissive;
        private Color[] _baseEmissiveColors = Array.Empty<Color>();
        private HDAdditionalLightData _nativeLightHD;
        private float _nativeLightBaseIntensity;
        private bool _usingNativeLight;
        private float _fadeOutStartedAt = -1f;

        internal void Initialize(bool repairImportedMaterials = true, bool scaleNativeEmissive = false)
        {
            _scaleNativeEmissive = scaleNativeEmissive;

            // Cloned vanilla art (fire-exit light bar) keeps its native HDRP
            // materials; the repair pass exists for the imported bundle asset
            // whose materials do not survive the bundle round-trip.
            if (repairImportedMaterials)
                RepairImportedAlarmMaterials(gameObject);
            EnsureAlarmVisualVisible();
            RefreshAlarmRenderTargets();
            if (scaleNativeEmissive)
                CaptureNativeEmissiveBaseline();

            // Prefer the cloned bar's own vanilla light so the effect is the
            // authentic fire-exit glow rather than a synthetic point light.
            if (scaleNativeEmissive)
                _usingNativeLight = TryAdoptNativeLight();

            if (!_usingNativeLight)
            {
                GameObject lightSource = new GameObject("AlarmCenterLight");
                lightSource.transform.SetParent(transform, worldPositionStays: false);
                _redLight = lightSource.AddComponent<Light>();
                _redLight.type = LightType.Point;
                _redLight.color = Color.red;
                _redLight.range = 18f;
                _redLight.intensity = 0f;
                _redLight.shadows = LightShadows.None;
                MoveLightToVisualCenter();
            }

            ApplyAlarmEmission(0f);

            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.spatialBlend = 1f;
            _audioSource.loop = true;
            _audioSource.volume = CctvSecurityConfig.Current.AlarmAudioVolume * AlarmVolumeScale * AlarmVolumeRebalance;
            _audioSource.playOnAwake = false;
            _audioSource.rolloffMode = AudioRolloffMode.Linear;
            _audioSource.minDistance = 3f;
            _audioSource.maxDistance = 42f;
            _audioSource.dopplerLevel = 0f;

            BeginAlarmAudioLoadIfNeeded();
        }

        private void EnsureAlarmVisualVisible()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (hasBounds && bounds.size.sqrMagnitude > 0.015f)
                return;

            GameObject back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = "RuntimeAlarmBackPlate";
            back.transform.SetParent(transform, worldPositionStays: false);
            back.transform.localPosition = new Vector3(0f, 0f, -0.025f);
            back.transform.localRotation = Quaternion.identity;
            back.transform.localScale = new Vector3(0.34f, 0.26f, 0.05f);
            AssignRenderer(back, EnsureFallbackAlarmDarkMaterial());

            GameObject lens = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lens.name = "RuntimeAlarmRedLens";
            lens.transform.SetParent(transform, worldPositionStays: false);
            lens.transform.localPosition = new Vector3(0f, 0.02f, 0.055f);
            lens.transform.localRotation = Quaternion.identity;
            lens.transform.localScale = new Vector3(0.22f, 0.16f, 0.08f);
            AssignRenderer(lens, EnsureFallbackAlarmRedMaterial());
        }

        private void RefreshAlarmRenderTargets()
        {
            _alarmRenderers = GetComponentsInChildren<Renderer>(includeInactive: true) ?? Array.Empty<Renderer>();
            _alarmMaterials = CollectAlarmMaterials(_alarmRenderers);
        }

        private static Material[] CollectAlarmMaterials(Renderer[] renderers)
        {
            if (renderers == null || renderers.Length == 0)
                return Array.Empty<Material>();

            var materials = new List<Material>();
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                Renderer renderer = renderers[rendererIndex];
                if (renderer == null) continue;

                Material[] sharedMaterials = renderer.sharedMaterials;
                if (sharedMaterials == null) continue;

                for (int materialIndex = 0; materialIndex < sharedMaterials.Length; materialIndex++)
                {
                    Material material = sharedMaterials[materialIndex];
                    if (material != null && !materials.Contains(material))
                        materials.Add(material);
                }
            }

            return materials.ToArray();
        }

        private void MoveLightToVisualCenter()
        {
            if (_redLight == null) return;

            Renderer[] renderers = _alarmRenderers ?? Array.Empty<Renderer>();
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (hasBounds)
                _redLight.transform.position = bounds.center;
            else
                _redLight.transform.localPosition = Vector3.zero;
        }

        private static void AssignRenderer(GameObject go, Material material)
        {
            if (go == null) return;

            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
        }

        private static void RepairImportedAlarmMaterials(GameObject root)
        {
            if (root == null) return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;

                Material[] materials = renderer.sharedMaterials;
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                    materials[materialIndex] = CreateRuntimeAlarmMaterial(materials[materialIndex]);
                renderer.sharedMaterials = materials;

                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }
        }

        private static Material CreateRuntimeAlarmMaterial(Material source)
        {
            Color color = ReadMaterialColor(source, new Color(0.55f, 0.04f, 0.03f, 1f));
            Shader shader = FindFirstSupportedShader("HDRP/Lit", "Standard", "Unlit/Texture", "Sprites/Default");
            Material material = new Material(shader) { name = $"{source?.name ?? "Alarm"}_Runtime" };
            Texture texture = ReadMaterialTexture(source);
            if (texture != null)
            {
                SetTextureIfPresent(material, "_BaseColorMap", texture);
                SetTextureIfPresent(material, "_MainTex", texture);
                SetTextureIfPresent(material, "_BaseMap", texture);
                SetTextureIfPresent(material, "_UnlitColorMap", texture);
            }

            SetColorIfPresent(material, "_BaseColor", color);
            SetColorIfPresent(material, "_Color", color);
            SetColorIfPresent(material, "_UnlitColor", color);
            SetFloatIfPresent(material, "_Metallic", 0f);
            SetFloatIfPresent(material, "_Smoothness", 0.25f);
            return material;
        }

        private static Texture ReadMaterialTexture(Material source)
        {
            if (source == null) return null;
            string[] properties = { "_BaseColorMap", "_MainTex", "_BaseMap", "_UnlitColorMap" };
            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                if (source.HasProperty(property))
                {
                    Texture texture = source.GetTexture(property);
                    if (texture != null)
                        return texture;
                }
            }
            return source.mainTexture;
        }

        private static Color ReadMaterialColor(Material source, Color fallback)
        {
            if (source == null) return fallback;
            string[] properties = { "_BaseColor", "_Color", "_UnlitColor" };
            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                if (source.HasProperty(property))
                    return source.GetColor(property);
            }
            return fallback;
        }

        private static Material EnsureFallbackAlarmRedMaterial()
        {
            if (_fallbackAlarmRedMaterial != null) return _fallbackAlarmRedMaterial;
            _fallbackAlarmRedMaterial = CreateColorMaterial("LGU_CCTV_RuntimeAlarmRed", new Color(0.74f, 0.02f, 0.01f, 1f));
            return _fallbackAlarmRedMaterial;
        }

        private static Material EnsureFallbackAlarmDarkMaterial()
        {
            if (_fallbackAlarmDarkMaterial != null) return _fallbackAlarmDarkMaterial;
            _fallbackAlarmDarkMaterial = CreateColorMaterial("LGU_CCTV_RuntimeAlarmDark", new Color(0.05f, 0.035f, 0.035f, 1f));
            return _fallbackAlarmDarkMaterial;
        }

        private static Material CreateColorMaterial(string name, Color color)
        {
            Shader shader = FindFirstSupportedShader("HDRP/Lit", "Standard", "Unlit/Color", "Sprites/Default");
            Material material = new Material(shader) { name = name, color = color };
            SetColorIfPresent(material, "_BaseColor", color);
            SetColorIfPresent(material, "_Color", color);
            SetColorIfPresent(material, "_UnlitColor", color);
            SetFloatIfPresent(material, "_Metallic", 0f);
            SetFloatIfPresent(material, "_Smoothness", 0.2f);
            return material;
        }

        private static Shader FindFirstSupportedShader(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Shader shader = Shader.Find(names[i]);
                if (shader != null)
                    return shader;
            }
            return Shader.Find("Standard");
        }

        private static void SetTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static void SetColorIfPresent(Material material, string property, Color color)
        {
            if (material != null && material.HasProperty(property))
                material.SetColor(property, color);
        }

        private static void SetFloatIfPresent(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void ConfigureAlarmEmissionMaterial(Material material)
        {
            if (material == null) return;

            material.EnableKeyword("_EMISSION");
            SetTextureIfPresent(material, "_EmissiveColorMap", Texture2D.whiteTexture);
            SetColorIfPresent(material, "_EmissionColor", Color.black);
            SetColorIfPresent(material, "_EmissiveColor", Color.black);
            SetColorIfPresent(material, "_EmissiveColorLDR", Color.black);
            SetFloatIfPresent(material, "_EmissiveIntensity", 0f);
            SetFloatIfPresent(material, "_UseEmissiveIntensity", 1f);
            ValidateAlarmMaterial(material);
        }

        private static void ValidateAlarmMaterial(Material material)
        {
            if (material == null) return;

            try
            {
                HDMaterial.ValidateMaterial(material);
            }
            catch
            {
                // Non-HDRP fallback shaders do not need HDRP material validation.
            }
        }

        private void Update()
        {
            bool securityActive = CctvSecurityDirector.IsTimedAlarmActive || MainframeProtocolDirector.IsProtocolAlarmVisualActive;

            // A facility meltdown retires these as security indicators and reuses them as
            // warning lights: visually lit, but silent and phase-locked to the facility's
            // red emergency lights rather than to the camera clock. Security wins when
            // both somehow apply, so the klaxon still means "camera saw you".
            bool meltdownActive = !securityActive && FacilityMeltdownCompat.EmergencyLightsOn;
            bool active = securityActive || meltdownActive;

            // Audio follows security only — the meltdown blink is silent, and Meltdown's
            // own announcer and music carry that sequence.
            EnsureClipAssigned(securityActive);

            float loudness = securityActive ? GetNormalizedLoudness(_audioSource) : 0f;
            float pulse = securityActive ? Mathf.Lerp(0.25f, 1f, loudness) : (meltdownActive ? 1f : 0f);

            // Square-wave strobe on the shared camera clock so the bars blink at
            // the same frequency and phase as the CCTV camera lenses. The meltdown case
            // needs no strobe of its own: EmergencyLightsOn is already the square wave,
            // so the bar sits solid through the lights' on-phase and drops out with them.
            bool strobeOn = meltdownActive
                || (securityActive && Mathf.Repeat(Time.unscaledTime, AlarmStrobePeriodSeconds) < AlarmStrobePeriodSeconds * 0.5f);
            float configuredLightScale = Mathf.Max(0f, CctvSecurityConfig.Current.AlarmLightIntensity) / 8f;
            // The cloned vanilla bar drives its emissive and light off the
            // strobe; the synthetic fallback keeps the loudness pulse.
            float nativePulse = _scaleNativeEmissive
                ? (active ? (strobeOn ? 1f : 0.12f) : 0f)
                : pulse;

            if (_usingNativeLight)
            {
                // Idle: authored vanilla brightness. Alarm: strobe between a
                // near-vanilla floor and a bright peak, synced to the cameras.
                float factor = active
                    ? (strobeOn ? NativeLightAlarmPeak : NativeLightAlarmFloor)
                    : NativeLightIdleFactor;
                SetNativeLightIntensity(_nativeLightBaseIntensity * factor * configuredLightScale);
            }
            else if (_redLight != null)
            {
                _redLight.intensity = pulse * CctvSecurityConfig.Current.AlarmLightIntensity * AlarmRoomLightIntensityMultiplier;
            }

            bool alreadyAppliedIdleEmission = !active && _lastAppliedPulse == 0f;
            if (_audioSource != null)
            {
                float targetVolume = CctvSecurityConfig.Current.AlarmAudioVolume * AlarmVolumeScale * AlarmVolumeRebalance;
                if (securityActive)
                {
                    _fadeOutStartedAt = -1f;
                    if (!Mathf.Approximately(_audioSource.volume, targetVolume))
                        _audioSource.volume = targetVolume;
                    if (_audioSource.clip != null && !_audioSource.isPlaying) _audioSource.Play();
                }
                else if (_audioSource.isPlaying)
                {
                    // Ramp out instead of cutting the loop mid-beep, and once
                    // the ramp is mostly done stop at the first loudness
                    // valley so the tail never ends inside a beep.
                    if (_fadeOutStartedAt < 0f) _fadeOutStartedAt = Time.unscaledTime;
                    float fadeT = (Time.unscaledTime - _fadeOutStartedAt) / AlarmFadeOutSeconds;
                    bool atBeepValley = fadeT >= FadeOutMinFractionForValleyStop
                        && GetNormalizedLoudness(_audioSource) <= FadeOutValleyLoudness;
                    if (fadeT >= 1f || atBeepValley)
                    {
                        _audioSource.Stop();
                        _fadeOutStartedAt = -1f;
                    }
                    else
                    {
                        float remaining = 1f - fadeT;
                        _audioSource.volume = targetVolume * remaining * remaining;
                    }
                }
                else
                {
                    _fadeOutStartedAt = -1f;
                }
            }

            if (alreadyAppliedIdleEmission)
                return;

            ApplyAlarmEmission(nativePulse);
        }

        private void ApplyAlarmEmission(float pulse)
        {
            if (_alarmMaterials == null || _alarmMaterials.Length == 0)
                RefreshAlarmRenderTargets();

            float clampedPulse = Mathf.Clamp01(pulse);

            if (_scaleNativeEmissive)
            {
                ApplyNativeEmissiveScale(clampedPulse);
                _lastAppliedPulse = clampedPulse;
                return;
            }

            Color hdrEmission = clampedPulse > 0f ? Color.red * (clampedPulse * AlarmEmissiveColorMultiplier) : Color.black;
            Color ldrEmission = clampedPulse > 0f ? Color.red * clampedPulse : Color.black;
            float emissiveIntensity = clampedPulse * AlarmEmissiveIntensityMultiplier;

            Material[] materials = _alarmMaterials ?? Array.Empty<Material>();
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null) continue;

                material.EnableKeyword("_EMISSION");
                SetTextureIfPresent(material, "_EmissiveColorMap", Texture2D.whiteTexture);
                SetColorIfPresent(material, "_EmissionColor", hdrEmission);
                SetColorIfPresent(material, "_EmissiveColor", hdrEmission);
                SetColorIfPresent(material, "_EmissiveColorLDR", ldrEmission);
                SetFloatIfPresent(material, "_EmissiveIntensity", emissiveIntensity);
                SetFloatIfPresent(material, "_UseEmissiveIntensity", clampedPulse > 0f ? 1f : 0f);
            }

            _lastAppliedPulse = clampedPulse;
        }

        // Records each cloned material's own emissive so the alarm pulse can
        // scale it. The vanilla lamp's shader reads _EmissiveColor as its
        // final HDR emission, so scaling that value directly reproduces the
        // bar's real color while modulating its brightness.
        // Adopts the cloned bar's own vanilla light (if the prefab node
        // carried one) as the pulse light, capturing its authored intensity
        // so the alarm can throb around the real value. HDRP stores intensity
        // on HDAdditionalLightData, so read/write that when present.
        private bool TryAdoptNativeLight()
        {
            _redLight = GetComponentInChildren<Light>(includeInactive: true);
            if (_redLight == null)
            {
                CctvModuleConfig.Log?.LogInfo("[MoonContracts.CctvSecurity] LIGHTBAR_NATIVE_LIGHT none on clone; using synthetic point light.");
                return false;
            }

            _redLight.enabled = true;
            _redLight.shadows = LightShadows.None;
            _nativeLightHD = _redLight.GetComponent<HDAdditionalLightData>();
            _nativeLightBaseIntensity = _nativeLightHD != null ? _nativeLightHD.intensity : _redLight.intensity;
            // Guard against a prefab light that stored zero intensity.
            if (_nativeLightBaseIntensity <= 0.01f)
                _nativeLightBaseIntensity = _nativeLightHD != null ? 4000f : 12f;
            SetNativeLightIntensity(_nativeLightBaseIntensity * NativeLightIdleFactor);
            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] LIGHTBAR_NATIVE_LIGHT adopted base={_nativeLightBaseIntensity:0} hd={_nativeLightHD != null} range={_redLight.range:0.0}.");
            return true;
        }

        private void SetNativeLightIntensity(float value)
        {
            if (_nativeLightHD != null)
                _nativeLightHD.intensity = value;
            else if (_redLight != null)
                _redLight.intensity = value;
        }

        private void CaptureNativeEmissiveBaseline()
        {
            Material[] materials = _alarmMaterials ?? Array.Empty<Material>();
            _baseEmissiveColors = new Color[materials.Length];
            bool anyLamp = false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null)
                {
                    _baseEmissiveColors[i] = Color.black;
                    continue;
                }

                material.EnableKeyword("_EMISSION");
                Color baseline = material.HasProperty("_EmissiveColor")
                    ? material.GetColor("_EmissiveColor")
                    : Color.black;
                // A ~black baseline means housing/frame, not lamp: record
                // black so the pulse skips the material entirely. Forcing a
                // red baseline here (the old behaviour) is what made the
                // whole asset flush red during an alarm.
                if (baseline.maxColorComponent <= NativeEmissiveLampThreshold)
                    baseline = Color.black;
                else
                    anyLamp = true;
                _baseEmissiveColors[i] = baseline;
            }

            // No material carries an authored emissive at all (some interiors
            // instance the bar with emission stripped). Elect exactly ONE
            // lens-like material as the lamp and give it the red baseline so
            // the bar can still throb — the rest stay untouched.
            if (!anyLamp && materials.Length > 0)
            {
                int lampIndex = FindLensFallbackMaterialIndex(materials);
                if (lampIndex >= 0)
                {
                    _baseEmissiveColors[lampIndex] = new Color(2.5f, 0.05f, 0.02f, 1f);
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts.CctvSecurity] LIGHTBAR_LENS_FALLBACK no authored emissive on clone; elected material '{materials[lampIndex]?.name}' as the lamp.");
                }
            }
        }

        // Ranks materials by how lens-like they look: name tokens first
        // (material name, then owning renderer name), then smallest renderer
        // bounds as the tiebreaker — the lamp lens is the small bright part,
        // the housing is the big one.
        private int FindLensFallbackMaterialIndex(Material[] materials)
        {
            int bestIndex = -1;
            int bestScore = int.MinValue;
            float bestVolume = float.MaxValue;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null) continue;

                int score = ScoreLensName(material.name);
                float volume = float.MaxValue;
                Renderer owner = FindOwningRenderer(material);
                if (owner != null)
                {
                    score += ScoreLensName(owner.name);
                    Vector3 size = owner.bounds.size;
                    volume = Mathf.Max(0.000001f, size.x * size.y * size.z);
                }

                if (score > bestScore || (score == bestScore && volume < bestVolume))
                {
                    bestScore = score;
                    bestVolume = volume;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        private static int ScoreLensName(string rawName)
        {
            string name = (rawName ?? string.Empty).ToLowerInvariant();
            int score = 0;
            if (name.Contains("lens")) score += 3;
            if (name.Contains("light") || name.Contains("lamp")) score += 2;
            if (name.Contains("glass")) score += 2;
            if (name.Contains("red")) score += 2;
            return score;
        }

        private Renderer FindOwningRenderer(Material material)
        {
            Renderer[] renderers = _alarmRenderers ?? Array.Empty<Renderer>();
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null) continue;
                Material[] shared = renderer.sharedMaterials;
                if (shared == null) continue;
                for (int m = 0; m < shared.Length; m++)
                {
                    if (ReferenceEquals(shared[m], material))
                        return renderer;
                }
            }
            return null;
        }

        private void ApplyNativeEmissiveScale(float clampedPulse)
        {
            float factor = clampedPulse > 0f
                ? Mathf.Lerp(NativeEmissiveAlarmFloor, NativeEmissiveAlarmPeak, clampedPulse)
                : NativeEmissiveIdleFactor;
            factor *= Mathf.Max(0f, CctvSecurityConfig.Current.AlarmLightIntensity) / 8f;

            Material[] materials = _alarmMaterials ?? Array.Empty<Material>();
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || i >= _baseEmissiveColors.Length) continue;
                // Black baseline = housing/frame material; leave it exactly
                // as authored so only the lamp inside the bar pulses.
                if (_baseEmissiveColors[i].maxColorComponent <= NativeEmissiveLampThreshold) continue;

                Color emissive = _baseEmissiveColors[i] * factor;
                if (material.HasProperty("_EmissiveColor"))
                    material.SetColor("_EmissiveColor", emissive);
            }
        }

        private void BeginAlarmAudioLoadIfNeeded()
        {
            if (_alarmClip != null || _loadStarted || _loadFailed) return;

            string path = ResolveAlarmAudioPath();
            if (string.IsNullOrEmpty(path))
            {
                _loadFailed = true;
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts.CctvSecurity] Alarm audio missing: {AlarmAudioFileName}");
                return;
            }

            _loadStarted = true;
            StartCoroutine(LoadAlarmAudio(path));
        }

        private IEnumerator LoadAlarmAudio(string path)
        {
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _loadFailed = true;
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts.CctvSecurity] Alarm audio load failed: {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    _loadFailed = true;
                    CctvModuleConfig.Log?.LogWarning("[MoonContracts.CctvSecurity] Alarm audio load returned no clip.");
                    yield break;
                }

                clip.name = "LGU_CCTV_SecurityAlarm";
                _alarmClip = clip;
                _loudnessEnvelope = BuildLoudnessEnvelope(clip);
                CctvModuleConfig.Log?.LogInfo($"[MoonContracts.CctvSecurity] Loaded CCTV alarm audio '{AlarmAudioFileName}' ({clip.length:0.00}s).");
            }
        }

        private void EnsureClipAssigned(bool active)
        {
            if (_audioSource == null || _alarmClip == null) return;
            if (_audioSource.clip == _alarmClip) return;

            _audioSource.clip = _alarmClip;
            if (active)
                _audioSource.Play();
        }

        private static float GetNormalizedLoudness(AudioSource source)
        {
            if (source == null || source.clip == null || _loudnessEnvelope == null || _loudnessEnvelope.Length == 0)
                return 0.5f + Mathf.Sin(Time.unscaledTime * FallbackPulseSpeed) * 0.5f;

            float clipLength = Mathf.Max(0.01f, source.clip.length);
            float normalizedTime = Mathf.Repeat(source.time, clipLength) / clipLength;
            int index = Mathf.Clamp(
                Mathf.FloorToInt(normalizedTime * _loudnessEnvelope.Length),
                0,
                _loudnessEnvelope.Length - 1);
            return Mathf.Clamp01(_loudnessEnvelope[index]);
        }

        private static float[] BuildLoudnessEnvelope(AudioClip clip)
        {
            if (clip == null || clip.samples <= 0) return Array.Empty<float>();

            try
            {
                int channels = Mathf.Max(1, clip.channels);
                int valueCount = clip.samples * channels;
                var samples = new float[valueCount];
                if (!clip.GetData(samples, 0))
                    return Array.Empty<float>();

                int frameCount = Mathf.Max(1, Mathf.CeilToInt(clip.length * LoudnessEnvelopeFramesPerSecond));
                var envelope = new float[frameCount];
                float max = 0f;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int startSample = Mathf.FloorToInt((frame / (float)frameCount) * clip.samples);
                    int endSample = Mathf.FloorToInt(((frame + 1) / (float)frameCount) * clip.samples);
                    int start = Mathf.Clamp(startSample * channels, 0, samples.Length);
                    int end = Mathf.Clamp(Mathf.Max(endSample, startSample + 1) * channels, start + 1, samples.Length);
                    double sum = 0d;
                    int count = 0;

                    for (int i = start; i < end; i++)
                    {
                        float sample = samples[i];
                        sum += sample * sample;
                        count++;
                    }

                    float rms = count > 0 ? Mathf.Sqrt((float)(sum / count)) : 0f;
                    envelope[frame] = rms;
                    if (rms > max) max = rms;
                }

                if (max > 0.0001f)
                {
                    for (int i = 0; i < envelope.Length; i++)
                        envelope[i] = Mathf.Clamp01(envelope[i] / max);
                }

                return envelope;
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts.CctvSecurity] Alarm loudness envelope failed: {ex.GetType().Name}: {ex.Message}");
                return Array.Empty<float>();
            }
        }

        private static string ResolveAlarmAudioPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string pluginPath = Paths.PluginPath ?? string.Empty;
            string[] candidates =
            {
                !string.IsNullOrEmpty(assemblyDir) ? Path.Combine(assemblyDir, "Audio", AlarmAudioFileName) : null,
                !string.IsNullOrEmpty(assemblyDir) ? Path.Combine(assemblyDir, AlarmAudioFileName) : null,
                !string.IsNullOrEmpty(pluginPath) ? Path.Combine(pluginPath, "y4ngz-Y4NGZCompany", "Audio", AlarmAudioFileName) : null,
                !string.IsNullOrEmpty(pluginPath) ? Path.Combine(pluginPath, "y4ngz-LethalCCTV", "Audio", AlarmAudioFileName) : null,
                !string.IsNullOrEmpty(pluginPath) ? Path.Combine(pluginPath, "Audio", AlarmAudioFileName) : null,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Audio", AlarmAudioFileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}


