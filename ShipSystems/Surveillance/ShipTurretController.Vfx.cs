using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using GameNetcodeStuff;
using Y4NGZCompany.Core.Compat;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class ShipTurretController
    {
        private static void TickAudioCues()
        {
            float now = Time.unscaledTime;
            if (_rotateSource != null && _rotateSource.isPlaying && now >= _rotateCueStopAt)
                _rotateSource.Stop();
            if (_zoomSource != null && _zoomSource.isPlaying && now >= _zoomCueStopAt)
                _zoomSource.Stop();
            if (_lastRotateDirection != 0 && now - _lastRotateAt > AudioDirectionResetSeconds)
                _lastRotateDirection = 0;
            if (_lastZoomDirection != 0 && now - _lastZoomAt > AudioDirectionResetSeconds)
                _lastZoomDirection = 0;
        }

        private static void TickRecoil()
        {
            if (_gunVisualRoot == null) return;
            bool recoilActive = Time.unscaledTime - _recoilStartedAt < RecoilDurationSeconds;
            if (recoilActive || _gunVisualRoot.localPosition.sqrMagnitude > 0.0001f)
                ApplyAim();
        }

        private static void TriggerRecoil()
        {
            _recoilStartedAt = Time.unscaledTime;
            ApplyAim();
        }

        private static float GetCurrentRecoil01()
        {
            float elapsed = Time.unscaledTime - _recoilStartedAt;
            if (elapsed < 0f || elapsed >= RecoilDurationSeconds)
                return 0f;

            float t = elapsed / RecoilDurationSeconds;
            if (t < 0.14f)
                return Mathf.Lerp(0f, 1f, t / 0.14f);

            float settle = Mathf.Clamp01((t - 0.14f) / 0.86f);
            return 1f - Mathf.SmoothStep(0f, 1f, settle);
        }

        private static void PlayShortCue(AudioSource source, AudioClip clip, float volume)
        {
            if (source == null || clip == null) return;
            source.Stop();
            source.clip = clip;
            source.volume = volume;
            source.loop = false;
            source.time = 0f;
            source.Play();
        }

        private static int ResolveDominantDirection(Vector2 delta)
        {
            if (delta.sqrMagnitude < 0.0001f) return 0;
            if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                return delta.x >= 0f ? 1 : -1;
            return delta.y >= 0f ? 2 : -2;
        }

        private static void PlayFireAudio()
        {
            EnsureAudioSources();
            if (_fireSource == null) return;
            AudioClip clip = null;
            if (_fireClips != null && _fireClips.Length > 0)
                clip = _fireClips[UnityEngine.Random.Range(0, _fireClips.Length)];
            if (clip != null)
                _fireSource.PlayOneShot(clip, 0.88f);
        }

        private static void SpawnImpact(Vector3 point, Vector3 normal)
        {
            EnsureAssetsLoaded();
            GameObject fallbackImpact = CreateFallbackDust(point, normal);

            if (fallbackImpact != null)
                UnityEngine.Object.Destroy(fallbackImpact, 4f);
        }

        private static void SpawnMuzzleEffects(Vector3 origin, Vector3 direction, Vector3 endPoint)
        {
            Vector3 forward = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.forward;
            Vector3 muzzle = _muzzle != null ? _muzzle.position : origin;
            Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);

            GameObject flash = CreateMuzzleFlash(muzzle + forward * 0.22f, rotation);
            if (flash != null)
                UnityEngine.Object.Destroy(flash, 0.22f);

            GameObject smoke = CreateMuzzleSmoke(muzzle + forward * 0.38f, rotation);
            if (smoke != null)
                UnityEngine.Object.Destroy(smoke, 1.4f);

            GameObject tracer = CreateTracer(muzzle + forward * 0.55f, endPoint);
            if (tracer != null)
                UnityEngine.Object.Destroy(tracer, 0.12f);
        }

        private static GameObject CreateMuzzleFlash(Vector3 position, Quaternion rotation)
        {
            GameObject go = new GameObject("LethalCCTV_TurretMuzzleFlash");
            go.transform.SetPositionAndRotation(position, rotation);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.09f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.045f, 0.095f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.6f, 4.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.20f, 0.58f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.92f, 0.48f, 0.95f),
                new Color(1f, 0.34f, 0.06f, 0.72f));
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 17f;
            shape.radius = 0.06f;
            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = GetMuzzleFlashMaterial();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sortingFudge = 8f;
                renderer.maxParticleSize = 2f;
            }

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.72f, 0.28f, 1f);
            light.range = 5.5f;
            light.intensity = 3.2f;
            ps.Play();
            return go;
        }

        private static GameObject CreateMuzzleSmoke(Vector3 position, Quaternion rotation)
        {
            GameObject go = new GameObject("LethalCCTV_TurretMuzzleSmoke");
            go.transform.SetPositionAndRotation(position, rotation);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.duration = 0.42f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.34f, 0.82f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.75f, 1.9f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.42f);
            main.startColor = new Color(0.46f, 0.46f, 0.42f, 0.42f);
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 16) });
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 26f;
            shape.radius = 0.08f;
            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = GetMuzzleSmokeMaterial();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sortingFudge = 5f;
                renderer.maxParticleSize = 1.3f;
            }

            ps.Play();
            return go;
        }

        private static GameObject CreateTracer(Vector3 start, Vector3 end)
        {
            if ((end - start).sqrMagnitude < 0.001f)
                return null;

            GameObject go = new GameObject("LethalCCTV_TurretTracer");
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, start);
            line.SetPosition(1, Vector3.Lerp(start, end, 0.88f));
            line.widthMultiplier = 0.035f;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            Material material = GetMuzzleFlashMaterial();
            if (material != null)
                line.sharedMaterial = material;
            line.startColor = new Color(1f, 0.92f, 0.52f, 0.82f);
            line.endColor = new Color(1f, 0.45f, 0.04f, 0f);
            return go;
        }

        private static GameObject CreateParticlePackDust(Vector3 point, Vector3 normal)
        {
            if (_dustExplosionPrefab == null) return null;

            Vector3 hitNormal = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
            GameObject impact = UnityEngine.Object.Instantiate(
                _dustExplosionPrefab,
                point + hitNormal * 0.08f,
                Quaternion.LookRotation(hitNormal));
            impact.name = "LethalCCTV_TurretDustExplosion";
            impact.transform.localScale = Vector3.one * 0.35f;

            Material dustMaterial = GetImpactDustMaterial();
            foreach (Renderer renderer in impact.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = true;
                if (dustMaterial != null)
                    renderer.sharedMaterials = new[] { dustMaterial };

                if (renderer is ParticleSystemRenderer particleRenderer)
                {
                    particleRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                    particleRenderer.sortingFudge = 4f;
                    particleRenderer.maxParticleSize = Mathf.Max(particleRenderer.maxParticleSize, 1.5f);
                }
            }

            foreach (ParticleSystem ps in impact.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = ps.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.startColor = new Color(0.54f, 0.47f, 0.36f, 0.78f);
                ps.Clear(withChildren: false);
                ps.Play(withChildren: false);
            }

            return impact;
        }

        private static GameObject CreateFallbackDust(Vector3 point, Vector3 normal)
        {
            GameObject go = new GameObject("LethalCCTV_TurretFallbackDust");
            Vector3 hitNormal = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
            go.transform.position = point + hitNormal * 0.08f;
            go.transform.rotation = Quaternion.LookRotation(hitNormal);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.duration = 1.0f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 1.05f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4.2f, 8.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 0.95f);
            main.startColor = new Color(0.54f, 0.47f, 0.36f, 0.82f);
            main.gravityModifier = 0.85f;
            main.maxParticles = 120;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.loop = false;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 72) });
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 58f;
            shape.radius = 0.34f;
            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            Material material = GetImpactDustMaterial();
            if (renderer != null && material != null)
            {
                renderer.sharedMaterial = material;
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sortingFudge = 5f;
                renderer.maxParticleSize = 2.5f;
            }
            ps.Play();
            return go;
        }

        private static Material GetImpactDustMaterial()
        {
            if (_impactDustMaterial != null) return _impactDustMaterial;
            _impactDustMaterial = CreateSoftParticleMaterial(
                "LethalCCTV_TurretDustMaterial",
                new Color(0.52f, 0.46f, 0.34f, 0.58f),
                additive: false);
            return _impactDustMaterial;
        }

        private static Material GetMuzzleFlashMaterial()
        {
            if (_muzzleFlashMaterial != null) return _muzzleFlashMaterial;
            _muzzleFlashMaterial = CreateSoftParticleMaterial(
                "LethalCCTV_TurretMuzzleFlashMaterial",
                new Color(1f, 0.72f, 0.16f, 0.86f),
                additive: true);
            return _muzzleFlashMaterial;
        }

        private static Material GetMuzzleSmokeMaterial()
        {
            if (_muzzleSmokeMaterial != null) return _muzzleSmokeMaterial;
            _muzzleSmokeMaterial = CreateSoftParticleMaterial(
                "LethalCCTV_TurretMuzzleSmokeMaterial",
                new Color(0.44f, 0.44f, 0.40f, 0.38f),
                additive: false);
            return _muzzleSmokeMaterial;
        }

        private static Material CreateSoftParticleMaterial(string name, Color color, bool additive)
        {
            Shader shader = Shader.Find(additive ? "Particles/Additive" : "Particles/Standard Unlit") ??
                            Shader.Find("HDRP/Unlit") ??
                            Shader.Find("Standard");
            if (shader == null) return null;

            Material material = new Material(shader)
            {
                name = name,
                color = color,
                renderQueue = 3000
            };
            Texture2D texture = GetSoftParticleTexture();
            if (texture != null)
            {
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", texture);
                if (material.HasProperty("_UnlitColorMap")) material.SetTexture("_UnlitColorMap", texture);
            }
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_SurfaceType")) material.SetFloat("_SurfaceType", 1f);
            if (material.HasProperty("_BlendMode")) material.SetFloat("_BlendMode", additive ? 1f : 0f);
            if (material.HasProperty("_AlphaCutoffEnable")) material.SetFloat("_AlphaCutoffEnable", 0f);
            material.EnableKeyword("_ALPHABLEND_ON");
            return material;
        }

        private static Texture2D GetSoftParticleTexture()
        {
            if (_softParticleTexture != null) return _softParticleTexture;

            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true, linear: false)
            {
                name = "LethalCCTV_SoftParticleTexture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            float radius = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), center) / radius;
                    float alpha = Mathf.Clamp01(1f - dist);
                    alpha = alpha * alpha * (3f - 2f * alpha);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            _softParticleTexture = texture;
            return _softParticleTexture;
        }

    }
}
