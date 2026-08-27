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
        private const float DoorwayOpeningDefaultTopM = 2.3f;
        private const float DoorwayOpeningMaxTopM = 3.2f;
        private const float DoorwayOpeningProbeDistanceM = 6f;
        // Clearance between the opening top and the bar's BOTTOM edge. Must
        // also clear the decorative doorframe lintel trim that rises above
        // the structural opening on most interiors.
        private const float LightBarAboveOpeningMarginM = 0.22f;
        // Flat-wall verification: corner rays around the seat point must hit
        // within this distance of the main hit's plane, with near-parallel
        // normals. Pipes, crates, and draped props fail this; real wall
        // panels pass.
        private const float WallPatchPlaneToleranceM = 0.06f;
        private const float WallPatchNormalMinDot = 0.95f;
        private const float LightBarWallHeightM = 2.35f;
        // Gap between a doorway's measured jamb and the bar's near edge for
        // the beside-opening mount. Measured from the jamb, not the doorway
        // center, so wide double doors push the bar clear of their frames.
        private const float BesideOpeningGapM = 0.25f;
        // Doorway-opening rejection: a bar seat whose plane lies this close
        // to a doorway's socket plane and whose span overlaps the measured
        // opening width is standing on the door (or its blocker), not on
        // wall. Static decorative doors and DunGen unused-doorway blockers
        // carry none of the forbidden components, so geometry is the only
        // reliable rejection.
        private const float DoorwayPlaneToleranceM = 0.45f;
        private const float DoorwayOpeningMaxHalfWidthM = 2.0f;
        private const float DoorwayOpeningFallbackHalfWidthM = 0.75f;
        private const float DoorwayMidOpeningProbeHeightM = 1.2f;
        private const float DoorwayFillerFlushToleranceM = 0.06f;
        // Facility entrances (main entrance + fire exits) are NOT DunGen
        // doorways, so the doorway-opening cache never sees them — yet their
        // doors are exactly where a bar must never sit (2026-07-09 playtest:
        // bar mounted on the main entrance door). Exclude a cylinder around
        // every interior-side entrance pad.
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
        private static readonly Dictionary<Doorway, DoorwayOpening> DoorwayOpenings = new Dictionary<Doorway, DoorwayOpening>();
        private static readonly List<Vector3> EntranceDoorPositions = new List<Vector3>();
        private static InteriorAlarmSpawnRunner _deferredSpawnRunner;
        private static int _spawnGeneration;

        private readonly struct DoorwayOpening
        {
            internal readonly Vector3 Position;
            internal readonly Vector3 Forward;
            internal readonly Vector3 Right;
            internal readonly float HalfWidthPlus;
            internal readonly float HalfWidthMinus;

            internal DoorwayOpening(Vector3 position, Vector3 forward, Vector3 right, float halfWidthPlus, float halfWidthMinus)
            {
                Position = position;
                Forward = forward;
                Right = right;
                HalfWidthPlus = halfWidthPlus;
                HalfWidthMinus = halfWidthMinus;
            }
        }

        public static void ResetRound()
        {
            CancelDeferredSpawn();
            for (int i = 0; i < SpawnedAlarms.Count; i++)
            {
                if (SpawnedAlarms[i] != null)
                    Object.Destroy(SpawnedAlarms[i]);
            }
            SpawnedAlarms.Clear();
            DoorwayOpenings.Clear();
            EntranceDoorPositions.Clear();
        }

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

            if (FireExitLightBarLocator.HasCachedSceneTemplate ||
                FireExitLightBarLocator.HasAnyInsideTeleport(out int totalCount, out int insideCount))
            {
                SpawnForRegisteredCamerasNow();
                return;
            }

            StartDeferredSpawn(totalCount, insideCount);
        }

        private static void SpawnForRegisteredCamerasNow()
        {
            FireExitLightBarLocator.ResetRound();
            BuildDoorwayOpeningCache();
            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            if (cameras == null) return;

            var servedTiles = new HashSet<Tile>();
            int spawned = 0;
            for (int i = 0; i < cameras.Count; i++)
            {
                CctvSecurityCameraState state = cameras[i];
                Tile tile = state?.CameraComponent != null ? ReadOwningTile(state.CameraComponent) : null;
                if (tile == null || servedTiles.Contains(tile)) continue;
                servedTiles.Add(tile);

                if (TrySpawnForTile(tile, out GameObject alarm)
                    || TrySpawnForNeighborTile(tile, servedTiles, out alarm))
                {
                    SpawnedAlarms.Add(alarm);
                    spawned++;
                    MarkTileCameras(tile, hasAlarmFixture: true);
                }
                else
                {
                    MarkTileCameras(tile, hasAlarmFixture: false);
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts.CctvSecurity] No valid {AlarmPlacementRole} mount in camera tile '{tile.name}' or its neighbors; tile cameras excluded from hostile security.");
                }
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
                    // above-doorway mounts only, never the beside fallback.
                    if (TrySpawnDoorwayLightBar(tile, aboveOnly: true, out GameObject alarm))
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

                if (TrySpawnDoorwayLightBar(tile, aboveOnly: true, out GameObject alarm))
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

            // The only alarm visual is now the cloned vanilla fire-exit red
            // light bar (the bundled ceiling siren is retired). Bars mount ONLY
            // at doorways — the header directly above the opening first, the
            // beside-opening spot for full-height openings as a last resort.
            // The old any-wall scan is gone: bars scattered on arbitrary wall
            // faces read as procedural noise, not installed security hardware.
            if (FireExitLightBarLocator.TryGetTemplate() == null) return false;

            if (TrySpawnDoorwayLightBar(tile, aboveOnly: true, out alarm)) return true;
            if (TrySpawnDoorwayLightBar(tile, aboveOnly: false, out alarm)) return true;

            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] LIGHTBAR_NO_SURFACE tile='{tile.name}'; no doorway accepted a light bar.");
            return false;
        }

        // Doorway-only fallback for a camera tile whose own doorways all
        // rejected the bar: seat it above a doorway of a directly connected
        // tile instead. The fixture is purely local (light + audio off global
        // alarm state), so a bar one room over still marks the camera's tile
        // as served — audible and visible from the doorway players use.
        private static bool TrySpawnForNeighborTile(Tile tile, HashSet<Tile> servedTiles, out GameObject alarm)
        {
            alarm = null;
            if (tile == null || tile.UsedDoorways == null) return false;

            for (int i = 0; i < tile.UsedDoorways.Count; i++)
            {
                Doorway doorway = tile.UsedDoorways[i];
                Tile neighbor = doorway != null && doorway.ConnectedDoorway != null
                    ? doorway.ConnectedDoorway.Tile
                    : null;
                if (neighbor == null || servedTiles.Contains(neighbor)) continue;
                if (!TrySpawnDoorwayLightBar(neighbor, aboveOnly: true, out alarm)) continue;

                servedTiles.Add(neighbor);
                MarkTileCameras(neighbor, hasAlarmFixture: true);
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_NEIGHBOR tile='{tile.name}' -> neighbor='{neighbor.name}'.");
                return true;
            }
            return false;
        }

        // Anchors the light bar to a tile doorway (where players walk) for a
        // uniform, deliberate look. aboveOnly=true runs the framed spot on the
        // wall header directly above each opening (the canonical, centered
        // look); aboveOnly=false runs the beside-opening fallback for
        // full-height openings. Callers try the above pass over ALL doorways
        // before any beside pass, so above-any-doorway always beats
        // beside-some-doorway.
        private static bool TrySpawnDoorwayLightBar(Tile tile, bool aboveOnly, out GameObject alarm)
        {
            alarm = null;
            if (FireExitLightBarLocator.TryGetTemplate() == null) return false;
            if (tile.UsedDoorways == null || tile.UsedDoorways.Count == 0) return false;
            if (!FireExitLightBarLocator.TryComputeBarFrame(out Vector3 thinAxisLocal, out Vector3 upAxisLocal, out Vector3 halfSize))
                return false;

            int mask = ResolveCollisionMask();
            for (int i = 0; i < tile.UsedDoorways.Count; i++)
            {
                Doorway doorway = tile.UsedDoorways[i];
                if (doorway == null || doorway.transform == null) continue;

                Vector3 intoTile = -doorway.transform.forward;
                intoTile.y = 0f;
                if (intoTile.sqrMagnitude < 0.001f) continue;
                intoTile.Normalize();

                Vector3 pos;
                Quaternion rot;
                bool seated = aboveOnly
                    ? TryMountAboveOpening(doorway, intoTile, thinAxisLocal, upAxisLocal, halfSize, mask, out pos, out rot)
                    : TryMountBesideOpening(doorway, intoTile, thinAxisLocal, upAxisLocal, halfSize, mask, out pos, out rot);
                if (!seated) continue;

                alarm = BuildLightBar(pos, rot, tile);
                if (alarm != null)
                {
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts.CctvSecurity] FIREEXIT_LIGHTBAR_MOUNT tile='{tile.name}' doorway={i} placement={(aboveOnly ? "above" : "beside")}.");
                    return true;
                }
            }

            return false;
        }

        // The framed spot: the wall header directly above the opening. Returns
        // false for full-height openings, where there is no wall above.
        private static bool TryMountAboveOpening(Doorway doorway, Vector3 intoTile, Vector3 thinAxisLocal, Vector3 upAxisLocal, Vector3 halfSize, int mask, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            Vector3 doorPos = doorway.transform.position;

            float openingTopY = doorPos.y + DoorwayOpeningDefaultTopM;
            Vector3 topProbeOrigin = doorPos + intoTile * 0.12f + Vector3.up * 0.4f;
            if (Physics.Raycast(topProbeOrigin, Vector3.up, out RaycastHit topHit, DoorwayOpeningProbeDistanceM, mask, QueryTriggerInteraction.Ignore))
                openingTopY = Mathf.Min(topHit.point.y, doorPos.y + DoorwayOpeningMaxTopM);

            float barCenterY = openingTopY + LightBarAboveOpeningMarginM + halfSize.y;
            Vector3 probeOrigin = new Vector3(doorPos.x, barCenterY, doorPos.z) + intoTile * 0.6f;
            if (!TrySeatOnWall(probeOrigin, -intoTile, 1.4f, thinAxisLocal, upAxisLocal, halfSize, mask, out position, out rotation))
                return false;

            Vector3 seatedFacing = rotation * (-thinAxisLocal);
            seatedFacing.y = 0f;
            if (seatedFacing.sqrMagnitude < 0.001f)
                return false;
            seatedFacing.Normalize();

            // A tall static door (or doorway blocker prefab) can masquerade
            // as the wall header: the opening-top probe clips a trim partway
            // up the panel and the wall probe then seats the bar on the door
            // face itself. A genuine header sits on the structural wall
            // plane while whatever fills the opening at body height is
            // either absent (open doorway) or recessed behind the frame
            // (real door panel). If the body-height filler is flush with
            // the bar's mount plane, the bar is standing on a door — reject.
            if (IsDoorwayFilledFlushWithSeat(doorPos, intoTile, position, seatedFacing, halfSize, mask))
                return false;

            // Deliberate look: the bar must sit centered on the doorway span,
            // not wherever the wall probe happened to land. Re-project the
            // seated position onto the doorway's lateral axis; keep the
            // as-seated spot only if the centered one is blocked.
            Vector3 lateral = doorway.transform.right;
            lateral.y = 0f;
            if (lateral.sqrMagnitude > 0.001f)
            {
                lateral.Normalize();
                Vector3 centered = position - lateral * Vector3.Dot(position - doorPos, lateral);
                if (!IsNearEntranceDoor(centered)
                    && !IsNearRegisteredCamera(centered)
                    && !IsBarVolumeBlocked(centered, seatedFacing, halfSize, mask))
                    position = centered;
            }
            return true;
        }

        // Detects the on-the-door-face seat for the above-opening mount: a
        // ray cast into the doorway at body height that strikes a surface
        // coplanar with the bar's mount plane means the opening is filled
        // flush up to (and including) where the bar sits.
        private static bool IsDoorwayFilledFlushWithSeat(Vector3 doorPos, Vector3 intoTile, Vector3 seatPosition, Vector3 seatFacing, Vector3 halfSize, int mask)
        {
            Vector3 fillerProbe = new Vector3(doorPos.x, doorPos.y + DoorwayMidOpeningProbeHeightM, doorPos.z) + intoTile * 0.6f;
            if (!Physics.Raycast(fillerProbe, -intoTile, out RaycastHit fillerHit, 1.4f, mask, QueryTriggerInteraction.Ignore))
                return false;

            float wallStandoff = halfSize.z + 0.02f;
            float fillerDepth = Mathf.Abs(Vector3.Dot(fillerHit.point - seatPosition, seatFacing));
            return Mathf.Abs(fillerDepth - wallStandoff) < DoorwayFillerFlushToleranceM;
        }

        // The beside spot: same wall plane as the opening, offset sideways past
        // the opening edge, at a fixed comfortable height. Tries both sides.
        private static bool TryMountBesideOpening(Doorway doorway, Vector3 intoTile, Vector3 thinAxisLocal, Vector3 upAxisLocal, Vector3 halfSize, int mask, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            Vector3 doorPos = doorway.transform.position;
            Vector3 along = doorway.transform.right;
            along.y = 0f;
            if (along.sqrMagnitude < 0.001f) return false;
            along.Normalize();

            float y = doorPos.y + LightBarWallHeightM;
            for (int s = -1; s <= 1; s += 2)
            {
                // Offset from the measured jamb, not the doorway center, so
                // the bar clears wide double-door frames too.
                float halfWidth = GetDoorwayHalfWidth(doorway, s);
                float sideOffset = halfWidth + BesideOpeningGapM + halfSize.x;
                Vector3 basePos = doorPos + along * (sideOffset * s);
                Vector3 probeOrigin = new Vector3(basePos.x, y, basePos.z) + intoTile * 0.6f;
                if (!TrySeatOnWall(probeOrigin, -intoTile, 1.4f, thinAxisLocal, upAxisLocal, halfSize, mask, out position, out rotation))
                    continue;
                if (IsSeatWithinDoorwayOpening(position, halfSize))
                    continue;
                return true;
            }
            return false;
        }

        // Shared wall-seat: probe toward a wall, reject sloped/blocked faces
        // AND non-structural surfaces (door panels, interactables, props),
        // and produce the flush position + room-facing rotation. Clearance
        // radius stays inside the wall standoff so the sphere cannot
        // self-reject against the wall the bar mounts on.
        private static bool TrySeatOnWall(Vector3 probeOrigin, Vector3 towardWall, float probeDistance, Vector3 thinAxisLocal, Vector3 upAxisLocal, Vector3 halfSize, int mask, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            if (!Physics.Raycast(probeOrigin, towardWall, out RaycastHit wallHit, probeDistance, mask, QueryTriggerInteraction.Ignore))
                return false;
            if (Mathf.Abs(wallHit.normal.y) > 0.4f)
                return false;
            if (IsForbiddenMountSurface(wallHit.collider))
                return false;

            Vector3 facing = wallHit.normal;
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.001f) return false;
            facing.Normalize();

            // The hit must be a genuine flat wall patch at least as big as
            // the bar — a single ray happily "seats" on pipes, crates, and
            // draped props, which is exactly the floating-on-clutter bug.
            if (!IsFlatWallPatch(wallHit.point, facing, halfSize, mask))
                return false;

            position = wallHit.point + facing * (halfSize.z + 0.02f);
            if (IsNearEntranceDoor(position) || IsNearRegisteredCamera(position))
                return false;
            if (IsBarVolumeBlocked(position, facing, halfSize, mask))
                return false;

            // The vanilla bar's +thin mesh axis points out the MOUNT side
            // (glow face on -thin), so map the negated axis into the room.
            rotation =
                Quaternion.LookRotation(facing, Vector3.up) *
                Quaternion.Inverse(Quaternion.LookRotation(-thinAxisLocal, upAxisLocal));
            return true;
        }

        // Verifies the wall behind a seat point is flat and large enough for
        // the bar: rays at the bar's four corner offsets must all strike the
        // same plane (tight distance tolerance) with near-parallel normals
        // and pass the forbidden-surface filter. A pipe's curvature, a
        // crate's edges, or a cloth drape all break coplanarity.
        private static bool IsFlatWallPatch(Vector3 seatPoint, Vector3 facing, Vector3 halfSize, int mask)
        {
            Vector3 tangent = Vector3.Cross(Vector3.up, facing);
            if (tangent.sqrMagnitude < 0.001f) return false;
            tangent.Normalize();

            float dx = halfSize.x * 0.7f;
            float dy = halfSize.y * 0.7f;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    Vector3 origin = seatPoint + facing * 0.5f + tangent * (dx * sx) + Vector3.up * (dy * sy);
                    if (!Physics.Raycast(origin, -facing, out RaycastHit cornerHit, 1f, mask, QueryTriggerInteraction.Ignore))
                        return false;
                    if (Vector3.Dot(cornerHit.normal, facing) < WallPatchNormalMinDot)
                        return false;
                    if (Mathf.Abs(Vector3.Dot(cornerHit.point - seatPoint, facing)) > WallPatchPlaneToleranceM)
                        return false;
                    if (IsForbiddenMountSurface(cornerHit.collider))
                        return false;
                }
            }
            return true;
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
        private static bool IsForbiddenMountSurface(Collider collider)
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

        // Measures every doorway opening (used AND unused — unused sockets
        // are filled with blocker prefabs, often fake doors) once per round:
        // jamb-to-jamb half widths found by casting along the socket's
        // lateral axis at body height, just off both sides of the plane so a
        // closed panel cannot block the measurement.
        private static void BuildDoorwayOpeningCache()
        {
            DoorwayOpenings.Clear();
            EntranceDoorPositions.Clear();

            // Interior-side entrance pads (main entrance + fire exits) —
            // the entrance doors stand right at these points.
            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            if (entrances != null)
                EntranceDoorPositions.AddRange(entrances);

            IReadOnlyList<Tile> tiles = ReadDungeonTiles();
            if (tiles == null) return;

            int mask = ResolveCollisionMask();
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile tile = tiles[i];
                if (tile == null) continue;
                AddDoorwayOpenings(tile.UsedDoorways, mask);
                AddDoorwayOpenings(tile.UnusedDoorways, mask);
            }
        }

        private static void AddDoorwayOpenings(List<Doorway> doorways, int mask)
        {
            if (doorways == null) return;
            for (int i = 0; i < doorways.Count; i++)
            {
                Doorway doorway = doorways[i];
                if (doorway == null || doorway.transform == null || DoorwayOpenings.ContainsKey(doorway))
                    continue;

                Vector3 forward = doorway.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.001f) continue;
                forward.Normalize();
                Vector3 right = doorway.transform.right;
                right.y = 0f;
                if (right.sqrMagnitude < 0.001f) continue;
                right.Normalize();

                Vector3 basePos = doorway.transform.position + Vector3.up * DoorwayMidOpeningProbeHeightM;
                float halfPlus = MeasureJambDistance(basePos, right, forward, mask);
                float halfMinus = MeasureJambDistance(basePos, -right, forward, mask);
                DoorwayOpenings[doorway] = new DoorwayOpening(doorway.transform.position, forward, right, halfPlus, halfMinus);
            }
        }

        private static float MeasureJambDistance(Vector3 basePos, Vector3 lateral, Vector3 forward, int mask)
        {
            float best = DoorwayOpeningMaxHalfWidthM;
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 origin = basePos + forward * (0.15f * s);
                if (Physics.Raycast(origin, lateral, out RaycastHit hit, DoorwayOpeningMaxHalfWidthM, mask, QueryTriggerInteraction.Ignore))
                    best = Mathf.Min(best, hit.distance);
            }
            return best;
        }

        private static float GetDoorwayHalfWidth(Doorway doorway, int side)
        {
            if (doorway == null || !DoorwayOpenings.TryGetValue(doorway, out DoorwayOpening opening))
                return DoorwayOpeningFallbackHalfWidthM;
            return side >= 0 ? opening.HalfWidthPlus : opening.HalfWidthMinus;
        }

        // Rejects generic (beside/wall-scan) seats whose bar overlaps any
        // doorway opening's span on that doorway's wall plane — at any
        // height, because the surface inside the span is a door, a blocker,
        // or the frame above one, never plain wall. The deliberate
        // above-opening mount does NOT use this check; it has its own
        // flush-filler guard.
        private static bool IsSeatWithinDoorwayOpening(Vector3 seatPosition, Vector3 halfSize)
        {
            foreach (DoorwayOpening opening in DoorwayOpenings.Values)
            {
                Vector3 delta = seatPosition - opening.Position;
                if (Mathf.Abs(Vector3.Dot(delta, opening.Forward)) > DoorwayPlaneToleranceM)
                    continue;
                float heightAbove = seatPosition.y - opening.Position.y;
                if (heightAbove < -0.5f || heightAbove > 5f)
                    continue;

                float lateral = Vector3.Dot(delta, opening.Right);
                float halfWidth = lateral >= 0f ? opening.HalfWidthPlus : opening.HalfWidthMinus;
                float nearEdge = Mathf.Abs(lateral) - halfSize.x * 0.8f;
                if (nearEdge <= halfWidth + 0.05f)
                    return true;
            }
            return false;
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

        private static GameObject BuildLightBar(Vector3 position, Quaternion rotation, Tile tile)
        {
            GameObject root = FireExitLightBarLocator.BuildClone();
            if (root == null) return null;

            root.transform.SetParent(tile.transform, worldPositionStays: true);
            root.transform.SetPositionAndRotation(position, rotation);

            // The template's pivot is not necessarily at its visual center;
            // shift so the mesh bounds land where the wall probe seated it.
            Renderer visual = root.GetComponentInChildren<Renderer>();
            if (visual != null)
                root.transform.position += position - visual.bounds.center;

            // Native vanilla materials: skip the imported-asset repair pass,
            // and scale the lamp's own emissive so it throbs during the alarm.
            var fixture = root.AddComponent<InteriorAlarmFixture>();
            fixture.Initialize(repairImportedMaterials: false, scaleNativeEmissive: true);
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
