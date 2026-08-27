using System.Collections.Generic;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Facility.Security
{
    // Locates the vanilla red light-bar art that ships above interior fire
    // exits so alarm fixtures can wear it instead of the bundled siren box.
    // The bar is prefab art baked into the interior dungeon assets (no game
    // code references it), so the only reliable handle is a scene scan near
    // an inside fire exit after generation. Every surviving candidate is
    // logged (FIREEXIT_LIGHTBAR_SCAN) so a wrong heuristic pick can be
    // corrected from a single round's log.
    internal static class FireExitLightBarLocator
    {
        private const float ScanRadiusM = 8f;
        private const float MinHeightAboveExitM = 1.2f;
        private const float MaxHeightAboveExitM = 5f;
        // Relaxed second pass. The strict band above assumes the inside
        // teleport pad sits on the floor directly under the bar; interiors
        // that park the pad off to one side (or a step below/above the door)
        // push every renderer out of the band and the strict pass evaluates
        // literally nothing. Retrying wider is cheaper than falling back.
        private const float RelaxedScanRadiusM = 20f;
        private const float RelaxedMinHeightAboveExitM = 0.4f;
        private const float RelaxedMaxHeightAboveExitM = 6.5f;
        private const float MaxTemplateVolumeM3 = 1.25f;
        private const int MinAcceptScore = 4;
        private const int MaxScanLogLines = 40;
        private const string TemplateCacheHolderName = "LGU_CCTV_LightBarTemplateCache";
        // Vanilla fire-exit door art. Every interior that spawns a fire exit
        // — vanilla Factory included — instantiates the stock
        // `AlleyExitDoorContainer` prop, whose `Light` child IS the red bar
        // this locator wants. Anchoring on that name finds it without
        // depending on where the paired EntranceTeleport pad happens to sit,
        // which is what the proximity scan gets wrong on vanilla layouts.
        private const string VanillaDoorContainerToken = "alleyexitdoorcontainer";
        private const string VanillaLightChildToken = "light";
        private const string DungeonRootToken = "levelgenerationroot";
        private const int MaxTransientRescans = 3;

        private static GameObject _template;
        private static GameObject _templateCacheHolder;
        private static GameObject _cachedTemplate;
        private static bool _cachedTemplateIsProcedural;
        private static bool _searched;
        private static int _transientRescansUsed;

        internal static void ResetRound()
        {
            _template = null;
            _searched = false;
            _transientRescansUsed = 0;
        }

        internal static bool HasCachedSceneTemplate =>
            _cachedTemplate != null && !_cachedTemplateIsProcedural;

        internal static bool HasAnyInsideTeleport(out int totalCount, out int insideCount)
        {
            EntranceTeleport[] teleports = Object.FindObjectsOfType<EntranceTeleport>(includeInactive: true);
            totalCount = teleports.Length;
            insideCount = 0;
            for (int i = 0; i < teleports.Length; i++)
            {
                EntranceTeleport teleport = teleports[i];
                if (teleport != null && !teleport.isEntranceToBuilding)
                    insideCount++;
            }
            return insideCount > 0;
        }

        internal static GameObject TryGetTemplate()
        {
            if (_searched) return _template;
            _searched = true;
            _template = FindTemplate();
            return _template;
        }

        // Clone carries only render geometry: scripts, colliders, and lights
        // are stripped (InteriorAlarmFixture adds its own light/audio), and
        // materials are instanced so the alarm's emissive pulse can never
        // touch the shared vanilla material used by the real fire exits.
        internal static GameObject BuildClone()
        {
            GameObject template = TryGetTemplate();
            if (template == null) return null;

            GameObject clone = Object.Instantiate(template);
            clone.name = "LGU_CCTV_WallAlarm";
            clone.transform.localScale = template.transform.lossyScale;

            // Keep render geometry AND the bar's own vanilla light (plus its
            // HDRP light data) so the fixture can drive the authentic glow
            // instead of bolting on a synthetic point light; strip only
            // scripts, colliders, and audio.
            Component[] components = clone.GetComponentsInChildren<Component>(includeInactive: true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;
                if (component is Transform || component is MeshFilter || component is MeshRenderer) continue;
                if (component is Light) continue;
                if (component.GetType().Name == "HDAdditionalLightData") continue;
                Object.Destroy(component);
            }

            MeshRenderer[] renderers = clone.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                if (materials == null) continue;
                for (int m = 0; m < materials.Length; m++)
                {
                    if (materials[m] != null)
                        materials[m] = new Material(materials[m]) { name = $"{materials[m].name}_AlarmInstance" };
                }
                renderer.sharedMaterials = materials;
            }

            clone.SetActive(true);
            return clone;
        }

        // Bar frame in template-local space, world-scaled: thin axis is the
        // wall normal, up axis the vertical, and half sizes let the spawner
        // seat the bar flush above a doorway. Encapsulates EVERY MeshFilter
        // in the template hierarchy — multi-mesh templates (cage + lens)
        // undersized the bar when only the first mesh was measured, which
        // sank seated bars into doorframes. Fails on near-cubic meshes
        // where the axis ranking would be arbitrary.
        internal static bool TryComputeBarFrame(out Vector3 thinAxisLocal, out Vector3 upAxisLocal, out Vector3 halfSize)
        {
            thinAxisLocal = Vector3.forward;
            upAxisLocal = Vector3.up;
            halfSize = new Vector3(0.5f, 0.12f, 0.08f);

            GameObject template = TryGetTemplate();
            if (template == null) return false;

            MeshFilter[] filters = template.GetComponentsInChildren<MeshFilter>(includeInactive: true);
            Bounds combined = default;
            bool hasBounds = false;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;

                // Child-mesh bounds mapped into template-local space via the
                // child's local pose relative to the template root.
                Matrix4x4 childToTemplate = template.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                Bounds meshBounds = mesh.bounds;
                Vector3 c = meshBounds.center;
                Vector3 e = meshBounds.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = new Vector3(
                        c.x + ((corner & 1) == 0 ? -e.x : e.x),
                        c.y + ((corner & 2) == 0 ? -e.y : e.y),
                        c.z + ((corner & 4) == 0 ? -e.z : e.z));
                    Vector3 inTemplate = childToTemplate.MultiplyPoint3x4(local);
                    if (!hasBounds)
                    {
                        combined = new Bounds(inTemplate, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        combined.Encapsulate(inTemplate);
                    }
                }
            }
            if (!hasBounds) return false;

            Vector3 scale = template.transform.lossyScale;
            Vector3 extents = new Vector3(
                Mathf.Abs(combined.extents.x * scale.x),
                Mathf.Abs(combined.extents.y * scale.y),
                Mathf.Abs(combined.extents.z * scale.z));

            int longAxis = 0, thinAxis = 0;
            for (int i = 1; i < 3; i++)
            {
                if (extents[i] > extents[longAxis]) longAxis = i;
                if (extents[i] < extents[thinAxis]) thinAxis = i;
            }
            if (longAxis == thinAxis || extents[longAxis] < extents[thinAxis] * 1.6f)
                return false;
            int midAxis = 3 - longAxis - thinAxis;

            thinAxisLocal = AxisVector(thinAxis);
            upAxisLocal = AxisVector(midAxis);
            halfSize = new Vector3(extents[longAxis], extents[midAxis], extents[thinAxis]);
            return true;
        }

        private static Vector3 AxisVector(int axis)
        {
            switch (axis)
            {
                case 0: return Vector3.right;
                case 1: return Vector3.up;
                default: return Vector3.forward;
            }
        }

        private static GameObject FindTemplate()
        {
            // 1. Name-anchored lookup for the stock fire-exit door lamp. This
            //    is position-independent, so it survives interiors where the
            //    inside teleport pad is nowhere near the door art.
            GameObject vanilla = FindVanillaFireExitLightBar();
            if (vanilla != null)
            {
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_TEMPLATE source=vanilla-fireexit-prop path='{vanilla.transform.GetHierarchyPath()}'.");
                CacheSceneTemplate(vanilla);
                return vanilla;
            }

            // 2. Proximity scan for interiors whose fire-exit art is bespoke.
            List<Vector3> fireExits = FindInsideFireExitPositions();
            if (fireExits.Count == 0)
            {
                return ResolveFallbackTemplate(
                    "no inside-side teleports and no vanilla fire-exit door prop in scene",
                    transient: !HasInteriorGeometry());
            }

            int evaluated = ScanForBestCandidate(
                fireExits, ScanRadiusM, MinHeightAboveExitM, MaxHeightAboveExitM,
                out GameObject best, out int bestScore);
            string pass = "strict";
            if (best == null || bestScore < MinAcceptScore)
            {
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_SCAN_SUMMARY pass=strict evaluated={evaluated} best={bestScore} need={MinAcceptScore}.");
                int relaxedEvaluated = ScanForBestCandidate(
                    fireExits, RelaxedScanRadiusM, RelaxedMinHeightAboveExitM, RelaxedMaxHeightAboveExitM,
                    out GameObject relaxedBest, out int relaxedScore);
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_SCAN_SUMMARY pass=relaxed evaluated={relaxedEvaluated} best={relaxedScore} need={MinAcceptScore}.");
                if (relaxedBest != null && relaxedScore >= MinAcceptScore)
                {
                    best = relaxedBest;
                    bestScore = relaxedScore;
                    pass = "relaxed";
                }
                else
                {
                    // Zero evaluated candidates in BOTH passes means the gate
                    // never even saw geometry: either the interior has not
                    // spawned yet, or the teleport pads are not in it.
                    bool nothingEvaluated = evaluated == 0 && relaxedEvaluated == 0;
                    bool noInterior = !HasInteriorGeometry();
                    string reason = noInterior
                        ? "scan ran before interior geometry existed (no LevelGenerationRoot renderers)"
                        : nothingEvaluated
                            ? $"scan gate matched no renderer near {fireExits.Count} inside teleport(s) (positions are not in the interior)"
                            : $"scan found no candidate at score {MinAcceptScore} (best={Mathf.Max(bestScore, relaxedScore)})";
                    return ResolveFallbackTemplate(reason, transient: noInterior);
                }
            }

            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_TEMPLATE source=scan-{pass} score={bestScore} path='{best.transform.GetHierarchyPath()}'.");
            CacheSceneTemplate(best);
            return best;
        }

        // Returns the number of renderers that actually passed the geometric
        // gate and were scored, so a zero-candidate scan can be told apart
        // from a scan that found only weak candidates.
        private static int ScanForBestCandidate(
            List<Vector3> fireExits,
            float radiusM,
            float minHeightM,
            float maxHeightM,
            out GameObject best,
            out int bestScore)
        {
            best = null;
            bestScore = 0;
            float bestPlanar = float.MaxValue;
            int logged = 0;
            int evaluated = 0;

            MeshRenderer[] renderers = Object.FindObjectsOfType<MeshRenderer>();
            for (int r = 0; r < renderers.Length; r++)
            {
                MeshRenderer renderer = renderers[r];
                if (renderer == null || !renderer.enabled) continue;

                Vector3 center = renderer.bounds.center;
                float planar = float.MaxValue;
                float heightAbove = 0f;
                for (int e = 0; e < fireExits.Count; e++)
                {
                    Vector3 exit = fireExits[e];
                    float dy = center.y - exit.y;
                    if (dy < minHeightM || dy > maxHeightM) continue;
                    float d = Vector2.Distance(new Vector2(center.x, center.z), new Vector2(exit.x, exit.z));
                    if (d < planar)
                    {
                        planar = d;
                        heightAbove = dy;
                    }
                }
                if (planar > radiusM) continue;

                string path = renderer.transform.GetHierarchyPath();
                string lowerPath = path.ToLowerInvariant();
                if (IsOwnAsset(lowerPath)) continue;

                evaluated++;
                int score = ScoreCandidate(renderer, lowerPath);
                if (logged < MaxScanLogLines)
                {
                    Vector3 size = renderer.bounds.size;
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_SCAN score={score} dist={planar:0.0}m dy={heightAbove:0.0}m size=({size.x:0.00},{size.y:0.00},{size.z:0.00}) path='{path}'");
                    logged++;
                }

                if (score > bestScore || (score == bestScore && score > 0 && planar < bestPlanar))
                {
                    bestScore = score;
                    bestPlanar = planar;
                    best = renderer.gameObject;
                }
            }

            return evaluated;
        }

        // The stock fire-exit door lamp, found by prefab name rather than by
        // proximity. Inactive renderers are included: the door prop can be
        // parented under a deactivated blocker on some flows, and the mesh
        // data (which is all BuildClone and TryComputeBarFrame need) is valid
        // either way. Prefers a candidate carrying its own Light so the
        // fixture can drive the authentic glow.
        private static GameObject FindVanillaFireExitLightBar()
        {
            MeshRenderer[] renderers = Object.FindObjectsOfType<MeshRenderer>(includeInactive: true);
            GameObject best = null;
            bool bestHasLight = false;
            int matches = 0;

            for (int r = 0; r < renderers.Length; r++)
            {
                MeshRenderer renderer = renderers[r];
                if (renderer == null) continue;

                string lowerName = (renderer.name ?? string.Empty).ToLowerInvariant();
                if (!lowerName.Contains(VanillaLightChildToken)) continue;
                if (!HasAncestorNameContaining(renderer.transform, VanillaDoorContainerToken)) continue;

                string path = renderer.transform.GetHierarchyPath();
                if (IsOwnAsset(path.ToLowerInvariant())) continue;

                matches++;
                bool hasLight = renderer.GetComponentInChildren<Light>(includeInactive: true) != null;
                if (matches <= MaxScanLogLines)
                {
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_VANILLA_MATCH light={hasLight} active={renderer.gameObject.activeInHierarchy} path='{path}'");
                }

                if (best == null || (hasLight && !bestHasLight))
                {
                    best = renderer.gameObject;
                    bestHasLight = hasLight;
                }
            }

            return best;
        }

        private static bool IsOwnAsset(string lowerPath) =>
            lowerPath.Contains("lethalcctv") || lowerPath.Contains("lgu_cctv");

        // Ancestor-name probe that avoids allocating a full hierarchy path
        // for every renderer in the scene.
        private static bool HasAncestorNameContaining(Transform transform, string lowerToken)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if ((current.name ?? string.Empty).ToLowerInvariant().Contains(lowerToken))
                    return true;
            }
            return false;
        }

        // Cheap "has the interior spawned yet" probe used only to classify a
        // failure as transient (retry later) vs. genuine (this interior has
        // no reusable bar).
        private static bool HasInteriorGeometry()
        {
            MeshRenderer[] renderers = Object.FindObjectsOfType<MeshRenderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null) continue;
                if (HasAncestorNameContaining(renderer.transform, DungeonRootToken))
                    return true;
            }
            return false;
        }

        private static GameObject ResolveFallbackTemplate(string reason, bool transient)
        {
            if (_cachedTemplate != null && !_cachedTemplateIsProcedural)
            {
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_FALLBACK using cross-round scene cache ({reason}).");
                return _cachedTemplate;
            }

            // A transient miss must not latch: let a later fixture re-run the
            // lookup once the interior exists, instead of freezing the whole
            // round onto the procedural bar after one early call.
            if (transient && _transientRescansUsed < MaxTransientRescans)
            {
                _transientRescansUsed++;
                _searched = false;
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_RETRY {reason}; rescan {_transientRescansUsed}/{MaxTransientRescans} armed for the next fixture.");
            }

            if (_cachedTemplate != null)
            {
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_FALLBACK using procedural cache ({reason}).");
                return _cachedTemplate;
            }

            GameObject procedural = BuildProceduralTemplate();
            if (procedural != null)
            {
                _cachedTemplate = procedural;
                _cachedTemplateIsProcedural = true;
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_FALLBACK using procedural light bar because {reason}.");
            }

            return procedural;
        }

        private static void CacheSceneTemplate(GameObject source)
        {
            if (source == null || (_cachedTemplate != null && !_cachedTemplateIsProcedural))
                return;

            GameObject holder = EnsureTemplateCacheHolder();
            if (holder == null)
                return;

            GameObject cached = Object.Instantiate(source, holder.transform, false);
            if (cached == null)
                return;

            cached.name = "SceneLightBarTemplate";
            cached.transform.localPosition = Vector3.zero;
            cached.transform.localRotation = Quaternion.identity;
            cached.transform.localScale = source.transform.lossyScale;
            cached.SetActive(false);

            if (_cachedTemplate != null)
                Object.Destroy(_cachedTemplate);

            _cachedTemplate = cached;
            _cachedTemplateIsProcedural = false;
            CctvModuleConfig.Log?.LogInfo(
                "[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_CACHE stored scene template for later rounds.");
        }

        private static GameObject BuildProceduralTemplate()
        {
            GameObject holder = EnsureTemplateCacheHolder();
            if (holder == null)
                return null;

            GameObject root = new GameObject("ProceduralLightBarTemplate");
            root.transform.SetParent(holder.transform, worldPositionStays: false);

            GameObject housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
            housing.name = "Housing";
            housing.transform.SetParent(root.transform, worldPositionStays: false);
            housing.transform.localScale = new Vector3(0.9f, 0.18f, 0.12f);
            AssignProceduralMaterial(
                housing.GetComponent<MeshRenderer>(),
                "HDRP/Lit",
                new Color(0.055f, 0.06f, 0.065f, 1f),
                Color.black,
                "LGU_CCTV_ProceduralAlarmHousing");

            GameObject lens = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lens.name = "RedLens";
            lens.transform.SetParent(root.transform, worldPositionStays: false);
            // The spawner maps the bar's -thin axis into the room (vanilla
            // glow face convention), so the lens must protrude on -z.
            lens.transform.localPosition = new Vector3(0f, 0f, -0.07f);
            lens.transform.localScale = new Vector3(0.78f, 0.085f, 0.025f);
            AssignProceduralMaterial(
                lens.GetComponent<MeshRenderer>(),
                "HDRP/Unlit",
                new Color(0.45f, 0.01f, 0.005f, 1f),
                new Color(3.5f, 0.015f, 0.008f, 1f),
                "LGU_CCTV_ProceduralAlarmLens");

            root.SetActive(false);
            return root;
        }

        private static GameObject EnsureTemplateCacheHolder()
        {
            if (_templateCacheHolder != null)
                return _templateCacheHolder;

            _templateCacheHolder = new GameObject(TemplateCacheHolderName);
            Object.DontDestroyOnLoad(_templateCacheHolder);
            _templateCacheHolder.SetActive(false);
            return _templateCacheHolder;
        }

        private static void AssignProceduralMaterial(
            MeshRenderer renderer,
            string preferredShaderName,
            Color baseColor,
            Color emissiveColor,
            string materialName)
        {
            if (renderer == null)
                return;

            Shader shader = Shader.Find(preferredShaderName);
            if (shader == null)
                shader = Shader.Find(emissiveColor.maxColorComponent > 0f ? "Unlit/Color" : "Standard");
            if (shader == null)
                return;

            var material = new Material(shader) { name = materialName };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", baseColor);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", emissiveColor);
            if (material.HasProperty("_EmissiveColorLDR")) material.SetColor("_EmissiveColorLDR", emissiveColor);
            renderer.sharedMaterial = material;
        }

        private static int ScoreCandidate(MeshRenderer renderer, string lowerPath)
        {
            int score = 0;
            string lowerName = (renderer.name ?? string.Empty).ToLowerInvariant();
            if (lowerName.Contains("light") || lowerPath.Contains("light")) score += 3;
            if (lowerName.Contains("red") || lowerPath.Contains("red")) score += 2;
            if (lowerPath.Contains("exit") || lowerPath.Contains("fire")) score += 1;

            Vector3 size = renderer.bounds.size;
            float widest = Mathf.Max(size.x, size.z);
            float volume = Mathf.Max(0.0001f, size.x * size.y * size.z);
            if (widest > size.y * 2.2f) score += 2;
            if (volume <= MaxTemplateVolumeM3) score += 1;
            if (HasRedEmissiveMaterial(renderer)) score += 2;
            if (renderer.GetComponentInChildren<Light>() != null) score += 1;
            return score;
        }

        private static bool HasRedEmissiveMaterial(MeshRenderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials == null) return false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null) continue;
                if ((material.name ?? string.Empty).ToLowerInvariant().Contains("red")) return true;
                if (material.HasProperty("_EmissiveColor"))
                {
                    Color emissive = material.GetColor("_EmissiveColor");
                    if (emissive.r > 0.2f && emissive.r > 2f * Mathf.Max(emissive.g, emissive.b)) return true;
                }
            }
            return false;
        }

        private static List<Vector3> FindInsideFireExitPositions()
        {
            var result = new List<Vector3>();
            EntranceTeleport[] teleports = Object.FindObjectsOfType<EntranceTeleport>(includeInactive: true);
            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_TELEPORT_SCAN total={teleports.Length} includeInactive=true.");

            for (int i = 0; i < teleports.Length; i++)
            {
                EntranceTeleport teleport = teleports[i];
                if (teleport == null || teleport.entranceId == 0) continue;
                // The inside variant of a fire exit teleports you OUT of the
                // building; that is the one standing under the vanilla bar.
                if (teleport.isEntranceToBuilding) continue;
                result.Add(teleport.transform.position);
            }

            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_TELEPORT_PREFERRED insideNonzeroId={result.Count}.");
            if (result.Count > 0)
                return result;

            for (int i = 0; i < teleports.Length; i++)
            {
                EntranceTeleport teleport = teleports[i];
                if (teleport == null || teleport.isEntranceToBuilding) continue;
                result.Add(teleport.transform.position);
            }

            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_TELEPORT_FALLBACK allInside={result.Count}.");
            return result;
        }
    }
}
