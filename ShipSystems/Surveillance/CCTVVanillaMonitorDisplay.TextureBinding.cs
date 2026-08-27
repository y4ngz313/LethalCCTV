using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVVanillaMonitorDisplay
    {
        /// <summary>
        /// #597 — latches the first time GeneralImprovements' replacement monitor wall
        /// resolved. GI destroys and rebuilds MonitorGroup on every
        /// <c>StartOfRound.Start</c> and again on client join with
        /// <c>SyncMonitorsFromOtherHost</c>; during that window <c>GetMonitorAtIndex</c>
        /// hands back nothing. Without this latch a bind pass landing in that window would
        /// fall through to the vanilla path and take a slot on <c>Cube.001</c> — which GI has
        /// made permanently invisible, so CCTV would silently paint nothing until something
        /// forced a rebind. With the latch set, a failed resolve just retries next pass.
        /// </summary>
        private static bool _generalImprovementsWallSeen;

        /// <summary>
        /// Resolves the two GI screen indices CCTV should own, honouring the per-client
        /// config overrides and logging the choice once. Returns false whenever the vanilla
        /// Cube.001 path is the correct one (GI absent, or better monitors off).
        /// </summary>
        private static bool TryResolveGeneralImprovementsScreens(out int feedIndex, out int radarIndex)
        {
            if (!TryResolveGeneralImprovementsScreensCore(out feedIndex, out radarIndex))
                return false;

            _generalImprovementsWallSeen = true;
            GeneralImprovementsMonitorCompat.LogBindingOnce(feedIndex, radarIndex);
            return true;
        }

        /// <summary>
        /// The side-effect-free half of <see cref="TryResolveGeneralImprovementsScreens"/>:
        /// answers "which GI screens would CCTV take right now" without latching
        /// <see cref="_generalImprovementsWallSeen"/> or emitting the one-shot binding log.
        /// The anchor resolvers call this on a path that can run long before the display
        /// layer has bound anything, and neither of those side effects belongs to a
        /// geometry query.
        /// </summary>
        private static bool TryResolveGeneralImprovementsScreensCore(out int feedIndex, out int radarIndex)
        {
            feedIndex = -1;
            radarIndex = -1;

            if (!GeneralImprovementsMonitorCompat.IsLoaded)
                return false;
            if (!GeneralImprovementsMonitorCompat.AreBetterMonitorsActive())
                return false;

            LethalCCTVConfig config = SurveillanceBootstrap.Config;
            int configuredFeed = config?.GeneralImprovementsFeedScreenIndex != null
                ? config.GeneralImprovementsFeedScreenIndex.Value
                : GeneralImprovementsMonitorCompat.AutoScreenIndex;
            int configuredRadar = config?.GeneralImprovementsRadarScreenIndex != null
                ? config.GeneralImprovementsRadarScreenIndex.Value
                : GeneralImprovementsMonitorCompat.AutoScreenIndex;

            if (!GeneralImprovementsMonitorCompat.TryResolveConfiguredScreens(
                    configuredFeed, configuredRadar, out feedIndex, out radarIndex))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// #598 — the renderer + material slot the CCTV feed is actually painted on, for
        /// consumers that need the surface the player SEES rather than the vanilla mesh
        /// the feed historically lived on. Under GeneralImprovements' better monitors the
        /// vanilla <c>Cube.001</c> is renderer- and collider-disabled, so any framing math
        /// keyed on it measures a hidden mesh.
        /// </summary>
        internal static bool TryGetBoundFeedScreen(
            out MeshRenderer renderer,
            out int materialIndex,
            out bool generalImprovementsScreen)
        {
            return TryGetVisibleScreen(_lowerLeftBinding, feed: true, out renderer, out materialIndex, out generalImprovementsScreen);
        }

        /// <summary>#598 — as <see cref="TryGetBoundFeedScreen"/>, for the radar screen.</summary>
        internal static bool TryGetBoundRadarScreen(
            out MeshRenderer renderer,
            out int materialIndex,
            out bool generalImprovementsScreen)
        {
            return TryGetVisibleScreen(_lowerRightBinding, feed: false, out renderer, out materialIndex, out generalImprovementsScreen);
        }

        /// <summary>
        /// Resolution order matters. A live GI binding is the ground truth. Failing that,
        /// a GI wall that is present but not yet bound (CCTV mode never entered this
        /// session, or GI mid-rebuild) still answers with the screen this feed WILL take,
        /// because the vanilla answer would be an invisible mesh. Only with no GI wall at
        /// all does an existing vanilla binding — and then the caller's own vanilla
        /// scene-path fallback — apply.
        /// </summary>
        private static bool TryGetVisibleScreen(
            ScreenBinding binding,
            bool feed,
            out MeshRenderer renderer,
            out int materialIndex,
            out bool generalImprovementsScreen)
        {
            renderer = null;
            materialIndex = -1;
            generalImprovementsScreen = false;

            if (binding != null && binding.IsBound && binding.IsGeneralImprovementsScreen && binding.Renderer != null)
            {
                renderer = binding.Renderer;
                materialIndex = binding.MaterialIndex;
                generalImprovementsScreen = true;
                return true;
            }

            if (TryResolveGeneralImprovementsScreensCore(out int feedIndex, out int radarIndex))
            {
                int index = feed ? feedIndex : radarIndex;
                if (GeneralImprovementsMonitorCompat.TryGetResolvedScreen(index, out MeshRenderer giRenderer, out int giMaterialIndex) &&
                    giRenderer != null)
                {
                    renderer = giRenderer;
                    materialIndex = giMaterialIndex;
                    generalImprovementsScreen = true;
                    return true;
                }

                // GI owns the wall but this feed has no screen (single-screen layout, or a
                // configured index that does not resolve). There is no visible surface to
                // measure; do NOT hand back the hidden vanilla one.
                return false;
            }

            if (binding != null && binding.IsBound && binding.Renderer != null)
            {
                renderer = binding.Renderer;
                materialIndex = binding.MaterialIndex;
                return true;
            }

            return false;
        }

        /// <summary>
        /// #600 — true while one of CCTV's bindings currently owns GeneralImprovements' map
        /// screen (<c>BigMiddle/MScreen</c>). That surface is the one GI drives from
        /// <c>ManualCameraRendererPatch.SwitchScreenOn</c> via <c>Monitors.UpdateMapMaterial</c>,
        /// so owning it changes two decisions: the red bezel button must be swallowed again
        /// (otherwise GI repaints our feed away on every press), and the vanilla map-screen
        /// <c>ManualCameraRenderer.Update</c> must be suppressed again (we are painting the
        /// visible surface, so freezing vanilla's driver freezes nothing the player can see).
        /// </summary>
        internal static bool OwnsGeneralImprovementsMapScreen()
        {
            return _lowerLeftBinding.IsGeneralImprovementsMapScreen ||
                   _lowerRightBinding.IsGeneralImprovementsMapScreen;
        }

        /// <summary>
        /// True when a bind pass must NOT fall back to the vanilla Cube.001 slots: GI owns
        /// this wall and is mid-rebuild. See <see cref="_generalImprovementsWallSeen"/>.
        /// </summary>
        private static bool IsGeneralImprovementsWallPending()
        {
            return _generalImprovementsWallSeen && GeneralImprovementsMonitorCompat.IsLoaded;
        }

        private static void PinLowerLeftVideoTexture()
        {
            // Under GI the vanilla lower-left screen is hidden and this RT feeds nothing the
            // player can see; the blit is pure cost. GI also owns the visible map surface
            // (BigMiddle/MScreen), which this must never touch.
            if (_lowerLeftBinding.IsGeneralImprovementsScreen)
                return;

            VideoPlayer reel = StartOfRound.Instance != null ? StartOfRound.Instance.screenLevelVideoReel : null;
            RenderTexture target = reel != null ? reel.targetTexture : null;
            if (target == null || _leftRenderTexture == null)
                return;

            try
            {
                Graphics.Blit(_leftRenderTexture, target);
                if (!_loggedPinningMapVideo)
                {
                    _loggedPinningMapVideo = true;
                    SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Pinning vanilla lower-left MapScreenVideo texture to CCTV feed ({target.width}x{target.height}).");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Lower-left MapScreenVideo pin failed: {ex.Message}");
            }
        }

        private static bool BindLeftMonitorTexture()
        {
            if (_leftRenderTexture == null)
                return false;

            // GI first: its screens are the only visible ones once UseBetterMonitors is on.
            // Bind() is idempotent — it reasserts when the target is unchanged and rebinds
            // when GI's rebuild handed out a fresh renderer — so this is also the re-resolve
            // pass that survives a lobby reload.
            if (TryResolveGeneralImprovementsScreens(out int feedIndex, out _) &&
                GeneralImprovementsMonitorCompat.TryGetResolvedScreen(feedIndex, out MeshRenderer giRenderer, out int giMaterialIndex))
            {
                _loggedLeftMissing = false;
                return _lowerLeftBinding.Bind(
                    giRenderer,
                    giMaterialIndex,
                    _leftRenderTexture,
                    generalImprovementsScreen: true,
                    identityDriver: GeneralImprovementsMonitorCompat.ResolveScreenDriver(feedIndex),
                    generalImprovementsMapScreen: GeneralImprovementsMonitorCompat.IsMapScreenIndex(feedIndex));
            }

            if (IsGeneralImprovementsWallPending())
                return false;

            if (_lowerLeftBinding.IsBound)
            {
                return _lowerLeftBinding.Reassert();
            }

            MeshRenderer renderer = FindMonitorWallRenderer();
            if (renderer == null)
            {
                if (!_loggedLeftMissing)
                {
                    _loggedLeftMissing = true;
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Lower-left CCTV monitor renderer Cube.001 not found; using UI overlay fallback only.");
                }
                return false;
            }

            int materialIndex = ResolveLowerLeftMonitorMaterialIndex(renderer);
            if (materialIndex < 0)
                return false;

            _loggedLeftMissing = false;
            return _lowerLeftBinding.Bind(renderer, materialIndex, _leftRenderTexture);
        }

        private static bool BindRightMonitorTexture()
        {
            if (_rightRenderTexture == null)
                return false;

            if (TryResolveGeneralImprovementsScreens(out _, out int radarIndex))
            {
                // The aux binding only ever exists to cover a second slot on the vanilla
                // Cube.001; GI's screens are one mesh, one material each.
                bool changed = _lowerRightAuxBinding.IsBound && _lowerRightAuxBinding.Restore();

                if (GeneralImprovementsMonitorCompat.TryGetResolvedScreen(radarIndex, out MeshRenderer giRenderer, out int giMaterialIndex))
                {
                    _loggedRightMissing = false;
                    changed |= _lowerRightBinding.Bind(
                        giRenderer,
                        giMaterialIndex,
                        _rightRenderTexture,
                        generalImprovementsScreen: true,
                        identityDriver: GeneralImprovementsMonitorCompat.ResolveScreenDriver(radarIndex),
                        generalImprovementsMapScreen: GeneralImprovementsMonitorCompat.IsMapScreenIndex(radarIndex));
                    return changed;
                }

                // Only one usable screen (or the radar index was pointed at the feed
                // screen): keep the feed and leave the radar unbound rather than taking a
                // slot on the hidden vanilla wall.
                return changed | (_lowerRightBinding.IsBound && _lowerRightBinding.Restore());
            }

            if (IsGeneralImprovementsWallPending())
                return false;

            if (_lowerRightBinding.IsBound)
            {
                bool changed = _lowerRightBinding.Reassert();
                if (_lowerRightAuxBinding.IsBound)
                    changed |= _lowerRightAuxBinding.Restore();
                return changed;
            }

            MeshRenderer cube = FindMonitorWallRenderer();
            int screenIndex = ResolveCubeSecondaryScreenIndex(cube);
            if (cube != null && screenIndex >= 0)
            {
                _loggedRightMissing = false;
                bool changed = _lowerRightBinding.Bind(cube, screenIndex, _rightRenderTexture);
                if (_lowerRightAuxBinding.IsBound)
                    changed |= _lowerRightAuxBinding.Restore();
                return changed;
            }

            if (!_loggedRightMissing)
            {
                _loggedRightMissing = true;
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Lower-right CCTV monitor Cube.001 secondary screen slot not found; radar monitor will stay vanilla.");
            }
            return false;
        }

        // Confident secondary screen slot on Cube.001: the non-map Unlit screen
        // material. A high score floor keeps this away from the HDRP/Lit bezel slot.
        private static int ResolveCubeSecondaryScreenIndex(MeshRenderer renderer)
        {
            if (renderer == null)
                return -1;

            Material[] materials;
            try { materials = renderer.sharedMaterials; }
            catch { return -1; }

            if (materials.Length < 3)
                return -1;

            int mapIndex = StartOfRound.Instance?.mapScreen != null && StartOfRound.Instance.mapScreen.mesh == renderer
                ? StartOfRound.Instance.mapScreen.materialIndex
                : -1;
            int leftIndex = _lowerLeftBinding.Renderer == renderer ? _lowerLeftBinding.MaterialIndex : -1;

            int bestIndex = -1;
            int bestScore = 199;
            for (int i = 0; i < materials.Length; i++)
            {
                if (i == mapIndex || i == leftIndex)
                    continue;

                Material material = materials[i];
                if (material == null)
                    continue;
                if (material.name != null && material.name.IndexOf("_LethalCCTV_", StringComparison.Ordinal) >= 0)
                    continue;

                int score = 0;
                string shaderName = material.shader != null ? material.shader.name : string.Empty;
                string name = material.name ?? string.Empty;
                if (shaderName.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) >= 0) score += 250;
                if (name.IndexOf("ShipScreen", StringComparison.OrdinalIgnoreCase) >= 0) score += 180;
                if (name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0) score -= 120;
                if (material.HasProperty("_UnlitColorMap")) score += 120;
                if (material.mainTexture is RenderTexture) score += 70;
                if (shaderName.IndexOf("HDRP/Lit", StringComparison.OrdinalIgnoreCase) >= 0) score -= 180;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static void DumpMonitorDiagnosticsOnce()
        {
            if (_diagnosticsDumped)
                return;
            _diagnosticsDumped = true;

            try
            {
                GameObject wall = GameObject.Find("Environment/HangarShip/ShipModels2b/MonitorWall");
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] --- monitor diagnostics (one-shot) ---");
                if (wall != null)
                {
                    MeshRenderer[] renderers = wall.GetComponentsInChildren<MeshRenderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        MeshRenderer r = renderers[i];
                        if (r == null) continue;
                        Material[] mats = r.sharedMaterials;
                        string summary = string.Join(", ", Array.ConvertAll(mats, m =>
                            m != null ? m.name + "(" + (m.shader != null ? m.shader.name : "?") + ")" : "null"));
                        SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV]   {GetPath(r.transform)} slots={mats.Length} [{summary}]");
                    }
                }
                else
                {
                    SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV]   MonitorWall not found.");
                }

                if (GeneralImprovementsMonitorCompat.IsLoaded)
                {
                    int giCount = GeneralImprovementsMonitorCompat.ActiveMonitorCount();
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV]   GeneralImprovements loaded: betterMonitorsActive={GeneralImprovementsMonitorCompat.AreBetterMonitorsActive()} " +
                        $"screens={giCount} poweredOn={GeneralImprovementsMonitorCompat.MonitorsPoweredOn()}");
                    for (int i = 0; i < giCount; i++)
                    {
                        if (!GeneralImprovementsMonitorCompat.TryGetScreen(i, out MeshRenderer giRenderer, out int giMaterialIndex))
                            continue;
                        SurveillanceBootstrap.Log?.LogInfo(
                            $"[LethalCCTV]     GI screen {i} {GetPath(giRenderer.transform)} slot={giMaterialIndex} " +
                            $"unassigned={GeneralImprovementsMonitorCompat.IsScreenUnassigned(i)}");
                    }

                    if (GeneralImprovementsMonitorCompat.TryGetMapScreen(out MeshRenderer mapRenderer, out int mapMaterialIndex))
                    {
                        SurveillanceBootstrap.Log?.LogInfo(
                            $"[LethalCCTV]     GI map screen (index {GeneralImprovementsMonitorCompat.MapScreenIndex}) " +
                            $"{GetPath(mapRenderer.transform)} slot={mapMaterialIndex}");
                    }
                    else
                    {
                        SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV]     GI map screen (BigMiddle/MScreen) not resolvable.");
                    }
                }

                ManualCameraRenderer[] mcrs = UnityEngine.Object.FindObjectsOfType<ManualCameraRenderer>();
                for (int i = 0; i < mcrs.Length; i++)
                {
                    ManualCameraRenderer mcr = mcrs[i];
                    if (mcr == null) continue;
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV]   MCR {GetPath(mcr.transform)} mesh={(mcr.mesh != null ? mcr.mesh.name : "null")} " +
                        $"mesh2={(mcr.mesh2 != null ? mcr.mesh2.name : "null")} materialIndex={mcr.materialIndex} enabled={mcr.enabled}");
                }
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] --- end monitor diagnostics ---");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Monitor diagnostics dump failed: {ex.Message}");
            }
        }

        private static MeshRenderer FindLegacyRightMonitorRenderer()
        {
            GameObject exact = GameObject.Find(LowerLeftMonitorPath);
            MeshRenderer exactRenderer = exact != null ? exact.GetComponent<MeshRenderer>() : null;
            if (exactRenderer != null)
                return exactRenderer;

            Transform monitor = StartOfRound.Instance != null && StartOfRound.Instance.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.Find("ShipModels2b/MonitorWall/Cube.001")
                : null;
            if (monitor != null)
            {
                MeshRenderer renderer = monitor.GetComponent<MeshRenderer>();
                if (renderer != null)
                    return renderer;
            }

            // Last resort: the MCR that paints the lower-right feed knows its own mesh.
            // Only accept it when the mesh actually lives under MonitorWall — the
            // hydraulic door monitor is driven by an MCR too and must stay vanilla.
            StartOfRound sor = StartOfRound.Instance;
            MeshRenderer fromInside = MonitorWallScreenOrNull(sor != null ? sor.insideCameraScreen : null);
            if (fromInside != null)
                return fromInside;
            return MonitorWallScreenOrNull(sor != null ? sor.securityCameraScreen : null);
        }

        private static MeshRenderer MonitorWallScreenOrNull(ManualCameraRenderer driver)
        {
            MeshRenderer renderer = driver != null ? driver.mesh : null;
            if (renderer == null)
                return null;

            MeshRenderer left = FindMonitorWallRenderer();
            if (renderer == left)
                return null;

            string name = renderer.gameObject.name ?? string.Empty;
            if (name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }

            Transform parent = renderer.transform.parent;
            return parent != null && parent.name == "MonitorWall" ? renderer : null;
        }

        private static MeshRenderer FindMonitorWallRenderer()
        {
            GameObject exact = GameObject.Find(LowerLeftMonitorPath);
            MeshRenderer exactRenderer = exact != null ? exact.GetComponent<MeshRenderer>() : null;
            if (exactRenderer != null)
                return exactRenderer;

            Transform monitor = StartOfRound.Instance != null && StartOfRound.Instance.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.Find("ShipModels2b/MonitorWall/Cube.001")
                : null;
            return monitor != null ? monitor.GetComponent<MeshRenderer>() : null;
        }

        private static int ResolveLowerLeftMonitorMaterialIndex(MeshRenderer renderer)
        {
            if (renderer == null)
                return -1;

            Material[] materials;
            try { materials = renderer.sharedMaterials; }
            catch { return -1; }

            // materialIndex is a slot index into mapScreen.mesh's material array, so it is
            // only meaningful for THIS renderer when this renderer is that mesh. The check
            // used to be missing, which was harmless while mapScreen.mesh was always
            // Cube.001 — GeneralImprovements repoints it at its own BigMiddle frame, whose
            // material array has a single entry, and the vanilla index would then either
            // pick the wrong slot or walk off the end (#597).
            ManualCameraRenderer mapScreen = StartOfRound.Instance != null ? StartOfRound.Instance.mapScreen : null;
            int mapIndex = mapScreen != null && mapScreen.mesh == renderer
                ? mapScreen.materialIndex
                : -1;

            if (mapIndex >= 0 && mapIndex < materials.Length)
                return mapIndex;

            return FallbackLowerLeftMaterialIndex >= 0 && FallbackLowerLeftMaterialIndex < materials.Length
                ? FallbackLowerLeftMaterialIndex
                : -1;
        }

        private static int ResolveRightMonitorMaterialIndex(MeshRenderer renderer)
        {
            if (renderer == null)
                return -1;

            Material[] materials;
            try { materials = renderer.sharedMaterials; }
            catch { return -1; }

            if (materials.Length == 0)
                return -1;
            if (materials.Length == 1)
                return 0;

            // The MCR painting this mesh knows the screen's material slot exactly
            // only when this renderer is the primary mesh; mesh2 still uses the
            // primary mesh materialIndex.
            ManualCameraRenderer driver = FindDriverForRenderer(renderer);
            if (driver != null && driver.mesh == renderer && driver.materialIndex >= 0 && driver.materialIndex < materials.Length)
                return driver.materialIndex;

            int mapIndex = StartOfRound.Instance?.mapScreen != null && StartOfRound.Instance.mapScreen.mesh == renderer
                ? StartOfRound.Instance.mapScreen.materialIndex
                : -1;

            int bestIndex = -1;
            int bestScore = int.MinValue;
            for (int i = 0; i < materials.Length; i++)
            {
                if (i == mapIndex)
                    continue;

                Material material = materials[i];
                if (material == null)
                    continue;

                int score = 0;
                string shaderName = material.shader != null ? material.shader.name : string.Empty;
                string name = material.name ?? string.Empty;
                if (shaderName.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) >= 0) score += 250;
                if (name.IndexOf("ShipScreen", StringComparison.OrdinalIgnoreCase) >= 0) score += 180;
                if (name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0) score -= 120;
                if (material.HasProperty("_UnlitColorMap")) score += 120;
                if (material.HasProperty("_BaseColorMap")) score += 60;
                if (material.mainTexture is RenderTexture) score += 70;
                if (shaderName.IndexOf("HDRP/Lit", StringComparison.OrdinalIgnoreCase) >= 0) score -= 180;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static ManualCameraRenderer FindDriverForRenderer(MeshRenderer renderer)
        {
            if (renderer == null)
                return null;

            ManualCameraRenderer[] cameras;
            try { cameras = UnityEngine.Object.FindObjectsOfType<ManualCameraRenderer>(); }
            catch { return null; }

            // Prefer a primary-mesh match: materialIndex always refers to the
            // primary mesh's slots, so a mesh2-only match may carry a wrong index.
            for (int i = 0; i < cameras.Length; i++)
            {
                ManualCameraRenderer camera = cameras[i];
                if (camera != null && camera.mesh == renderer)
                    return camera;
            }

            for (int i = 0; i < cameras.Length; i++)
            {
                ManualCameraRenderer camera = cameras[i];
                if (camera != null && camera.mesh2 == renderer)
                    return camera;
            }

            return null;
        }

    }
}
