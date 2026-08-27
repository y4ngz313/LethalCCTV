using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Mainframe
{
    /// <summary>
    /// Owns everything the mainframe does with light: the authored emissive LED and
    /// shelf-strip materials flicker, and the fixture lights those emissives imply are
    /// created at runtime and flicker with them off the same clock, so a strip and the
    /// light it casts dim together instead of drifting apart.
    /// </summary>
    internal sealed class MainframeLightFlicker : MonoBehaviour
    {
        private const float MinMultiplier = 0.72f;
        private const float MaxMultiplier = 1.18f;
        private const float DropEveryMin = 1.8f;
        private const float DropEveryMax = 4.2f;

        // A material is a lamp when its authored emissive is non-black; everything else
        // is housing. In the shipped bundle that split is unambiguous — the four lamp
        // materials (LED_Green/Red/Amber, ShelfLight) carry HDR emissives peaking at
        // 25-60, and the other thirteen are exactly zero.
        //
        // The previous test required a bound _EmissiveColorMap/_EmissionMap instead.
        // No material in the prop binds either — the source pack only ever had albedo —
        // so _slots stayed empty and Update() returned on its first line: this component
        // had never once run. HDRP/Lit reads _EmissiveColor directly and treats a missing
        // map as white, so a bound map was never what made these materials emit. Matching
        // on the emissive value is also what the nearest sibling does
        // (InteriorAlarmFixture.CaptureNativeEmissiveBaseline).
        private const float EmissiveLampThreshold = 0.001f;

        // Fixture lights. The shipped prefab carries no Light on any of its 38 nodes, and
        // HDRP emission lights only the emitting pixels — it spills onto nothing. So the
        // cabinet interior received room ambient and the player's flashlight and nothing
        // else, which is why the rack gear reads black however good its textures are.
        // Runtime creation is the mod's established pattern for this (nearest sibling:
        // Facility/Security/InteriorAlarmFixture.cs).
        private const string ShelfStripObjectToken = "ShelfLight";
        private const string ScreenObjectToken = "ScreenSurface";

        // HDRP stores the rendered intensity on HDAdditionalLightData in lumens; the
        // legacy Light.intensity scale is far smaller. InteriorAlarmFixture calibrates a
        // vanilla facility fixture at 4000 HDRP / 12 legacy, so these are sized against
        // that: an under-shelf strip and a monitor are local sources, not room lights.
        private const float ShelfStripLumens = 300f;
        private const float ShelfStripLegacy = 0.90f;
        private const float ScreenGlowLumens = 140f;
        private const float ScreenGlowLegacy = 0.42f;
        private const float InteriorFillLumens = 80f;
        private const float InteriorFillLegacy = 0.24f;

        // Shadows stay off, as on every other fixture light in the mod: a shadow-casting
        // light inside a 110k-triangle prop is expensive and self-shadows badly. The cost
        // is that light leaks through the cabinet shell, so these are spot cones with
        // modest range rather than bare point lights wherever direction is meaningful.
        private const float ShelfStripRange = 2.2f;
        private const float ShelfStripAngle = 110f;
        private const float ScreenGlowRange = 1.6f;
        private const float ScreenGlowAngle = 80f;
        private const float InteriorFillRange = 1.5f;

        private static readonly Color ShelfStripFallbackTint = new Color(1f, 0.85f, 0.60f, 1f);
        private static readonly Color ScreenGlowTint = new Color(0.62f, 0.78f, 1f, 1f);
        private static readonly Color InteriorFillTint = new Color(0.72f, 0.78f, 0.82f, 1f);

        private readonly List<MaterialSlot> _slots = new List<MaterialSlot>();
        private readonly List<FixtureLight> _lights = new List<FixtureLight>();
        private float _seed;
        private float _nextDropTime;
        private float _dropBlend;
        private bool _lightsBuilt;

        private void OnEnable()
        {
            // Wrapped into Perlin's own domain: an unwrapped instance ID lands in the
            // millions, where float precision is coarser than the noise period and the
            // "random" phase stops varying.
            _seed = Mathf.Repeat(GetInstanceID() * 0.137f, 256f);
            _nextDropTime = Time.unscaledTime + Random.Range(DropEveryMin, DropEveryMax);
            CollectEmissiveMaterials();
            BuildFixtureLights();
        }

        private void OnDisable()
        {
            for (int i = 0; i < _slots.Count; i++)
                _slots[i].Restore();
        }

        private void Update()
        {
            if (_slots.Count == 0 && _lights.Count == 0)
                return;

            float now = Time.unscaledTime;
            if (now >= _nextDropTime)
            {
                _dropBlend = 1f;
                _nextDropTime = now + Random.Range(DropEveryMin, DropEveryMax);
            }

            _dropBlend = Mathf.MoveTowards(_dropBlend, 0f, Time.unscaledDeltaTime * 6f);

            float slow = Mathf.PerlinNoise(_seed, now * 0.65f);
            float quick = Mathf.PerlinNoise(_seed + 19.37f, now * 3.1f);
            float noise = 0.88f + slow * 0.22f + (quick - 0.5f) * 0.08f;
            float drop = Mathf.Lerp(1f, 0.68f, _dropBlend);
            float multiplier = Mathf.Clamp(noise * drop, MinMultiplier, MaxMultiplier);

            for (int i = 0; i < _slots.Count; i++)
                _slots[i].Apply(multiplier);
            // Rewritten every frame on purpose: HDRP creates HDAdditionalLightData lazily
            // for a light added at runtime and seeds it with its own default intensity, so
            // a value written once at spawn is silently replaced on the frame HDRP catches
            // up. Re-asserting each frame means our value always wins, and it costs three
            // property writes.
            for (int i = 0; i < _lights.Count; i++)
                _lights[i].Apply(multiplier);
        }

        private void CollectEmissiveMaterials()
        {
            _slots.Clear();

            Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                // sharedMaterials, not materials: the latter instantiates a private copy
                // per renderer on first access, so the flicker would drive throwaway
                // clones while everything else in the mod still reads the shared asset.
                // That makes deduplication this component's job — several shell parts do
                // share one material.
                Material[] materials = renderer.sharedMaterials;
                if (materials == null)
                    continue;

                for (int j = 0; j < materials.Length; j++)
                {
                    Material material = materials[j];
                    if (!IsMainframeEmissionMaterial(material) || ContainsMaterial(material))
                        continue;

                    material.EnableKeyword("_EMISSION");
                    _slots.Add(new MaterialSlot(material));
                }
            }
        }

        private bool ContainsMaterial(Material material)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Owns(material))
                    return true;
            }
            return false;
        }

        private static bool IsMainframeEmissionMaterial(Material material)
        {
            if (material == null)
                return false;

            return ReadEmissive(material).maxColorComponent > EmissiveLampThreshold;
        }

        private static Color ReadEmissive(Material material)
        {
            if (material.HasProperty("_EmissiveColor"))
                return material.GetColor("_EmissiveColor");
            if (material.HasProperty("_EmissionColor"))
                return material.GetColor("_EmissionColor");
            return Color.black;
        }

        private void BuildFixtureLights()
        {
            if (_lightsBuilt)
                return;
            _lightsBuilt = true;

            Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            Renderer shelfStrip = null;
            Renderer screen = null;
            Bounds propBounds = default;
            bool hasBounds = false;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                string name = renderer.gameObject.name ?? string.Empty;
                if (shelfStrip == null && name.IndexOf(ShelfStripObjectToken, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    shelfStrip = renderer;
                if (screen == null && name.IndexOf(ScreenObjectToken, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    screen = renderer;

                EncapsulateLocal(renderer, ref propBounds, ref hasBounds);
            }

            Vector3 propCenter = hasBounds ? propBounds.center : new Vector3(0f, 1.2f, 0.4f);

            // AXIS NOTE. These offsets are in the fixture root's space, where the cabinet
            // FRONT IS -Z: the prefab parents the model under a MainframeModel child
            // carrying a 180-degree Y rotation, so the mesh's own -Z back wall lands at
            // +Z here. Read straight off the mesh you get the opposite sign and the
            // screen light ends up shining into the back of the cabinet. The player root
            // anchor sits at z -1.05 and the back wall at z +0.85, which is the check.
            //
            // Under-shelf strip, pushed a little below and deeper into the cabinet than
            // its own geometry so it sits in the cavity and throws down the rack face
            // rather than grazing the shelf lip it is mounted to. This is the light that
            // reaches the patch panels and server units at the bottom of the rack.
            Vector3 shelfAnchor = shelfStrip != null
                ? LocalCenter(shelfStrip) + new Vector3(0f, -0.03f, 0.18f)
                : new Vector3(propCenter.x, propCenter.y + 0.4f, propCenter.z);
            Color shelfTint = shelfStrip != null
                ? NormalizeTint(ReadEmissive(shelfStrip.sharedMaterial), ShelfStripFallbackTint)
                : ShelfStripFallbackTint;
            AddSpot("MainframeShelfStripLight", shelfAnchor, Quaternion.Euler(90f, 0f, 0f), shelfTint,
                ShelfStripRange, ShelfStripAngle, ShelfStripLumens, ShelfStripLegacy, flickerAmount: 1f);

            // Monitor spill, in front of the screen plane and aimed out of the cabinet at
            // the player. The screen is recessed ~0.5 m into the rack, so without this the
            // console and the keyboard the player is looking at catch nothing from it.
            if (screen != null)
            {
                AddSpot("MainframeScreenGlowLight", LocalCenter(screen) + new Vector3(0f, 0f, -0.15f),
                    Quaternion.Euler(0f, 180f, 0f), ScreenGlowTint, ScreenGlowRange, ScreenGlowAngle,
                    ScreenGlowLumens, ScreenGlowLegacy, flickerAmount: 0.35f);
            }

            // Interior fill so the rack gear is not lit purely by whatever the player is
            // pointing a flashlight at. Deliberately the dimmest of the three.
            AddPoint("MainframeInteriorFillLight", propCenter, InteriorFillTint,
                InteriorFillRange, InteriorFillLumens, InteriorFillLegacy, flickerAmount: 1f);

            // This component silently did nothing at all for its entire shipped life, so
            // it now says what it found. Zero on either count means it is dead again.
            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts] MAINFRAME_LIGHTING emissive materials={_slots.Count} fixture lights={_lights.Count} " +
                $"shelfStrip={(shelfStrip != null ? shelfStrip.gameObject.name : "none")} screen={(screen != null ? screen.gameObject.name : "none")}.");
        }

        private void AddSpot(string name, Vector3 localPosition, Quaternion localRotation, Color color,
            float range, float spotAngle, float lumens, float legacyIntensity, float flickerAmount)
        {
            Light light = CreateLight(name, localPosition, localRotation, color, range, lumens, legacyIntensity, flickerAmount);
            if (light == null)
                return;
            light.type = LightType.Spot;
            light.spotAngle = spotAngle;
        }

        private void AddPoint(string name, Vector3 localPosition, Color color,
            float range, float lumens, float legacyIntensity, float flickerAmount)
        {
            Light light = CreateLight(name, localPosition, Quaternion.identity, color, range, lumens, legacyIntensity, flickerAmount);
            if (light == null)
                return;
            light.type = LightType.Point;
        }

        // Reuses a light of the same name if one is already there instead of adding a
        // second. The component is attached to the shared prefab and every spawn clones
        // it, so a set of lights created once can arrive already attached while the
        // clone's own bookkeeping starts empty — without this that path silently doubles
        // the fixture lighting on every instance.
        private Light CreateLight(string name, Vector3 localPosition, Quaternion localRotation, Color color,
            float range, float lumens, float legacyIntensity, float flickerAmount)
        {
            Transform existing = transform.Find(name);
            GameObject host;
            if (existing != null)
            {
                host = existing.gameObject;
            }
            else
            {
                host = new GameObject(name);
                host.transform.SetParent(transform, worldPositionStays: false);
            }

            host.transform.localPosition = localPosition;
            host.transform.localRotation = localRotation;
            host.transform.localScale = Vector3.one;

            Light light = host.GetComponent<Light>();
            if (light == null)
                light = host.AddComponent<Light>();
            light.color = color;
            light.range = range;
            light.shadows = LightShadows.None;
            light.intensity = legacyIntensity;

            _lights.Add(new FixtureLight(light, lumens, legacyIntensity, flickerAmount));
            return light;
        }

        private Vector3 LocalCenter(Renderer renderer)
        {
            Vector3 world = renderer.localToWorldMatrix.MultiplyPoint3x4(renderer.localBounds.center);
            return transform.InverseTransformPoint(world);
        }

        // Local bounds transformed through the renderer and back into this transform's
        // space, so a rotated prop does not inflate the box the way world-space
        // Renderer.bounds would.
        private void EncapsulateLocal(Renderer renderer, ref Bounds bounds, ref bool hasBounds)
        {
            Bounds local = renderer.localBounds;
            Vector3 center = local.center;
            Vector3 extents = local.extents;
            Matrix4x4 toWorld = renderer.localToWorldMatrix;

            for (int corner = 0; corner < 8; corner++)
            {
                var offset = new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                Vector3 point = transform.InverseTransformPoint(toWorld.MultiplyPoint3x4(center + offset));

                if (!hasBounds)
                {
                    bounds = new Bounds(point, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(point);
                }
            }
        }

        private static Color NormalizeTint(Color color, Color fallback)
        {
            float max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            if (max <= 0.0001f)
                return fallback;
            return new Color(color.r / max, color.g / max, color.b / max, 1f);
        }

        // One runtime fixture light and the intensity it should hold. HDRP and the legacy
        // pipeline disagree by more than two orders of magnitude on what Light.intensity
        // means, so both values are carried and the live one is picked by whether HDRP has
        // attached its data yet.
        private sealed class FixtureLight
        {
            private readonly Light _light;
            private readonly float _lumens;
            private readonly float _legacyIntensity;
            private readonly float _flickerAmount;
            private HDAdditionalLightData _hd;

            public FixtureLight(Light light, float lumens, float legacyIntensity, float flickerAmount)
            {
                _light = light;
                _lumens = lumens;
                _legacyIntensity = legacyIntensity;
                _flickerAmount = Mathf.Clamp01(flickerAmount);
            }

            public void Apply(float multiplier)
            {
                if (_light == null)
                    return;

                if (_hd == null)
                    _hd = _light.GetComponent<HDAdditionalLightData>();

                float scaled = Mathf.Lerp(1f, multiplier, _flickerAmount);
                if (_hd != null)
                    _hd.intensity = _lumens * scaled;
                else
                    _light.intensity = _legacyIntensity * scaled;
            }
        }

        private sealed class MaterialSlot
        {
            private readonly Material _material;
            private readonly Color _emissiveColor;
            private readonly Color _emissionColor;
            private readonly Color _emissiveColorLdr;
            private readonly bool _hasEmissiveColor;
            private readonly bool _hasEmissionColor;
            private readonly bool _hasEmissiveColorLdr;

            // Each colour is captured only when the material actually authored one. The
            // old code substituted white for a missing or black property and then wrote
            // that back scaled, which would have pushed a white emission into slots the
            // artist had left at zero — harmless while HDRP ignores them at runtime, and
            // a trap the moment anything recomputes the material.
            public MaterialSlot(Material material)
            {
                _material = material;
                _hasEmissiveColor = TryReadColor(material, "_EmissiveColor", out _emissiveColor);
                _hasEmissionColor = TryReadColor(material, "_EmissionColor", out _emissionColor);
                _hasEmissiveColorLdr = TryReadColor(material, "_EmissiveColorLDR", out _emissiveColorLdr);
            }

            public bool Owns(Material material)
            {
                return ReferenceEquals(_material, material);
            }

            // Only the colours are scaled. _EmissiveIntensity and _UseEmissiveIntensity are
            // deliberately left alone: HDRP/Lit reads _EmissiveColor as the final HDR
            // emission at runtime and those two are an editor-side convenience that bakes
            // into it. The prop authors _UseEmissiveIntensity=0 with _EmissiveIntensity=0,
            // so forcing the flag on — as this component used to — would arm a later
            // HDMaterial.ValidateMaterial to recompute _EmissiveColor as LDR x 0 and put
            // every LED out.
            public void Apply(float multiplier)
            {
                Write(multiplier);
            }

            public void Restore()
            {
                Write(1f);
            }

            private void Write(float multiplier)
            {
                if (_material == null)
                    return;

                if (_hasEmissiveColor)
                    _material.SetColor("_EmissiveColor", _emissiveColor * multiplier);
                if (_hasEmissionColor)
                    _material.SetColor("_EmissionColor", _emissionColor * multiplier);
                if (_hasEmissiveColorLdr)
                    _material.SetColor("_EmissiveColorLDR", _emissiveColorLdr * multiplier);
            }

            private static bool TryReadColor(Material material, string property, out Color color)
            {
                color = Color.black;
                if (material == null || !material.HasProperty(property))
                    return false;

                color = material.GetColor(property);
                return color.maxColorComponent > EmissiveLampThreshold;
            }
        }
    }
}
