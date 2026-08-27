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
        private static void BuildVisual(Transform fixtureParent, Transform gunParent)
        {
            if (_visualPrefab != null)
            {
                GameObject visual = UnityEngine.Object.Instantiate(_visualPrefab, fixtureParent);
                visual.name = "PetitCanonVisual";
                visual.transform.localPosition = PitchPivotLocalPosition;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one;
                ScaleVisualToTargetSize(visual);
                SplitVisualIntoFixtureAndGun(visual, gunParent);
                NormalizeTurretMaterialsForVisibility(visual);
                return;
            }

            GameObject baseCylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            baseCylinder.name = "FallbackTurretBase";
            baseCylinder.transform.SetParent(fixtureParent, worldPositionStays: false);
            baseCylinder.transform.localPosition = new Vector3(0f, 0.24f, 0f);
            baseCylinder.transform.localScale = new Vector3(1.15f, 0.24f, 1.15f);

            GameObject housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
            housing.name = "FallbackTurretHousing";
            housing.transform.SetParent(gunParent, worldPositionStays: false);
            housing.transform.localPosition = Vector3.zero;
            housing.transform.localScale = new Vector3(0.95f, 0.55f, 0.78f);

            GameObject barrel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barrel.name = "FallbackTurretBarrel";
            barrel.transform.SetParent(gunParent, worldPositionStays: false);
            barrel.transform.localPosition = new Vector3(0f, 0.08f, 1.33f);
            barrel.transform.localScale = new Vector3(0.26f, 0.22f, 2.55f);

            Material mat = new Material(Shader.Find("HDRP/Unlit") ?? Shader.Find("Standard"));
            mat.color = new Color(0.28f, 0.29f, 0.27f, 1f);
            foreach (Renderer renderer in _root.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterial = mat;
        }

        private static void SplitVisualIntoFixtureAndGun(GameObject visual, Transform gunParent)
        {
            if (visual == null || gunParent == null) return;

            Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer != null && !(renderer is ParticleSystemRenderer))
                .ToArray();
            if (renderers.Length == 0) return;

            Transform visualRoot = visual.transform;
            bool hasBounds = false;
            Bounds combined = default;
            Dictionary<Renderer, Bounds> localBoundsByRenderer = new Dictionary<Renderer, Bounds>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                Bounds localBounds = ConvertWorldBoundsToLocal(renderer.bounds, visualRoot);
                localBoundsByRenderer[renderer] = localBounds;
                if (!hasBounds)
                {
                    combined = localBounds;
                    hasBounds = true;
                }
                else
                {
                    combined.Encapsulate(localBounds);
                }
            }

            if (!hasBounds || combined.size.sqrMagnitude <= 0.0001f) return;

            float fixtureCutoffY = combined.min.y + combined.size.y * 0.36f;
            float lowGunCutoffY = combined.min.y + combined.size.y * 0.18f;
            float forwardCutoffZ = combined.center.z + combined.size.z * 0.16f;
            HashSet<Renderer> gunRenderers = new HashSet<Renderer>();

            foreach (KeyValuePair<Renderer, Bounds> pair in localBoundsByRenderer)
            {
                Bounds bounds = pair.Value;
                bool upperAssembly = bounds.center.y > fixtureCutoffY;
                bool forwardBarrel = bounds.center.z > forwardCutoffZ && bounds.center.y > lowGunCutoffY;
                if (upperAssembly || forwardBarrel)
                    gunRenderers.Add(pair.Key);
            }

            if (gunRenderers.Count == 0)
            {
                Renderer fallbackRenderer = renderers
                    .OrderByDescending(renderer =>
                    {
                        Bounds bounds = localBoundsByRenderer[renderer];
                        return bounds.center.y + bounds.center.z * 0.20f;
                    })
                    .FirstOrDefault();
                if (fallbackRenderer != null)
                    gunRenderers.Add(fallbackRenderer);
            }

            HashSet<Transform> movedRoots = new HashSet<Transform>();
            foreach (Renderer renderer in gunRenderers
                         .OrderByDescending(renderer => GetTransformDepth(renderer.transform)))
            {
                Transform target = renderer.transform;
                if (target == null || target == visualRoot) continue;
                if (HasMovedAncestor(target, movedRoots, visualRoot)) continue;

                target.SetParent(gunParent, worldPositionStays: true);
                movedRoots.Add(target);
            }

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV.Turret] Split turret visual into fixed fixture and moving gun. renderers={renderers.Length} gun={movedRoots.Count}.");
        }

        private static Bounds ConvertWorldBoundsToLocal(Bounds worldBounds, Transform localRoot)
        {
            Vector3 ext = worldBounds.extents;
            Vector3 center = worldBounds.center;
            Vector3[] corners =
            {
                center + new Vector3(-ext.x, -ext.y, -ext.z),
                center + new Vector3(-ext.x, -ext.y, ext.z),
                center + new Vector3(-ext.x, ext.y, -ext.z),
                center + new Vector3(-ext.x, ext.y, ext.z),
                center + new Vector3(ext.x, -ext.y, -ext.z),
                center + new Vector3(ext.x, -ext.y, ext.z),
                center + new Vector3(ext.x, ext.y, -ext.z),
                center + new Vector3(ext.x, ext.y, ext.z),
            };

            Bounds localBounds = new Bounds(localRoot.InverseTransformPoint(corners[0]), Vector3.zero);
            for (int i = 1; i < corners.Length; i++)
                localBounds.Encapsulate(localRoot.InverseTransformPoint(corners[i]));
            return localBounds;
        }

        private static int GetTransformDepth(Transform transform)
        {
            int depth = 0;
            while (transform != null)
            {
                depth++;
                transform = transform.parent;
            }
            return depth;
        }

        private static bool HasMovedAncestor(Transform transform, HashSet<Transform> movedRoots, Transform stopAt)
        {
            Transform current = transform.parent;
            while (current != null && current != stopAt)
            {
                if (movedRoots.Contains(current))
                    return true;
                current = current.parent;
            }
            return false;
        }

        private static void NormalizeTurretMaterialsForVisibility(GameObject visual)
        {
            Renderer[] renderers = visual != null ? visual.GetComponentsInChildren<Renderer>(true) : null;
            if (renderers == null || renderers.Length == 0)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;

                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                    continue;

                Material[] normalized = new Material[sourceMaterials.Length];
                for (int j = 0; j < sourceMaterials.Length; j++)
                    normalized[j] = CreateStableUnlitMaterial(sourceMaterials[j], "LethalCCTV_TurretVisibleMaterial");
                renderer.sharedMaterials = normalized;
            }
        }

        private static Material CreateStableUnlitMaterial(Material source, string name)
        {
            Shader shader = Shader.Find("HDRP/Unlit") ??
                            Shader.Find("Unlit/Texture") ??
                            Shader.Find("Standard");
            if (shader == null && source == null)
                return null;

            Material material = shader != null ? new Material(shader) : new Material(source);
            material.name = name;

            Texture main = GetMaterialTexture(source);
            if (main != null)
            {
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", main);
                if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", main);
                if (material.HasProperty("_UnlitColorMap")) material.SetTexture("_UnlitColorMap", main);
            }

            Color color = GetMaterialColor(source);
            color.a = Mathf.Max(color.a, 1f);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color * 0.32f);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", color * 0.32f);
            material.EnableKeyword("_EMISSION");
            return material;
        }

        private static Texture GetMaterialTexture(Material material)
        {
            if (material == null)
                return null;

            string[] properties = { "_BaseColorMap", "_UnlitColorMap", "_MainTex", "_BaseMap" };
            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                if (material.HasProperty(property))
                {
                    Texture texture = material.GetTexture(property);
                    if (texture != null)
                        return texture;
                }
            }

            return null;
        }

        private static Color GetMaterialColor(Material material)
        {
            if (material == null)
                return Color.white;

            string[] properties = { "_BaseColor", "_UnlitColor", "_Color" };
            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                if (material.HasProperty(property))
                    return material.GetColor(property);
            }

            return Color.white;
        }

        private static void ScaleVisualToTargetSize(GameObject visual)
        {
            Renderer[] renderers = visual != null ? visual.GetComponentsInChildren<Renderer>(true) : null;
            if (renderers == null || renderers.Length == 0) return;

            bool hasBounds = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
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

            if (!hasBounds) return;
            float maxDimension = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (maxDimension <= 0.001f) return;

            float scale = Mathf.Clamp(VisualTargetMaxDimensionMeters / maxDimension, 1f, 80f);
            visual.transform.localScale = Vector3.one * scale;
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV.Turret] Visual autoscaled maxDimension={maxDimension:0.###}m target={VisualTargetMaxDimensionMeters:0.##}m scale={scale:0.###}.");
        }

        private static void EnsureRenderTexture()
        {
            if (_renderTexture != null) return;

            _renderTexture = new RenderTexture(1024, 768, 16, RenderTextureFormat.ARGB32)
            {
                name = "LethalCCTV_ShipTurretRT",
                useMipMap = false,
                autoGenerateMips = false
            };
            _renderTexture.Create();
        }

        private static void EnsureAudioSources()
        {
            if (_root == null) return;

            _fireSource ??= CreateAudioSource("TurretFireAudio", spatial: true, loop: false);
            _rotateSource ??= CreateAudioSource("TurretRotateAudio", spatial: false, loop: false);
            _zoomSource ??= CreateAudioSource("TurretZoomAudio", spatial: false, loop: false);
        }

        private static AudioSource CreateAudioSource(string name, bool spatial, bool loop)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = spatial ? 1f : 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 4f;
            source.maxDistance = 85f;
            return source;
        }

        private static void EnsureAssetsLoaded()
        {
            if (_bundleLoadAttempted) return;
            _bundleLoadAttempted = true;

            string pluginDir = Path.GetDirectoryName(typeof(SurveillanceBootstrap).Assembly.Location);
            string bundlePath = !string.IsNullOrEmpty(pluginDir)
                ? Path.Combine(pluginDir, BundleFileName)
                : BundleFileName;
            if (!File.Exists(bundlePath))
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] Asset bundle missing at '{bundlePath}'. Using fallback turret visual.");
                return;
            }

            try
            {
                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] AssetBundle.LoadFromFile returned null for '{bundlePath}'.");
                    return;
                }

                string[] names = _bundle.GetAllAssetNames();
                _visualPrefab = LoadFirst<GameObject>(names, "shipturret_prefab", "petit", "turret");
                _dustExplosionPrefab = LoadFirst<GameObject>(names, "dustexplosion");
                _fireClips = names
                    .Where(n => n.IndexOf("cannon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("firing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("blast", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(n => _bundle.LoadAsset<AudioClip>(n))
                    .Where(c => c != null)
                    .ToArray();
                _zoomClip = LoadFirst<AudioClip>(names, "zoom");
                _rotateClip = _zoomClip;

                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV.Turret] Bundle loaded prefab={_visualPrefab != null} dust={_dustExplosionPrefab != null} fireClips={(_fireClips != null ? _fireClips.Length : 0)} rotate={_rotateClip != null} zoom={_zoomClip != null}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] Bundle load failed: {ex.Message}");
            }
        }

        private static T LoadFirst<T>(IEnumerable<string> names, params string[] tokens) where T : UnityEngine.Object
        {
            if (names == null) return null;
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                bool match = false;
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (name.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = true;
                        break;
                    }
                }
                if (!match) continue;
                T asset = _bundle.LoadAsset<T>(name);
                if (asset != null) return asset;
            }
            return null;
        }

    }
}
