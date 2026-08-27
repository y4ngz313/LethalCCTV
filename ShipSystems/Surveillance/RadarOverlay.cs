using System.Collections.Generic;
using DunGen;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class RadarOverlay
    {
        private const int MAP_TEXTURE_WIDTH = 2048;
        private const int MAP_TEXTURE_HEIGHT = 864;
        private const int GRID_SPACING_PX = 96;
        private const float MAP_PADDING_M = 2f;
        private const float MAP_PADDING_RATIO = 0.020f;
        private const float MIN_MAP_SPAN_M = 34f;
        private const float PAGE_REGION_RADIUS_M = 44f;
        private const float RADAR_VIEW_FILL_HEADROOM = 0.96f;
        private const float STATS_REFRESH_INTERVAL = 0.75f;
        private const float FLOOR_BAND_MERGE_M = 7.5f;
        private const float INTERMEDIATE_FLOOR_NOISE_MAX_AREA_RATIO = 0.22f;
        private const int INTERMEDIATE_FLOOR_NOISE_MAX_TILES = 2;

        private static readonly Color32 COLOR_BG = new Color32(5, 13, 9, 255);
        private static readonly Color32 COLOR_GRID = new Color32(10, 34, 26, 255);
        private static readonly Color32 COLOR_TILE_FILL = new Color32(0, 50, 29, 255);
        private static readonly Color32 COLOR_TILE_FILL_PRIMARY = new Color32(0, 70, 38, 255);
        private static readonly Color32 COLOR_BRIDGE = new Color32(0, 128, 78, 255);
        private static readonly Color32 COLOR_TILE = new Color32(0, 238, 105, 255);
        private static readonly Color32 COLOR_TILE_SECONDARY = new Color32(0, 170, 88, 255);
        private static readonly Color32 COLOR_OPENING = new Color32(0, 212, 91, 255);
        private static readonly Color32 COLOR_LABEL = new Color32(255, 184, 46, 255);
        private static readonly Color32 COLOR_ALL = new Color32(56, 148, 214, 255);
        private static readonly Color32 COLOR_PAGE = new Color32(255, 184, 46, 255);
        private static readonly Color32 COLOR_ACTIVE = new Color32(64, 236, 96, 255);
        private static readonly Color32 COLOR_FORWARD = new Color32(106, 224, 126, 255);
        private static readonly Color32 COLOR_PLAYER = new Color32(190, 240, 255, 255);
        private static readonly Color32 COLOR_ENEMY = new Color32(255, 68, 68, 255);
        private static readonly Color32 COLOR_CONTRACT = new Color32(255, 226, 52, 255);
        private static readonly Color32 COLOR_CONTRACT_DANGER = new Color32(255, 76, 62, 255);
        private static readonly Color32 COLOR_SUPPORT = new Color32(55, 255, 105, 255);
        private static readonly Color32 COLOR_SUPPORT_CORE = new Color32(190, 255, 205, 255);
        private static readonly Color32 COLOR_STASH = new Color32(72, 202, 255, 255);
        private static readonly Color32 COLOR_STASH_CORE = new Color32(215, 246, 255, 255);

        private static IReadOnlyList<CCTVCamera> _cameras;
        private static readonly List<TileFootprint> _tileFootprints = new List<TileFootprint>(64);
        private static readonly List<Vector3> _mapPoints = new List<Vector3>(128);
        private static readonly List<FloorBand> _floorBands = new List<FloorBand>(8);
        private static readonly HashSet<Tile> _seenTiles = new HashSet<Tile>();
        private static Texture2D _mapTexture;
        private static Color32[] _pixels;
        private static Color32[] _staticPixels;
        private static MapBounds _cachedBounds;
        private static bool _dirty = true;
        private static bool _staticDirty = true;
        private static int _cachedFloorIndex = -1;
        private static float _nextStatsRefreshTime;
        private static bool _hasCachedStats;
        private static StatsSnapshot _cachedStats;

        internal static Texture ResolveMapTexture()
        {
            EnsureTexture();
            if (_dirty) Redraw();
            return _mapTexture;
        }

        /// <summary>
        /// #305 — pays the first full map raster (static rebuild + dynamic pass,
        /// ~116ms in the 2026-08-05 evidence session) at camera-assignment time,
        /// inside the round-start load, so the first focus entry only runs the
        /// cheap dynamic redraw.
        /// </summary>
        internal static void Prewarm()
        {
            if (_cameras == null || _cameras.Count == 0) return;
            try
            {
                ResolveMapTexture();
            }
            catch (System.Exception ex)
            {
                Y4NGZCompany.Bootstrap.SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Radar map prewarm skipped: {ex.Message}");
            }
        }

        internal static string GetActiveInteriorFloorLabel()
        {
            CCTVCamera active = MonitorFocus.GetActiveCamera();
            if (active != null) return "RADAR MAP  " + active.ResolvedLabel;
            if (!TryResolveActiveFloorRank(out int activeFloorRank)) return "RADAR MAP";
            return "RADAR MAP  " + FormatFloorLabel(activeFloorRank);
        }

        internal static void SetCameras(IReadOnlyList<CCTVCamera> cameras)
        {
            _cameras = cameras;
            RebuildTileFootprints();
            _staticDirty = true;
            _dirty = true;
            _nextStatsRefreshTime = 0f;
            _hasCachedStats = false;
        }

        internal static void ClearCameras()
        {
            _cameras = null;
            _tileFootprints.Clear();
            _mapPoints.Clear();
            _floorBands.Clear();
            _staticDirty = true;
            _dirty = true;
            FacilityRadarMarkerProvider.Clear();
        }

        internal static void UpdateHighlights()
        {
            _dirty = true;
        }

        internal static void Shutdown()
        {
            _cameras = null;
            _tileFootprints.Clear();
            _mapPoints.Clear();
            _floorBands.Clear();
            _seenTiles.Clear();
            FacilityRadarMarkerProvider.Clear();
            if (_mapTexture != null)
            {
                UnityEngine.Object.Destroy(_mapTexture);
                _mapTexture = null;
            }
            _pixels = null;
            _staticPixels = null;
            _dirty = true;
            _staticDirty = true;
            _cachedFloorIndex = -1;
            _nextStatsRefreshTime = 0f;
            _hasCachedStats = false;
            VanillaRadarFeed.Shutdown();
        }

        internal static StatsSnapshot CollectStats()
        {
            if (_hasCachedStats && Time.unscaledTime < _nextStatsRefreshTime)
                return _cachedStats;

            int scrap = 0;
            GrabbableObject[] scrapObjects = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < scrapObjects.Length; i++)
            {
                GrabbableObject item = scrapObjects[i];
                if (item == null || item.itemProperties == null) continue;
                if (!item.itemProperties.isScrap) continue;
                if (item.isInShipRoom || item.isInElevator) continue;
                if (!item.isInFactory) continue;
                scrap += Mathf.Max(0, item.scrapValue);
            }

            int players = 0;
            StartOfRound sor = StartOfRound.Instance;
            if (sor != null && sor.allPlayerScripts != null)
            {
                for (int i = 0; i < sor.allPlayerScripts.Length; i++)
                {
                    PlayerControllerB player = sor.allPlayerScripts[i];
                    if (player == null) continue;
                    if (!player.isPlayerControlled || player.isPlayerDead) continue;
                    if (!player.isInsideFactory) continue;
                    players++;
                }
            }

            int enemies = 0;
            RoundManager round = RoundManager.Instance;
            if (round != null && round.SpawnedEnemies != null)
            {
                for (int i = 0; i < round.SpawnedEnemies.Count; i++)
                {
                    EnemyAI enemy = round.SpawnedEnemies[i];
                    if (enemy == null || enemy.isEnemyDead) continue;
                    if (enemy.isInsidePlayerShip) continue;
                    if (enemy.isOutside) continue;
                    enemies++;
                }
            }

            _cachedStats = new StatsSnapshot(scrap, players, enemies);
            _hasCachedStats = true;
            _nextStatsRefreshTime = Time.unscaledTime + STATS_REFRESH_INTERVAL;
            return _cachedStats;
        }

        private static void EnsureTexture()
        {
            if (_mapTexture != null && _pixels != null && _staticPixels != null) return;
            _mapTexture = new Texture2D(MAP_TEXTURE_WIDTH, MAP_TEXTURE_HEIGHT, TextureFormat.RGBA32, false)
            {
                name = "LethalCCTV_RadarSchematic",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _pixels = new Color32[MAP_TEXTURE_WIDTH * MAP_TEXTURE_HEIGHT];
            _staticPixels = new Color32[MAP_TEXTURE_WIDTH * MAP_TEXTURE_HEIGHT];
            _dirty = true;
            _staticDirty = true;
        }

        private static void RebuildTileFootprints()
        {
            _tileFootprints.Clear();
            _mapPoints.Clear();
            _seenTiles.Clear();

            if (_cameras == null) return;

            var cameraTiles = new HashSet<Tile>();
            for (int i = 0; i < _cameras.Count; i++)
            {
                CCTVCamera camera = _cameras[i];
                if (camera == null) continue;
                _mapPoints.Add(camera.transform.position);
                if (camera.OwningTile != null) cameraTiles.Add(camera.OwningTile);
            }

            Dungeon dungeon = ResolveDungeonOrNull();
            IReadOnlyList<Tile> allTiles = dungeon != null ? dungeon.AllTiles : null;
            if (allTiles != null && allTiles.Count > 0)
            {
                for (int i = 0; i < allTiles.Count; i++)
                {
                    Tile tile = allTiles[i];
                    AddTileFootprint(tile, cameraTiles.Contains(tile));
                }
                AssignFloorIndices();
                return;
            }

            foreach (Tile tile in cameraTiles)
            {
                AddTileFootprint(tile, primary: true);
            }
            AssignFloorIndices();
        }

        private static void AddTileFootprint(Tile tile, bool primary)
        {
            if (tile == null || _seenTiles.Contains(tile) || tile.Placement == null) return;
            _seenTiles.Add(tile);

            Bounds local = DungeonCameraSpawner.ComputeWorldAabbLocal(tile);
            if (local.size.sqrMagnitude < 1e-6f)
            {
                local = tile.Placement.LocalBounds;
            }
            if (local.size.sqrMagnitude < 1e-6f) return;

            Vector3[] corners = new Vector3[4];
            Vector3 min = local.min;
            Vector3 max = local.max;
            corners[0] = ToWorld(tile, new Vector3(min.x, local.center.y, min.z));
            corners[1] = ToWorld(tile, new Vector3(max.x, local.center.y, min.z));
            corners[2] = ToWorld(tile, new Vector3(max.x, local.center.y, max.z));
            corners[3] = ToWorld(tile, new Vector3(min.x, local.center.y, max.z));
            for (int c = 0; c < corners.Length; c++) _mapPoints.Add(corners[c]);
            DoorwayMarker[] doorways = CollectDoorwayMarkers(tile);
            Vector3 center = ToWorld(tile, local.center);
            float floorY = ResolveTileFloorY(tile, local);
            float area = Mathf.Max(1f, Mathf.Abs(local.size.x * local.size.z));
            _tileFootprints.Add(new TileFootprint(tile, corners, center, floorY, area, primary, floorIndex: 0, doorways));
        }

        private static void AssignFloorIndices()
        {
            _floorBands.Clear();
            if (_tileFootprints.Count == 0) return;

            var sorted = new List<TileFootprint>(_tileFootprints);
            sorted.Sort((a, b) => a.FloorY.CompareTo(b.FloorY));

            for (int i = 0; i < sorted.Count; i++)
            {
                TileFootprint footprint = sorted[i];
                int bandIndex = FindNearestFloorBandIndex(footprint.FloorY);
                if (bandIndex < 0 || Mathf.Abs(_floorBands[bandIndex].CenterY - footprint.FloorY) > FLOOR_BAND_MERGE_M)
                {
                    _floorBands.Add(new FloorBand(footprint.FloorY, footprint.Area));
                    _floorBands.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));
                }
                else
                {
                    _floorBands[bandIndex].Add(footprint.FloorY, footprint.Area);
                }
            }

            CollapseIntermediateFloorNoiseBands();

            int entranceBand = ResolveMainEntranceFloorBandIndex();
            for (int i = 0; i < _floorBands.Count; i++)
            {
                _floorBands[i].Rank = i - entranceBand;
            }

            for (int i = 0; i < _tileFootprints.Count; i++)
            {
                TileFootprint footprint = _tileFootprints[i];
                int band = FindNearestFloorBandIndex(footprint.FloorY);
                int floorRank = band >= 0 ? _floorBands[band].Rank : 0;
                _tileFootprints[i] = footprint.WithFloorIndex(floorRank);
            }
        }

        private static Dungeon ResolveDungeonOrNull()
        {
            RoundManager rm = RoundManager.Instance;
            RuntimeDungeon rtd = rm != null ? rm.dungeonGenerator : null;
            DungeonGenerator gen = rtd != null ? rtd.Generator : null;
            return gen != null ? gen.CurrentDungeon : null;
        }

        private static Vector3 ToWorld(Tile tile, Vector3 local)
        {
            return tile.Placement.Position + tile.Placement.Rotation * local;
        }

        private static float ResolveTileFloorY(Tile tile, Bounds local)
        {
            if (tile == null || tile.Placement == null) return 0f;

            Vector3 basePoint = new Vector3(local.center.x, local.min.y, local.center.z);
            float floorY = ToWorld(tile, basePoint).y;

            if (tile.UsedDoorways != null)
            {
                for (int i = 0; i < tile.UsedDoorways.Count; i++)
                {
                    Doorway doorway = tile.UsedDoorways[i];
                    if (doorway == null) continue;
                    float doorwayY = doorway.transform.position.y;
                    if (Mathf.Abs(doorwayY - floorY) <= FLOOR_BAND_MERGE_M)
                        floorY = Mathf.Min(floorY, doorwayY);
                }
            }

            return floorY;
        }

        private static int ResolveMainEntranceFloorBandIndex()
        {
            if (_floorBands.Count == 0) return 0;
            if (!TryResolveInteriorMainEntrancePosition(out Vector3 entrancePosition))
                return FindNearestFloorBandIndex(0f);

            int bestFootprint = -1;
            float bestScore = float.PositiveInfinity;
            for (int i = 0; i < _tileFootprints.Count; i++)
            {
                TileFootprint footprint = _tileFootprints[i];
                Vector3 delta = footprint.Center - entrancePosition;
                float vertical = Mathf.Abs(footprint.FloorY - entrancePosition.y);
                float score = delta.x * delta.x + delta.z * delta.z + vertical * vertical * 16f;
                if (score >= bestScore) continue;
                bestFootprint = i;
                bestScore = score;
            }

            if (bestFootprint >= 0)
                return FindNearestFloorBandIndex(_tileFootprints[bestFootprint].FloorY);

            return FindNearestFloorBandIndex(entrancePosition.y);
        }

        private static bool TryResolveInteriorMainEntrancePosition(out Vector3 position)
        {
            EntranceTeleport[] entrances = UnityEngine.Object.FindObjectsByType<EntranceTeleport>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            EntranceTeleport best = null;
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null || entrance.isEntranceToBuilding) continue;
                if (best == null || entrance.entranceId == 0)
                {
                    best = entrance;
                    if (entrance.entranceId == 0) break;
                }
            }

            if (best != null)
            {
                position = best.entrancePoint != null ? best.entrancePoint.position : best.transform.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private static string FormatFloorLabel(int floorRank)
        {
            if (floorRank == 0) return "FLOOR 1";
            if (floorRank > 0) return "FLOOR " + (floorRank + 1).ToString();
            if (floorRank == -1) return "BASEMENT";
            if (floorRank == -2) return "SUB-BASEMENT";
            return "SUB-BASEMENT " + (-floorRank - 1).ToString();
        }

        private static void Redraw()
        {
            EnsureTexture();

            bool hasActiveFloor = false;
            int activeFloorRank = 0;
            int activeFloorIndex = int.MinValue;
            // #305 — the static layer (background, grid, footprints, bridges,
            // labels) does not depend on the current page; page/active camera dots
            // are drawn in the dynamic pass below. Keying the static rebuild on
            // CurrentPage forced the full ~100ms raster on every page change.
            if (_staticDirty || _cachedFloorIndex != activeFloorIndex)
            {
                RebuildStaticMap(hasActiveFloor, activeFloorRank, activeFloorIndex);
            }

            System.Array.Copy(_staticPixels, _pixels, _pixels.Length);
            MapBounds bounds = _cachedBounds;

            if (_cameras != null)
            {
                for (int i = 0; i < _cameras.Count; i++)
                {
                    CCTVCamera camera = _cameras[i];
                    if (camera == null) continue;
                    bool isPage = IsCameraBound(camera);
                    bool isActive = ReferenceEquals(camera, MonitorFocus.GetActiveCamera());
                    if (!isPage && !isActive && !IsWorldInsideBounds(camera.transform.position, bounds, 4f)) continue;
                    Color32 color = isActive ? COLOR_ACTIVE : (isPage ? COLOR_PAGE : COLOR_ALL);
                    int radius = isActive ? 8 : (isPage ? 4 : 3);
                    Vector2Int p = WorldToPixel(camera.transform.position, bounds);
                    DrawFilledCircle(p.x, p.y, radius, color);
                    DrawCircle(p.x, p.y, radius + 1, color);
                    if (isActive)
                    {
                        DrawActiveCameraMarker(camera, bounds, p);
                    }
                }
            }

            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> contractMarkers = ContractObjectiveProvider.GetMarkers();
            DrawDynamicEntities(bounds, hasActiveFloor, activeFloorRank);
            DrawSupportFixtures(bounds, hasActiveFloor, activeFloorRank);
            DrawContractObjectiveRoom(contractMarkers, bounds, hasActiveFloor, activeFloorRank);

            _mapTexture.SetPixels32(_pixels);
            _mapTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _dirty = false;
        }

        private static void RebuildStaticMap(bool hasActiveFloor, int activeFloorRank, int activeFloorIndex)
        {
            Fill(COLOR_BG);
            DrawGrid();

            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> contractMarkers = ContractObjectiveProvider.GetMarkers();
            List<Vector3> supportPositions = CollectSupportFixturePositions();
            _cachedBounds = ComputeMapBounds(hasActiveFloor, activeFloorRank, contractMarkers, supportPositions);

            for (int i = 0; i < _tileFootprints.Count; i++)
            {
                TileFootprint footprint = _tileFootprints[i];
                if (!ShouldDrawFootprint(footprint, _cachedBounds, hasActiveFloor, activeFloorRank)) continue;
                DrawFootprintFill(footprint, _cachedBounds);
            }
            DrawConnectionBridges(_cachedBounds, hasActiveFloor, activeFloorRank);
            for (int i = 0; i < _tileFootprints.Count; i++)
            {
                TileFootprint footprint = _tileFootprints[i];
                if (!ShouldDrawFootprint(footprint, _cachedBounds, hasActiveFloor, activeFloorRank)) continue;
                DrawFootprintOutline(footprint, _cachedBounds);
            }
            DrawFloorLabel(_cachedBounds, hasActiveFloor, activeFloorRank);

            System.Array.Copy(_pixels, _staticPixels, _pixels.Length);
            _cachedFloorIndex = activeFloorIndex;
            _staticDirty = false;
        }

        private static void DrawDynamicEntities(MapBounds bounds, bool hasActiveFloor, int activeFloorRank)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor != null && sor.allPlayerScripts != null)
            {
                for (int i = 0; i < sor.allPlayerScripts.Length; i++)
                {
                    PlayerControllerB player = sor.allPlayerScripts[i];
                    if (player == null) continue;
                    if (!player.isPlayerControlled || player.isPlayerDead) continue;
                    if (!player.isInsideFactory) continue;
                    if (!IsWorldOnActiveFloor(player.transform.position.y, hasActiveFloor, activeFloorRank)) continue;
                    if (!IsWorldInsideBounds(player.transform.position, bounds, 2f)) continue;

                    Vector2Int p = WorldToPixel(player.transform.position, bounds);
                    DrawFilledCircle(p.x, p.y, 4, COLOR_PLAYER);
                    DrawCircle(p.x, p.y, 5, COLOR_PLAYER);
                }
            }

            RoundManager round = RoundManager.Instance;
            if (round == null || round.SpawnedEnemies == null) return;
            for (int i = 0; i < round.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = round.SpawnedEnemies[i];
                if (enemy == null || enemy.isEnemyDead) continue;
                if (enemy.isOutside || enemy.isInsidePlayerShip) continue;
                if (!IsWorldOnActiveFloor(enemy.transform.position.y, hasActiveFloor, activeFloorRank)) continue;
                if (!IsWorldInsideBounds(enemy.transform.position, bounds, 2f)) continue;

                Vector2Int p = WorldToPixel(enemy.transform.position, bounds);
                DrawDiamond(p.x, p.y, 5, COLOR_ENEMY);
            }
        }

        private static void DrawContractObjectiveRoom(
            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> markers,
            MapBounds bounds,
            bool hasActiveFloor,
            int activeFloorRank)
        {
            if (markers == null || markers.Count == 0)
                return;

            for (int i = 0; i < markers.Count; i++)
            {
                ContractObjectiveProvider.MarkerSnapshot marker = markers[i];
                if (marker.IsComplete)
                    continue;
                if (!IsWorldOnActiveFloor(marker.Position.y, hasActiveFloor, activeFloorRank))
                    continue;
                if (!IsWorldInsideBounds(marker.Position, bounds, 2f))
                    continue;

                Color32 color = marker.ColorKind == 1 ? COLOR_CONTRACT_DANGER : COLOR_CONTRACT;
                Vector2Int p = WorldToPixel(marker.Position, bounds);
                DrawLine(p.x - 5, p.y - 5, p.x + 5, p.y + 5, color);
                DrawLine(p.x - 5, p.y + 5, p.x + 5, p.y - 5, color);
                break;
            }
        }

        private static void DrawActiveCameraMarker(CCTVCamera camera, MapBounds bounds, Vector2Int origin)
        {
            DrawCircle(origin.x, origin.y, 13, COLOR_ACTIVE);
            DrawCircle(origin.x, origin.y, 17, COLOR_FORWARD);
            DrawLine(origin.x - 22, origin.y, origin.x - 13, origin.y, COLOR_ACTIVE);
            DrawLine(origin.x + 13, origin.y, origin.x + 22, origin.y, COLOR_ACTIVE);
            DrawLine(origin.x, origin.y - 22, origin.x, origin.y - 13, COLOR_ACTIVE);
            DrawLine(origin.x, origin.y + 13, origin.x, origin.y + 22, COLOR_ACTIVE);
            DrawCameraCone(camera, bounds, origin);
        }

        private static void DrawCameraCone(CCTVCamera camera, MapBounds bounds, Vector2Int origin)
        {
            Vector3 fwd = camera.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();

            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 basePos = camera.transform.position;
            Vector2Int center = WorldToPixel(basePos + fwd * 11f, bounds);
            Vector2Int left = WorldToPixel(basePos + fwd * 10f - right * 4.8f, bounds);
            Vector2Int rightPx = WorldToPixel(basePos + fwd * 10f + right * 4.8f, bounds);

            DrawThickLine(origin.x, origin.y, center.x, center.y, COLOR_FORWARD, radius: 2);
            DrawThickLine(origin.x, origin.y, left.x, left.y, COLOR_FORWARD, radius: 1);
            DrawThickLine(origin.x, origin.y, rightPx.x, rightPx.y, COLOR_FORWARD, radius: 1);
            DrawThickLine(left.x, left.y, rightPx.x, rightPx.y, COLOR_BRIDGE, radius: 1);
        }

        private static void Fill(Color32 color)
        {
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = color;
        }

        private static void DrawGrid()
        {
            for (int x = 0; x < MAP_TEXTURE_WIDTH; x += GRID_SPACING_PX)
            {
                DrawLine(x, 0, x, MAP_TEXTURE_HEIGHT - 1, COLOR_GRID);
            }
            for (int y = 0; y < MAP_TEXTURE_HEIGHT; y += GRID_SPACING_PX)
            {
                DrawLine(0, y, MAP_TEXTURE_WIDTH - 1, y, COLOR_GRID);
            }
        }

        private static void DrawFootprintFill(TileFootprint footprint, MapBounds bounds)
        {
            if (footprint.Corners == null || footprint.Corners.Length != 4) return;

            Vector2Int a = WorldToPixel(footprint.Corners[0], bounds);
            Vector2Int b = WorldToPixel(footprint.Corners[1], bounds);
            Vector2Int c = WorldToPixel(footprint.Corners[2], bounds);
            Vector2Int d = WorldToPixel(footprint.Corners[3], bounds);
            FillQuad(a, b, c, d, footprint.Primary ? COLOR_TILE_FILL_PRIMARY : COLOR_TILE_FILL);
        }

        private static void DrawFootprintOutline(TileFootprint footprint, MapBounds bounds)
        {
            if (footprint.Corners == null || footprint.Corners.Length != 4) return;
            Color32 tileColor = footprint.Primary ? COLOR_TILE : COLOR_TILE_SECONDARY;

            Vector2Int a = WorldToPixel(footprint.Corners[0], bounds);
            Vector2Int b = WorldToPixel(footprint.Corners[1], bounds);
            Vector2Int c = WorldToPixel(footprint.Corners[2], bounds);
            Vector2Int d = WorldToPixel(footprint.Corners[3], bounds);

            DoorwayPixel[] doorPx = null;
            if (footprint.Doorways != null && footprint.Doorways.Length > 0)
            {
                doorPx = new DoorwayPixel[footprint.Doorways.Length];
                for (int i = 0; i < footprint.Doorways.Length; i++)
                    doorPx[i] = new DoorwayPixel(WorldToPixel(footprint.Doorways[i].Position, bounds), footprint.Doorways[i]);
            }

            DrawWallEdge(a, b, doorPx, tileColor);
            DrawWallEdge(b, c, doorPx, tileColor);
            DrawWallEdge(c, d, doorPx, tileColor);
            DrawWallEdge(d, a, doorPx, tileColor);
        }

        private static void DrawConnectionBridges(MapBounds bounds, bool hasActiveFloor, int activeFloorRank)
        {
            for (int i = 0; i < _tileFootprints.Count; i++)
            {
                TileFootprint footprint = _tileFootprints[i];
                if (!IsFootprintOnActiveFloor(footprint, hasActiveFloor, activeFloorRank)) continue;
                if (!FootprintIntersectsBounds(footprint, bounds, 2f)) continue;
                if (footprint.Doorways == null) continue;

                for (int d = 0; d < footprint.Doorways.Length; d++)
                {
                    DoorwayMarker marker = footprint.Doorways[d];
                    if (marker.ConnectedTile == null) continue;
                    if (!IsTileOnActiveFloor(marker.ConnectedTile, hasActiveFloor, activeFloorRank)) continue;
                    if (!ShouldDrawConnectionFrom(footprint.Tile, marker.ConnectedTile)) continue;
                    if (!SegmentIntersectsBounds(marker.Position, marker.ConnectedPosition, bounds, 2f)) continue;

                    Vector2Int from = WorldToPixel(marker.Position, bounds);
                    Vector2Int to = WorldToPixel(marker.ConnectedPosition, bounds);
                    DrawThickLine(from.x, from.y, to.x, to.y, COLOR_BRIDGE, radius: 1);
                }
            }
        }

        // Draws a wall segment between two corner pixels, 2px thick,
        // with a gap at each doorway that projects close to the edge.
        private static void DrawWallEdge(Vector2Int from, Vector2Int to, DoorwayPixel[] doorPx, Color32 color)
        {
            const int GAP_HALF = 5;
            const float PERP_SNAP = 12f;

            float dx = to.x - from.x;
            float dy = to.y - from.y;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.5f) return;
            if (len < 9f && (doorPx == null || doorPx.Length == 0)) return;

            float ux = dx / len;
            float uy = dy / len;
            // Perpendicular offset for a readable CRT-width wall stroke.
            int ox = Mathf.RoundToInt(-uy);
            int oy = Mathf.RoundToInt(ux);

            // Project each doorway onto this edge; collect only the ones that lie on it.
            int gapCount = 0;
            float[] gapMids = null;
            if (doorPx != null && doorPx.Length > 0)
            {
                gapMids = new float[doorPx.Length];
                for (int i = 0; i < doorPx.Length; i++)
                {
                    float tdx = doorPx[i].Pixel.x - from.x;
                    float tdy = doorPx[i].Pixel.y - from.y;
                    float along = tdx * ux + tdy * uy;
                    if (along < -GAP_HALF || along > len + GAP_HALF) continue;
                    float perp = Mathf.Abs(tdx * (-uy) + tdy * ux);
                    if (perp > PERP_SNAP) continue;
                    gapMids[gapCount] = along;
                    gapCount++;
                }
            }

            int steps = Mathf.CeilToInt(len);
            for (int s = 0; s <= steps; s++)
            {
                float t = s;
                bool skip = false;
                for (int g = 0; g < gapCount; g++)
                {
                    if (t >= gapMids[g] - GAP_HALF && t <= gapMids[g] + GAP_HALF) { skip = true; break; }
                }
                if (skip) continue;

                int wx = Mathf.RoundToInt(from.x + t * ux);
                int wy = Mathf.RoundToInt(from.y + t * uy);
                SetPixel(wx, wy, color);
                SetPixel(wx + ox, wy + oy, color);
                SetPixel(wx - ox, wy - oy, color);
            }

            // Door gaps remain in the wall outlines, but per-door yellow glyphs
            // are intentionally suppressed to keep radar generation lighter and
            // reduce visual clutter in CCTV focus.
        }

        private static DoorwayMarker[] CollectDoorwayMarkers(Tile tile)
        {
            if (tile?.UsedDoorways == null || tile.UsedDoorways.Count == 0)
                return System.Array.Empty<DoorwayMarker>();
            var list = new List<DoorwayMarker>(tile.UsedDoorways.Count);
            foreach (Doorway dw in tile.UsedDoorways)
            {
                if (dw == null) continue;
                Doorway connected = dw.ConnectedDoorway;
                Tile connectedTile = connected != null ? connected.Tile : null;
                Vector3 connectedPosition = connected != null ? connected.transform.position : dw.transform.position;
                list.Add(new DoorwayMarker(
                    dw.transform.position,
                    connectedTile,
                    connectedPosition));
            }
            return list.ToArray();
        }

        private static bool IsCameraBound(CCTVCamera camera)
        {
            if (camera == null) return false;
            for (int slot = 0; slot < 4; slot++)
            {
                if (ReferenceEquals(QuadCameraAssignment.GetBoundCamera(slot), camera)) return true;
            }
            return false;
        }

        private static bool TryResolveActiveFloorRank(out int floorRank)
        {
            CCTVCamera active = MonitorFocus.GetActiveCamera();
            if (active != null)
            {
                floorRank = ResolveCameraFloorRank(active);
                return true;
            }

            floorRank = 0;
            return false;
        }

        private static int ResolveCameraFloorRank(CCTVCamera camera)
        {
            if (camera == null) return 0;
            Tile tile = camera.OwningTile;
            if (tile != null)
            {
                for (int i = 0; i < _tileFootprints.Count; i++)
                {
                    if (ReferenceEquals(_tileFootprints[i].Tile, tile))
                        return _tileFootprints[i].FloorIndex;
                }
            }
            return ResolveFloorRank(camera.transform.position.y);
        }

        private static int ResolveFloorRank(float worldY)
        {
            int band = FindNearestFloorBandIndex(worldY);
            return band >= 0 ? _floorBands[band].Rank : 0;
        }

        private static bool IsWorldOnActiveFloor(float worldY, bool hasActiveFloor, int activeFloorRank)
        {
            return !hasActiveFloor || ResolveFloorRank(worldY) == activeFloorRank;
        }

        private static bool IsFootprintOnActiveFloor(TileFootprint footprint, bool hasActiveFloor, int activeFloorRank)
        {
            return !hasActiveFloor || footprint.FloorIndex == activeFloorRank;
        }

        private static bool IsTileOnActiveFloor(Tile tile, bool hasActiveFloor, int activeFloorRank)
        {
            if (tile == null) return false;
            for (int i = 0; i < _tileFootprints.Count; i++)
            {
                TileFootprint footprint = _tileFootprints[i];
                if (!ReferenceEquals(footprint.Tile, tile)) continue;
                return IsFootprintOnActiveFloor(footprint, hasActiveFloor, activeFloorRank);
            }
            return false;
        }

        private static bool ShouldDrawConnectionFrom(Tile a, Tile b)
        {
            if (a == null || b == null) return false;
            return true;
        }

        private static int FindNearestFloorBandIndex(float worldY)
        {
            if (_floorBands.Count == 0) return -1;

            int best = 0;
            float bestDiff = Mathf.Abs(worldY - _floorBands[0].CenterY);
            for (int i = 1; i < _floorBands.Count; i++)
            {
                float diff = Mathf.Abs(worldY - _floorBands[i].CenterY);
                if (diff >= bestDiff) continue;
                best = i;
                bestDiff = diff;
            }
            return best;
        }

        private static void CollapseIntermediateFloorNoiseBands()
        {
            if (_floorBands.Count < 3) return;

            float strongestArea = 0f;
            for (int i = 0; i < _floorBands.Count; i++)
            {
                strongestArea = Mathf.Max(strongestArea, _floorBands[i].Weight);
            }
            if (strongestArea <= 0f) return;

            for (int i = 1; i < _floorBands.Count - 1; i++)
            {
                FloorBand band = _floorBands[i];
                if (band.TileCount > INTERMEDIATE_FLOOR_NOISE_MAX_TILES) continue;
                if (band.Weight > strongestArea * INTERMEDIATE_FLOOR_NOISE_MAX_AREA_RATIO) continue;

                FloorBand lower = _floorBands[i - 1];
                FloorBand upper = _floorBands[i + 1];
                float lowerGap = Mathf.Abs(band.CenterY - lower.CenterY);
                float upperGap = Mathf.Abs(upper.CenterY - band.CenterY);
                int target = lowerGap <= upperGap ? i - 1 : i + 1;

                _floorBands[target].Add(band.CenterY, band.Weight, band.TileCount);
                _floorBands.RemoveAt(i);
                i--;
            }
        }

        private static void DrawSupportFixtures(
            MapBounds bounds,
            bool hasActiveFloor,
            int activeFloorRank)
        {
            IReadOnlyList<FacilityRadarMarkerProvider.MarkerSnapshot> markers = FacilityRadarMarkerProvider.GetMarkers();
            for (int i = 0; i < markers.Count; i++)
            {
                FacilityRadarMarkerProvider.MarkerSnapshot marker = markers[i];
                if (marker.Target == null) continue;
                Vector3 position = marker.Position;
                if (!IsWorldOnActiveFloor(position.y, hasActiveFloor, activeFloorRank)) continue;
                if (!IsWorldInsideBounds(position, bounds, 2f)) continue;
                DrawSupportIcon(position, bounds, marker.Kind);
            }
        }

        private static List<Vector3> CollectSupportFixturePositions()
        {
            var positions = new List<Vector3>(8);
            IReadOnlyList<FacilityRadarMarkerProvider.MarkerSnapshot> markers = FacilityRadarMarkerProvider.GetMarkers();
            for (int i = 0; i < markers.Count; i++)
            {
                if (markers[i].Target != null)
                    positions.Add(markers[i].Position);
            }
            return positions;
        }

        private static void DrawSupportIcon(
            Vector3 worldPosition,
            MapBounds bounds,
            FacilityRadarMarkerProvider.MarkerKind kind)
        {
            Vector2Int p = WorldToPixel(worldPosition, bounds);
            if (kind == FacilityRadarMarkerProvider.MarkerKind.Mainframe)
            {
                DrawSquare(p.x, p.y, 10, COLOR_SUPPORT);
                DrawSquare(p.x, p.y, 9, COLOR_SUPPORT);
                DrawLine(p.x - 6, p.y, p.x + 6, p.y, COLOR_SUPPORT_CORE);
                DrawLine(p.x, p.y - 6, p.x, p.y + 6, COLOR_SUPPORT_CORE);
                DrawFilledCircle(p.x, p.y, 2, COLOR_SUPPORT_CORE);
                DrawTinyText(p.x + 13, p.y - 3, "MF", COLOR_SUPPORT);
                return;
            }

            // A double square with a central lock reads as a vault door and cannot be
            // confused with the mainframe's circuit-cross or a contract diamond.
            DrawSquare(p.x, p.y, 10, COLOR_STASH);
            DrawSquare(p.x, p.y, 7, COLOR_STASH);
            DrawFilledCircle(p.x, p.y, 2, COLOR_STASH_CORE);
            DrawLine(p.x, p.y - 2, p.x, p.y - 5, COLOR_STASH_CORE);
            DrawTinyText(p.x + 13, p.y - 3, "ST", COLOR_STASH);
        }

        private static MapBounds ComputeMapBounds(
            bool hasActiveFloor,
            int activeFloorRank,
            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> contractMarkers = null,
            List<Vector3> supportPositions = null)
        {
            List<Vector3> points = CollectInteriorPageRegionBoundsPoints();
            bool usingInteriorFootprint = points.Count > 0;
            if (!usingInteriorFootprint && !HasVisibleMarkers(contractMarkers, hasActiveFloor, activeFloorRank) && !HasVisiblePositions(supportPositions, hasActiveFloor, activeFloorRank))
            {
                return new MapBounds(-MIN_MAP_SPAN_M, MIN_MAP_SPAN_M, -MIN_MAP_SPAN_M, MIN_MAP_SPAN_M);
            }

            Vector3 first = ResolveFirstBoundsPoint(points, contractMarkers, supportPositions, hasActiveFloor, activeFloorRank);
            float minX = first.x;
            float maxX = first.x;
            float minZ = first.z;
            float maxZ = first.z;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }
            if (!usingInteriorFootprint && contractMarkers != null)
            {
                for (int i = 0; i < contractMarkers.Count; i++)
                {
                    Vector3 p = contractMarkers[i].Position;
                    if (!IsWorldOnActiveFloor(p.y, hasActiveFloor, activeFloorRank)) continue;
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
            }
            if (!usingInteriorFootprint && supportPositions != null)
            {
                for (int i = 0; i < supportPositions.Count; i++)
                {
                    Vector3 p = supportPositions[i];
                    if (!IsWorldOnActiveFloor(p.y, hasActiveFloor, activeFloorRank)) continue;
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
            }

            float width = Mathf.Max(maxX - minX, MIN_MAP_SPAN_M);
            float height = Mathf.Max(maxZ - minZ, MIN_MAP_SPAN_M);
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float aspect = MAP_TEXTURE_WIDTH / (float)MAP_TEXTURE_HEIGHT;
            if (width / height > aspect)
            {
                height = width / aspect;
            }
            else
            {
                width = height * aspect;
            }

            float padding = Mathf.Max(MAP_PADDING_M, Mathf.Max(width, height) * MAP_PADDING_RATIO);
            width += padding * 2f;
            height += padding * 2f;
            width *= RADAR_VIEW_FILL_HEADROOM;
            height *= RADAR_VIEW_FILL_HEADROOM;
            return new MapBounds(
                centerX - width * 0.5f,
                centerX + width * 0.5f,
                centerZ - height * 0.5f,
                centerZ + height * 0.5f);
        }

        private static List<Vector3> CollectInteriorBoundsPoints(bool hasActiveFloor, int activeFloorRank)
        {
            var points = new List<Vector3>(_mapPoints.Count);
            for (int i = 0; i < _tileFootprints.Count; i++)
            {
                TileFootprint footprint = _tileFootprints[i];
                if (!IsFootprintOnActiveFloor(footprint, hasActiveFloor, activeFloorRank)) continue;
                if (footprint.Corners == null) continue;
                for (int c = 0; c < footprint.Corners.Length; c++)
                    points.Add(footprint.Corners[c]);
            }

            if (points.Count == 0 && _mapPoints.Count > 0)
            {
                for (int i = 0; i < _mapPoints.Count; i++)
                    points.Add(_mapPoints[i]);
            }

            if (_cameras != null)
            {
                for (int i = 0; i < _cameras.Count; i++)
                {
                    CCTVCamera camera = _cameras[i];
                    if (camera == null) continue;
                    if (hasActiveFloor && ResolveCameraFloorRank(camera) != activeFloorRank) continue;
                    points.Add(camera.transform.position);
                }
            }

            return points;
        }

        private static List<Vector3> CollectInteriorPageRegionBoundsPoints()
        {
            var anchors = new List<CCTVCamera>(4);
            for (int slot = 0; slot < 4; slot++)
            {
                CCTVCamera camera = QuadCameraAssignment.GetBoundCamera(slot);
                if (camera == null) continue;
                anchors.Add(camera);
            }

            if (anchors.Count == 0)
                return CollectInteriorBoundsPoints(false, 0);

            float radiusSq = PAGE_REGION_RADIUS_M * PAGE_REGION_RADIUS_M;
            var points = new List<Vector3>(128);
            for (int i = 0; i < anchors.Count; i++)
                points.Add(anchors[i].transform.position);

            for (int i = 0; i < _tileFootprints.Count; i++)
            {
                TileFootprint footprint = _tileFootprints[i];
                if (footprint.Corners == null) continue;
                if (!IsFootprintNearAnyAnchor(footprint, anchors, radiusSq)) continue;
                for (int c = 0; c < footprint.Corners.Length; c++)
                    points.Add(footprint.Corners[c]);
            }

            if (points.Count < anchors.Count + 4)
                return CollectInteriorBoundsPoints(false, 0);

            return points;
        }

        private static bool IsFootprintNearAnyAnchor(TileFootprint footprint, List<CCTVCamera> anchors, float radiusSq)
        {
            if (anchors == null || footprint.Corners == null)
                return false;

            for (int i = 0; i < anchors.Count; i++)
            {
                CCTVCamera camera = anchors[i];
                if (camera == null) continue;
                if (ResolveCameraFloorRank(camera) != footprint.FloorIndex) continue;

                Vector3 delta = footprint.Center - camera.transform.position;
                if (delta.x * delta.x + delta.z * delta.z <= radiusSq)
                    return true;
            }

            return false;
        }

        private static bool ShouldDrawFootprint(TileFootprint footprint, MapBounds bounds, bool hasActiveFloor, int activeFloorRank)
        {
            return IsFootprintOnActiveFloor(footprint, hasActiveFloor, activeFloorRank)
                   && FootprintIntersectsBounds(footprint, bounds, 2f);
        }

        private static bool FootprintIntersectsBounds(TileFootprint footprint, MapBounds bounds, float margin)
        {
            if (footprint.Corners == null || footprint.Corners.Length == 0)
                return false;

            float minX = footprint.Corners[0].x;
            float maxX = footprint.Corners[0].x;
            float minZ = footprint.Corners[0].z;
            float maxZ = footprint.Corners[0].z;
            for (int i = 1; i < footprint.Corners.Length; i++)
            {
                Vector3 p = footprint.Corners[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            return maxX >= bounds.MinX - margin
                   && minX <= bounds.MaxX + margin
                   && maxZ >= bounds.MinZ - margin
                   && minZ <= bounds.MaxZ + margin;
        }

        private static bool SegmentIntersectsBounds(Vector3 a, Vector3 b, MapBounds bounds, float margin)
        {
            float minX = Mathf.Min(a.x, b.x);
            float maxX = Mathf.Max(a.x, b.x);
            float minZ = Mathf.Min(a.z, b.z);
            float maxZ = Mathf.Max(a.z, b.z);
            return maxX >= bounds.MinX - margin
                   && minX <= bounds.MaxX + margin
                   && maxZ >= bounds.MinZ - margin
                   && minZ <= bounds.MaxZ + margin;
        }

        private static bool IsWorldInsideBounds(Vector3 world, MapBounds bounds, float margin)
        {
            return world.x >= bounds.MinX - margin
                   && world.x <= bounds.MaxX + margin
                   && world.z >= bounds.MinZ - margin
                   && world.z <= bounds.MaxZ + margin;
        }

        private static bool HasVisibleMarkers(IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> markers, bool hasActiveFloor, int activeFloorRank)
        {
            if (markers == null) return false;
            for (int i = 0; i < markers.Count; i++)
            {
                if (IsWorldOnActiveFloor(markers[i].Position.y, hasActiveFloor, activeFloorRank))
                    return true;
            }
            return false;
        }

        private static bool HasVisiblePositions(List<Vector3> positions, bool hasActiveFloor, int activeFloorRank)
        {
            if (positions == null) return false;
            for (int i = 0; i < positions.Count; i++)
            {
                if (IsWorldOnActiveFloor(positions[i].y, hasActiveFloor, activeFloorRank))
                    return true;
            }
            return false;
        }

        private static Vector3 ResolveFirstBoundsPoint(
            List<Vector3> points,
            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> contractMarkers,
            List<Vector3> supportPositions,
            bool hasActiveFloor,
            int activeFloorRank)
        {
            if (points != null && points.Count > 0) return points[0];
            if (contractMarkers != null)
            {
                for (int i = 0; i < contractMarkers.Count; i++)
                {
                    if (IsWorldOnActiveFloor(contractMarkers[i].Position.y, hasActiveFloor, activeFloorRank))
                        return contractMarkers[i].Position;
                }
            }
            if (supportPositions != null)
            {
                for (int i = 0; i < supportPositions.Count; i++)
                {
                    if (IsWorldOnActiveFloor(supportPositions[i].y, hasActiveFloor, activeFloorRank))
                        return supportPositions[i];
                }
            }
            return Vector3.zero;
        }

        private static Vector2Int WorldToPixel(Vector3 world, MapBounds bounds)
        {
            float u = Mathf.InverseLerp(bounds.MinX, bounds.MaxX, world.x);
            float v = Mathf.InverseLerp(bounds.MinZ, bounds.MaxZ, world.z);
            int x = Mathf.Clamp(Mathf.RoundToInt(u * (MAP_TEXTURE_WIDTH - 1)), 0, MAP_TEXTURE_WIDTH - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt((1f - v) * (MAP_TEXTURE_HEIGHT - 1)), 0, MAP_TEXTURE_HEIGHT - 1);
            return new Vector2Int(x, y);
        }

        private static void DrawFilledCircle(int cx, int cy, int radius, Color32 color)
        {
            int r2 = radius * radius;
            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    if (x * x + y * y <= r2) SetPixel(cx + x, cy + y, color);
                }
            }
        }

        private static void DrawCircle(int cx, int cy, int radius, Color32 color)
        {
            int x = radius;
            int y = 0;
            int err = 0;
            while (x >= y)
            {
                SetPixel(cx + x, cy + y, color);
                SetPixel(cx + y, cy + x, color);
                SetPixel(cx - y, cy + x, color);
                SetPixel(cx - x, cy + y, color);
                SetPixel(cx - x, cy - y, color);
                SetPixel(cx - y, cy - x, color);
                SetPixel(cx + y, cy - x, color);
                SetPixel(cx + x, cy - y, color);

                y++;
                if (err <= 0)
                {
                    err += 2 * y + 1;
                }
                if (err > 0)
                {
                    x--;
                    err -= 2 * x + 1;
                }
            }
        }

        private static void DrawDiamond(int cx, int cy, int radius, Color32 color)
        {
            for (int y = -radius; y <= radius; y++)
            {
                int span = radius - Mathf.Abs(y);
                for (int x = -span; x <= span; x++)
                {
                    SetPixel(cx + x, cy + y, color);
                }
            }
        }

        private static void DrawSquare(int cx, int cy, int radius, Color32 color)
        {
            DrawLine(cx - radius, cy - radius, cx + radius, cy - radius, color);
            DrawLine(cx + radius, cy - radius, cx + radius, cy + radius, color);
            DrawLine(cx + radius, cy + radius, cx - radius, cy + radius, color);
            DrawLine(cx - radius, cy + radius, cx - radius, cy - radius, color);
        }

        private static void FillQuad(Vector2Int a, Vector2Int b, Vector2Int c, Vector2Int d, Color32 color)
        {
            FillTriangle(a, b, c, color);
            FillTriangle(a, c, d, color);
        }

        private static void FillTriangle(Vector2Int a, Vector2Int b, Vector2Int c, Color32 color)
        {
            int minX = Mathf.Max(0, Mathf.Min(a.x, Mathf.Min(b.x, c.x)));
            int maxX = Mathf.Min(MAP_TEXTURE_WIDTH - 1, Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
            int minY = Mathf.Max(0, Mathf.Min(a.y, Mathf.Min(b.y, c.y)));
            int maxY = Mathf.Min(MAP_TEXTURE_HEIGHT - 1, Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
            float area = Edge(a, b, c);
            if (Mathf.Abs(area) < 0.5f) return;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new Vector2Int(x, y);
                    float w0 = Edge(b, c, p);
                    float w1 = Edge(c, a, p);
                    float w2 = Edge(a, b, p);
                    if ((w0 >= 0f && w1 >= 0f && w2 >= 0f) ||
                        (w0 <= 0f && w1 <= 0f && w2 <= 0f))
                    {
                        SetPixel(x, y, color);
                    }
                }
            }
        }

        private static float Edge(Vector2Int a, Vector2Int b, Vector2Int c)
        {
            return (c.x - a.x) * (b.y - a.y) - (c.y - a.y) * (b.x - a.x);
        }

        private static void DrawFloorLabel(MapBounds bounds, bool hasActiveFloor, int activeFloorRank)
        {
            if (!hasActiveFloor) return;
            DrawTinyText(20, 18, FormatFloorLabel(activeFloorRank), COLOR_LABEL);
        }

        private static void DrawTinyText(int x, int y, string text, Color32 color)
        {
            if (string.IsNullOrEmpty(text)) return;
            int cursor = x;
            for (int i = 0; i < text.Length; i++)
            {
                DrawTinyChar(cursor, y, text[i], color);
                cursor += 6;
            }
        }

        private static void DrawTinyChar(int x, int y, char ch, Color32 color)
        {
            string[] rows = TinyGlyph(ch);
            if (rows == null) return;
            for (int row = 0; row < rows.Length; row++)
            {
                string line = rows[row];
                for (int col = 0; col < line.Length; col++)
                {
                    if (line[col] != ' ')
                        SetPixel(x + col, y + row, color);
                }
            }
        }

        private static string[] TinyGlyph(char ch)
        {
            switch (char.ToUpperInvariant(ch))
            {
                case 'A': return new[] { " ## ", "#  #", "####", "#  #", "#  #" };
                case 'B': return new[] { "### ", "#  #", "### ", "#  #", "### " };
                case 'E': return new[] { "####", "#   ", "### ", "#   ", "####" };
                case 'F': return new[] { "####", "#   ", "### ", "#   ", "#   " };
                case 'H': return new[] { "#  #", "#  #", "####", "#  #", "#  #" };
                case 'L': return new[] { "#   ", "#   ", "#   ", "#   ", "####" };
                case 'M': return new[] { "#  #", "####", "####", "#  #", "#  #" };
                case 'N': return new[] { "#  #", "## #", "# ##", "#  #", "#  #" };
                case 'O': return new[] { " ## ", "#  #", "#  #", "#  #", " ## " };
                case 'R': return new[] { "### ", "#  #", "### ", "# # ", "#  #" };
                case 'S': return new[] { " ###", "#   ", " ## ", "   #", "### " };
                case 'T': return new[] { "####", " ## ", " ## ", " ## ", " ## " };
                case 'U': return new[] { "#  #", "#  #", "#  #", "#  #", " ## " };
                case '-': return new[] { "    ", "    ", "### ", "    ", "    " };
                case ' ': return new[] { "    ", "    ", "    ", "    ", "    " };
                case '1': return new[] { " #  ", "##  ", " #  ", " #  ", "### " };
                case '2': return new[] { "### ", "   #", " ## ", "#   ", "####" };
                case '3': return new[] { "### ", "   #", " ## ", "   #", "### " };
                case '4': return new[] { "#  #", "#  #", "####", "   #", "   #" };
                case '5': return new[] { "####", "#   ", "### ", "   #", "### " };
                case '6': return new[] { " ## ", "#   ", "### ", "#  #", " ## " };
                case '7': return new[] { "####", "   #", "  # ", " #  ", " #  " };
                case '8': return new[] { " ## ", "#  #", " ## ", "#  #", " ## " };
                case '9': return new[] { " ## ", "#  #", " ###", "   #", " ## " };
                case '0': return new[] { " ## ", "#  #", "#  #", "#  #", " ## " };
                default: return null;
            }
        }

        private static void DrawThickLine(int x0, int y0, int x1, int y1, Color32 color, int radius = 1)
        {
            for (int oy = -radius; oy <= radius; oy++)
            {
                for (int ox = -radius; ox <= radius; ox++)
                {
                    if (ox * ox + oy * oy > radius * radius) continue;
                    DrawLine(x0 + ox, y0 + oy, x1 + ox, y1 + oy, color);
                }
            }
        }

        private static void DrawLine(int x0, int y0, int x1, int y1, Color32 color)
        {
            int dx = Mathf.Abs(x1 - x0);
            int sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0);
            int sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                SetPixel(x0, y0, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }
                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private static void SetPixel(int x, int y, Color32 color)
        {
            if (x < 0 || x >= MAP_TEXTURE_WIDTH || y < 0 || y >= MAP_TEXTURE_HEIGHT) return;
            _pixels[y * MAP_TEXTURE_WIDTH + x] = color;
        }

        private readonly struct TileFootprint
        {
            public readonly Tile Tile;
            public readonly Vector3[] Corners;
            public readonly Vector3 Center;
            public readonly float FloorY;
            public readonly float Area;
            public readonly bool Primary;
            public readonly int FloorIndex;
            public readonly DoorwayMarker[] Doorways;

            public TileFootprint(Tile tile, Vector3[] corners, Vector3 center, float floorY, float area, bool primary, int floorIndex, DoorwayMarker[] doorways)
            {
                Tile = tile;
                Corners = corners;
                Center = center;
                FloorY = floorY;
                Area = area;
                Primary = primary;
                FloorIndex = floorIndex;
                Doorways = doorways;
            }

            public TileFootprint WithFloorIndex(int floorIndex)
            {
                return new TileFootprint(Tile, Corners, Center, FloorY, Area, Primary, floorIndex, Doorways);
            }
        }

        private sealed class FloorBand
        {
            private float _weightedY;
            private float _weight;
            private int _tileCount;

            public int Rank;

            public FloorBand(float y, float weight)
            {
                _weight = Mathf.Max(0.01f, weight);
                _weightedY = y * _weight;
                _tileCount = 1;
            }

            public float CenterY
            {
                get { return _weightedY / Mathf.Max(0.01f, _weight); }
            }

            public float Weight
            {
                get { return _weight; }
            }

            public int TileCount
            {
                get { return _tileCount; }
            }

            public void Add(float y, float weight)
            {
                Add(y, weight, 1);
            }

            public void Add(float y, float weight, int tileCount)
            {
                float safeWeight = Mathf.Max(0.01f, weight);
                _weightedY += y * safeWeight;
                _weight += safeWeight;
                _tileCount += Mathf.Max(1, tileCount);
            }
        }

        private readonly struct DoorwayMarker
        {
            public readonly Vector3 Position;
            public readonly Tile ConnectedTile;
            public readonly Vector3 ConnectedPosition;

            public DoorwayMarker(Vector3 position, Tile connectedTile, Vector3 connectedPosition)
            {
                Position = position;
                ConnectedTile = connectedTile;
                ConnectedPosition = connectedPosition;
            }
        }

        private readonly struct DoorwayPixel
        {
            public readonly Vector2Int Pixel;
            public readonly DoorwayMarker Marker;

            public DoorwayPixel(Vector2Int pixel, DoorwayMarker marker)
            {
                Pixel = pixel;
                Marker = marker;
            }
        }

        private readonly struct MapBounds
        {
            public readonly float MinX;
            public readonly float MaxX;
            public readonly float MinZ;
            public readonly float MaxZ;

            public MapBounds(float minX, float maxX, float minZ, float maxZ)
            {
                MinX = minX;
                MaxX = maxX;
                MinZ = minZ;
                MaxZ = maxZ;
            }
        }

        internal readonly struct StatsSnapshot
        {
            public readonly int FacilityScrapValue;
            public readonly int LiveInteriorPlayers;
            public readonly int LiveInteriorEnemies;

            public StatsSnapshot(int facilityScrapValue, int liveInteriorPlayers, int liveInteriorEnemies)
            {
                FacilityScrapValue = facilityScrapValue;
                LiveInteriorPlayers = liveInteriorPlayers;
                LiveInteriorEnemies = liveInteriorEnemies;
            }
        }
    }
}
