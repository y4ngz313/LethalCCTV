using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CCTVScanGlowManager
    {
        private const int FALLBACK_SCAN_NODE_MASK = 1 << 22;
        private const float TARGET_SEARCH_RADIUS = 2.5f;
        private const float MAX_GLOW_LIFETIME = 15f;
        private const float MIN_GLOW_LIFETIME = 1f;

        private static readonly Color ITEM_GREEN = new Color(0.10f, 1f, 0.32f, 1f);
        private static readonly Color HOSTILE_RED = new Color(1f, 0.08f, 0.06f, 1f);
        private static readonly Collider[] _overlapHits = new Collider[64];
        private static readonly Dictionary<Renderer, GlowRenderer> _tracked = new Dictionary<Renderer, GlowRenderer>(64);
        private static readonly List<Renderer> _expired = new List<Renderer>(32);

        private static Material _overlayMaterial;
        private static int _scanNodeMask = -1;
        private static bool _initialized;

        internal static bool HasActiveGlows => _tracked.Count > 0;

        private sealed class GlowRenderer
        {
            public Renderer Original;
            public Material[] OriginalMaterials;
            public Material[] CopiedMaterials;
            public Renderer Overlay;
            public MaterialPropertyBlock OverlayBlock;
            public float ExpiresAt;
            public Color Color;
        }

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _scanNodeMask = ResolveScanNodeMask();
        }

        internal static void Shutdown()
        {
            ClearAll();
            if (_overlayMaterial != null)
            {
                UnityEngine.Object.Destroy(_overlayMaterial);
                _overlayMaterial = null;
            }
            _initialized = false;
            _scanNodeMask = -1;
        }

        internal static void ClearAll()
        {
            foreach (GlowRenderer glow in _tracked.Values)
            {
                Restore(glow);
            }
            _tracked.Clear();
            _expired.Clear();
        }

        internal static void MarkScanTarget(Vector3 position, float radius, int colorKind, float lifetime)
        {
            Initialize();

            float clampedLifetime = Mathf.Clamp(lifetime, MIN_GLOW_LIFETIME, MAX_GLOW_LIFETIME);
            Color color = colorKind == 1 ? HOSTILE_RED : ITEM_GREEN;

            ScanNodeProperties node = FindNearestScanNode(position, Mathf.Max(radius, TARGET_SEARCH_RADIUS));
            GameObject root = node != null ? ResolveTargetRoot(node) : ResolveFallbackRoot(position, radius);
            if (root == null)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV scan glow skipped; no renderer target near {position}.");
                return;
            }

            MarkRoot(root, color, clampedLifetime);
        }

        internal static void Tick()
        {
            if (_tracked.Count == 0) return;

            float now = Time.unscaledTime;
            _expired.Clear();

            foreach (KeyValuePair<Renderer, GlowRenderer> pair in _tracked)
            {
                GlowRenderer glow = pair.Value;
                if (glow == null || glow.Original == null || now >= glow.ExpiresAt)
                {
                    _expired.Add(pair.Key);
                    continue;
                }

                ApplyPulse(glow, now);
            }

            for (int i = 0; i < _expired.Count; i++)
            {
                Renderer key = _expired[i];
                if (_tracked.TryGetValue(key, out GlowRenderer glow))
                {
                    Restore(glow);
                    _tracked.Remove(key);
                }
            }
            _expired.Clear();
        }

        private static void MarkRoot(GameObject root, Color color, float lifetime)
        {
            if (root == null) return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers == null || renderers.Length == 0)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV scan glow target '{root.name}' has no renderers.");
                return;
            }

            float expiresAt = Time.unscaledTime + lifetime;
            int applied = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsEligibleRenderer(renderer)) continue;
                if (TrackRenderer(renderer, color, expiresAt)) applied++;
            }

            if (applied > 0)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV scan glow applied to '{root.name}' renderers={applied}.");
            }
        }

        private static bool TrackRenderer(Renderer renderer, Color color, float expiresAt)
        {
            if (renderer == null) return false;

            if (_tracked.TryGetValue(renderer, out GlowRenderer existing))
            {
                existing.Color = color;
                existing.ExpiresAt = Mathf.Max(existing.ExpiresAt, expiresAt);
                ApplyPulse(existing, Time.unscaledTime);
                return true;
            }

            Material[] originalMaterials = renderer.sharedMaterials;
            Renderer overlay = CreateOverlay(renderer);
            if (overlay == null) return false;

            var glow = new GlowRenderer
            {
                Original = renderer,
                OriginalMaterials = originalMaterials,
                CopiedMaterials = null,
                Overlay = overlay,
                OverlayBlock = overlay != null ? new MaterialPropertyBlock() : null,
                ExpiresAt = expiresAt,
                Color = color,
            };
            _tracked[renderer] = glow;
            ApplyPulse(glow, Time.unscaledTime);
            return true;
        }

        private static Renderer CreateOverlay(Renderer source)
        {
            if (source == null) return null;

            GameObject go = new GameObject("LethalCCTV_ScanGlowOverlay");
            go.layer = source.gameObject.layer;
            go.transform.SetParent(source.transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            Renderer overlay = null;
            if (source is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                var skinnedOverlay = go.AddComponent<SkinnedMeshRenderer>();
                skinnedOverlay.sharedMesh = skinned.sharedMesh;
                skinnedOverlay.bones = skinned.bones;
                skinnedOverlay.rootBone = skinned.rootBone;
                skinnedOverlay.localBounds = skinned.localBounds;
                skinnedOverlay.updateWhenOffscreen = true;
                skinnedOverlay.quality = skinned.quality;
                overlay = skinnedOverlay;
            }
            else if (source is MeshRenderer meshRenderer)
            {
                MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    var overlayFilter = go.AddComponent<MeshFilter>();
                    overlayFilter.sharedMesh = meshFilter.sharedMesh;
                    overlay = go.AddComponent<MeshRenderer>();
                }
            }

            if (overlay == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }

            overlay.enabled = source.enabled;
            overlay.sharedMaterial = EnsureOverlayMaterial();
            overlay.shadowCastingMode = ShadowCastingMode.Off;
            overlay.receiveShadows = false;
            overlay.allowOcclusionWhenDynamic = false;
            overlay.lightProbeUsage = LightProbeUsage.Off;
            overlay.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return overlay;
        }

        private static Material EnsureOverlayMaterial()
        {
            if (_overlayMaterial != null) return _overlayMaterial;

            Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("HDRP/Lit") ?? Shader.Find("Unlit/Color");
            _overlayMaterial = new Material(shader)
            {
                name = "LethalCCTV_ScanGlowOverlayMat",
                renderQueue = 5000,
            };

            SetFloatIfExists(_overlayMaterial, "_SurfaceType", 1f);
            SetFloatIfExists(_overlayMaterial, "_BlendMode", 0f);
            SetFloatIfExists(_overlayMaterial, "_ZWrite", 0f);
            SetFloatIfExists(_overlayMaterial, "_TransparentZWrite", 0f);
            SetFloatIfExists(_overlayMaterial, "_CullMode", (float)CullMode.Off);
            SetFloatIfExists(_overlayMaterial, "_CullModeForward", (float)CullMode.Off);
            SetFloatIfExists(_overlayMaterial, "_ZTest", (float)CompareFunction.Always);
            SetFloatIfExists(_overlayMaterial, "_ZTestDepthEqualForOpaque", (float)CompareFunction.Always);
            SetColorIfExists(_overlayMaterial, "_BaseColor", new Color(0f, 0f, 0f, 0f));
            SetColorIfExists(_overlayMaterial, "_UnlitColor", new Color(0f, 0f, 0f, 0f));
            SetColorIfExists(_overlayMaterial, "_EmissiveColor", Color.black);
            SetColorIfExists(_overlayMaterial, "_EmissionColor", Color.black);
            SetFloatIfExists(_overlayMaterial, "_EmissiveIntensity", 1f);
            _overlayMaterial.EnableKeyword("_EMISSION");
            return _overlayMaterial;
        }

        private static void ApplyPulse(GlowRenderer glow, float now)
        {
            if (glow == null) return;

            bool hostile = IsHostileColor(glow.Color);
            float pulse = 0.72f + 0.28f * Mathf.Sin(now * 7.25f);
            float intensity = hostile ? Mathf.Lerp(16f, 48f, pulse) : Mathf.Lerp(70f, 190f, pulse);
            Color glowColor = hostile
                ? new Color(1f, 0.035f, 0.025f, 1f)
                : glow.Color;
            Color emissive = hostile
                ? new Color(glowColor.r * intensity, glowColor.g * intensity * 0.42f, glowColor.b * intensity * 0.32f, 1f)
                : glowColor * intensity;
            Color softBase = hostile
                ? new Color(1f, 0.04f, 0.035f, 0.10f + 0.06f * pulse)
                : new Color(glowColor.r, glowColor.g, glowColor.b, 0.18f + 0.10f * pulse);

            if (glow.Overlay != null)
            {
                MaterialPropertyBlock block = glow.OverlayBlock ?? new MaterialPropertyBlock();
                block.SetColor("_BaseColor", softBase);
                block.SetColor("_UnlitColor", softBase);
                block.SetColor("_EmissiveColor", emissive);
                block.SetColor("_EmissionColor", emissive);
                block.SetColor("_EmissiveColorLDR", emissive);
                block.SetFloat("_EmissiveIntensity", intensity);
                block.SetTexture("_EmissiveColorMap", Texture2D.whiteTexture);
                glow.Overlay.SetPropertyBlock(block);
                glow.OverlayBlock = block;
            }
        }

        private static void ApplyMaterialEmission(Material material, Color baseColor, Color emissive, float intensity, bool hostile)
        {
            if (material == null) return;

            material.EnableKeyword("_EMISSION");
            SetColorIfExists(material, "_EmissionColor", emissive);
            SetColorIfExists(material, "_EmissiveColor", emissive);
            SetColorIfExists(material, "_EmissiveColorLDR", emissive);
            SetFloatIfExists(material, "_EmissiveIntensity", intensity);
            SetFloatIfExists(material, "_UseEmissiveIntensity", 1f);
            SetTextureIfExists(material, "_EmissiveColorMap", Texture2D.whiteTexture);

            Color tintedBase = hostile
                ? new Color(baseColor.r, baseColor.g, baseColor.b, 0.12f)
                : new Color(baseColor.r, baseColor.g, baseColor.b, 0.22f);
            SetColorIfExists(material, "_BaseColor", tintedBase);
            SetColorIfExists(material, "_UnlitColor", tintedBase);
        }

        private static bool IsHostileColor(Color color)
        {
            return color.r > 0.75f && color.g < 0.22f && color.b < 0.18f;
        }

        private static void Restore(GlowRenderer glow)
        {
            if (glow == null) return;

            if (glow.Original != null && glow.OriginalMaterials != null)
            {
                glow.Original.sharedMaterials = glow.OriginalMaterials;
            }

            if (glow.Overlay != null)
            {
                UnityEngine.Object.Destroy(glow.Overlay.gameObject);
            }
        }

        private static bool IsEligibleRenderer(Renderer renderer)
        {
            if (renderer == null) return false;
            if (renderer is LineRenderer) return false;
            string typeName = renderer.GetType().Name;
            if (typeName.IndexOf("VFX", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (renderer.gameObject.name.IndexOf("LethalCCTV_ScanGlowOverlay", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (renderer.GetComponentInParent<Canvas>() != null) return false;
            return renderer.sharedMaterials != null && renderer.sharedMaterials.Length > 0;
        }

        private static ScanNodeProperties FindNearestScanNode(Vector3 position, float radius)
        {
            ScanNodeProperties best = null;
            float bestDistance = float.PositiveInfinity;
            int hitCount = Physics.OverlapSphereNonAlloc(position, radius, _overlapHits, _scanNodeMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _overlapHits[i];
                if (hit == null) continue;
                ScanNodeProperties node = hit.GetComponent<ScanNodeProperties>() ?? hit.GetComponentInParent<ScanNodeProperties>();
                TryChooseNearestNode(node, position, ref best, ref bestDistance);
            }

            if (best != null) return best;

            ScanNodeProperties[] nodes = UnityEngine.Object.FindObjectsOfType<ScanNodeProperties>(includeInactive: true);
            float maxDistanceSq = radius * radius;
            for (int i = 0; i < nodes.Length; i++)
            {
                ScanNodeProperties node = nodes[i];
                if (node == null) continue;
                float distanceSq = (node.transform.position - position).sqrMagnitude;
                if (distanceSq > maxDistanceSq || distanceSq >= bestDistance) continue;
                bestDistance = distanceSq;
                best = node;
            }

            return best;
        }

        private static void TryChooseNearestNode(ScanNodeProperties node, Vector3 position, ref ScanNodeProperties best, ref float bestDistance)
        {
            if (node == null) return;
            float distanceSq = (node.transform.position - position).sqrMagnitude;
            if (distanceSq >= bestDistance) return;
            bestDistance = distanceSq;
            best = node;
        }

        private static GameObject ResolveTargetRoot(ScanNodeProperties node)
        {
            if (node == null) return null;

            EnemyAI enemy = node.GetComponentInParent<EnemyAI>(includeInactive: true);
            if (enemy != null) return enemy.gameObject;

            GrabbableObject item = node.GetComponentInParent<GrabbableObject>(includeInactive: true);
            if (item != null) return item.gameObject;

            Turret turret = node.GetComponentInParent<Turret>(includeInactive: true);
            if (turret != null) return turret.gameObject;

            Landmine landmine = node.GetComponentInParent<Landmine>(includeInactive: true);
            if (landmine != null) return landmine.gameObject;

            SpikeRoofTrap spikeTrap = node.GetComponentInParent<SpikeRoofTrap>(includeInactive: true);
            if (spikeTrap != null) return spikeTrap.gameObject;

            PlayerControllerB player = node.GetComponentInParent<PlayerControllerB>(includeInactive: true);
            if (player != null) return player.gameObject;

            Transform parent = node.transform.parent;
            return parent != null ? parent.gameObject : node.gameObject;
        }

        private static GameObject ResolveFallbackRoot(Vector3 position, float radius)
        {
            float searchRadius = Mathf.Max(radius, TARGET_SEARCH_RADIUS);
            int hitCount = Physics.OverlapSphereNonAlloc(position, searchRadius, _overlapHits, ~0, QueryTriggerInteraction.Collide);
            GameObject bestRoot = null;
            float bestDistance = float.PositiveInfinity;

            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _overlapHits[i];
                if (hit == null) continue;

                GameObject root = ResolveKnownRoot(hit.transform);
                if (root == null) continue;

                float distanceSq = (root.transform.position - position).sqrMagnitude;
                if (distanceSq >= bestDistance) continue;
                bestDistance = distanceSq;
                bestRoot = root;
            }

            return bestRoot;
        }

        private static GameObject ResolveKnownRoot(Transform transform)
        {
            if (transform == null) return null;

            EnemyAI enemy = transform.GetComponentInParent<EnemyAI>(includeInactive: true);
            if (enemy != null) return enemy.gameObject;

            GrabbableObject item = transform.GetComponentInParent<GrabbableObject>(includeInactive: true);
            if (item != null) return item.gameObject;

            Turret turret = transform.GetComponentInParent<Turret>(includeInactive: true);
            if (turret != null) return turret.gameObject;

            Landmine landmine = transform.GetComponentInParent<Landmine>(includeInactive: true);
            if (landmine != null) return landmine.gameObject;

            SpikeRoofTrap spikeTrap = transform.GetComponentInParent<SpikeRoofTrap>(includeInactive: true);
            return spikeTrap != null ? spikeTrap.gameObject : null;
        }

        private static int ResolveScanNodeMask()
        {
            int scanNodeLayer = LayerMask.NameToLayer("ScanNode");
            if (scanNodeLayer >= 0) return 1 << scanNodeLayer;
            return FALLBACK_SCAN_NODE_MASK;
        }

        private static void SetColorIfExists(Material material, string property, Color value)
        {
            if (material != null && material.HasProperty(property)) material.SetColor(property, value);
        }

        private static void SetFloatIfExists(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static void SetTextureIfExists(Material material, string property, Texture value)
        {
            if (material != null && material.HasProperty(property)) material.SetTexture(property, value);
        }
    }
}
