using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DunGen;
using Y4NGZCompany.Facility.Interior.Placement;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Surveillance;
using Y4NGZCompany.Facility.Interior;
namespace Y4NGZCompany.Facility.Security
{
    public static class InteriorAlarmSpawner
    {
        private const float DoorwayPlaneMaxDistanceM = 0.45f;
        // Clearance between the opening top and the bar's BOTTOM edge. Must
        // also clear the decorative doorframe lintel trim that rises above
        // the structural opening on most interiors.
        private const float LightBarAboveOpeningMarginM = 0.22f;
        private const float BarPatchHalfWidthM = 0.45f;
        private const float BarPatchHalfHeightM = 0.12f;
        // Facility entrances (main entrance + fire exits) are NOT DunGen
        // doorways; exclude a cylinder around every interior-side entrance pad.
        private const float EntranceDoorExclusionRadiusM = 3.0f;
        private const float EntranceDoorExclusionHeightM = 4.5f;
        private const float CameraExclusionRadiusM = 1.2f;
        private const float DeferredTeleportPollIntervalSeconds = 0.5f;
        private const float DeferredTeleportTimeoutSeconds = 20f;
        private const int JunctionAlarmMinDegree = 3;
        private const int MaxJunctionAlarms = 3;
        private const float ObjectiveTileToleranceM = 2f;
        private const string AlarmPlacementRole = nameof(InteriorPlacementRole.AlarmWallMounted);
        private static readonly List<GameObject> SpawnedAlarms = new List<GameObject>();
        private static readonly List<Vector3> EntranceDoorPositions = new List<Vector3>();
        private static readonly List<Renderer> DoorRenderers = new List<Renderer>();
        private static InteriorAlarmSpawnRunner _deferredSpawnRunner;
        private static int _spawnGeneration;

#if DEBUG
        internal static System.Action<Doorway, GameObject, Vector3, float, string> PlacementProbe;
#endif

        public static void ResetRound()
        {
            CancelDeferredSpawn();
            for (int i = 0; i < SpawnedAlarms.Count; i++)
            {
                if (SpawnedAlarms[i] != null)
                    Object.Destroy(SpawnedAlarms[i]);
            }
            SpawnedAlarms.Clear();
            EntranceDoorPositions.Clear();
        }

        /// <summary>Spawns the alarm fixtures for this round's registered cameras. #1283: called
        /// from the CamerasReady follow-ups inside the dungeon-finished event, before vanilla
        /// spawns the dungeon-side teleports. The whole pass runs inside this call only when an
        /// inside teleport already exists; otherwise, on every round, it waits for the
        /// network-spawned inside teleports first, because the entrance-door exclusion and the
        /// junction reachability check need their positions.</summary>
        public static void SpawnForRegisteredCameras()
        {
            ResetRound();
            FireExitLightBarLocator.ResetRound();
            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            if (cameras == null) return;

            if (CctvModuleConfig.CctvSecurityWallAlarmFixturesEnabled != null &&
                !CctvModuleConfig.CctvSecurityWallAlarmFixturesEnabled.Value)
            {
                for (int i = 0; i < cameras.Count; i++)
                {
                    CctvSecurityCameraState state = cameras[i];
                    if (state?.CameraComponent != null)
                        CctvSecurityCameraRegistry.SetCameraHasAlarmFixture(state.CameraComponent, true);
                }

                CctvModuleConfig.Log?.LogInfo(
                    "[MoonContracts.CctvSecurity] Wall alarm fixtures disabled by config; cameras remain eligible for hostile security.");
                return;
            }

            if (FireExitLightBarLocator.HasAnyInsideTeleport(out int totalCount, out int insideCount))
            {
                DrainSpawnRoutine();
                return;
            }

            StartDeferredSpawn(totalCount, insideCount);
        }

        /// <summary>#716 A6. Per-frame budget for the sliced alarm fixture pass. Matches
        /// InteriorSupportSpawner.SpawnFrameBudgetMilliseconds so both interior passes read
        /// the same way in a profile capture.</summary>
        private const double SpawnFrameBudgetMilliseconds = 3.0;

        /// <summary>#716 A6. The deferred alarm pass (after the inside-teleport wait) runs on a
        /// host runner and yields whenever the current frame's share exceeds the budget above.
        /// Ordering is unchanged: camera tiles in registry order, then the expansion pass, then
        /// the summary line.</summary>
        private static void SpawnForRegisteredCamerasNow()
        {
            InteriorAlarmSpawnRunner runner = EnsureSpawnRunner();
            if (runner == null)
            {
                // No host available (tests, teardown): run inline, exactly as before.
                DrainSpawnRoutine();
                return;
            }

            runner.StartCoroutine(SpawnForRegisteredCamerasRoutine());
        }

        // #1271: on the runner an exception ended only the runner's coroutine (Unity logs it).
        // Drained inline it would escape into the CamerasReady follow-ups and cancel the
        // monitor frame, so it is logged the same way here and ends only the alarm pass.
        private static void DrainSpawnRoutine()
        {
            IEnumerator inline = SpawnForRegisteredCamerasRoutine();
            try
            {
                while (inline.MoveNext()) { }
            }
            catch (System.Exception ex)
            {
                CctvModuleConfig.Log?.LogError($"[MoonContracts.CctvSecurity] Alarm fixture pass threw: {ex}");
            }
        }

        private static InteriorAlarmSpawnRunner EnsureSpawnRunner()
        {
            if (_deferredSpawnRunner != null)
                return _deferredSpawnRunner;

            try
            {
                GameObject host = new GameObject("LGU_CCTV_AlarmSpawnSlice");
                host.hideFlags = HideFlags.HideAndDontSave;
                _deferredSpawnRunner = host.AddComponent<InteriorAlarmSpawnRunner>();
                return _deferredSpawnRunner;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerator SpawnForRegisteredCamerasRoutine()
        {
            System.Diagnostics.Stopwatch frame = System.Diagnostics.Stopwatch.StartNew();
            FireExitLightBarLocator.ResetRound();
            BuildEntranceDoorPositions();
            // #1271: the entrance cache and the expansion pass are slices of their own on the
            if (frame.Elapsed.TotalMilliseconds >= SpawnFrameBudgetMilliseconds)
            {
                yield return null;
                frame.Restart();
            }
            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            if (cameras == null) yield break;

            var servedTiles = new HashSet<Tile>();
            // servedTiles means "processed"; only these tiles actually hold a spawned bar.
            // The distinction lets a later tile be served by a neighbour's existing
            // fixture instead of failing out of hostile security (#706).
            var fixtureTiles = new HashSet<Tile>();
            int spawned = 0;
            for (int i = 0; i < cameras.Count; i++)
            {
                CctvSecurityCameraState state = cameras[i];
                Tile tile = state?.CameraComponent != null ? ReadOwningTile(state.CameraComponent) : null;
                if (tile == null || servedTiles.Contains(tile)) continue;
                servedTiles.Add(tile);

                if (TrySpawnForTile(tile, out GameObject alarm))
                    fixtureTiles.Add(tile);
                else if (!TrySpawnForNeighborTile(tile, servedTiles, fixtureTiles, out alarm))
                {
                    MarkTileCameras(tile, hasAlarmFixture: false);
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts.CctvSecurity] No valid {AlarmPlacementRole} mount in camera tile '{tile.name}' or its neighbors; tile cameras excluded from hostile security.");
                    continue;
                }

                if (alarm != null)
                {
                    SpawnedAlarms.Add(alarm);
                    spawned++;
                }

                MarkTileCameras(tile, hasAlarmFixture: true);

                if (frame.Elapsed.TotalMilliseconds >= SpawnFrameBudgetMilliseconds)
                {
                    yield return null;
                    frame.Reset();
                    frame.Start();
                }
            }

            if (frame.Elapsed.TotalMilliseconds >= SpawnFrameBudgetMilliseconds)
            {
                yield return null;
                frame.Restart();
            }

            int expansionSpawned = SpawnExpansionAlarms(servedTiles);
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts.CctvSecurity] Spawned {spawned} alarm light bar(s) for {servedTiles.Count} camera tile(s) + {expansionSpawned} expansion fixture(s) (objective/junction tiles).");
        }

        private static void StartDeferredSpawn(int totalCount, int insideCount)
        {
            GameObject runnerObject = new GameObject("LGU_CCTV_DeferredAlarmSpawn");
            runnerObject.hideFlags = HideFlags.HideAndDontSave;
            _deferredSpawnRunner = runnerObject.AddComponent<InteriorAlarmSpawnRunner>();
            int generation = _spawnGeneration;
            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_DEFER start totalTeleports={totalCount} inside={insideCount}; waiting up to {DeferredTeleportTimeoutSeconds:0}s for network-spawned inside teleports.");
            _deferredSpawnRunner.StartCoroutine(WaitForInsideTeleports(generation));
        }

        private static IEnumerator WaitForInsideTeleports(int generation)
        {
            float elapsed = 0f;
            while (elapsed < DeferredTeleportTimeoutSeconds)
            {
                yield return new WaitForSecondsRealtime(DeferredTeleportPollIntervalSeconds);
                if (generation != _spawnGeneration)
                    yield break;

                elapsed = Mathf.Min(
                    DeferredTeleportTimeoutSeconds,
                    elapsed + DeferredTeleportPollIntervalSeconds);
                if (!FireExitLightBarLocator.HasAnyInsideTeleport(out int totalCount, out int insideCount))
                    continue;

                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_DEFER inside teleports appeared after {elapsed:0.0}s totalTeleports={totalCount} inside={insideCount}; running alarm spawn pass.");
                CompleteDeferredSpawn(generation);
                yield break;
            }

            if (generation != _spawnGeneration)
                yield break;

            CctvModuleConfig.Log?.LogWarning(
                $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_DEFER timed out after {DeferredTeleportTimeoutSeconds:0.0}s; running alarm spawn pass with cached/procedural fallback chain.");
            CompleteDeferredSpawn(generation);
        }

        private static void CompleteDeferredSpawn(int generation)
        {
            if (generation != _spawnGeneration)
                return;

            InteriorAlarmSpawnRunner runner = _deferredSpawnRunner;
            _deferredSpawnRunner = null;
            if (runner != null)
                Object.Destroy(runner.gameObject);
            SpawnForRegisteredCamerasNow();
        }

        private static void CancelDeferredSpawn()
        {
            _spawnGeneration++;
            InteriorAlarmSpawnRunner runner = _deferredSpawnRunner;
            _deferredSpawnRunner = null;
            if (runner == null)
                return;

            runner.StopAllCoroutines();
            Object.Destroy(runner.gameObject);
        }

        // Coverage expansion beyond camera tiles: contract-objective rooms
        // and high-traffic junction tiles get siren fixtures even when no
        // camera landed there, so an active alarm is audible/visible along
        // the routes players actually take. Fixtures are purely local
        // (light + audio driven by the global alarm state), so this needs
        // no registry entry and no server gating.
        private static int SpawnExpansionAlarms(HashSet<Tile> servedTiles)
        {
            IReadOnlyList<Tile> allTiles = ReadDungeonTiles();
            if (allTiles == null || allTiles.Count == 0) return 0;

            int spawned = 0;

            // Objective tiles. Containment only, with a small closest-point
            // tolerance — contracts can also place exterior objectives, and
            // an unconditional closest-tile fallback would drag those onto
            // an arbitrary interior tile.
            // Read through the CCTV-side reflective snapshot rather than the contracts
            // plugin's own type (#393): objective markers only exist when Y4NGZCompany is
            // installed, and an empty list is the correct answer when it is not.
            IReadOnlyList<ContractObjectiveProvider.MarkerSnapshot> markers =
                ContractObjectiveProvider.GetMarkers();
            if (markers != null)
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    ContractObjectiveProvider.MarkerSnapshot marker = markers[i];
                    if (marker.IsComplete) continue;
                    if (!TryFindOwningTile(allTiles, marker.Position, out Tile tile)) continue;
                    if (!servedTiles.Add(tile)) continue;

                    // Expansion fixtures are decorative coverage, not eligibility:
                    // only a doorway with a structural header receives one.
                    if (TrySpawnDoorwayLightBar(tile, out GameObject alarm))
                    {
                        SpawnedAlarms.Add(alarm);
                        spawned++;
                        CctvModuleConfig.Log?.LogInfo($"[MoonContracts.CctvSecurity] ALARM_EXPANSION objective='{marker.Label}' tile='{tile.name}'.");
                    }
                }
            }

            // Junction tiles: highest doorway degree first, srcIndex as the
            // deterministic tiebreaker (AllTiles order is canonical across
            // host and clients), capped so corridor-heavy interiors don't
            // fill with sirens.
            var junctions = new List<(int Degree, int SrcIndex, Tile Tile)>();
            List<Vector3> junctionEntrances = InteriorAnchorService.GetInteriorEntrancePositions();
            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile tile = allTiles[i];
                if (tile == null || servedTiles.Contains(tile)) continue;
                int degree = tile.UsedDoorways != null ? tile.UsedDoorways.Count : 0;
                if (degree < JunctionAlarmMinDegree) continue;
                // #550: expansion coverage exists so the alarm is audible along the routes the
                // crew actually takes. A tile sealed off from the entrance is on no route, and a
                // siren there is spent fixture budget nobody will ever hear. Degree is checked
                // first because it is a list count and the verdict can cost a path query.
                if (!InteriorAnchorService.IsTileReachableFromEntrance(tile, junctionEntrances)) continue;
                junctions.Add((degree, i, tile));
            }
            junctions.Sort((a, b) => a.Degree != b.Degree ? b.Degree.CompareTo(a.Degree) : a.SrcIndex.CompareTo(b.SrcIndex));

            int junctionSpawned = 0;
            for (int i = 0; i < junctions.Count && junctionSpawned < MaxJunctionAlarms; i++)
            {
                Tile tile = junctions[i].Tile;
                if (!servedTiles.Add(tile)) continue;

                if (TrySpawnDoorwayLightBar(tile, out GameObject alarm))
                {
                    SpawnedAlarms.Add(alarm);
                    spawned++;
                    junctionSpawned++;
                    CctvModuleConfig.Log?.LogInfo($"[MoonContracts.CctvSecurity] ALARM_EXPANSION junction degree={junctions[i].Degree} tile='{tile.name}'.");
                }
            }

            return spawned;
        }

        private static bool TryFindOwningTile(IReadOnlyList<Tile> allTiles, Vector3 position, out Tile tile)
        {
            tile = null;
            float bestSqrDistance = ObjectiveTileToleranceM * ObjectiveTileToleranceM;
            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile candidate = allTiles[i];
                if (candidate == null) continue;
                if (candidate.Bounds.Contains(position))
                {
                    tile = candidate;
                    return true;
                }

                float sqrDistance = Vector3.SqrMagnitude(candidate.Bounds.ClosestPoint(position) - position);
                if (sqrDistance < bestSqrDistance)
                {
                    bestSqrDistance = sqrDistance;
                    tile = candidate;
                }
            }
            return tile != null;
        }

        private static IReadOnlyList<Tile> ReadDungeonTiles()
        {
            RoundManager rm = RoundManager.Instance;
            RuntimeDungeon rtd = rm != null ? rm.dungeonGenerator : null;
            DungeonGenerator gen = rtd != null ? rtd.Generator : null;
            Dungeon dungeon = gen != null ? gen.CurrentDungeon : null;
            return dungeon != null ? dungeon.AllTiles : null;
        }

        private static bool TrySpawnForTile(Tile tile, out GameObject alarm)
        {
            alarm = null;
            if (tile == null || tile.transform == null) return false;

            // The cloned vanilla fire-exit bar mounts only over a used
            // doorway's centred, structurally supported header.
            if (FireExitLightBarLocator.TryGetTemplate() == null) return false;

            if (TrySpawnDoorwayLightBar(tile, out alarm)) return true;

            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] LIGHTBAR_NO_SURFACE tile='{tile.name}'; no doorway accepted a light bar.");
            return false;
        }

        // Doorway-only fallback for a camera tile whose own doorways all
        // rejected the bar: seat it above a doorway of a directly connected
        // tile instead. The fixture is purely local (light + audio off global
        // alarm state), so a bar one room over still marks the camera's tile
        // as served — audible and visible from the doorway players use.
        private static bool TrySpawnForNeighborTile(
            Tile tile,
            HashSet<Tile> servedTiles,
            HashSet<Tile> fixtureTiles,
            out GameObject alarm)
        {
            alarm = null;
            if (tile == null || tile.UsedDoorways == null) return false;

            for (int i = 0; i < tile.UsedDoorways.Count; i++)
            {
                Doorway doorway = tile.UsedDoorways[i];
                Tile neighbor = doorway != null && doorway.ConnectedDoorway != null
                    ? doorway.ConnectedDoorway.Tile
                    : null;
                if (neighbor == null) continue;

                // A neighbour that already hosts a bar serves this tile too — the
                // fixture is local, one shared doorway away. Only a processed
                // neighbour with no fixture of its own is disqualified (#706).
                if (fixtureTiles.Contains(neighbor))
                {
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_NEIGHBOR_SHARED tile='{tile.name}' -> neighbor='{neighbor.name}' (existing fixture).");
                    return true;
                }

                if (servedTiles.Contains(neighbor)) continue;
                if (!TrySpawnDoorwayLightBar(neighbor, out alarm)) continue;

                fixtureTiles.Add(neighbor);
                servedTiles.Add(neighbor);
                MarkTileCameras(neighbor, hasAlarmFixture: true);
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_NEIGHBOR tile='{tile.name}' -> neighbor='{neighbor.name}'.");
                return true;
            }
            return false;
        }

        // Try each used doorway in order; the first valid centred header wins.
        private static bool TrySpawnDoorwayLightBar(Tile tile, out GameObject alarm)
        {
            alarm = null;
            if (FireExitLightBarLocator.TryGetTemplate() == null) return false;
            if (tile.UsedDoorways == null || tile.UsedDoorways.Count == 0) return false;
            if (!FireExitLightBarLocator.TryComputeBarFrame(
                out Vector3 thinAxisLocal, out Vector3 upAxisLocal, out Vector3 halfSize, out Vector3 visualCenterLocal))
                return false;

            int mask = ResolveCollisionMask();
            for (int i = 0; i < tile.UsedDoorways.Count; i++)
            {
                Doorway doorway = tile.UsedDoorways[i];
                if (doorway == null || doorway.transform == null) continue;

                Vector3 intoTile = -doorway.transform.forward;
                intoTile.y = 0f;
                if (intoTile.sqrMagnitude < 0.001f)
                {
#if DEBUG
                    if (PlacementProbe != null)
                        PlacementProbe(doorway, null, default, float.NaN, "invalid-direction");
#endif
                    continue;
                }
                intoTile.Normalize();

                if (!TryMountAboveOpening(doorway, intoTile, thinAxisLocal, upAxisLocal, halfSize,
                    mask, out Vector3 seat, out Quaternion rotation, out float topY, out string reason))
                {
#if DEBUG
                    if (PlacementProbe != null)
                        PlacementProbe(doorway, null, seat, topY, reason);
#endif
                    continue;
                }

                alarm = BuildLightBar(seat, rotation, tile, doorway, visualCenterLocal, intoTile,
                    out Vector3 actualCenter, out reason);
#if DEBUG
                if (PlacementProbe != null)
                    PlacementProbe(doorway, alarm, alarm != null ? actualCenter : seat, topY, reason);
#endif
                if (alarm == null) continue;

                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_MOUNT tile='{tile.name}' doorway={i} placement=above.");
                return true;
            }

            return false;
        }

        // The socket origin is the opening base, never the opening top.
        private static bool TryMountAboveOpening(Doorway doorway, Vector3 intoTile,
            Vector3 thinAxisLocal, Vector3 upAxisLocal, Vector3 halfSize, int mask,
            out Vector3 position, out Quaternion rotation, out float openingTopY, out string reason)
        {
            position = default;
            rotation = default;
            openingTopY = float.NaN;
            Vector3 doorPos = doorway.transform.position;
            if (!TryResolveOpeningHeight(doorway, out float height))
            {
                reason = "missing-opening-height";
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CctvSecurity] LIGHTBAR_NO_HEIGHT doorway='{doorway.name}'; socket and spawned door prefab have no usable height.");
                return false;
            }

            openingTopY = AlarmPose.TopFromSocket(doorPos.y, height);
            float centerY = openingTopY + LightBarAboveOpeningMarginM + halfSize.y;
            position = new Vector3(doorPos.x, centerY, doorPos.z);
            Vector3 probeOrigin = position + intoTile * 0.6f;
            if (!Physics.Raycast(probeOrigin, -intoTile, out RaycastHit hit, 0.6f, mask, QueryTriggerInteraction.Ignore))
            {
                reason = "no-wall";
                return false;
            }
            if (Mathf.Abs(Vector3.Dot(hit.point - doorPos, intoTile)) > DoorwayPlaneMaxDistanceM)
            {
                reason = "outside-socket-plane";
                return false;
            }
            if (!AlarmSurfacePatch.IsStructuralPatch(in hit,
                new Vector2(BarPatchHalfWidthM, BarPatchHalfHeightM), mask, out string patchReason))
            {
                reason = patchReason;
                return false;
            }

            Vector3 facing = hit.normal;
            facing.y = 0f;
            facing.Normalize();
            Vector3 lateral = doorway.transform.right;
            lateral.y = 0f;
            if (lateral.sqrMagnitude < 0.001f)
            {
                reason = "invalid-lateral";
                return false;
            }
            lateral.Normalize();
            Vector3 candidate = hit.point + facing * (halfSize.z + 0.02f);
            float offset = Vector3.Dot(candidate - doorPos, lateral);
            if (Mathf.Abs(offset) > 0.02f)
            {
                reason = "off-centre-wall";
                return false;
            }
            position = candidate - lateral * offset;
            position.y = centerY;
            if (IsNearEntranceDoor(position) || IsNearRegisteredCamera(position))
            {
                reason = "excluded";
                return false;
            }
            if (IsBarVolumeBlocked(position, facing, halfSize, mask))
            {
                reason = "centred-volume-blocked";
                return false;
            }

            // The vanilla bar's +thin mesh axis points at the mount; -thin faces the room.
            rotation = Quaternion.LookRotation(facing, Vector3.up)
                * Quaternion.Inverse(Quaternion.LookRotation(-thinAxisLocal, upAxisLocal));
            reason = "accepted";
            return true;
        }

        private static bool TryResolveOpeningHeight(Doorway doorway, out float height)
        {
            height = 0f;
            if (doorway.HasSocketAssigned && doorway.Socket != null)
            {
                height = doorway.Socket.Size.y;
                if (height > 0f && !float.IsNaN(height) && !float.IsInfinity(height))
                    return true;
            }

            // DunGen's UsedDoorPrefabInstance is the spawned connector, not
            // DoorPrefabPriority (an int), nor an uninstantiated asset entry.
            // Either side of a used doorway may own that instance.
            if (TryGetRenderedDoorHeight(doorway.UsedDoorPrefabInstance, out height)
                || (doorway.ConnectedDoorway != null
                    && TryGetRenderedDoorHeight(doorway.ConnectedDoorway.UsedDoorPrefabInstance, out height)))
                return true;

            // Some tiles enable authored connector scene objects instead of
            // spawning a prefab; only the active doorway objects qualify.
            return TryGetConnectorSceneHeight(doorway, out height)
                || (doorway.ConnectedDoorway != null
                    && TryGetConnectorSceneHeight(doorway.ConnectedDoorway, out height));
        }

        private static bool TryGetConnectorSceneHeight(Doorway doorway, out float height)
        {
            height = 0f;
            List<GameObject> connectors = doorway.ConnectorSceneObjects;
            if (connectors == null) return false;
            for (int i = 0; i < connectors.Count; i++)
            {
                GameObject connector = connectors[i];
                if (connector != null && connector.activeInHierarchy
                    && TryGetRenderedDoorHeight(connector, out height))
                    return true;
            }
            return false;
        }

        private static bool TryGetRenderedDoorHeight(GameObject instance, out float height)
        {
            height = 0f;
            if (instance == null) return false;
            DoorRenderers.Clear();
            instance.GetComponentsInChildren(false, DoorRenderers);
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < DoorRenderers.Count; i++)
            {
                Renderer renderer = DoorRenderers[i];
                if (renderer == null || !renderer.enabled) continue;
                Bounds bounds = renderer.bounds;
                minY = Mathf.Min(minY, bounds.min.y);
                maxY = Mathf.Max(maxY, bounds.max.y);
            }
            DoorRenderers.Clear();
            height = maxY - minY;
            return height > 0f && !float.IsNaN(height) && !float.IsInfinity(height);
        }


        // Volume clearance for the whole bar body (the old sphere check only
        // covered the thin-axis core, letting the bar overlap pipes and
        // props). The box is pulled slightly off the wall plane and shrunk on
        // the thin axis so the mount wall itself can never self-reject.
        private static bool IsBarVolumeBlocked(Vector3 position, Vector3 facing, Vector3 halfSize, int mask)
        {
            Quaternion boxRot = Quaternion.LookRotation(facing, Vector3.up);
            Vector3 center = position + facing * (halfSize.z * 0.25f);
            Vector3 halfExtents = new Vector3(halfSize.x * 0.85f, halfSize.y * 0.85f, halfSize.z * 0.6f);
            return Physics.CheckBox(center, halfExtents, boxRot, mask, QueryTriggerInteraction.Ignore);
        }

        // A light bar may only mount on static room structure. Door panels,
        // door frames' animated parts, interactables, grabbable props, and
        // terminal-driven objects (big doors, turrets, mines) all move, open,
        // or despawn — a bar seated on them reads as a bug the moment they
        // animate. Rigidbody colliders are movers by definition. Static
        // decorative doors carry none of those components, so door-like
        // object names are rejected as well.
        internal static bool IsForbiddenMountSurface(Collider collider)
        {
            if (collider == null) return true;
            if (collider.attachedRigidbody != null) return true;
            if (collider.GetComponentInParent<DoorLock>() != null) return true;
            if (collider.GetComponentInParent<DunGen.Door>() != null) return true;
            if (collider.GetComponentInParent<InteractTrigger>() != null) return true;
            if (collider.GetComponentInParent<GrabbableObject>() != null) return true;
            if (collider.GetComponentInParent<TerminalAccessibleObject>() != null) return true;
            if (HasDoorLikeName(collider)) return true;
            return false;
        }

        private static readonly string[] DoorLikeNameTokens = { "door", "gate", "shutter", "hatch" };
        // Frame/structure names that legitimately contain a door token: the
        // wall header above an opening often lives under the DunGen doorway
        // prefab ("Doorway_...", "DoorFrame") and must stay mountable.
        private static readonly string[] DoorLikeNameExemptTokens = { "doorway", "doorframe", "door_frame", "door frame", "gateway", "lintel", "header" };

        private static bool HasDoorLikeName(Collider collider)
        {
            Transform current = collider != null ? collider.transform : null;
            for (int depth = 0; current != null && depth < 4; depth++, current = current.parent)
            {
                // Never judge the tile root itself — a tile legitimately
                // named after its doors would forbid every wall in it.
                if (current.GetComponent<Tile>() != null)
                    break;

                string name = current.name;
                if (string.IsNullOrEmpty(name))
                    continue;

                string lower = name.ToLowerInvariant();
                bool exempt = false;
                for (int i = 0; i < DoorLikeNameExemptTokens.Length && !exempt; i++)
                {
                    if (lower.Contains(DoorLikeNameExemptTokens[i]))
                        exempt = true;
                }
                if (exempt)
                    continue;

                for (int i = 0; i < DoorLikeNameTokens.Length; i++)
                {
                    if (lower.Contains(DoorLikeNameTokens[i]))
                        return true;
                }
            }
            return false;
        }

        private static void BuildEntranceDoorPositions()
        {
            EntranceDoorPositions.Clear();
            // Interior-side entrance pads (main entrance + fire exits).
            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            if (entrances != null)
                EntranceDoorPositions.AddRange(entrances);
        }

        // Interior entrance doors (main entrance + fire exits) are not DunGen
        // doorways; keep bars out of a cylinder around each entrance pad.
        private static bool IsNearEntranceDoor(Vector3 seatPosition)
        {
            for (int i = 0; i < EntranceDoorPositions.Count; i++)
            {
                Vector3 entrance = EntranceDoorPositions[i];
                float heightAbove = seatPosition.y - entrance.y;
                if (heightAbove < -0.5f || heightAbove > EntranceDoorExclusionHeightM)
                    continue;
                float dx = seatPosition.x - entrance.x;
                float dz = seatPosition.z - entrance.z;
                if (dx * dx + dz * dz < EntranceDoorExclusionRadiusM * EntranceDoorExclusionRadiusM)
                    return true;
            }
            return false;
        }

        private static bool IsNearRegisteredCamera(Vector3 seatPosition)
        {
            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            if (cameras == null)
                return false;

            float exclusionRadiusSqr = CameraExclusionRadiusM * CameraExclusionRadiusM;
            for (int i = 0; i < cameras.Count; i++)
            {
                Transform cameraTransform = cameras[i]?.Transform;
                if (cameraTransform == null)
                    continue;
                if ((cameraTransform.position - seatPosition).sqrMagnitude < exclusionRadiusSqr)
                    return true;
            }
            return false;
        }

        private static GameObject BuildLightBar(Vector3 position, Quaternion rotation, Tile tile,
            Doorway doorway, Vector3 visualCenterLocal, Vector3 intoTile,
            out Vector3 actualCenter, out string reason)
        {
            actualCenter = default;
            GameObject root = FireExitLightBarLocator.BuildClone();
            if (root == null)
            {
                reason = "clone-unavailable";
                return null;
            }

            root.transform.SetParent(tile.transform, worldPositionStays: true);
            root.transform.SetPositionAndRotation(position, rotation);
            // The template's known local geometry centre determines its root
            // pose before validating the rendered seat. Never shift a clone
            // after accepting it by the first renderer's bounds.
            root.transform.position = position - root.transform.TransformVector(visualCenterLocal);

            DoorRenderers.Clear();
            root.GetComponentsInChildren(true, DoorRenderers);
            Bounds combined = default;
            bool hasBounds = false;
            for (int i = 0; i < DoorRenderers.Count; i++)
            {
                Renderer renderer = DoorRenderers[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (!hasBounds)
                {
                    combined = renderer.bounds;
                    hasBounds = true;
                }
                else
                    combined.Encapsulate(renderer.bounds);
            }
            DoorRenderers.Clear();
            if (!hasBounds)
            {
                reason = "clone-no-renderers";
                Object.Destroy(root);
                return null;
            }

            actualCenter = combined.center;
            Vector3 lateral = doorway.transform.right;
            lateral.y = 0f;
            lateral.Normalize();
            float lateralError = Mathf.Abs(Vector3.Dot(actualCenter - doorway.transform.position, lateral));
            float verticalError = Mathf.Abs(actualCenter.y - position.y);
            float depthError = Mathf.Abs(Vector3.Dot(actualCenter - position, intoTile));
            if (lateralError > 0.02f || verticalError > 0.02f || depthError > 0.02f)
            {
                reason = "clone-centre-mismatch";
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CctvSecurity] LIGHTBAR_CENTRE_REJECT doorway='{doorway.name}' lateral={lateralError:0.000} vertical={verticalError:0.000} depth={depthError:0.000}.");
                Object.Destroy(root);
                return null;
            }

            // Clearance and exclusions used this same visual centre before cloning.
            var fixture = root.AddComponent<InteriorAlarmFixture>();
            fixture.Initialize(repairImportedMaterials: false, scaleNativeEmissive: true);
            reason = "accepted";
            return root;
        }

        private static Tile ReadOwningTile(Component camera)
        {
            if (camera == null) return null;

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo prop = camera.GetType().GetProperty("OwningTile", flags);
            return prop != null ? prop.GetValue(camera) as Tile : null;
        }

        private static void MarkTileCameras(Tile tile, bool hasAlarmFixture)
        {
            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            if (cameras == null) return;

            for (int i = 0; i < cameras.Count; i++)
            {
                CctvSecurityCameraState state = cameras[i];
                if (state?.CameraComponent == null) continue;
                if (ReadOwningTile(state.CameraComponent) == tile)
                    CctvSecurityCameraRegistry.SetCameraHasAlarmFixture(state.CameraComponent, hasAlarmFixture);
            }
        }

        private static int ResolveCollisionMask()
        {
            StartOfRound sor = StartOfRound.Instance;
            return sor != null ? sor.collidersAndRoomMaskAndDefault : ~0;
        }

    }

    internal sealed class InteriorAlarmSpawnRunner : MonoBehaviour
    {
    }

    internal static class TransformPathExtensions
    {
        internal static string GetHierarchyPath(this Transform transform)
        {
            if (transform == null) return string.Empty;

            var parts = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Push(current.name ?? string.Empty);
                current = current.parent;
            }

            return string.Join("/", parts.ToArray());
        }
    }
}
