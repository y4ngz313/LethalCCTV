using System.Collections.Generic;
using DunGen;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class VanillaRadarFeed
    {
        // Used only when the vanilla map RT is unavailable. The feed otherwise matches
        // the vanilla RT dimensions exactly — the previous Max(source, 1536x864)
        // floor UPSCALED the radar past vanilla and made every 4 fps HDRP radar
        // render ~2-3x more expensive for no visible gain on a ship monitor.
        private const int FALLBACK_WIDTH = 1024;
        private const int FALLBACK_HEIGHT = 576;
        private const float SETUP_RETRY_SECONDS = 1.0f;
        private const float TARGET_UPDATE_INTERVAL = 1f / 6f;
        private const float TARGET_FLOOR_RAY_UP_M = 2.0f;
        private const float TARGET_FLOOR_RAY_DOWN_M = 24.0f;
        private const float TARGET_FLOOR_OFFSET_M = 0.06f;
        private const float RADAR_FPS = 4f;
        private const float FIXED_RADAR_ORTHOGRAPHIC_MULTIPLIER = 1.0f;
        private const float RADAR_ZOOM_MIN_MULTIPLIER = 0.55f;
        private const float RADAR_ZOOM_MAX_MULTIPLIER = 1.75f;
        private const float RADAR_ZOOM_STEP = 0.88f;
        private const float FLOOR_GROUP_TOLERANCE_M = 7.5f;
        private const float MARKER_FLOOR_OFFSET_M = 0.18f;
        private const float MARKER_FLOOR_WINDOW_M = 8.0f;
        private const float CAMERA_CONE_LENGTH_M = 4.25f;
        private const float CAMERA_CONE_HALF_ANGLE_DEG = 18f;
        private const float CAMERA_CONE_ACTIVE_SCALE = 1.25f;
        // #542 - the active camera is the one marker the operator must find
        // instantly, and an orange triangle among blue triangles is only a hue
        // difference. A ring drawn behind it at this multiple of its own radius
        // gives it a silhouette no other class has.
        private const float CAMERA_ACTIVE_HALO_SCALE = 1.60f;
        // #542 - one knob plus per-class ratios. MARKER_BASE_RADIUS_M is the
        // single "every blip is too small / too big" dial; the ratios lock the
        // required ranking objective > active camera > monster > player > idle
        // camera > item, so retuning the base can never reorder the classes.
        // Every value below is authored against MARKER_REFERENCE_ORTHO_M -
        // ResolveMarkerScreenScale pins each class to a constant fraction of
        // the visible map, across the 0.55x-1.75x zoom range and regardless of
        // how large the live map camera's base view actually is.
        //
        // The first pass shipped a 1.40 m base and a flat player = monster =
        // 1.00 tier, which on a 768x432 monitor inset put most classes inside a
        // handful of pixels and gave the operator no size cue at all. The base
        // is doubled and the ratios spread so that "what is that blip" is
        // answerable from size alone before colour or shape is read.
        private const float MARKER_BASE_RADIUS_M = 2.80f;
        // The half-height the meter constants above were authored against (the
        // vanilla map camera's orthographicSize). The live camera's base ortho
        // varies wildly with interiors and map mods, and normalizing against it
        // (the first pass's behaviour) silently shrank every blip on larger
        // views - a 2.8 m disc over a 60 m half-height is a dot. Dividing by
        // this authoring reference instead makes a 2.8 m constant mean "the
        // fraction of the map view that 2.8 m occupies on a vanilla screen", on
        // every map.
        private const float MARKER_REFERENCE_ORTHO_M = 19.7f;
        private const float MARKER_RATIO_PLAYER = 1.00f;
        private const float MARKER_RATIO_ENEMY = 1.15f;
        private const float MARKER_RATIO_OBJECTIVE = 1.75f;
        private const float MARKER_RATIO_FACILITY = 1.35f;
        private const float MARKER_RATIO_CAMERA = 0.70f;
        private const float MARKER_RATIO_CAMERA_ACTIVE = 1.25f;
        private const float MARKER_RATIO_ITEM = 0.55f;

        // #542 - operator taste dial on top of the authored ratios. It multiplies
        // the same screen-scale term every class already goes through, so it can
        // only make the whole set bigger or smaller and never reorder it.
        private const float RADAR_BLIP_SCALE_MIN = 0.5f;
        private const float RADAR_BLIP_SCALE_MAX = 2.5f;

        private const float CAMERA_MARKER_RADIUS_M = MARKER_BASE_RADIUS_M * MARKER_RATIO_CAMERA;
        private const float ACTIVE_CAMERA_MARKER_RADIUS_M = MARKER_BASE_RADIUS_M * MARKER_RATIO_CAMERA_ACTIVE;
        private const float PLAYER_MARKER_RADIUS_M = MARKER_BASE_RADIUS_M * MARKER_RATIO_PLAYER;
        private const float ENEMY_MARKER_RADIUS_M = MARKER_BASE_RADIUS_M * MARKER_RATIO_ENEMY;
        private const float OBJECTIVE_MARKER_RADIUS_M = MARKER_BASE_RADIUS_M * MARKER_RATIO_OBJECTIVE;
        private const float FACILITY_MARKER_RADIUS_M = MARKER_BASE_RADIUS_M * MARKER_RATIO_FACILITY;
        private const float ITEM_MARKER_RADIUS_M = MARKER_BASE_RADIUS_M * MARKER_RATIO_ITEM;

        // MarkerSnapshot clamps Radius to 0.35-4 m, and the old marker used a flat
        // 0.22 multiplier against a 0.78 m floor - so the room-size term only took
        // over above ~3.55 m. Left at 0.22 the enlarged floor would swallow it
        // outright and every objective would read the same size. Deriving the
        // multiplier from that crossover keeps the term meaningful and re-bases it
        // automatically whenever MARKER_BASE_RADIUS_M is retuned.
        private const float OBJECTIVE_RADIUS_CROSSOVER_M = 3.55f;
        private const float OBJECTIVE_RADIUS_TERM_SCALE =
            OBJECTIVE_MARKER_RADIUS_M / OBJECTIVE_RADIUS_CROSSOVER_M;

        // Label metrics track the base too, or the text ends up inside the
        // enlarged camera triangles it is meant to sit beside. Height keeps its
        // historical parity with the idle camera radius, offset with the active.
        private const float CAMERA_LABEL_OFFSET_M = ACTIVE_CAMERA_MARKER_RADIUS_M;
        private const float CAMERA_LABEL_HEIGHT_M = CAMERA_MARKER_RADIUS_M;
        private const float FACILITY_LABEL_OFFSET_M = FACILITY_MARKER_RADIUS_M * 0.92f;
        private const float FACILITY_LABEL_HEIGHT_M = MARKER_BASE_RADIUS_M * 0.58f;
        private const float PLAYER_LABEL_HEIGHT_M = MARKER_BASE_RADIUS_M * 0.62f;
        // Must clear the backing disc (PLAYER_BACKING_SCALE radii) plus half a
        // line of text, or the name lands on top of the blip it identifies.
        private const float PLAYER_LABEL_OFFSET_M = MARKER_BASE_RADIUS_M * 1.85f;
        private const int PLAYER_LABEL_MAX_CHARS = 10;

        // A player blip is a stack: dark backing disc, facing wedge, health
        // fill over both, then the off-floor / off-screen glyph on top. All four
        // share ZTest Always, so render queue alone decides that order.
        private const float PLAYER_BACKING_SCALE = 1.34f;
        private const float PLAYER_FACING_SCALE = 0.62f;
        private const float PLAYER_GLYPH_SCALE = 0.54f;
        // The floor glyph is parked beside the blip, not above it: the name label
        // already owns the space directly above, and the facing wedge already owns
        // the centre.
        private const float PLAYER_GLYPH_SIDE_OFFSET_RADII = 1.55f;
        private const float PLAYER_HEALTH_HEALTHY_MIN = 66f;
        private const float PLAYER_HEALTH_HURT_MIN = 33f;
        private const float OFFSCREEN_EDGE_MARGIN_VIEWPORT = 0.045f;
        // Positions are read live every marker tick; only the FindObjectsOfType
        // sweep is throttled, because the array it allocates is the one real
        // per-tick cost in this file.
        private const float ITEM_SCAN_INTERVAL = 1.0f;
        // 0xB3 == 70% opacity. The legend is a key, not a readout.
        private const string LEGEND_ALPHA_HEX = "E6";
        private const int MAP_RADAR_LAYER = 14;
        private const int ROOM_LAYER = 8;
        private const string StationaryMapUiPlaceholderName = "LethalCCTV_RadarPlaceholder_MapCameraStationaryUI";

        private static GameObject _root;
        private static GameObject _targetObject;
        private static GameObject _markerRoot;
        private static ManualCameraRenderer _renderer;
        private static Camera _mapCamera;
        private static RenderTexture _texture;
        private static IReadOnlyList<CCTVCamera> _cameras;
        private static float _baseOrthographicSize;
        private static int _originalCullingMask;
        private static bool _interiorRadarChecked;
        private static bool _interiorHasRadarSprites = true;
        private static bool _loggedInteriorFallback;
        private static float _nextCullingOverrideAt;
        private static bool _loggedCullingOverride;
        private static bool _cullingOverridesSettled;
        private static readonly List<Renderer> _deadOverrideKeys = new List<Renderer>(64);
        private static float _nextSetupAttemptTime;
        private static bool _loggedReady;
        private static bool _loggedMissing;
        private static float _radarZoomMultiplier = 1f;
        private static Material _cameraMarkerMaterial;
        private static Material _activeCameraMarkerMaterial;
        private static Material _activeCameraHaloMaterial;
        private static Material _cameraConeMaterial;
        private static Material _playerBackingMaterial;
        private static Material _playerHealthyMaterial;
        private static Material _playerHurtMaterial;
        private static Material _playerCriticalMaterial;
        private static Material _playerFacingMaterial;
        private static Material _playerGlyphMaterial;
        private static Material _enemyMarkerMaterial;
        private static Material _objectiveMarkerMaterial;
        private static Material _mainframeMarkerMaterial;
        private static Material _stashMarkerMaterial;
        private static Material _itemMarkerMaterial;
        private static Mesh _circleMesh;
        private static Mesh _ringMesh;
        private static Mesh _squareMesh;
        private static Mesh _diamondMesh;
        private static Mesh _mainframeMesh;
        private static Mesh _stashMesh;
        private static Mesh _triangleMesh;
        private static Mesh _coneMesh;
        private static Mesh _xMesh;
        private static float _nextTargetUpdateAt;
        private static bool _active = true;
        // Refreshed once at the top of every UpdateMarkers pass, after ApplyZoom
        // has written orthographicSize for this tick. Every marker multiplies its
        // world-metre size by it so a blip holds one on-screen size at any zoom.
        private static float _markerScreenScale = 1f;
        private static bool _radarViewValid;
        // Built once from the marker colour constants; those are compile-time
        // values, so the string never goes stale.
        private static string _legendLabel;
        private static float _nextItemScanAt;
        private static readonly List<RadarMesh> _cameraMarkers = new List<RadarMesh>(64);
        private static readonly List<RadarMesh> _cameraCones = new List<RadarMesh>(64);
        private static readonly List<RadarMesh> _cameraHalos = new List<RadarMesh>(8);
        private static readonly List<RadarLabel> _cameraLabels = new List<RadarLabel>(64);
        private static readonly List<RadarMesh> _playerBackings = new List<RadarMesh>(8);
        private static readonly List<RadarMesh> _playerMarkers = new List<RadarMesh>(8);
        private static readonly List<RadarMesh> _playerFacings = new List<RadarMesh>(8);
        private static readonly List<RadarMesh> _playerGlyphs = new List<RadarMesh>(8);
        private static readonly List<RadarLabel> _playerLabels = new List<RadarLabel>(8);
        private static readonly List<RadarMesh> _enemyMarkers = new List<RadarMesh>(32);
        private static readonly List<RadarMesh> _objectiveMarkers = new List<RadarMesh>(8);
        private static readonly List<RadarMesh> _facilityMarkers = new List<RadarMesh>(8);
        private static readonly List<RadarLabel> _facilityLabels = new List<RadarLabel>(8);
        private static readonly List<RadarMesh> _itemMarkers = new List<RadarMesh>(64);
        private static readonly List<GrabbableObject> _itemScanBuffer = new List<GrabbableObject>(64);
        private static readonly List<float> _floorYScratch = new List<float>(8);

        // #542 - one source of truth for every hue an operator has to tell apart.
        // The materials in EnsureMarkerAssets read these, and so does the monitor
        // legend strip, so a recolour can never leave the key disagreeing with
        // the map. Orange belongs to the active camera alone; objectives moved to
        // yellow because the two used to be byte-identical, and monsters moved
        // off the circle entirely because their red sat a shade away from the
        // critical-health player fill.
        private static readonly Color ObjectiveMarkerColor = new Color(1.00f, 0.88f, 0.10f, 1.00f);
        private static readonly Color ActiveCameraMarkerColor = new Color(1.00f, 0.52f, 0.02f, 1.00f);
        private static readonly Color IdleCameraMarkerColor = new Color(0.42f, 0.78f, 1.00f, 0.98f);
        private static readonly Color PlayerHealthyMarkerColor = new Color(0.30f, 1.00f, 0.46f, 0.98f);
        private static readonly Color EnemyMarkerColor = new Color(1.00f, 0.18f, 0.10f, 1.00f);
        private static readonly Color ItemMarkerColor = new Color(0.46f, 1.00f, 0.84f, 0.94f);
        private static readonly Color MainframeMarkerColor = new Color(0.22f, 1.00f, 0.42f, 1.00f);
        private static readonly Color StashMarkerColor = new Color(0.28f, 0.79f, 1.00f, 1.00f);

        private static readonly Color CameraLabelColor = new Color(0.74f, 0.92f, 1.00f, 0.98f);
        private static readonly Color ActiveCameraLabelColor = new Color(1.00f, 0.68f, 0.12f, 1f);
        private static readonly Color PlayerLabelColor = new Color(0.92f, 0.97f, 1.00f, 0.98f);

        internal static bool IsEnabled =>
            SurveillanceBootstrap.Config != null &&
            SurveillanceBootstrap.Config.UseVanillaRadarFeed != null &&
            SurveillanceBootstrap.Config.UseVanillaRadarFeed.Value;

        internal static bool HasLiveTexture => IsEnabled && _texture != null && _renderer != null && _mapCamera != null;

        internal static void SetCameras(IReadOnlyList<CCTVCamera> cameras)
        {
            _cameras = cameras;
        }

        internal static void ClearCameras()
        {
            _cameras = null;
            HideAllMarkers();
            // Per-level teardown: the next dungeon is a different interior, so the
            // radar-sprite detection verdict must not carry over (issue #22 — the
            // first interior of the session used to decide sprite-vs-geometry mode
            // for every later moon because only ShutdownPartial reset this).
            _interiorRadarChecked = false;
            _interiorHasRadarSprites = true;
            _loggedInteriorFallback = false;
            // The culling-override registrations died with the dungeon's
            // renderers; re-register (and re-log) on the next interior.
            _nextCullingOverrideAt = 0f;
            _loggedCullingOverride = false;
            _cullingOverridesSettled = false;
            // Same reason: the scrap in the buffer belongs to the interior that
            // just went away.
            _itemScanBuffer.Clear();
            _nextItemScanAt = 0f;
        }

        internal static Texture PeekTexture()
        {
            return HasLiveTexture ? _texture : null;
        }

        internal static Texture ResolveTexture()
        {
            if (!IsEnabled) return null;
            if (!_active) return PeekTexture();
            EnsureReady();
            TickTargetThrottled();
            // This is the path that actually runs while the radar is live
            // (SyncRightMonitor); Tick() below has no registered caller.
            EnsureRadarSpritesUncullable();
            return HasLiveTexture ? _texture : null;
        }

        internal static void Tick()
        {
            if (!IsEnabled) return;
            if (!_active) return;
            EnsureReady();
            TickTargetThrottled();
            EnsureRadarSpritesUncullable();
        }

        /// <summary>
        /// The interior radar room sprites are tile children, so DunGen's
        /// AdjacentRoomCullingModified disables them with the rest of a tile's
        /// renderers whenever the local player is not adjacent — which is the
        /// normal case for a CCTV operator sitting in the ship, and leaves the
        /// cloned map camera a black underlay (#569). CullFactory happens to fix
        /// this by re-enabling tiles per rendering camera, which is why modded
        /// dev profiles never showed it. Registering the sprites in the culling
        /// component's visibility-override set keeps them enabled everywhere;
        /// they live on the radar layer no gameplay camera renders, so forcing
        /// them on has no player-facing cost.
        /// </summary>
        private static void EnsureRadarSpritesUncullable()
        {
            if (Time.unscaledTime < _nextCullingOverrideAt) return;
            _nextCullingOverrideAt = Time.unscaledTime + 5f;
            if (!InteriorHasRadarSprites()) return;
            // Once a pass finds nothing to register or re-enable, everything is
            // pinned; back off to a slow safety-net cadence (ClearCameras resets
            // the cadence for the next interior).
            if (_cullingOverridesSettled)
                _nextCullingOverrideAt = Time.unscaledTime + 30f;

            GameObject root = GameObject.Find("Systems/LevelGeneration/LevelGenerationRoot");
            if (root == null) return;

            AdjacentRoomCullingModified[] cullers = UnityEngine.Object.FindObjectsOfType<AdjacentRoomCullingModified>(includeInactive: true);
            if (cullers == null || cullers.Length == 0) return;

            List<GameObject> layerObjects = new List<GameObject>(128);
            CollectChildrenOnLayer(root.transform, MAP_RADAR_LAYER, layerObjects);
            if (layerObjects.Count == 0) return;

            int registered = 0;
            int reenabled = 0;
            for (int c = 0; c < cullers.Length; c++)
            {
                AdjacentRoomCullingModified culler = cullers[c];
                if (culler == null) continue;
                Dictionary<Renderer, bool> overrides = culler.OverrideRendererVisibilities;
                if (overrides == null)
                {
                    overrides = new Dictionary<Renderer, bool>();
                    culler.OverrideRendererVisibilities = overrides;
                }
                else if (overrides.Count > 0)
                {
                    // The culling component can outlive the dungeon whose
                    // renderers we registered; prune destroyed keys so regens
                    // never accumulate dead entries.
                    _deadOverrideKeys.Clear();
                    foreach (KeyValuePair<Renderer, bool> pair in overrides)
                    {
                        if (pair.Key == null)
                            _deadOverrideKeys.Add(pair.Key);
                    }
                    for (int d = 0; d < _deadOverrideKeys.Count; d++)
                        overrides.Remove(_deadOverrideKeys[d]);
                }

                for (int i = 0; i < layerObjects.Count; i++)
                {
                    GameObject go = layerObjects[i];
                    if (go == null) continue;
                    SpriteRenderer sprite = go.GetComponent<SpriteRenderer>();
                    if (sprite == null) continue;
                    if (!overrides.ContainsKey(sprite))
                    {
                        overrides[sprite] = true;
                        registered++;
                        culler.dirty = true;
                    }
                    if (!sprite.enabled)
                    {
                        sprite.enabled = true;
                        reenabled++;
                    }
                }
            }

            _cullingOverridesSettled = registered == 0 && reenabled == 0;

            if ((registered > 0 || reenabled > 0) && !_loggedCullingOverride)
            {
                _loggedCullingOverride = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Radar room sprites exempted from adjacent-room culling: registered={registered} " +
                    $"reenabled={reenabled} cullers={cullers.Length} (#569 black-underlay fix).");
            }
        }

        internal static void SetActive(bool active)
        {
            _active = active;
            if (_root != null && _root.activeSelf != active)
                _root.SetActive(active);
            if (_renderer != null && _renderer.enabled != active)
                _renderer.enabled = active;
            if (!active)
            {
                if (_mapCamera != null)
                    _mapCamera.enabled = false;
                HideAllMarkers();
            }
            else if (_root != null && !_root.activeSelf)
            {
                _root.SetActive(true);
            }
        }

        internal static string GetRenderDiagnostics()
        {
            if (!IsEnabled)
                return "radar feed: disabled by config";
            string rt = _texture != null ? $"{_texture.width}x{_texture.height}" : "<null>";
            bool lowFramerate = _renderer != null && _renderer.renderAtLowerFramerate;
            float fps = _renderer != null ? _renderer.fps : 0f;
            bool camEnabled = _mapCamera != null && _mapCamera.enabled;
            return $"radar feed: rt={rt} fps={fps:F1} renderAtLowerFramerate={lowFramerate} " +
                   $"camEnabled={camEnabled} targetUpdateInterval={TARGET_UPDATE_INTERVAL:F3}s zoom={_radarZoomMultiplier:F2}x live={HasLiveTexture}";
        }

        internal static string GetStatusLabel()
        {
            CCTVCamera active = MonitorFocus.GetActiveCamera();
            if (active == null) return "RADAR MAP  STANDBY";
            return "RADAR MAP  " + ResolveActiveFloorLabel(active);
        }

        /// <summary>
        /// #542 - the shape/colour key for the marker classes an operator has to
        /// act on. Built from the same Color constants the marker materials use,
        /// so the strip can never describe a colour the map no longer draws.
        /// Glyphs are deliberately ASCII: every other string this UI renders is
        /// ASCII too, and the vanilla control-tip font the monitor labels borrow
        /// is not guaranteed to carry the geometric-shape block.
        /// </summary>
        internal static string GetLegendLabel()
        {
            if (_legendLabel != null)
                return _legendLabel;

            _legendLabel =
                BuildLegendEntry(ObjectiveMarkerColor, "<>", "OBJECTIVE") + "   " +
                BuildLegendEntry(IdleCameraMarkerColor, "^", "CAM") + "   " +
                BuildLegendEntry(PlayerHealthyMarkerColor, "O", "CREW") + "   " +
                BuildLegendEntry(EnemyMarkerColor, "X", "THREAT");
            return _legendLabel;
        }

        /// <summary>
        /// The alpha is baked into the tag rather than left to the label's own
        /// colour: a TMP colour tag replaces the vertex colour outright, so a
        /// dimmed TextMeshProUGUI.color would be discarded on every tagged span
        /// and the strip would come back at full strength.
        ///
        /// The glyph goes through noparse because the objective diamond is drawn
        /// as "&lt;&gt;", which a rich-text label would otherwise try to read as
        /// markup and swallow.
        /// </summary>
        private static string BuildLegendEntry(Color color, string glyph, string name)
        {
            return "<color=#" + ColorUtility.ToHtmlStringRGB(color) + LEGEND_ALPHA_HEX + ">" +
                   "<noparse>" + glyph + "</noparse> " + name + "</color>";
        }

        internal static void AdjustZoom(float wheelSteps)
        {
            if (Mathf.Abs(wheelSteps) < 0.001f)
                return;

            _radarZoomMultiplier = Mathf.Clamp(
                _radarZoomMultiplier * Mathf.Pow(RADAR_ZOOM_STEP, wheelSteps),
                RADAR_ZOOM_MIN_MULTIPLIER,
                RADAR_ZOOM_MAX_MULTIPLIER);
            ApplyZoom(MonitorFocus.GetActiveCamera());
            _nextTargetUpdateAt = 0f;
        }

        internal static void Shutdown()
        {
            // #502 — unbind before release. _mapCamera is a clone under _root,
            // and _root is only Destroy()ed further down (deferred to end of
            // frame), so the camera outlives the texture it targets.
            if (_mapCamera != null)
                _mapCamera.targetTexture = null;
            if (_texture != null)
            {
                _texture.Release();
                UnityEngine.Object.Destroy(_texture);
            }
            _texture = null;

            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _root = null;
            _targetObject = null;
            _markerRoot = null;
            _renderer = null;
            _mapCamera = null;
            _baseOrthographicSize = 0f;
            ClearMarkerPools();
            DestroyMarkerResources();
            _loggedReady = false;
            _loggedMissing = false;
            _nextSetupAttemptTime = 0f;
            _nextTargetUpdateAt = 0f;
            _radarZoomMultiplier = 1f;
            _active = true;
        }

        private static void EnsureReady()
        {
            if (HasLiveTexture) return;
            if (Time.unscaledTime < _nextSetupAttemptTime) return;
            _nextSetupAttemptTime = Time.unscaledTime + SETUP_RETRY_SECONDS;

            ManualCameraRenderer source = StartOfRound.Instance != null
                ? StartOfRound.Instance.mapScreen
                : null;
            if (source == null || source.mapCamera == null)
            {
                LogMissingOnce("vanilla mapScreen/mapCamera unavailable");
                return;
            }

            Transform itemSystems = ResolveItemSystems();
            GameObject mapCameraTemplate = ResolveMapCameraTemplate(itemSystems, source);
            GameObject mapUiTemplate = ResolveMapUiTemplate(itemSystems);
            if (mapCameraTemplate == null || mapUiTemplate == null)
            {
                LogMissingOnce("vanilla MapCamera or MapScreenUI template unavailable");
                return;
            }

            ShutdownPartial();

            try
            {
                _root = new GameObject("LethalCCTV_VanillaRadarFeed");
                if (SurveillanceBootstrap.Instance != null)
                    _root.transform.SetParent(SurveillanceBootstrap.Instance.transform, worldPositionStays: false);

                _targetObject = new GameObject("LethalCCTV_VanillaRadarTarget");
                _targetObject.transform.SetParent(_root.transform, worldPositionStays: false);

                _markerRoot = new GameObject("LethalCCTV_VanillaRadarMarkers");
                _markerRoot.transform.SetParent(_root.transform, worldPositionStays: false);
                SetLayerRecursively(_markerRoot, MAP_RADAR_LAYER);

                GameObject cameraObject = UnityEngine.Object.Instantiate(mapCameraTemplate, _root.transform, worldPositionStays: false);
                cameraObject.name = "LethalCCTV_VanillaRadarCamera";
                TrySetUntagged(cameraObject);
                DisableClonedAnimators(cameraObject);
                _mapCamera = cameraObject.GetComponent<Camera>();
                if (_mapCamera == null)
                    throw new System.InvalidOperationException("cloned MapCamera has no Camera component");
                _originalCullingMask = _mapCamera.cullingMask;
                _baseOrthographicSize = _mapCamera.orthographicSize;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Radar map camera base ortho {_baseOrthographicSize:0.##} m " +
                    $"(marker reference {MARKER_REFERENCE_ORTHO_M} m).");

                ManualCameraRenderer[] accidentalRenderers = cameraObject.GetComponents<ManualCameraRenderer>();
                for (int i = 0; i < accidentalRenderers.Length; i++)
                    accidentalRenderers[i].enabled = false;

                _texture = CreateTargetTexture(source);
                _mapCamera.targetTexture = _texture;
                _mapCamera.enabled = false;

                GameObject uiObject = UnityEngine.Object.Instantiate(mapUiTemplate, _root.transform, worldPositionStays: false);
                uiObject.name = "LethalCCTV_VanillaRadarScreenUI";
                DisableClonedAnimators(uiObject);
                Canvas canvas = uiObject.GetComponent<Canvas>();
                if (canvas != null)
                {
                    canvas.worldCamera = _mapCamera;
                    if (StartOfRound.Instance != null && StartOfRound.Instance.radarCanvas != null)
                        canvas.planeDistance = StartOfRound.Instance.radarCanvas.planeDistance;
                }
                DestroyChildIfPresent(uiObject.transform, "PlanetVideoReel");
                DestroyChildIfPresent(uiObject.transform, "PlanetDescription");

                // CCTV radar is a fixed tactical map, not the vanilla "route to exit"
                // monitor. Keep vanilla's required references non-null, but point
                // them at inert placeholders so Update can run without drawing
                // route/ship UI into the CCTV monitor.
                LineRenderer exitLine = CreateExitLine(source);
                GameObject shipArrowUI = CreateHiddenPlaceholder(uiObject.transform, "ShipArrowUI");
                Transform shipArrowPointer = CreateHiddenPlaceholder(uiObject.transform, "ShipArrowPointer").transform;
                GameObject shipIcon = CreateHiddenPlaceholder(uiObject.transform, "ShipIcon");
                GameObject lostSignal = CreateHiddenPlaceholder(uiObject.transform, "LostSignalUI");
                Image compass = CreateHiddenComponent<Image>(uiObject.transform, "CompassRose");
                RawImage headMountedUi = CreateHiddenComponent<RawImage>(uiObject.transform, "HeadMountedCamUI");
                Image localPlaceholder = CreateHiddenComponent<Image>(uiObject.transform, "LocalPlayerPlaceholder");
                HideClonedChildIfPresent(uiObject.transform, source.shipArrowUI, "ShipArrowUI");
                HideClonedChildIfPresent(uiObject.transform, source.shipIcon, "ShipIcon");
                HideClonedChildIfPresent(uiObject.transform, source.LostSignalUI, "LostSignalUI");
                HideClonedChildIfPresent(uiObject.transform, source.compassRose, "CompassRose");
                HideClonedChildIfPresent(uiObject.transform, source.headMountedCamUI, "HeadMountedCamUI");
                HideClonedChildIfPresent(uiObject.transform, source.localPlayerPlaceholder, "LocalPlayerPlaceholder");
                if (shipArrowUI != null) shipArrowUI.SetActive(false);
                if (shipIcon != null) shipIcon.SetActive(false);
                if (lostSignal != null) lostSignal.SetActive(false);
                if (compass != null) compass.enabled = false;
                if (headMountedUi != null) headMountedUi.enabled = false;
                if (localPlaceholder != null) localPlaceholder.enabled = false;

                Transform stationaryUi = SuppressClonedStationaryMapUi(uiObject.transform, source.mapCameraStationaryUI);

                _renderer = _root.AddComponent<ManualCameraRenderer>();
                _renderer.cameraNearPlane = source.cameraNearPlane;
                _renderer.cameraFarPlane = source.cameraFarPlane;
                _renderer.cam = _mapCamera;
                _renderer.mapCamera = _mapCamera;
                _renderer.mapCameraStationaryUI = stationaryUi;
                _renderer.shipArrowUI = shipArrowUI;
                _renderer.shipArrowPointer = shipArrowPointer;
                _renderer.shipIcon = shipIcon;
                _renderer.compassRose = compass;
                _renderer.headMountedCam = null;
                _renderer.headMountedCamData = null;
                _renderer.headMountedCamUI = headMountedUi;
                _renderer.localPlayerPlaceholder = localPlaceholder;
                _renderer.LostSignalUI = lostSignal;
                _renderer.mapCameraAnimator = null;
                _renderer.lineFromRadarTargetToExit = exitLine;
                _renderer.radarTargets = new List<TransformAndName>
                {
                    new TransformAndName(_targetObject.transform, "CCTV", nonPlayer: true)
                };
                _renderer.targetTransformIndex = 0;
                _renderer.targetedPlayer = null;
                _renderer.currentCameraDisabled = false;
                _renderer.overrideCameraForOtherUse = false;
                _renderer.overrideRadarCameraOnAlways = true;
                // ALWAYS throttle. The old HD-data-conditional left a fallback path
                // where vanilla's MCR would drive cam.enabled from mesh visibility —
                // with no mesh assigned that means an HDRP radar render every frame.
                _renderer.renderAtLowerFramerate = true;
                _renderer.fps = RADAR_FPS;

                ValidateManualRendererReferences(_renderer);
                TickTarget();
                _nextTargetUpdateAt = Time.unscaledTime + TARGET_UPDATE_INTERVAL;
                LogReadyOnce();
            }
            catch (System.Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Vanilla radar feed unavailable; using schematic fallback. {ex.GetType().Name}: {ex.Message}");
                ShutdownPartial();
            }
        }

        private static void TickTargetThrottled()
        {
            if (Time.unscaledTime < _nextTargetUpdateAt)
                return;

            _nextTargetUpdateAt = Time.unscaledTime + TARGET_UPDATE_INTERVAL;
            TickTarget();
        }

        private static void TickTarget()
        {
            if (_targetObject == null || _renderer == null) return;

            CCTVCamera active = MonitorFocus.GetActiveCamera();
            Vector3 target = ResolveTargetPosition(active);
            ApplyStableTargetPose(target);
            ApplyInteriorFallbackMask(active, target);
            ApplyZoom(active);
            UpdateMarkers(active, target);

            if (_renderer.radarTargets == null)
                _renderer.radarTargets = new List<TransformAndName>();
            if (_renderer.radarTargets.Count == 0)
                _renderer.radarTargets.Add(new TransformAndName(_targetObject.transform, "CCTV", nonPlayer: true));
            else
                _renderer.radarTargets[0] = new TransformAndName(_targetObject.transform, active != null ? active.ResolvedLabel : "CCTV", nonPlayer: true);

            _renderer.targetTransformIndex = 0;
            _renderer.targetedPlayer = null;
            _renderer.currentCameraDisabled = active == null;
        }

        private static Vector3 ResolveTargetPosition(CCTVCamera active)
        {
            if (active == null)
                return StartOfRound.Instance != null && StartOfRound.Instance.elevatorTransform != null
                    ? StartOfRound.Instance.elevatorTransform.position
                    : Vector3.zero;

            if (TryResolveTileCenterAnchor(active, out Vector3 tileAnchor))
                return tileAnchor;

            Vector3 basePosition = active.transform.position;
            if (TryProjectToFloor(basePosition, out Vector3 floor))
                return floor + Vector3.up * TARGET_FLOOR_OFFSET_M;

            return basePosition;
        }

        private static bool TryResolveTileCenterAnchor(CCTVCamera active, out Vector3 target)
        {
            target = Vector3.zero;
            Tile tile = active.OwningTile;
            if (tile != null && tile.Placement != null)
            {
                Bounds local = DungeonCameraSpawner.ComputeWorldAabbLocal(tile);
                if (local.size.sqrMagnitude < 1e-6f)
                    local = tile.Placement.LocalBounds;
                if (local.size.sqrMagnitude > 1e-6f)
                {
                    Vector3 localPoint = new Vector3(local.center.x, local.min.y + TARGET_FLOOR_OFFSET_M, local.center.z);
                    Vector3 world = tile.Placement.Position + tile.Placement.Rotation * localPoint;
                    if (TryProjectToFloor(world + Vector3.up * 0.5f, out Vector3 floor))
                        world = floor + Vector3.up * TARGET_FLOOR_OFFSET_M;
                    target = world;
                    return true;
                }
            }

            return false;
        }

        private static void ApplyInteriorFallbackMask(CCTVCamera active, Vector3 target)
        {
            if (_mapCamera == null) return;

            // The fallback decision is per-INTERIOR, not per-room. The old
            // target.y >= -80 guard flipped rooms above that height back to the
            // sprites-only mask, which renders nothing in spriteless interiors —
            // the radar "disappeared" in exactly those rooms (issue #22).
            if (active == null || InteriorHasRadarSprites())
            {
                _mapCamera.cullingMask = _originalCullingMask;
                return;
            }

            _mapCamera.cullingMask = BuildOldInteriorRadarMask();
            if (!_loggedInteriorFallback)
            {
                _loggedInteriorFallback = true;
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Vanilla radar feed detected an interior without radar sprites; using Universal Radar-style old interior radar mask.");
            }
        }

        private static bool InteriorHasRadarSprites()
        {
            if (_interiorRadarChecked) return _interiorHasRadarSprites;
            _interiorRadarChecked = true;
            _interiorHasRadarSprites = DetectInteriorRadarSprites();
            return _interiorHasRadarSprites;
        }

        private static bool DetectInteriorRadarSprites()
        {
            GameObject root = GameObject.Find("Systems/LevelGeneration/LevelGenerationRoot");
            if (root == null) return LogSpriteVerdict(false, "no LevelGenerationRoot", 0, 0, 0, 0);

            List<GameObject> layerObjects = new List<GameObject>(128);
            CollectChildrenOnLayer(root.transform, MAP_RADAR_LAYER, layerObjects);
            if (layerObjects.Count < 5) return LogSpriteVerdict(false, "fewer than 5 radar-layer objects", layerObjects.Count, 0, 0, 0);

            int taggedSprites = 0;
            int largeSprites = 0;
            for (int i = 0; i < layerObjects.Count; i++)
            {
                GameObject go = layerObjects[i];
                if (go == null) continue;
                if (go.CompareTag("RadarRoomSprite"))
                    taggedSprites++;

                SpriteRenderer sprite = go.GetComponent<SpriteRenderer>();
                if (sprite != null && sprite.bounds.size.x * sprite.bounds.size.z > 2f)
                    largeSprites++;
            }
            if (taggedSprites >= 5) return LogSpriteVerdict(true, "tagged sprites", layerObjects.Count, taggedSprites, largeSprites, 0);

            int tileCount = UnityEngine.Object.FindObjectsOfType<Tile>().Length;
            if (tileCount <= 0) return LogSpriteVerdict(false, "no tiles", layerObjects.Count, taggedSprites, largeSprites, 0);

            if (1f - (float)layerObjects.Count / tileCount > 0.1f)
                return LogSpriteVerdict(false, "layer-object/tile ratio", layerObjects.Count, taggedSprites, largeSprites, tileCount);
            if (1f - (float)largeSprites / tileCount > 0.1f)
                return LogSpriteVerdict(false, "large-sprite/tile ratio", layerObjects.Count, taggedSprites, largeSprites, tileCount);

            return LogSpriteVerdict(true, "sprite/tile ratios", layerObjects.Count, taggedSprites, largeSprites, tileCount);
        }

        private static bool LogSpriteVerdict(bool hasSprites, string reason, int layerObjects, int tagged, int large, int tiles)
        {
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Interior radar-sprite detection: hasSprites={hasSprites} ({reason}); " +
                $"radarLayerObjects={layerObjects} taggedSprites={tagged} largeSprites={large} tiles={tiles}. " +
                $"Radar mode for this interior: {(hasSprites ? "vanilla sprites" : "old-interior geometry fallback")}.");
            return hasSprites;
        }

        private static void CollectChildrenOnLayer(Transform current, int targetLayer, List<GameObject> results)
        {
            if (current == null) return;
            for (int i = 0; i < current.childCount; i++)
            {
                Transform child = current.GetChild(i);
                if (child.gameObject.layer == targetLayer)
                    results.Add(child.gameObject);
                CollectChildrenOnLayer(child, targetLayer, results);
            }
        }

        private static int BuildOldInteriorRadarMask()
        {
            return (1 << MAP_RADAR_LAYER) | (1 << ROOM_LAYER);
        }

        private static void ApplyZoom(CCTVCamera active)
        {
            if (_mapCamera == null || !_mapCamera.orthographic || _baseOrthographicSize <= 0f)
                return;

            _mapCamera.orthographicSize = _baseOrthographicSize * FIXED_RADAR_ORTHOGRAPHIC_MULTIPLIER * _radarZoomMultiplier;
        }

        private static void ApplyStableTargetPose(Vector3 target)
        {
            if (_targetObject == null)
                return;

            // Vanilla's map target is usually a player transform, so the map can
            // inherit player/camera heading. CCTV should be a learnable fixed map:
            // only the target position moves; heading remains world-stable.
            _targetObject.transform.SetPositionAndRotation(target, Quaternion.identity);
        }

        private static void UpdateMarkers(CCTVCamera active, Vector3 target)
        {
            if (_markerRoot == null)
                return;

            if (active == null || !EnsureMarkerAssets())
            {
                HideAllMarkers();
                return;
            }

            // Read after ApplyZoom, which TickTarget runs immediately before this
            // and which writes orthographicSize every marker tick.
            _markerScreenScale = ResolveMarkerScreenScale();
            _radarViewValid = ResolveRadarViewValid();

            int cameraCount = 0;
            int coneCount = 0;
            int haloCount = 0;
            int labelCount = 0;
            var players = new PlayerMarkerCounts();
            int enemyCount = 0;
            int objectiveCount = 0;
            int facilityCount = 0;
            int facilityLabelCount = 0;
            int itemCount = 0;

            UpdateCameraMarkers(active, target, ref cameraCount, ref coneCount, ref haloCount, ref labelCount);
            UpdatePlayerMarkers(active, target, ref players);
            UpdateEnemyMarkers(active, target, ref enemyCount);
            UpdateObjectiveMarkers(active, target, ref objectiveCount);
            UpdateFacilityMarkers(target, ref facilityCount, ref facilityLabelCount);
            UpdateItemMarkers(active, target, ref itemCount);

            DeactivateUnused(_cameraMarkers, cameraCount);
            DeactivateUnused(_cameraCones, coneCount);
            DeactivateUnused(_cameraHalos, haloCount);
            DeactivateUnused(_cameraLabels, labelCount);
            DeactivateUnused(_playerBackings, players.Backings);
            DeactivateUnused(_playerMarkers, players.Fills);
            DeactivateUnused(_playerFacings, players.Facings);
            DeactivateUnused(_playerGlyphs, players.Glyphs);
            DeactivateUnused(_playerLabels, players.Labels);
            DeactivateUnused(_enemyMarkers, enemyCount);
            DeactivateUnused(_objectiveMarkers, objectiveCount);
            DeactivateUnused(_facilityMarkers, facilityCount);
            DeactivateUnused(_facilityLabels, facilityLabelCount);
            DeactivateUnused(_itemMarkers, itemCount);
        }

        /// <summary>
        /// Sizes are authored in world metres against an orthographic camera, so
        /// without this every blip shrinks as the operator zooms out. Scaling by
        /// the ratio of current to base orthographic size cancels that exactly,
        /// making the constants "size at zoom 1.0".
        ///
        /// The operator's Radar Blip Scale rides on the same term, so one config
        /// value reaches every marker class without any draw site knowing it
        /// exists.
        /// </summary>
        private static float ResolveMarkerScreenScale()
        {
            float blipScale = ResolveBlipScale();
            if (_mapCamera == null || !_mapCamera.orthographic || _baseOrthographicSize <= 0f)
                return blipScale;

            // Dividing by the authoring reference rather than the live base
            // ortho: rendered fraction = radius * scale / ortho, so this makes
            // every marker's on-screen size radius / MARKER_REFERENCE_ORTHO_M -
            // constant across zoom AND across map cameras of any size. See the
            // constant's comment for the failure the old base-ortho division
            // caused on large maps.
            return (_mapCamera.orthographicSize / MARKER_REFERENCE_ORTHO_M) * blipScale;
        }

        /// <summary>
        /// Clamped here as well as at the bind, because a hand-edited config file
        /// can hold anything and a zero or negative scale would collapse every
        /// blip in the facility to nothing.
        /// </summary>
        private static float ResolveBlipScale()
        {
            LethalCCTVConfig config = SurveillanceBootstrap.Config;
            if (config == null || config.RadarBlipScale == null)
                return 1f;

            return Mathf.Clamp(config.RadarBlipScale.Value, RADAR_BLIP_SCALE_MIN, RADAR_BLIP_SCALE_MAX);
        }

        /// <summary>
        /// The cloned ManualCameraRenderer is what actually moves the map camera,
        /// on its own 4 fps schedule, so the camera can lag or precede this 6 Hz
        /// marker pass. The radar is centred on our target by construction: if the
        /// target is not near the middle of the view, the viewport maths below
        /// describes a view that does not exist yet, and every player would be
        /// pinned to an edge that is not there. Fail open to drawing in place.
        /// </summary>
        private static bool ResolveRadarViewValid()
        {
            if (_mapCamera == null || _targetObject == null)
                return false;

            Vector3 viewport = _mapCamera.WorldToViewportPoint(_targetObject.transform.position);
            return viewport.z > 0f
                   && Mathf.Abs(viewport.x - 0.5f) <= 0.25f
                   && Mathf.Abs(viewport.y - 0.5f) <= 0.25f;
        }

        /// <summary>
        /// A player draws up to four meshes plus a label, and each layer has its
        /// own pool, so the used counts travel together rather than as five
        /// separate ref ints.
        /// </summary>
        private struct PlayerMarkerCounts
        {
            internal int Backings;
            internal int Fills;
            internal int Facings;
            internal int Glyphs;
            internal int Labels;
        }

        private static void UpdateCameraMarkers(CCTVCamera active, Vector3 target, ref int markerCount, ref int coneCount, ref int haloCount, ref int labelCount)
        {
            bool activeIncluded = false;
            if (_cameras != null)
            {
                for (int i = 0; i < _cameras.Count; i++)
                {
                    CCTVCamera camera = _cameras[i];
                    if (camera == null) continue;
                    if (camera == active) activeIncluded = true;
                    AddCameraMarker(camera, active, target, ref markerCount, ref coneCount, ref haloCount, ref labelCount);
                }
            }

            if (!activeIncluded)
                AddCameraMarker(active, active, target, ref markerCount, ref coneCount, ref haloCount, ref labelCount);
        }

        private static void AddCameraMarker(
            CCTVCamera camera,
            CCTVCamera active,
            Vector3 target,
            ref int markerCount,
            ref int coneCount,
            ref int haloCount,
            ref int labelCount)
        {
            if (camera == null || !ShouldShowCamera(camera, active, target))
                return;

            Vector3 markerPosition = ResolveCameraMarkerPosition(camera);
            Vector3 forward = FlattenForward(camera.transform.forward);
            bool isActive = camera == active;

            RadarMesh marker = EnsureRadarMesh(
                _cameraMarkers,
                markerCount++,
                "CameraMarker",
                EnsureTriangleMesh(),
                isActive ? _activeCameraMarkerMaterial : _cameraMarkerMaterial);
            marker.Transform.position = markerPosition;
            marker.Transform.rotation = forward.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(forward, Vector3.up)
                : Quaternion.identity;
            float radius = (isActive ? ACTIVE_CAMERA_MARKER_RADIUS_M : CAMERA_MARKER_RADIUS_M) * _markerScreenScale;
            marker.Transform.localScale = new Vector3(radius, radius, radius);

            if (isActive)
            {
                // Drawn from its own pool and behind the triangle by render queue,
                // so the orange fill stays legible inside the ring rather than
                // being tinted by it.
                RadarMesh halo = EnsureRadarMesh(
                    _cameraHalos,
                    haloCount++,
                    "CameraHalo",
                    EnsureRingMesh(),
                    _activeCameraHaloMaterial);
                halo.Transform.position = markerPosition;
                halo.Transform.rotation = Quaternion.identity;
                float haloRadius = radius * CAMERA_ACTIVE_HALO_SCALE;
                halo.Transform.localScale = new Vector3(haloRadius, haloRadius, haloRadius);
            }

            if (forward.sqrMagnitude > 1e-6f)
            {
                if (isActive)
                {
                    RadarMesh cone = EnsureRadarMesh(
                        _cameraCones,
                        coneCount++,
                        "CameraCone",
                        EnsureConeMesh(),
                        _cameraConeMaterial);
                    cone.Transform.position = markerPosition;
                    cone.Transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
                    // Scales with the triangle it grows out of. Left in world
                    // metres it would visibly detach from the marker at both
                    // ends of the zoom range.
                    cone.Transform.localScale = Vector3.one * CAMERA_CONE_ACTIVE_SCALE * _markerScreenScale;
                }
            }

            RadarLabel label = EnsureCameraLabel(labelCount++);
            UpdateCameraLabel(label, camera, markerPosition, isActive);
        }

        private static bool ShouldShowCamera(CCTVCamera camera, CCTVCamera active, Vector3 target)
        {
            if (camera == null || active == null)
                return false;

            Vector3 markerPosition = ResolveCameraMarkerPosition(camera);
            return IsOnActiveInteriorFloor(markerPosition, target);
        }

        private static Vector3 ResolveCameraMarkerPosition(CCTVCamera camera)
        {
            return ProjectMarkerPosition(camera.transform.position);
        }

        /// <summary>
        /// #542 - the one marker class an operator has to identify individually.
        /// Every in-facility teammate is drawn; the floor and view-bounds tests
        /// change how a teammate reads rather than whether they appear at all,
        /// because "vanished" and "not in the facility" are otherwise the same
        /// picture.
        /// </summary>
        private static void UpdatePlayerMarkers(CCTVCamera active, Vector3 target, ref PlayerMarkerCounts counts)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.allPlayerScripts == null)
                return;

            for (int i = 0; i < sor.allPlayerScripts.Length; i++)
            {
                PlayerControllerB player = sor.allPlayerScripts[i];
                if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                    continue;

                if (!player.isInsideFactory)
                    continue;

                // The shared +/-8 m predicate stays exactly as cameras, enemies
                // and objectives read it (#22 is open against that same window);
                // only the presentation branches here.
                bool onFloor = IsOnActiveInteriorFloor(player.transform.position, target);
                Vector3 markerPosition = ProjectMarkerPosition(player.transform.position);
                if (!IsWithinRadarView(markerPosition, out Vector3 clamped, out Vector3 edgeDirection))
                {
                    AddOffScreenPlayerMarker(player, clamped, edgeDirection, ref counts);
                    continue;
                }

                AddPlayerMarker(player, markerPosition, onFloor, target, ref counts);
            }
        }

        private static void AddPlayerMarker(
            PlayerControllerB player,
            Vector3 markerPosition,
            bool onFloor,
            Vector3 target,
            ref PlayerMarkerCounts counts)
        {
            float radius = PLAYER_MARKER_RADIUS_M * _markerScreenScale;

            // Backing first, and always a filled disc even when the player is
            // off-floor: it is the silhouette that keeps the blip legible over
            // both dark corridors and bright vanilla tile sprites, so it must not
            // change shape with floor or health.
            RadarMesh backing = EnsureRadarMesh(
                _playerBackings,
                counts.Backings++,
                "PlayerBacking",
                EnsureCircleMesh(),
                _playerBackingMaterial);
            backing.Transform.position = markerPosition;
            backing.Transform.rotation = Quaternion.identity;
            backing.Transform.localScale = Vector3.one * radius * PLAYER_BACKING_SCALE;

            // On another floor the fill goes hollow: a ring instead of a disc.
            RadarMesh fill = EnsureRadarMesh(
                _playerMarkers,
                counts.Fills++,
                "PlayerMarker",
                onFloor ? EnsureCircleMesh() : EnsureRingMesh(),
                ResolvePlayerHealthMaterial(player));
            fill.Transform.position = markerPosition;
            fill.Transform.rotation = Quaternion.identity;
            fill.Transform.localScale = Vector3.one * radius;

            Vector3 forward = FlattenForward(player.transform.forward);
            if (forward.sqrMagnitude > 1e-6f)
            {
                RadarMesh facing = EnsureRadarMesh(
                    _playerFacings,
                    counts.Facings++,
                    "PlayerFacing",
                    EnsureConeMesh(),
                    _playerFacingMaterial);
                facing.Transform.position = markerPosition;
                facing.Transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
                facing.Transform.localScale = Vector3.one * PLAYER_FACING_SCALE * _markerScreenScale;
            }

            if (!onFloor)
            {
                Vector3 screenUp = ResolveRadarScreenUp();
                bool above = player.transform.position.y > target.y;
                Vector3 sideOffset =
                    ResolveRadarScreenRight() * (radius * PLAYER_GLYPH_SIDE_OFFSET_RADII);
                AddPlayerGlyph(markerPosition, above ? screenUp : -screenUp, sideOffset, ref counts);
            }

            AddPlayerLabel(player, markerPosition, ref counts);
        }

        /// <summary>
        /// A teammate on the active floor but outside the orthographic bounds used
        /// to simply not be drawn. Clamping to the edge as an arrow plus name keeps
        /// the bearing readable without pretending the position is on-screen.
        /// </summary>
        private static void AddOffScreenPlayerMarker(
            PlayerControllerB player,
            Vector3 clampedPosition,
            Vector3 edgeDirection,
            ref PlayerMarkerCounts counts)
        {
            RadarMesh backing = EnsureRadarMesh(
                _playerBackings,
                counts.Backings++,
                "PlayerBacking",
                EnsureCircleMesh(),
                _playerBackingMaterial);
            backing.Transform.position = clampedPosition;
            backing.Transform.rotation = Quaternion.identity;
            backing.Transform.localScale =
                Vector3.one * PLAYER_MARKER_RADIUS_M * _markerScreenScale * PLAYER_BACKING_SCALE * 0.82f;

            AddPlayerGlyph(clampedPosition, edgeDirection, Vector3.zero, ref counts);
            AddPlayerLabel(player, clampedPosition, ref counts);
        }

        /// <summary>
        /// Point direction and placement are separate: the floor glyph points up
        /// or down the screen while sitting beside the blip, whereas the
        /// off-screen arrow points outward from the edge dot it is centred on.
        /// </summary>
        private static void AddPlayerGlyph(
            Vector3 position,
            Vector3 pointDirection,
            Vector3 offset,
            ref PlayerMarkerCounts counts)
        {
            RadarMesh glyph = EnsureRadarMesh(
                _playerGlyphs,
                counts.Glyphs++,
                "PlayerGlyph",
                EnsureTriangleMesh(),
                _playerGlyphMaterial);
            glyph.Transform.position = position + offset;
            glyph.Transform.rotation = pointDirection.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(pointDirection.normalized, Vector3.up)
                : Quaternion.identity;
            glyph.Transform.localScale = Vector3.one * PLAYER_MARKER_RADIUS_M * _markerScreenScale * PLAYER_GLYPH_SCALE;
        }

        private static void AddPlayerLabel(PlayerControllerB player, Vector3 markerPosition, ref PlayerMarkerCounts counts)
        {
            RadarLabel label = EnsureLabel(_playerLabels, counts.Labels++, "PlayerLabel", PLAYER_LABEL_HEIGHT_M, PlayerLabelColor);
            if (label == null || label.Transform == null || label.Text == null)
                return;

            string text = FormatPlayerLabel(player);
            if (label.Text.text != text)
                label.Text.text = text;

            label.Transform.position =
                markerPosition +
                ResolveRadarScreenUp() * (PLAYER_LABEL_OFFSET_M * _markerScreenScale) +
                Vector3.up * 0.10f;
            label.Transform.rotation = ResolveRadarTextRotation();
            label.Transform.localScale = Vector3.one * _markerScreenScale;
        }

        /// <summary>
        /// Three discrete tiers rather than a continuous ramp. The backing disc
        /// carries the silhouette, so the fill only has to answer "fine / hurt /
        /// about to go down" at a glance, and fixed materials avoid a per-player
        /// MaterialPropertyBlock against a shader this file picks by fallback.
        /// </summary>
        private static Material ResolvePlayerHealthMaterial(PlayerControllerB player)
        {
            float health = player != null ? Mathf.Clamp(player.health, 0f, 100f) : 100f;
            if (health >= PLAYER_HEALTH_HEALTHY_MIN)
                return _playerHealthyMaterial;
            if (health >= PLAYER_HEALTH_HURT_MIN)
                return _playerHurtMaterial;
            return _playerCriticalMaterial;
        }

        private static string FormatPlayerLabel(PlayerControllerB player)
        {
            // Same fallback CctvTargetCache uses for its player targets.
            string name = player != null && !string.IsNullOrWhiteSpace(player.playerUsername)
                ? player.playerUsername.Trim()
                : "PLAYER";
            return name.Length > PLAYER_LABEL_MAX_CHARS
                ? name.Substring(0, PLAYER_LABEL_MAX_CHARS)
                : name;
        }

        /// <summary>
        /// The world direction that maps to "up" on the radar image. The map
        /// camera looks straight down, so its up vector is horizontal and label
        /// and glyph offsets can be expressed in screen terms without knowing the
        /// interior's world orientation.
        /// </summary>
        private static Vector3 ResolveRadarScreenUp()
        {
            if (_mapCamera == null)
                return Vector3.forward;

            Vector3 up = _mapCamera.transform.up;
            up.y = 0f;
            return up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.forward;
        }

        private static Vector3 ResolveRadarScreenRight()
        {
            return Vector3.Cross(Vector3.up, ResolveRadarScreenUp());
        }

        /// <summary>
        /// True when the point falls inside the orthographic view. When it does
        /// not, clampedPosition is that point pushed to the nearest edge with a
        /// margin, and edgeDirection points from there towards the real position.
        /// </summary>
        private static bool IsWithinRadarView(Vector3 position, out Vector3 clampedPosition, out Vector3 edgeDirection)
        {
            clampedPosition = position;
            edgeDirection = Vector3.zero;
            if (_mapCamera == null || !_radarViewValid)
                return true;

            Vector3 viewport = _mapCamera.WorldToViewportPoint(position);
            const float margin = OFFSCREEN_EDGE_MARGIN_VIEWPORT;
            if (viewport.x >= margin && viewport.x <= 1f - margin &&
                viewport.y >= margin && viewport.y <= 1f - margin)
            {
                return true;
            }

            var clampedViewport = new Vector3(
                Mathf.Clamp(viewport.x, margin, 1f - margin),
                Mathf.Clamp(viewport.y, margin, 1f - margin),
                viewport.z);
            clampedPosition = _mapCamera.ViewportToWorldPoint(clampedViewport);
            edgeDirection = position - clampedPosition;
            edgeDirection.y = 0f;
            if (edgeDirection.sqrMagnitude <= 1e-6f)
                edgeDirection = ResolveRadarScreenUp();
            return false;
        }

        /// <summary>
        /// #542 - monsters used to be a red circle, one shade away from the
        /// critical-health player fill, which is the single most expensive
        /// confusion this map can cause. They now own the X: no other class uses
        /// it, and it survives being read at a glance in a crowded corridor.
        /// </summary>
        private static void UpdateEnemyMarkers(CCTVCamera active, Vector3 target, ref int count)
        {
            RoundManager round = RoundManager.Instance;
            if (round == null || round.SpawnedEnemies == null)
                return;

            for (int i = 0; i < round.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = round.SpawnedEnemies[i];
                if (enemy == null || enemy.isEnemyDead || enemy.isInsidePlayerShip)
                    continue;

                if (enemy.isOutside || !IsOnActiveInteriorFloor(enemy.transform.position, target))
                    continue;

                RadarMesh marker = EnsureRadarMesh(
                    _enemyMarkers,
                    count++,
                    "EnemyMarker",
                    EnsureXMesh(),
                    _enemyMarkerMaterial);
                marker.Transform.position = ProjectMarkerPosition(enemy.transform.position);
                marker.Transform.rotation = Quaternion.identity;
                marker.Transform.localScale = Vector3.one * ENEMY_MARKER_RADIUS_M * _markerScreenScale;
            }
        }

        private static void UpdateObjectiveMarkers(CCTVCamera active, Vector3 target, ref int count)
        {
            if (active == null)
                return;

            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> markers = ContractObjectiveProvider.GetMarkers();
            if (markers == null || markers.Count == 0)
                return;

            for (int i = 0; i < markers.Count; i++)
            {
                ContractObjectiveProvider.MarkerSnapshot objective = markers[i];
                if (!ShouldShowObjectiveOnRadar(objective) || !IsOnActiveInteriorFloor(objective.Position, target))
                    continue;

                RadarMesh marker = EnsureRadarMesh(
                    _objectiveMarkers,
                    count++,
                    "ObjectiveMarker",
                    EnsureDiamondMesh(),
                    _objectiveMarkerMaterial);
                marker.Transform.position = ProjectMarkerPosition(objective.Position);
                marker.Transform.rotation = Quaternion.identity;
                // The Radius term is what makes a large objective room read as
                // large, so it stays; both it and the floor under it are re-based
                // on the new objective size.
                float radius = Mathf.Max(
                    OBJECTIVE_MARKER_RADIUS_M,
                    objective.Radius * OBJECTIVE_RADIUS_TERM_SCALE);
                marker.Transform.localScale = Vector3.one * radius * _markerScreenScale;
            }
        }

        private static void UpdateFacilityMarkers(Vector3 target, ref int markerCount, ref int labelCount)
        {
            IReadOnlyList<FacilityRadarMarkerProvider.MarkerSnapshot> fixtures = FacilityRadarMarkerProvider.GetMarkers();
            for (int i = 0; i < fixtures.Count; i++)
            {
                FacilityRadarMarkerProvider.MarkerSnapshot fixture = fixtures[i];
                if (fixture.Target == null || !IsOnActiveInteriorFloor(fixture.Position, target))
                    continue;

                bool isMainframe = fixture.Kind == FacilityRadarMarkerProvider.MarkerKind.Mainframe;
                Mesh mesh = isMainframe ? EnsureMainframeMesh() : EnsureStashMesh();
                Material material = isMainframe ? _mainframeMarkerMaterial : _stashMarkerMaterial;
                Color color = isMainframe ? MainframeMarkerColor : StashMarkerColor;
                string shortLabel = isMainframe ? "MF" : "ST";

                RadarMesh marker = EnsureRadarMesh(
                    _facilityMarkers,
                    markerCount++,
                    "FacilityMarker",
                    mesh,
                    material);
                Vector3 markerPosition = ProjectMarkerPosition(fixture.Position);
                marker.Transform.position = markerPosition;
                marker.Transform.rotation = Quaternion.identity;
                marker.Transform.localScale = Vector3.one * FACILITY_MARKER_RADIUS_M * _markerScreenScale;

                RadarLabel label = EnsureLabel(
                    _facilityLabels,
                    labelCount++,
                    "FacilityLabel",
                    FACILITY_LABEL_HEIGHT_M,
                    color);
                if (label?.Text != null)
                {
                    label.Text.text = shortLabel;
                    label.Text.color = color;
                    label.Text.fontSize = FACILITY_LABEL_HEIGHT_M;
                }
                if (label?.Transform != null)
                {
                    float offset = FACILITY_LABEL_OFFSET_M * _markerScreenScale;
                    label.Transform.position = markerPosition + new Vector3(offset, 0.08f, offset * 0.58f);
                    label.Transform.rotation = ResolveRadarTextRotation();
                    label.Transform.localScale = Vector3.one * _markerScreenScale;
                }
            }
        }

        /// <summary>
        /// #542 - loose scrap was the one thing the outline channel showed in the
        /// camera feed but the map never did. Behind its own config bool because a
        /// heavily looted interior is the case where blips become noise.
        /// </summary>
        private static void UpdateItemMarkers(CCTVCamera active, Vector3 target, ref int count)
        {
            if (active == null || !ShowItemBlips)
                return;

            RefreshItemScan();
            for (int i = 0; i < _itemScanBuffer.Count; i++)
            {
                GrabbableObject item = _itemScanBuffer[i];
                if (item == null || item.itemProperties == null)
                    continue;
                // Carried scrap sits under its carrier's blip, so it would only
                // ever be a smudge on the player marker.
                if (item.isHeld || item.isPocketed || item.isHeldByEnemy)
                    continue;
                if (!IsOnActiveInteriorFloor(item.transform.position, target))
                    continue;
                // Items take the same view test as players but none of the edge
                // treatment: forty arrows crowding the border would be noise, not
                // information. It also spares the floor raycast for everything
                // that is out of view anyway.
                if (!IsWithinRadarView(item.transform.position, out _, out _))
                    continue;

                RadarMesh marker = EnsureRadarMesh(
                    _itemMarkers,
                    count++,
                    "ItemMarker",
                    EnsureSquareMesh(),
                    _itemMarkerMaterial);
                marker.Transform.position = ProjectMarkerPosition(item.transform.position);
                marker.Transform.rotation = Quaternion.identity;
                marker.Transform.localScale = Vector3.one * ITEM_MARKER_RADIUS_M * _markerScreenScale;
            }
        }

        /// <summary>
        /// The scene sweep is the only allocating call on the marker path, so it
        /// runs at 1 Hz into a reused list while positions are still read live at
        /// the 6 Hz marker rate. The scrap filter matches RadarOverlay.CollectStats.
        /// </summary>
        private static void RefreshItemScan()
        {
            if (Time.unscaledTime < _nextItemScanAt)
                return;

            _nextItemScanAt = Time.unscaledTime + ITEM_SCAN_INTERVAL;
            _itemScanBuffer.Clear();

            GrabbableObject[] objects = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < objects.Length; i++)
            {
                GrabbableObject item = objects[i];
                if (item == null || item.itemProperties == null) continue;
                if (!item.itemProperties.isScrap) continue;
                if (item.isInShipRoom || item.isInElevator) continue;
                if (!item.isInFactory) continue;
                _itemScanBuffer.Add(item);
            }
        }

        private static bool ShowItemBlips =>
            SurveillanceBootstrap.Config?.ShowRadarItemBlips == null ||
            SurveillanceBootstrap.Config.ShowRadarItemBlips.Value;

        /// <summary>
        /// #542 - this used to run the incoming label through a hardcoded
        /// whitelist, which silently dropped every marker whose wording was not
        /// in it: BREAKER, AUDIT LEVER, PAYLOAD and INCINERATOR all reached the
        /// radar and were thrown away. MoonContractState.GetCctvObjectiveMarkers
        /// is the component that decides what is radar-worthy — it only emits a
        /// marker for a live, unfinished objective — so the only gate left here
        /// is the completion flag. Do not reintroduce a label filter; a new
        /// contract must not have to be added to a list in this file to appear.
        /// </summary>
        private static bool ShouldShowObjectiveOnRadar(ContractObjectiveProvider.MarkerSnapshot objective)
        {
            return !objective.IsComplete;
        }

        private static bool IsOnActiveInteriorFloor(Vector3 position, Vector3 target)
        {
            return Mathf.Abs(position.y - target.y) <= MARKER_FLOOR_WINDOW_M;
        }

        private static Vector3 ProjectMarkerPosition(Vector3 position)
        {
            if (TryProjectToFloor(position, out Vector3 floor))
                return floor + Vector3.up * MARKER_FLOOR_OFFSET_M;
            return position + Vector3.up * MARKER_FLOOR_OFFSET_M;
        }

        private static Vector3 FlattenForward(Vector3 forward)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f)
                return Vector3.zero;
            return forward.normalized;
        }

        private static string ResolveActiveFloorLabel(CCTVCamera active)
        {
            if (active == null)
                return "STANDBY";

            List<float> floorYs = _floorYScratch;
            floorYs.Clear();
            if (_cameras != null)
            {
                for (int i = 0; i < _cameras.Count; i++)
                {
                    CCTVCamera camera = _cameras[i];
                    if (camera == null)
                        continue;

                    AddFloorGroupY(floorYs, ResolveCameraFloorY(camera));
                }
            }

            float activeY = ResolveCameraFloorY(active);
            AddFloorGroupY(floorYs, activeY);
            floorYs.Sort();

            int activeIndex = FindFloorGroupIndex(floorYs, activeY);
            int entranceIndex = ResolveMainEntranceFloorGroupIndex(floorYs);
            int floorRank = Mathf.Max(0, activeIndex) - Mathf.Max(0, entranceIndex);
            return FormatFloorLabel(floorRank);
        }

        private static float ResolveCameraFloorY(CCTVCamera camera)
        {
            if (camera == null)
                return 0f;

            Tile tile = camera.OwningTile;
            if (tile != null && tile.Placement != null)
            {
                Bounds local = DungeonCameraSpawner.ComputeWorldAabbLocal(tile);
                if (local.size.sqrMagnitude < 1e-6f)
                    local = tile.Placement.LocalBounds;
                if (local.size.sqrMagnitude >= 1e-6f)
                {
                    Vector3 floorPoint = new Vector3(local.center.x, local.min.y, local.center.z);
                    return (tile.Placement.Position + tile.Placement.Rotation * floorPoint).y;
                }
                return tile.Placement.Position.y;
            }

            return camera.transform.position.y;
        }

        private static void AddFloorGroupY(List<float> floorYs, float y)
        {
            if (floorYs == null)
                return;

            int existing = FindFloorGroupIndex(floorYs, y);
            if (existing >= 0)
            {
                floorYs[existing] = (floorYs[existing] + y) * 0.5f;
                return;
            }

            floorYs.Add(y);
        }

        private static int FindFloorGroupIndex(List<float> floorYs, float y)
        {
            if (floorYs == null)
                return -1;

            for (int i = 0; i < floorYs.Count; i++)
            {
                if (Mathf.Abs(floorYs[i] - y) <= FLOOR_GROUP_TOLERANCE_M)
                    return i;
            }

            return -1;
        }

        private static int ResolveMainEntranceFloorGroupIndex(List<float> floorYs)
        {
            if (floorYs == null || floorYs.Count == 0)
                return 0;
            if (!TryResolveInteriorMainEntranceY(out float entranceY))
                return 0;

            int best = 0;
            float bestDiff = Mathf.Abs(floorYs[0] - entranceY);
            for (int i = 1; i < floorYs.Count; i++)
            {
                float diff = Mathf.Abs(floorYs[i] - entranceY);
                if (diff >= bestDiff)
                    continue;

                best = i;
                bestDiff = diff;
            }

            return best;
        }

        private static bool TryResolveInteriorMainEntranceY(out float y)
        {
            EntranceTeleport[] entrances = UnityEngine.Object.FindObjectsByType<EntranceTeleport>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            EntranceTeleport best = null;
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null || entrance.isEntranceToBuilding)
                    continue;

                if (best == null || entrance.entranceId == 0)
                {
                    best = entrance;
                    if (entrance.entranceId == 0)
                        break;
                }
            }

            if (best != null)
            {
                Transform entrancePoint = best.entrancePoint != null ? best.entrancePoint : best.transform;
                y = entrancePoint.position.y;
                return true;
            }

            y = 0f;
            return false;
        }

        private static string FormatFloorLabel(int floorRank)
        {
            if (floorRank == 0)
                return "FLOOR 1";
            if (floorRank > 0)
                return "FLOOR " + (floorRank + 1).ToString();
            return "BASEMENT " + (-floorRank).ToString();
        }

        private static RadarMesh EnsureRadarMesh(
            List<RadarMesh> pool,
            int index,
            string name,
            Mesh mesh,
            Material material)
        {
            while (pool.Count <= index)
                pool.Add(CreateRadarMesh(name + "_" + pool.Count.ToString("D2")));

            RadarMesh marker = pool[index];
            if (marker == null || marker.GameObject == null)
            {
                marker = CreateRadarMesh(name + "_" + index.ToString("D2"));
                pool[index] = marker;
            }

            marker.GameObject.SetActive(true);
            marker.Filter.sharedMesh = mesh;
            marker.Renderer.sharedMaterial = material;
            return marker;
        }

        private static RadarMesh CreateRadarMesh(string name)
        {
            var go = new GameObject("LethalCCTV_Radar_" + name);
            if (_markerRoot != null)
                go.transform.SetParent(_markerRoot.transform, worldPositionStays: false);
            SetLayerRecursively(go, MAP_RADAR_LAYER);

            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return new RadarMesh(go, filter, renderer);
        }

        private static RadarLabel EnsureCameraLabel(int index)
        {
            return EnsureLabel(_cameraLabels, index, "CameraLabel", CAMERA_LABEL_HEIGHT_M, CameraLabelColor);
        }

        private static RadarLabel EnsureLabel(
            List<RadarLabel> pool,
            int index,
            string name,
            float fontSize,
            Color color)
        {
            while (pool.Count <= index)
                pool.Add(CreateRadarLabel(name, pool.Count, fontSize, color));

            RadarLabel label = pool[index];
            if (label == null || label.GameObject == null)
            {
                label = CreateRadarLabel(name, index, fontSize, color);
                pool[index] = label;
            }

            label.GameObject.SetActive(true);
            return label;
        }

        private static RadarLabel CreateRadarLabel(string name, int index, float fontSize, Color color)
        {
            var go = new GameObject("LethalCCTV_Radar_" + name + "_" + index.ToString("D2"));
            if (_markerRoot != null)
                go.transform.SetParent(_markerRoot.transform, worldPositionStays: false);
            SetLayerRecursively(go, MAP_RADAR_LAYER);

            TextMeshPro text = go.AddComponent<TextMeshPro>();
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.fontSize = fontSize;
            text.fontStyle = FontStyles.Bold;
            text.overflowMode = TextOverflowModes.Overflow;
            text.enableCulling = false;
            text.text = string.Empty;
            text.color = color;
            text.rectTransform.sizeDelta = new Vector2(4.4f, 1.1f);
            TryAssignHudFont(text);

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            return new RadarLabel(go, text, renderer);
        }

        private static void UpdateCameraLabel(RadarLabel label, CCTVCamera camera, Vector3 markerPosition, bool isActive)
        {
            if (label == null || label.Transform == null || label.Text == null)
                return;

            string text = FormatCameraShortLabel(camera);
            if (label.Text.text != text)
                label.Text.text = text;

            label.Text.color = isActive ? ActiveCameraLabelColor : CameraLabelColor;
            label.Text.fontSize = isActive ? CAMERA_LABEL_HEIGHT_M * 1.15f : CAMERA_LABEL_HEIGHT_M;
            float offset = CAMERA_LABEL_OFFSET_M * _markerScreenScale;
            label.Transform.position = markerPosition + new Vector3(offset, 0.08f, offset * 0.58f);
            label.Transform.rotation = ResolveRadarTextRotation();
            label.Transform.localScale = Vector3.one * _markerScreenScale;
        }

        private static Quaternion ResolveRadarTextRotation()
        {
            if (_mapCamera != null)
                return Quaternion.LookRotation(-_mapCamera.transform.forward, _mapCamera.transform.up);

            return Quaternion.Euler(-90f, 0f, 0f);
        }

        private static string FormatCameraShortLabel(CCTVCamera camera)
        {
            if (camera == null)
                return "CAM";

            int index = camera.CameraIndex >= 0 ? camera.CameraIndex : IndexOfCamera(camera);
            return index >= 0
                ? "C" + (index + 1).ToString("00")
                : "CAM";
        }

        private static int IndexOfCamera(CCTVCamera camera)
        {
            if (camera == null || _cameras == null)
                return -1;

            for (int i = 0; i < _cameras.Count; i++)
            {
                if (ReferenceEquals(_cameras[i], camera))
                    return i;
            }

            return -1;
        }

        private static void DeactivateUnused(List<RadarMesh> pool, int used)
        {
            for (int i = used; i < pool.Count; i++)
            {
                RadarMesh marker = pool[i];
                if (marker != null && marker.GameObject != null)
                    marker.GameObject.SetActive(false);
            }
        }

        private static void DeactivateUnused(List<RadarLabel> pool, int used)
        {
            for (int i = used; i < pool.Count; i++)
            {
                RadarLabel label = pool[i];
                if (label != null && label.GameObject != null)
                    label.GameObject.SetActive(false);
            }
        }

        private static void HideAllMarkers()
        {
            DeactivateUnused(_cameraMarkers, 0);
            DeactivateUnused(_cameraCones, 0);
            DeactivateUnused(_cameraHalos, 0);
            DeactivateUnused(_cameraLabels, 0);
            DeactivateUnused(_playerBackings, 0);
            DeactivateUnused(_playerMarkers, 0);
            DeactivateUnused(_playerFacings, 0);
            DeactivateUnused(_playerGlyphs, 0);
            DeactivateUnused(_playerLabels, 0);
            DeactivateUnused(_enemyMarkers, 0);
            DeactivateUnused(_objectiveMarkers, 0);
            DeactivateUnused(_facilityMarkers, 0);
            DeactivateUnused(_facilityLabels, 0);
            DeactivateUnused(_itemMarkers, 0);
        }

        private static bool EnsureMarkerAssets()
        {
            _circleMesh = _circleMesh != null ? _circleMesh : CreateCircleMesh();
            _ringMesh = _ringMesh != null ? _ringMesh : CreateRingMesh();
            _squareMesh = _squareMesh != null ? _squareMesh : CreateSquareMesh();
            _diamondMesh = _diamondMesh != null ? _diamondMesh : CreateDiamondMesh();
            _mainframeMesh = _mainframeMesh != null ? _mainframeMesh : CreateMainframeMesh();
            _stashMesh = _stashMesh != null ? _stashMesh : CreateStashMesh();
            _triangleMesh = _triangleMesh != null ? _triangleMesh : CreateTriangleMesh();
            _coneMesh = _coneMesh != null ? _coneMesh : CreateConeMesh();
            _xMesh = _xMesh != null ? _xMesh : CreateXMesh();

            _cameraMarkerMaterial = _cameraMarkerMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarCameraMarker",
                IdleCameraMarkerColor,
                transparent: true);
            _activeCameraMarkerMaterial = _activeCameraMarkerMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarActiveCameraMarker",
                ActiveCameraMarkerColor,
                transparent: true);
            // Behind the triangle by queue, and dim enough that it frames the
            // active camera without competing with it for attention.
            _activeCameraHaloMaterial = _activeCameraHaloMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarActiveCameraHalo",
                new Color(
                    ActiveCameraMarkerColor.r,
                    ActiveCameraMarkerColor.g,
                    ActiveCameraMarkerColor.b,
                    0.45f),
                transparent: true,
                renderQueueOffset: -2);
            _cameraConeMaterial = _cameraConeMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarCameraCone",
                new Color(
                    ActiveCameraMarkerColor.r,
                    ActiveCameraMarkerColor.g,
                    ActiveCameraMarkerColor.b,
                    0.30f),
                transparent: true);
            // The player stack shares ZTest Always with everything else here, so
            // draw order comes from render queue alone: backing, then the facing
            // wedge, then the health fill over both, then the glyph on top.
            _playerBackingMaterial = _playerBackingMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarPlayerBacking",
                new Color(0.02f, 0.04f, 0.06f, 0.94f),
                transparent: true,
                renderQueueOffset: -4);
            _playerHealthyMaterial = _playerHealthyMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarPlayerHealthy",
                PlayerHealthyMarkerColor,
                transparent: true,
                renderQueueOffset: -2);
            _playerHurtMaterial = _playerHurtMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarPlayerHurt",
                new Color(1.00f, 0.76f, 0.10f, 0.98f),
                transparent: true,
                renderQueueOffset: -2);
            _playerCriticalMaterial = _playerCriticalMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarPlayerCritical",
                new Color(1.00f, 0.26f, 0.18f, 0.98f),
                transparent: true,
                renderQueueOffset: -2);
            _playerFacingMaterial = _playerFacingMaterial ?? CreateRadarMaterial(
                // Under the fill, not over it: the disc keeps a clean health
                // colour and the wedge reads as a beam leaving it.
                "LethalCCTV_RadarPlayerFacing",
                new Color(0.86f, 0.96f, 1.00f, 0.40f),
                transparent: true,
                renderQueueOffset: -3);
            _playerGlyphMaterial = _playerGlyphMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarPlayerGlyph",
                new Color(0.92f, 0.98f, 1.00f, 0.96f),
                transparent: true);
            _enemyMarkerMaterial = _enemyMarkerMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarEnemyMarker",
                EnemyMarkerColor,
                transparent: true);
            // Yellow, not the orange it used to share byte-for-byte with the
            // active camera and its cone. Orange is now the active camera's
            // alone.
            _objectiveMarkerMaterial = _objectiveMarkerMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarObjectiveMarker",
                ObjectiveMarkerColor,
                transparent: true);
            _mainframeMarkerMaterial = _mainframeMarkerMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarMainframeMarker",
                MainframeMarkerColor,
                transparent: true);
            _stashMarkerMaterial = _stashMarkerMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarStashMarker",
                StashMarkerColor,
                transparent: true);
            // Mint: the one hue not already spoken for by cameras (blue), the
            // active camera (orange), objectives (yellow), players
            // (green/amber/red) or monsters (red).
            _itemMarkerMaterial = _itemMarkerMaterial ?? CreateRadarMaterial(
                "LethalCCTV_RadarItemMarker",
                ItemMarkerColor,
                transparent: true);

            return _circleMesh != null
                && _ringMesh != null
                && _squareMesh != null
                && _diamondMesh != null
                && _mainframeMesh != null
                && _stashMesh != null
                && _triangleMesh != null
                && _coneMesh != null
                && _xMesh != null
                && _cameraMarkerMaterial != null
                && _activeCameraMarkerMaterial != null
                && _activeCameraHaloMaterial != null
                && _cameraConeMaterial != null
                && _playerBackingMaterial != null
                && _playerHealthyMaterial != null
                && _playerHurtMaterial != null
                && _playerCriticalMaterial != null
                && _playerFacingMaterial != null
                && _playerGlyphMaterial != null
                && _enemyMarkerMaterial != null
                && _objectiveMarkerMaterial != null
                && _mainframeMarkerMaterial != null
                && _stashMarkerMaterial != null
                && _itemMarkerMaterial != null;
        }

        private static Mesh EnsureCircleMesh()
        {
            return _circleMesh ?? (_circleMesh = CreateCircleMesh());
        }

        private static Mesh EnsureRingMesh()
        {
            return _ringMesh ?? (_ringMesh = CreateRingMesh());
        }

        private static Mesh EnsureSquareMesh()
        {
            return _squareMesh ?? (_squareMesh = CreateSquareMesh());
        }

        private static Mesh EnsureDiamondMesh()
        {
            return _diamondMesh ?? (_diamondMesh = CreateDiamondMesh());
        }

        private static Mesh EnsureMainframeMesh()
        {
            return _mainframeMesh ?? (_mainframeMesh = CreateMainframeMesh());
        }

        private static Mesh EnsureStashMesh()
        {
            return _stashMesh ?? (_stashMesh = CreateStashMesh());
        }

        private static Mesh EnsureTriangleMesh()
        {
            return _triangleMesh ?? (_triangleMesh = CreateTriangleMesh());
        }

        private static Mesh EnsureConeMesh()
        {
            return _coneMesh ?? (_coneMesh = CreateConeMesh());
        }

        private static Mesh EnsureXMesh()
        {
            return _xMesh ?? (_xMesh = CreateXMesh());
        }

        private static Mesh CreateCircleMesh()
        {
            const int segments = 18;
            Vector3[] vertices = new Vector3[segments + 1];
            int[] triangles = new int[segments * 6];
            vertices[0] = Vector3.zero;
            for (int i = 0; i < segments; i++)
            {
                float angle = (Mathf.PI * 2f * i) / segments;
                vertices[i + 1] = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            }

            int cursor = 0;
            for (int i = 0; i < segments; i++)
            {
                int a = i + 1;
                int b = i == segments - 1 ? 1 : i + 2;
                triangles[cursor++] = 0;
                triangles[cursor++] = a;
                triangles[cursor++] = b;
                triangles[cursor++] = 0;
                triangles[cursor++] = b;
                triangles[cursor++] = a;
            }

            return CreateFlatMesh("LethalCCTV_RadarCircleMesh", vertices, triangles);
        }

        /// <summary>
        /// Hollow counterpart to the circle, at the same outer radius so a
        /// teammate one floor away keeps the same footprint as one on the active
        /// floor and only the fill changes.
        /// </summary>
        private static Mesh CreateRingMesh()
        {
            const int segments = 18;
            const float innerRadius = 0.58f;
            Vector3[] vertices = new Vector3[segments * 2];
            int[] triangles = new int[segments * 12];
            for (int i = 0; i < segments; i++)
            {
                float angle = (Mathf.PI * 2f * i) / segments;
                float sin = Mathf.Sin(angle);
                float cos = Mathf.Cos(angle);
                vertices[i * 2] = new Vector3(sin * innerRadius, 0f, cos * innerRadius);
                vertices[i * 2 + 1] = new Vector3(sin, 0f, cos);
            }

            int cursor = 0;
            for (int i = 0; i < segments; i++)
            {
                int inner = i * 2;
                int outer = inner + 1;
                int nextInner = ((i + 1) % segments) * 2;
                int nextOuter = nextInner + 1;

                // Wound both ways: these render through a double-sided unlit
                // material under an orthographic camera whose facing is fixed by
                // the clone, and every other mesh in this file does the same.
                triangles[cursor++] = inner;
                triangles[cursor++] = outer;
                triangles[cursor++] = nextOuter;
                triangles[cursor++] = inner;
                triangles[cursor++] = nextOuter;
                triangles[cursor++] = outer;

                triangles[cursor++] = inner;
                triangles[cursor++] = nextOuter;
                triangles[cursor++] = nextInner;
                triangles[cursor++] = inner;
                triangles[cursor++] = nextInner;
                triangles[cursor++] = nextOuter;
            }

            return CreateFlatMesh("LethalCCTV_RadarRingMesh", vertices, triangles);
        }

        private static Mesh CreateSquareMesh()
        {
            const float half = 0.78f;
            Vector3[] vertices =
            {
                new Vector3(-half, 0f, -half),
                new Vector3(-half, 0f, half),
                new Vector3(half, 0f, half),
                new Vector3(half, 0f, -half),
            };
            int[] triangles =
            {
                0, 1, 2, 0, 2, 1,
                0, 2, 3, 0, 3, 2,
            };
            return CreateFlatMesh("LethalCCTV_RadarSquareMesh", vertices, triangles);
        }

        private static Mesh CreateDiamondMesh()
        {
            Vector3[] vertices =
            {
                Vector3.zero,
                new Vector3(0f, 0f, 1f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 0f, -1f),
                new Vector3(-1f, 0f, 0f),
            };
            int[] triangles =
            {
                0, 1, 2, 0, 2, 1,
                0, 2, 3, 0, 3, 2,
                0, 3, 4, 0, 4, 3,
                0, 4, 1, 0, 1, 4,
            };
            return CreateFlatMesh("LethalCCTV_RadarDiamondMesh", vertices, triangles);
        }

        /// <summary>A circuit-cross silhouette reserved for the mainframe.</summary>
        private static Mesh CreateMainframeMesh()
        {
            const float armLength = 1.05f;
            const float armHalfWidth = 0.23f;
            Vector3[] vertices =
            {
                new Vector3(-armLength, 0f, -armHalfWidth),
                new Vector3(-armLength, 0f, armHalfWidth),
                new Vector3(armLength, 0f, armHalfWidth),
                new Vector3(armLength, 0f, -armHalfWidth),
                new Vector3(-armHalfWidth, 0f, -armLength),
                new Vector3(-armHalfWidth, 0f, armLength),
                new Vector3(armHalfWidth, 0f, armLength),
                new Vector3(armHalfWidth, 0f, -armLength),
            };
            int[] triangles =
            {
                0, 1, 2, 0, 2, 1,
                0, 2, 3, 0, 3, 2,
                4, 5, 6, 4, 6, 5,
                4, 6, 7, 4, 7, 6,
            };
            return CreateFlatMesh("LethalCCTV_RadarMainframeMesh", vertices, triangles);
        }

        /// <summary>A hollow vault door with a solid central lock, reserved for stashes.</summary>
        private static Mesh CreateStashMesh()
        {
            const float outer = 1f;
            const float inner = 0.58f;
            const float hub = 0.17f;
            Vector3[] vertices =
            {
                new Vector3(-outer, 0f, -outer),
                new Vector3(-outer, 0f, outer),
                new Vector3(outer, 0f, outer),
                new Vector3(outer, 0f, -outer),
                new Vector3(-inner, 0f, -inner),
                new Vector3(-inner, 0f, inner),
                new Vector3(inner, 0f, inner),
                new Vector3(inner, 0f, -inner),
                new Vector3(-hub, 0f, -hub),
                new Vector3(-hub, 0f, hub),
                new Vector3(hub, 0f, hub),
                new Vector3(hub, 0f, -hub),
            };
            int[] triangles =
            {
                0, 1, 5, 0, 5, 1, 0, 5, 4, 0, 4, 5,
                1, 2, 6, 1, 6, 2, 1, 6, 5, 1, 5, 6,
                2, 3, 7, 2, 7, 3, 2, 7, 6, 2, 6, 7,
                3, 0, 4, 3, 4, 0, 3, 4, 7, 3, 7, 4,
                8, 9, 10, 8, 10, 9,
                8, 10, 11, 8, 11, 10,
            };
            return CreateFlatMesh("LethalCCTV_RadarStashMesh", vertices, triangles);
        }

        private static Mesh CreateTriangleMesh()
        {
            Vector3[] vertices =
            {
                new Vector3(0f, 0f, 1f),
                new Vector3(-0.72f, 0f, -0.55f),
                new Vector3(0.72f, 0f, -0.55f),
            };
            int[] triangles = { 0, 1, 2, 0, 2, 1 };
            return CreateFlatMesh("LethalCCTV_RadarTriangleMesh", vertices, triangles);
        }

        private static Mesh CreateConeMesh()
        {
            float halfWidth = Mathf.Tan(CAMERA_CONE_HALF_ANGLE_DEG * Mathf.Deg2Rad) * CAMERA_CONE_LENGTH_M;
            Vector3[] vertices =
            {
                Vector3.zero,
                new Vector3(-halfWidth, 0f, CAMERA_CONE_LENGTH_M),
                new Vector3(halfWidth, 0f, CAMERA_CONE_LENGTH_M),
            };
            int[] triangles = { 0, 1, 2, 0, 2, 1 };
            return CreateFlatMesh("LethalCCTV_RadarConeMesh", vertices, triangles);
        }

        /// <summary>
        /// Two crossed bars on the XZ plane, sized so the arm tips very nearly
        /// reach the unit radius every other marker mesh is authored against —
        /// at the enemy ratio that is a bold X rather than a thin scratch, which
        /// is the whole point of moving monsters off the circle.
        /// </summary>
        private static Mesh CreateXMesh()
        {
            const float armLength = 1.05f;
            const float armHalfWidth = 0.30f;
            float diagonal = Mathf.Sqrt(0.5f);

            Vector3 alongA = new Vector3(diagonal, 0f, diagonal) * armLength;
            Vector3 acrossA = new Vector3(diagonal, 0f, -diagonal) * armHalfWidth;
            Vector3 alongB = new Vector3(diagonal, 0f, -diagonal) * armLength;
            Vector3 acrossB = new Vector3(diagonal, 0f, diagonal) * armHalfWidth;

            Vector3[] vertices =
            {
                -alongA - acrossA,
                -alongA + acrossA,
                alongA + acrossA,
                alongA - acrossA,
                -alongB - acrossB,
                -alongB + acrossB,
                alongB + acrossB,
                alongB - acrossB,
            };
            // Wound both ways, same as every other mesh in this file.
            int[] triangles =
            {
                0, 1, 2, 0, 2, 1,
                0, 2, 3, 0, 3, 2,
                4, 5, 6, 4, 6, 5,
                4, 6, 7, 4, 7, 6,
            };
            return CreateFlatMesh("LethalCCTV_RadarXMesh", vertices, triangles);
        }

        private static Mesh CreateFlatMesh(string name, Vector3[] vertices, int[] triangles)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material CreateRadarMaterial(string name, Color color, bool transparent, int renderQueueOffset = 0)
        {
            Shader shader = Shader.Find("HDRP/Unlit")
                ?? Shader.Find("HDRP/Lit")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Standard");
            if (shader == null)
                return null;

            var material = new Material(shader)
            {
                name = name,
                color = color,
            };

            SetColorIfExists(material, "_BaseColor", color);
            SetColorIfExists(material, "_Color", color);
            SetColorIfExists(material, "_UnlitColor", color);
            SetColorIfExists(material, "_EmissionColor", color);
            SetColorIfExists(material, "_EmissiveColor", color);
            SetColorIfExists(material, "_EmissiveColorLDR", color);
            SetFloatIfExists(material, "_EmissiveIntensity", 1f);
            SetFloatIfExists(material, "_UseEmissiveIntensity", 1f);
            SetFloatIfExists(material, "_DoubleSidedEnable", 1f);
            SetFloatIfExists(material, "_CullMode", (float)CullMode.Off);
            SetFloatIfExists(material, "_CullModeForward", (float)CullMode.Off);
            material.EnableKeyword("_EMISSION");
            material.EnableKeyword("_DOUBLESIDED_ON");

            if (transparent)
            {
                material.SetOverrideTag("RenderType", "Transparent");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.EnableKeyword("_ALPHABLEND_ON");
                SetFloatIfExists(material, "_SurfaceType", 1f);
                SetFloatIfExists(material, "_BlendMode", 0f);
                SetFloatIfExists(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
                SetFloatIfExists(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                SetFloatIfExists(material, "_AlphaSrcBlend", (float)BlendMode.One);
                SetFloatIfExists(material, "_AlphaDstBlend", (float)BlendMode.OneMinusSrcAlpha);
                SetFloatIfExists(material, "_ZWrite", 0f);
                SetFloatIfExists(material, "_TransparentZWrite", 0f);
                SetFloatIfExists(material, "_ZTest", (float)CompareFunction.Always);
                SetFloatIfExists(material, "_ZTestTransparent", (float)CompareFunction.Always);
                material.renderQueue = (int)RenderQueue.Overlay + renderQueueOffset;
            }
            else
            {
                material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                SetFloatIfExists(material, "_SurfaceType", 0f);
                SetFloatIfExists(material, "_ZWrite", 1f);
                material.renderQueue = (int)RenderQueue.Geometry + 20;
            }

            try
            {
                HDMaterial.ValidateMaterial(material);
            }
            catch (System.Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Radar marker material validation skipped for '{name}': {ex.Message}");
            }

            return material;
        }

        private static void SetColorIfExists(Material material, string propertyName, Color color)
        {
            if (material != null && material.HasProperty(propertyName))
                material.SetColor(propertyName, color);
        }

        private static void SetFloatIfExists(Material material, string propertyName, float value)
        {
            if (material != null && material.HasProperty(propertyName))
                material.SetFloat(propertyName, value);
        }

        private static bool TryProjectToFloor(Vector3 position, out Vector3 floor)
        {
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            Vector3 origin = position + Vector3.up * TARGET_FLOOR_RAY_UP_M;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, TARGET_FLOOR_RAY_UP_M + TARGET_FLOOR_RAY_DOWN_M, mask, QueryTriggerInteraction.Ignore))
            {
                floor = hit.point;
                return true;
            }

            floor = position;
            return false;
        }

        private static Transform ResolveItemSystems()
        {
            GameObject itemSystems = GameObject.Find("Systems/GameSystems/ItemSystems");
            return itemSystems != null ? itemSystems.transform : null;
        }

        private static GameObject ResolveMapCameraTemplate(Transform itemSystems, ManualCameraRenderer source)
        {
            Transform byPath = itemSystems != null ? itemSystems.Find("MapCamera") : null;
            if (byPath != null) return byPath.gameObject;
            return source.mapCamera != null ? source.mapCamera.gameObject : null;
        }

        private static GameObject ResolveMapUiTemplate(Transform itemSystems)
        {
            Transform byPath = itemSystems != null ? itemSystems.Find("MapScreenUI") : null;
            if (byPath != null) return byPath.gameObject;

            GameObject byName = GameObject.Find("Systems/GameSystems/ItemSystems/MapScreenUI");
            return byName;
        }

        private static RenderTexture CreateTargetTexture(ManualCameraRenderer source)
        {
            RenderTexture sourceRt = source.cam != null ? source.cam.targetTexture : null;
            int width = sourceRt != null ? sourceRt.width : FALLBACK_WIDTH;
            int height = sourceRt != null ? sourceRt.height : FALLBACK_HEIGHT;
            int depth = sourceRt != null ? sourceRt.depth : 16;
            RenderTextureFormat format = sourceRt != null ? sourceRt.format : RenderTextureFormat.ARGB32;
            var rt = new RenderTexture(width, height, depth, format)
            {
                name = "LethalCCTV_VanillaRadarTexture",
                useDynamicScale = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        private static LineRenderer CreateExitLine(ManualCameraRenderer source)
        {
            if (source.lineFromRadarTargetToExit != null)
            {
                LineRenderer clone = UnityEngine.Object.Instantiate(source.lineFromRadarTargetToExit, _root.transform, worldPositionStays: false);
                clone.name = "LethalCCTV_VanillaRadarExitLine";
                clone.enabled = false;
                return clone;
            }

            var go = new GameObject("LethalCCTV_VanillaRadarExitLine");
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.enabled = false;
            return line;
        }

        private static GameObject CreateHiddenPlaceholder(Transform parent, string name)
        {
            var go = new GameObject("LethalCCTV_RadarPlaceholder_" + name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        private static T CreateHiddenComponent<T>(Transform parent, string name)
            where T : Component
        {
            var go = new GameObject("LethalCCTV_RadarPlaceholder_" + name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            T component = go.AddComponent<T>();
            if (component is Graphic graphic)
            {
                graphic.enabled = false;
                graphic.raycastTarget = false;
                graphic.color = Color.clear;
            }
            return component;
        }

        private static Transform SuppressClonedStationaryMapUi(Transform uiRoot, Transform sourceStationaryUi)
        {
            HideClonedChildIfPresent(uiRoot, sourceStationaryUi != null ? sourceStationaryUi.name : "MapCameraStationaryUI");
            GameObject placeholder = CreateHiddenPlaceholder(uiRoot, "MapCameraStationaryUI");
            placeholder.name = StationaryMapUiPlaceholderName;
            placeholder.SetActive(false);
            return placeholder.transform;
        }

        private static void ValidateManualRendererReferences(ManualCameraRenderer renderer)
        {
            if (renderer == null)
                throw new System.InvalidOperationException("ManualCameraRenderer missing");
            if (renderer.cam == null || renderer.mapCamera == null)
                throw new System.InvalidOperationException("ManualCameraRenderer camera/mapCamera missing");
            if (renderer.shipArrowUI == null)
                throw new System.InvalidOperationException("ManualCameraRenderer shipArrowUI placeholder missing");
            if (renderer.shipArrowPointer == null)
                throw new System.InvalidOperationException("ManualCameraRenderer shipArrowPointer placeholder missing");
            if (renderer.shipIcon == null)
                throw new System.InvalidOperationException("ManualCameraRenderer shipIcon placeholder missing");
            if (renderer.lineFromRadarTargetToExit == null)
                throw new System.InvalidOperationException("ManualCameraRenderer exit line placeholder missing");
            if (renderer.radarTargets == null || renderer.radarTargets.Count == 0 || renderer.radarTargets[0].transform == null)
                throw new System.InvalidOperationException("ManualCameraRenderer radar target placeholder missing");
        }

        private static GameObject ResolveChildGameObject(Transform root, GameObject sourceObject, string fallbackName)
        {
            Transform found = FindDeepChild(root, sourceObject != null ? sourceObject.name : null);
            if (found != null) return found.gameObject;

            var go = new GameObject(fallbackName);
            go.transform.SetParent(root, worldPositionStays: false);
            return go;
        }

        private static Transform ResolveChildTransform(Transform root, Transform sourceTransform, string fallbackName)
        {
            Transform found = FindDeepChild(root, sourceTransform != null ? sourceTransform.name : null);
            if (found != null) return found;

            var go = new GameObject(fallbackName);
            go.transform.SetParent(root, worldPositionStays: false);
            return go.transform;
        }

        private static T ResolveChildComponent<T>(Transform root, Component sourceComponent, string fallbackName)
            where T : Component
        {
            Transform found = FindDeepChild(root, sourceComponent != null ? sourceComponent.name : null);
            if (found != null)
            {
                T component = found.GetComponent<T>();
                if (component != null) return component;
            }

            var go = new GameObject(fallbackName);
            go.transform.SetParent(root, worldPositionStays: false);
            return go.AddComponent<T>();
        }

        private static Transform FindDeepChild(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                Transform found = FindDeepChild(child, name);
                if (found != null) return found;
            }
            return null;
        }

        private static void DestroyChildIfPresent(Transform root, string name)
        {
            Transform child = FindDeepChild(root, name);
            if (child != null)
                UnityEngine.Object.Destroy(child.gameObject);
        }

        private static void HideClonedChildIfPresent(Transform root, Component sourceComponent, string fallbackName)
        {
            HideClonedChildIfPresent(root, sourceComponent != null ? sourceComponent.name : fallbackName);
        }

        private static void HideClonedChildIfPresent(Transform root, GameObject sourceObject, string fallbackName)
        {
            HideClonedChildIfPresent(root, sourceObject != null ? sourceObject.name : fallbackName);
        }

        private static void HideClonedChildIfPresent(Transform root, string name)
        {
            Transform child = FindDeepChild(root, name);
            if (child == null)
                return;

            child.gameObject.SetActive(false);
            Graphic[] graphics = child.GetComponentsInChildren<Graphic>(includeInactive: true);
            for (int i = 0; i < graphics.Length; i++)
            {
                if (graphics[i] == null) continue;
                graphics[i].enabled = false;
                graphics[i].raycastTarget = false;
            }
        }

        private static void TrySetUntagged(GameObject go)
        {
            if (go == null) return;
            try
            {
                go.tag = "Untagged";
            }
            catch
            {
            }
        }

        private static void DisableClonedAnimators(GameObject root)
        {
            if (root == null)
                return;

            Animator[] animators = root.GetComponentsInChildren<Animator>(includeInactive: true);
            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator == null)
                    continue;

                try { animator.cullingMode = AnimatorCullingMode.CullCompletely; } catch { }
                try { animator.enabled = false; } catch { }
            }
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            if (go == null) return;
            go.layer = layer;
            Transform transform = go.transform;
            for (int i = 0; i < transform.childCount; i++)
                SetLayerRecursively(transform.GetChild(i).gameObject, layer);
        }

        private static void TryAssignHudFont(TMP_Text target)
        {
            if (target == null)
                return;

            try
            {
                HUDManager hud = HUDManager.Instance;
                TextMeshProUGUI[] tips = hud != null ? hud.controlTipLines : null;
                if (tips != null && tips.Length > 0 && tips[0] != null && tips[0].font != null)
                    target.font = tips[0].font;
            }
            catch { }
        }

        private static void ShutdownPartial()
        {
            // #502 — unbind before release. _mapCamera is a clone under _root,
            // and _root is only Destroy()ed further down (deferred to end of
            // frame), so the camera outlives the texture it targets.
            if (_mapCamera != null)
                _mapCamera.targetTexture = null;
            if (_texture != null)
            {
                _texture.Release();
                UnityEngine.Object.Destroy(_texture);
            }
            _texture = null;

            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _targetObject = null;
            _markerRoot = null;
            _renderer = null;
            _mapCamera = null;
            _baseOrthographicSize = 0f;
            _originalCullingMask = 0;
            _interiorRadarChecked = false;
            _interiorHasRadarSprites = true;
            _loggedInteriorFallback = false;
            _nextTargetUpdateAt = 0f;
            ClearMarkerPools();
        }

        private static void ClearMarkerPools()
        {
            _cameraMarkers.Clear();
            _cameraCones.Clear();
            _cameraHalos.Clear();
            _cameraLabels.Clear();
            _playerBackings.Clear();
            _playerMarkers.Clear();
            _playerFacings.Clear();
            _playerGlyphs.Clear();
            _playerLabels.Clear();
            _enemyMarkers.Clear();
            _objectiveMarkers.Clear();
            _facilityMarkers.Clear();
            _facilityLabels.Clear();
            _itemMarkers.Clear();
            // The GameObjects the buffer's entries belong to are the level's, not
            // ours, but a stale list would outlive the interior it was scanned in.
            _itemScanBuffer.Clear();
            _nextItemScanAt = 0f;
        }

        private static void DestroyMarkerResources()
        {
            DestroyMaterial(ref _cameraMarkerMaterial);
            DestroyMaterial(ref _activeCameraMarkerMaterial);
            DestroyMaterial(ref _activeCameraHaloMaterial);
            DestroyMaterial(ref _cameraConeMaterial);
            DestroyMaterial(ref _playerBackingMaterial);
            DestroyMaterial(ref _playerHealthyMaterial);
            DestroyMaterial(ref _playerHurtMaterial);
            DestroyMaterial(ref _playerCriticalMaterial);
            DestroyMaterial(ref _playerFacingMaterial);
            DestroyMaterial(ref _playerGlyphMaterial);
            DestroyMaterial(ref _enemyMarkerMaterial);
            DestroyMaterial(ref _objectiveMarkerMaterial);
            DestroyMaterial(ref _mainframeMarkerMaterial);
            DestroyMaterial(ref _stashMarkerMaterial);
            DestroyMaterial(ref _itemMarkerMaterial);
            DestroyMesh(ref _circleMesh);
            DestroyMesh(ref _ringMesh);
            DestroyMesh(ref _squareMesh);
            DestroyMesh(ref _diamondMesh);
            DestroyMesh(ref _mainframeMesh);
            DestroyMesh(ref _stashMesh);
            DestroyMesh(ref _triangleMesh);
            DestroyMesh(ref _coneMesh);
            DestroyMesh(ref _xMesh);
        }

        private static void DestroyMaterial(ref Material material)
        {
            if (material == null) return;
            UnityEngine.Object.Destroy(material);
            material = null;
        }

        private static void DestroyMesh(ref Mesh mesh)
        {
            if (mesh == null) return;
            UnityEngine.Object.Destroy(mesh);
            mesh = null;
        }

        private static void LogReadyOnce()
        {
            if (_loggedReady) return;
            _loggedReady = true;
            _loggedMissing = false;
            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Vanilla radar feed initialized; CCTV radar inset is using cloned MapCamera output with schematic fallback available.");
        }

        private static void LogMissingOnce(string reason)
        {
            if (_loggedMissing) return;
            _loggedMissing = true;
            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Vanilla radar feed waiting: {reason}; schematic fallback remains active.");
        }

        private sealed class RadarMesh
        {
            internal readonly GameObject GameObject;
            internal readonly Transform Transform;
            internal readonly MeshFilter Filter;
            internal readonly MeshRenderer Renderer;

            internal RadarMesh(GameObject gameObject, MeshFilter filter, MeshRenderer renderer)
            {
                GameObject = gameObject;
                Transform = gameObject != null ? gameObject.transform : null;
                Filter = filter;
                Renderer = renderer;
            }
        }

        private sealed class RadarLabel
        {
            internal readonly GameObject GameObject;
            internal readonly Transform Transform;
            internal readonly TextMeshPro Text;
            internal readonly MeshRenderer Renderer;

            internal RadarLabel(GameObject gameObject, TextMeshPro text, MeshRenderer renderer)
            {
                GameObject = gameObject;
                Transform = gameObject != null ? gameObject.transform : null;
                Text = text;
                Renderer = renderer;
            }
        }
    }
}
