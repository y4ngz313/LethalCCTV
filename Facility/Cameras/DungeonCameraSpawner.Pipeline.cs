using System;
using System.Collections.Generic;
using System.Text;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Facility.Cameras.Placement;
using Y4NGZCompany.ShipSystems.Surveillance;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    public sealed partial class DungeonCameraSpawner
    {
        // #1283 — one synchronous pass inside the dungeon-finished event (see
        // OnDungeonFinished). CamerasReady fires exactly once, with the _spawnedCameras
        // payload, after the last camera and its prop exist.
        private void RunSpawnPipeline()
        {
            ClearPriorRunIfAny();

            Dungeon dungeon = ResolveDungeonOrNull();
            if (dungeon == null)
            {
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] Dungeon null inside OnFinishedGeneratingDungeon — skipping spawn.");
                CamerasReady?.Invoke(_spawnedCameras);
                return;
            }

            IReadOnlyList<Tile> allTiles = dungeon.AllTiles;
            int totalTiles = allTiles.Count;
            bool reconLogging = _config.ReconLoggingEnabled.Value;
            bool placementDebugLogging = _config.PlacementDebugLoggingEnabled.Value;
            AuthoredInteriorPlacementStore.TryResolvePoses(
                AuthoredInteriorPlacementKinds.CctvCamera,
                out List<AuthoredInteriorPlacementPose> authoredCameraPoses);
            authoredCameraPoses = authoredCameraPoses ?? new List<AuthoredInteriorPlacementPose>();

            // Phase 1.9 probe extension moved this call to AFTER
            // BuildEntranceTileSets (below) so each row can flag whether
            // the tile is in the entrance / fire-exit attribution set —
            // needed to resolve whether degenerate-worldAABB tiles are
            // already excluded from placement anyway (in which case the
            // §4 fallback may be moot). Tradeoff: under Phase18GateAOnly
            // the function returns before reaching the new call site, so
            // the bounds probe is silent in that mode. Acceptable — the
            // probe's consumer (the geometry-fix workstream) runs with
            // Phase18GateAOnly=false, and Gate A diagnostics have their
            // own classification report that is unchanged.

            // Phase 1.8 — Gate A classification report. Walks every tile in
            // AllTiles, applies the room-vs-corridor heuristic without regard
            // to other exclusion rules (mineshaft / tiny / name / entrance /
            // fire-exit), and logs the per-tile verdict + multi-tile-room and
            // suspicious-dropped-room flags. Always emitted, every dungeon
            // load — cheap (single linear pass + a HashSet group), and the
            // classification SPEC says this is human-eyes territory regardless
            // of the diagnostic flag.
            if (placementDebugLogging)
            {
                EmitGateAClassificationReport(allTiles);
            }

            // The per-attribution log lines emitted by BuildEntranceTileSets are the
            // canonical record of what got attributed where. #1283: the dungeon-side
            // teleports and the apparatus are network-spawned after this event, so their
            // pads and positions are read once per pass from the SpawnSyncedObject markers
            // that spawn them, identically on every peer.
            CollectSpawnMarkerInputs(s_markerPads, s_markerApparatus);

            // Build the entrance and fire-exit tile sets via closest-center-always
            // attribution. The filter consults these instead of walking each tile's
            // subtree for an EntranceTeleport child (which always returned null
            // because EntranceTeleport components live under Environment/Teleports/).
            (HashSet<Tile> entranceTiles, HashSet<Tile> fireExitTiles) =
                BuildEntranceTileSets(allTiles, s_markerPads);

            // Phase 1.9 — extended bounds probe (§4.1.1 origin). One-shot
            // per spawner instance: restart the game between rolls if you
            // need probes on two different interiors. Hoisted past
            // BuildEntranceTileSets so each row can flag in-entrance /
            // in-fire-exit set membership, and the summary line at the
            // end can report whether degenerate-worldAABB tiles overlap
            // the existing exclusion sets.
            if (reconLogging && !_boundsProbeDone)
            {
                _boundsProbeDone = true;
                LogTileBoundsProbe(
                    allTiles, entranceTiles, fireExitTiles,
                    _config.WallMountMinRoomHeightM);
            }

            // Phase 1.8 T5 — per-rule exclusion accumulators. Moved out
            // of the (now-replaced) corner-based keptIndexed loop into a
            // side pass over AllTiles so the shipped "excluded N
            // mineshaft tunnels, M tiny tiles, …" summary still prints
            // after a successful spawn. Pure refactor of the counting
            // logic; the per-rule numbers under Phase18GateAOnly=true
            // are never emitted (we return at the Gate A STOP above),
            // and under both flags false they match the pre-T5 build
            // because ShouldInclude is unchanged.
            int mineshaft = 0, tiny = 0, name = 0, entrance = 0, fireExit = 0, corridor = 0, other = 0;
            for (int i = 0; i < totalTiles; i++)
            {
                Tile t = allTiles[i];
                if (_filter.ShouldInclude(t, out string reason, entranceTiles, fireExitTiles)) continue;
                switch (reason)
                {
                    case TileExclusionFilter.ReasonMineshaftTunnel: mineshaft++; break;
                    case TileExclusionFilter.ReasonTinyTile: tiny++; break;
                    case TileExclusionFilter.ReasonNamePattern: name++; break;
                    case TileExclusionFilter.ReasonEntrance: entrance++; break;
                    case TileExclusionFilter.ReasonFireExit: fireExit++; break;
                    case TileExclusionFilter.ReasonCorridor: corridor++; break;
                    default: other++; break;
                }
            }

            // Phase 1.8 T5 — cluster → select → cap pipeline. First gate
            // where the T1–T4 building blocks have callers. Replaces the
            // Phase 1 corner-based one-camera-per-non-excluded-tile loop.
            //   Build:    room-only subgraph (corridors are dividers, not
            //             edges) → connected components, BFS-rooted by
            //             lowest dungeon.AllTiles srcIndex per component.
            //   Select:   single-tile → 1 camera; multi-tile with junctions
            //             → 1 per (deg >= JunctionMinDegree); zero-junction
            //             multi-tile → 1 per degree-1 endpoint (or ring
            //             fallback to largest-footprint tile).
            //   Apply:    budget-cap truncation by configured priority
            //             with SrcIndex as the ALWAYS-final tiebreaker —
            //             total-ordered → host/client-identical truncation.
            List<RoomComponent> components = RoomComponentBuilder.Build(
                allTiles, _filter, entranceTiles, fireExitTiles);
            List<CameraPick> rawPicks = CameraSelector.Select(components, _config);
            rawPicks = TopUpSparseInteriorSelection(rawPicks, allTiles, entranceTiles, fireExitTiles);
            List<CameraPick> cappedPicks = CameraBudgetCapper.Apply(
                rawPicks, _config, out bool capEngaged, out int droppedCount);

            // Always-on summary line + (under placement debug logging)
            // verbose per-component + per-pick lines.
            if (placementDebugLogging)
            {
                EmitPlacementDiagnosticReport(components, rawPicks, cappedPicks, capEngaged, droppedCount);
            }

            // The Phase 1.8 T5 dry-run STOP ("Dry Run Camera Placement") was a
            // first-deploy safety; its config key and this branch were deleted
            // for 1.0 (#575), along with the Phase 1.8 Gate A STOP. Nothing
            // short-circuits this pipeline any more: the diagnostic flags only
            // decide whether the reports are logged, never whether cameras
            // spawn.

            // Composite sort for cameraIndex stability across host/client.
            // Reuses the existing eight-key CompareTiles (IsOnMainPath →
            // PathDepth → BranchDepth → quantized position → name →
            // srcIndex) via a CameraPick → (Tile, SrcIndex) projection.
            List<CameraPick> orderedPicks = SortPicksForInstantiation(cappedPicks);

            // P2.0 §6 — entrance tile promotion. Identify the entrance
            // (over allTiles, not orderedPicks) via the revised §2 rule;
            // if it's already in orderedPicks → no-op; else evict the
            // deterministically-lowest-priority capped pick (by the
            // capper's active comparator) and insert the entrance. One-
            // for-one swap; never grows past the cap. The eviction
            // ordering matches the capper's sort key so the result is
            // "as if the cap were one tile tighter," host/client
            // identical. Re-sort orderedPicks after promotion so
            // instantiation order remains stable.
            // Promotion priority = application order: a pick inserted by an
            // earlier pass is immune to eviction by every later pass (the
            // shared eviction target skips promoted picks). Under a tiny cap
            // the guarantees therefore rank entrance > apparatus > mainframe > objective
            // > fire exit.
            orderedPicks = ApplyEntrancePromotion(allTiles, orderedPicks);
            orderedPicks = ApplyApparatusPromotion(allTiles, orderedPicks, s_markerApparatus);
            orderedPicks = ApplyMainframePromotion(allTiles, orderedPicks);
            orderedPicks = ApplyObjectivePromotion(allTiles, orderedPicks);
            orderedPicks = ApplyFireExitPromotion(allTiles, orderedPicks, fireExitTiles);

            // Instantiate. Phase 1.9 — bundle the config-derived placement
            // floats into PlacementParams (SurfaceMount.Compute takes it by
            // `in` ref). Geometry inputs (tile + tile-local worldAABB +
            // sortedIndex) stay as their own parameters per the PLAN
            // rationale.
            PlacementParams placementParams = new PlacementParams(
                heightFraction:            _config.PlacementHeightFraction,
                horizontalFraction:        _config.PlacementHorizontalFraction,
                wallMountMinRoomHeightM:   _config.WallMountMinRoomHeightM,
                wallMountHeightM:          _config.WallMountHeightM);

            // Phase 2.0 Task 1 — Legibility probe. Post-selection, pre-
            // instantiation: feeds the eventual scorer its complete input
            // space (4 corners × 2 tiers × N doorways per picked tile)
            // plus the fail-loud Room-layer assertion and unified-rule
            // entrance preview. Pure read; does not alter what gets
            // spawned. It was originally independent of the
            // PlacementDiagnosticOnly STOP so probe data could be captured on a
            // recon roll that spawned nothing; that STOP is gone (#575), so the
            // probe now simply runs on every roll where its own key is true.
            if (_config.P20ProbeEnabled.Value)
            {
                Probe.PlacementProbe.Run(orderedPicks, in placementParams);
            }

            // 0.0.36 surface-mount placement + redundancy suppression.
            // Resolve the placement physics mask (kept SEPARATE from the CCTV
            // render culling mask) once per dungeon. When it is valid, each
            // camera is snapped to a real raycast-found mount surface and any
            // pick whose candidates are all unmounted / void / near-wall, or
            // redundant with an already-placed camera, is SKIPPED rather than
            // spawned floating or staring at a wall. When the mask is invalid
            // (physics not ready / a moon renamed the collision layers), this
            // pass spawns no cameras. Cross-client determinism: identical builds run
            // identical raycasts against identical DunGen-seeded static
            // geometry, so the kept-camera set matches on host and client.
            PlacementMasks placementMasks = PlacementMask.Resolve(placementDebugLogging);
            if (!placementMasks.Valid)
            {
                MarkAuthoredCamerasUnused(authoredCameraPoses, "consumer=cctv-camera reason=invalid-placement-mask");
                SurveillanceBootstrap.Log.LogWarning(
                    "[LethalCCTV] SURFACE_PLACEMENT_ABORT reason=invalid-placement-mask — spawned no cameras.");
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] Spawned {_spawnedCameras.Count} cameras (interior picks={orderedPicks.Count}, skipped {orderedPicks.Count}: invalid placement mask) of {totalTiles} total tiles");
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] excluded {mineshaft} mineshaft tunnels, {tiny} tiny tiles, {name} name-matched tiles, {entrance} entrance tiles, {fireExit} fire-exit tiles, {corridor} corridors" +
                    (other > 0 ? $", {other} other" : string.Empty));
                CamerasReady?.Invoke(_spawnedCameras);
                return;
            }

            CameraPlacementSafety.Prepare(allTiles);
            foreach (var authored in authoredCameraPoses)
                CameraPlacementSafety.PrepareFloor(authored.Tile);
            foreach (CameraPick safetyPick in orderedPicks)
            {
                CameraPlacementSafety.PrepareFloor(safetyPick.Tile);
                if (placementDebugLogging)
                    SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV][PlacementSafety] tile='{safetyPick.Tile?.name}' " +
                        CameraPlacementSafety.DescribeFloor(safetyPick.Tile));
            }

            var acceptedPoses = new List<AcceptedPose>(orderedPicks.Count);
            PlacementMasks expandedPlacementMasks =
                PlacementMask.ResolveExpandedMountFallback(placementMasks, placementDebugLogging);
            int skippedNoCandidate = 0;
            int skippedRedundant = 0;
            int expandedFallbackAttempts = 0;
            int expandedFallbackAccepted = 0;
            var occupiedCameraTiles = new HashSet<Tile>();
            // Per-pick tally of structural-patch rejects (#1313), reset for each
            // pick, so a tile left without a camera reports why in one line. Only
            // the strict and expanded (non-relaxed) Compute passes and the
            // grand-room overview feed it: a relaxed pass re-probes the same hits
            // with the same patch verdicts, so it would only double the counts.
            var patchRejects = new StructuralPatchRejectTally();
            for (int i = 0; i < authoredCameraPoses.Count; i++)
            {
                AuthoredInteriorPlacementPose authored = authoredCameraPoses[i];
                if (_spawnedCameras.Count >= _config.NormalizedMaximumCameraCount)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        authored.Record,
                        "consumer=cctv-camera reason=maximum-camera-count");
                    continue;
                }

                if (authored.Tile == null)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        authored.Record,
                        "consumer=cctv-camera reason=resolved-tile-null");
                    continue;
                }
                if (InteriorAuthoredPlacementStore.TryGetAuthoredCameraDeletion(
                        authored.Record,
                        authored.Tile,
                        out AuthoredInteriorPlacementRecord deletionRecord,
                        out string deletionSource))
                {
                    AuthoredPlacementRoundReport.RecordSuppressed(authored.Record, "camera-deleted");
                    AuthoredPlacementRoundReport.RecordConsidered(deletionRecord, deletionSource);
                    AuthoredPlacementRoundReport.RecordSuppressed(deletionRecord, "camera-deleted");
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] AUTHORED_CAMERA_SKIP tile='{authored.Tile.name}' reason=deleted id='{authored.Record?.objectId}'");
                    continue;
                }
                if (occupiedCameraTiles.Contains(authored.Tile))
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        authored.Record,
                        "consumer=cctv-camera reason=duplicate-tile");
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] AUTHORED_CAMERA_SKIP tile='{authored.Tile.name}' reason=duplicate-tile id='{authored.Record?.objectId}'");
                    continue;
                }

                Bounds authoredBox = ComputeWorldAabbLocal(authored.Tile);
                if (authoredBox.size.sqrMagnitude < 1e-6f)
                    authoredBox = FallbackBox(authored.Tile);
                int authoredCameraIndex = _spawnedCameras.Count;
                CCTVCamera authoredHolder = InstantiateAtPose(
                    authored.Tile, authoredCameraIndex, authored.WorldPosition, authored.WorldRotation,
                    cornerId: 777, cornerLocal: authored.Record != null ? authored.Record.tileLocalPosition : Vector3.zero,
                    mountMode: CameraMountMode.Wall, drivingRoomHeightM: authoredBox.size.y, source: "authored");
                if (authoredHolder == null)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        authored.Record,
                        "consumer=cctv-camera reason=fixture-reservation-conflict");
                    SurveillanceBootstrap.Log.LogWarning(
                        $"[LethalCCTV] AUTHORED_CAMERA_SKIP tile='{authored.Tile.name}' reason=fixture-reservation-conflict id='{authored.Record?.objectId}'");
                    continue;
                }

                authoredHolder.AuthoredPlacementId = authored.Record?.objectId;
                InteriorAuthoredPlacementStore.ApplyCameraDiagnostics(
                    authoredHolder,
                    authored.Tile,
                    string.IsNullOrEmpty(authored.Source) ? "authored-camera" : authored.Source);
                authoredHolder.PlacementTier = "authored";
                _spawnedCameras.Add(authoredHolder);
                occupiedCameraTiles.Add(authored.Tile);
                AuthoredPlacementRoundReport.RecordSpawned(authored.Record);
                acceptedPoses.Add(new AcceptedPose(
                    authored.WorldPosition,
                    authored.WorldRotation * Vector3.forward,
                    authored.Tile,
                    comp: null));
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] AUTHORED_CAMERA_ACCEPT cam={authoredCameraIndex} tile='{authored.Tile.name}' source={authored.Source} " +
                    $"pos=({authored.WorldPosition.x:F2},{authored.WorldPosition.y:F2},{authored.WorldPosition.z:F2}) yaw={authored.WorldRotation.eulerAngles.y:F1}");
            }

            for (int sortedIndex = 0; sortedIndex < orderedPicks.Count; sortedIndex++)
            {
                if (_spawnedCameras.Count >= _config.NormalizedMaximumCameraCount)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] Camera maximum reached after authored placement; skipped {orderedPicks.Count - sortedIndex} procedural pick(s).");
                    break;
                }

                CameraPick pick = orderedPicks[sortedIndex];
                Tile tile = pick.Tile;
                // Guaranteed-coverage picks (entrance / apparatus / mainframe / objective /
                // fire exit) bypass redundancy suppression — the whole point of
                // a promotion is that this specific room gets its own camera —
                // and any skip that still defeats one is logged as a warning so
                // a missing guaranteed camera is diagnosable from one log line.
                bool promotedPick = IsPromotedPick(pick.Reason);
                if (occupiedCameraTiles.Contains(tile))
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] SURFACE_SKIP tile={sortedIndex} name={tile?.name} reason=tile-already-has-camera");
                    continue;
                }
                // #874: promotions (apparatus, objective, entrance...) bypass the tile filter,
                // so the padded-cell exclusion is enforced here for every procedural pick.
                if (InteriorPlacementService.IsTileExcludedFromPlacement(tile))
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] SURFACE_SKIP tile={sortedIndex} name={tile?.name} reason={TileExclusionFilter.ReasonPaddedCell}");
                    if (promotedPick)
                        WarnPromotedPickUnmounted(pick, TileExclusionFilter.ReasonPaddedCell);
                    continue;
                }
                if (InteriorAuthoredPlacementStore.TryGetCameraTileSuppression(
                        tile,
                        out AuthoredInteriorPlacementRecord suppressionRecord,
                        out string suppressionSource))
                {
                    AuthoredPlacementRoundReport.RecordConsidered(suppressionRecord, suppressionSource);
                    AuthoredPlacementRoundReport.RecordSuppressed(suppressionRecord, "camera-disabled");
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] SURFACE_SKIP tile={sortedIndex} name={tile?.name} reason=authored-camera-disabled");
                    if (promotedPick)
                        WarnPromotedPickUnmounted(pick, "authored-camera-disabled");
                    continue;
                }

                Bounds worldAabbLocal = ComputeWorldAabbLocal(tile);
                // Use the tight rendered-geometry AABB when available; on a
                // degenerate-mesh tile fall back to the tile-local LocalBounds
                // box so the surface probe still has somewhere to cast from
                // (collision colliders exist even when MeshFilters do not).
                Bounds box = worldAabbLocal.size.sqrMagnitude < 1e-6f
                    ? FallbackBox(tile)
                    : worldAabbLocal;
                // Promoted semantic rooms watch the object that justified their
                // guaranteed camera rather than falling back to a generic room-center view.
                Vector3? semanticFocus = null;
                if (_hasMainEntrancePad && ReferenceEquals(tile, _mainEntranceTile))
                    semanticFocus = _mainEntrancePadWorld + Vector3.up * 1.0f;
                else if (_hasApparatusTarget && ReferenceEquals(tile, _apparatusTile))
                    semanticFocus = _apparatusWorldPosition + Vector3.up * 0.8f;

                CameraSemanticContext semantic = CameraSemanticContextBuilder.Build(
                    tile,
                    box,
                    semanticFocus);

                if (CameraPlacementReviewStore.TryResolveReviewedPose(
                        tile, placementMasks, box, out ReviewedCameraPose reviewedPose, out string reviewedRejectReason))
                {
                    Vector3 reviewedForward = reviewedPose.WorldRotation * Vector3.forward;
                    string reviewedRedundantReason = null;
                    if (promotedPick
                        || !IsRedundantPose(reviewedPose.WorldPosition, reviewedForward, acceptedPoses, pick, out reviewedRedundantReason))
                    {
                        int reviewedCameraIndex = _spawnedCameras.Count;
                        CCTVCamera reviewedHolder = InstantiateAtPose(
                            tile, reviewedCameraIndex, reviewedPose.WorldPosition, reviewedPose.WorldRotation,
                            reviewedPose.Record.placementCornerId, reviewedPose.Record.tileLocalPosition,
                            ParseMountMode(reviewedPose.Record.mountMode), reviewedPose.Record.tileBoundsSize.y, source: "reviewed-perfect");
                        if (reviewedHolder != null)
                        {
                            CameraPlacementReviewStore.ApplyReviewedDiagnostics(reviewedHolder, reviewedPose);
                            _spawnedCameras.Add(reviewedHolder);
                            occupiedCameraTiles.Add(tile);
                            acceptedPoses.Add(new AcceptedPose(reviewedPose.WorldPosition, reviewedForward, tile, pick.Component));
                            SurveillanceBootstrap.Log.LogInfo(
                                $"[LethalCCTV] REVIEWED_POSE_ACCEPT cam={reviewedCameraIndex} tile={sortedIndex} name={tile?.name} " +
                                $"source=perfect-review height={reviewedPose.Metrics.HeightAboveFloor:F2} centerDist={reviewedPose.Metrics.CenterSightDistance:F2} " +
                                $"frustum={reviewedPose.Metrics.FrustumHits}/{reviewedPose.Metrics.FrustumVoids}/{reviewedPose.Metrics.FrustumNearWall} " +
                                $"pos=({reviewedPose.WorldPosition.x:F2},{reviewedPose.WorldPosition.y:F2},{reviewedPose.WorldPosition.z:F2})");
                            continue;
                        }

                        SurveillanceBootstrap.Log.LogWarning(
                            $"[LethalCCTV] REVIEWED_POSE_REJECT tile={sortedIndex} name={tile?.name} " +
                            "reason=fixture-reservation-conflict");
                    }
                    else
                    {
                        SurveillanceBootstrap.Log.LogInfo(
                            $"[LethalCCTV] REVIEWED_POSE_SKIP tile={sortedIndex} name={tile?.name} reason=redundant {reviewedRedundantReason}");
                    }
                }
                else if (!string.IsNullOrEmpty(reviewedRejectReason))
                {
                    // Unconditional: fires at most once per tile, and gating it behind
                    // placementDebugLogging/reconLogging left the surface-contact guards
                    // unmeasurable in a normal run - the exact blind spot 3e exists to close.
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] REVIEWED_POSE_REJECT tile={sortedIndex} name={tile?.name} reason={reviewedRejectReason}");
                }

                patchRejects.Reset();
                List<SurfaceMount.Candidate> candidates = null;
                bool usingExpandedFallback = false;
                // Which Compute pass produced the candidate list (#1313 diagnostics).
                string candidateTier = "strict";
                if (box.size.sqrMagnitude >= 1e-6f)
                {
                    candidates = SurfaceMount.Compute(
                        tile, box, sortedIndex, in placementParams, in placementMasks, in semantic,
                        relaxed: false, debug: placementDebugLogging, patchRejects);
                    if (candidates.Count == 0)
                    {
                        // Relaxed pass — allow a shorter useful center distance
                        // ("unless no alternative exists"), while still hard-
                        // gating surface support, embedding, and skybox/void.
                        candidateTier = "relaxed";
                        candidates = SurfaceMount.Compute(
                            tile, box, sortedIndex, in placementParams, in placementMasks, in semantic,
                            relaxed: true, debug: placementDebugLogging);
                    }
                    if (candidates.Count == 0 && expandedPlacementMasks.Valid)
                    {
                        expandedFallbackAttempts++;
                        usingExpandedFallback = true;
                        candidateTier = "expanded";
                        candidates = SurfaceMount.Compute(
                            tile, box, sortedIndex, in placementParams, in expandedPlacementMasks, in semantic,
                            relaxed: false, debug: placementDebugLogging, patchRejects);
                        if (candidates.Count == 0)
                        {
                            candidateTier = "expanded-relaxed";
                            candidates = SurfaceMount.Compute(
                                tile, box, sortedIndex, in placementParams, in expandedPlacementMasks, in semantic,
                                relaxed: true, debug: placementDebugLogging);
                        }
                    }
                }

                int grandRoomIndex = -1;
                if (box.size.sqrMagnitude >= 1e-6f &&
                    SurfaceMount.TryComputeGrandRoomOverview(
                        tile, box, sortedIndex, in placementMasks,
                        placementDebugLogging, patchRejects, out SurfaceMount.Candidate grandRoomCandidate))
                {
                    if (candidates == null)
                        candidates = new List<SurfaceMount.Candidate>(1);
                    candidates.Insert(0, grandRoomCandidate);
                    grandRoomIndex = 0;
                    if (placementDebugLogging || reconLogging)
                    {
                        SurveillanceBootstrap.Log.LogInfo(
                            $"[LethalCCTV] GRAND_ROOM_OVERVIEW candidate tile={sortedIndex} name={tile?.name} " +
                            $"pos=({grandRoomCandidate.WorldPos.x:F2},{grandRoomCandidate.WorldPos.y:F2},{grandRoomCandidate.WorldPos.z:F2}) " +
                            $"score={grandRoomCandidate.Score:F1}");
                    }
                }

                // Pick the best candidate that is not redundant with an
                // already-placed camera. Candidates are ranked best→worst, so
                // the first non-redundant one is the best legal choice.
                SurfaceMount.Candidate chosen = default;
                bool haveChoice = false;
                string lastRedundantReason = null;
                bool sawReviewRejected = false;
                string lastReviewRejectReason = null;
                string chosenTier = candidateTier;
                if (candidates != null)
                {
                    int bestAdjustedIndex = -1;
                    float bestAdjustedScore = float.NegativeInfinity;
                    for (int ci = 0; ci < candidates.Count; ci++)
                    {
                        string r = null;
                        if (promotedPick || !IsRedundant(candidates[ci], acceptedPoses, pick, out r))
                        {
                            if (CameraPlacementReviewStore.ShouldRejectCandidate(
                                    tile, candidates[ci], out string reviewRejectReason))
                            {
                                sawReviewRejected = true;
                                lastReviewRejectReason = reviewRejectReason;
                                if (placementDebugLogging || reconLogging)
                                {
                                    SurveillanceBootstrap.Log.LogInfo(
                                        $"[LethalCCTV] SURFACE_SKIP tile={sortedIndex} name={tile?.name} reason={reviewRejectReason}");
                                }
                                continue;
                            }

                            float adjustedScore = CameraPlacementReviewStore.AdjustCandidateScore(
                                tile, candidates[ci], usingExpandedFallback);
                            if (adjustedScore > bestAdjustedScore)
                            {
                                bestAdjustedIndex = ci;
                                bestAdjustedScore = adjustedScore;
                            }
                            haveChoice = true;
                            continue;
                        }
                        lastRedundantReason = r;
                    }

                    if (haveChoice)
                    {
                        chosen = candidates[bestAdjustedIndex];
                        if (bestAdjustedIndex == grandRoomIndex)
                            chosenTier = "grand-room";
                    }
                }

                if (!haveChoice)
                {
                    if (candidates == null || candidates.Count == 0)
                    {
                        // #1313: no flat structural wall or ceiling survived, so
                        // this tile gets no camera — there is no synthesized pose.
                        skippedNoCandidate++;
                        SurveillanceBootstrap.Log.LogInfo(
                            $"[LethalCCTV] SURFACE_SKIP tile={sortedIndex} name={tile?.name} reason=no-structural-candidate " +
                            $"patchRejects={patchRejects.Format()}");
                        if (promotedPick)
                            WarnPromotedPickUnmounted(pick, "no-structural-candidate");
                    }
                    else
                    {
                        if (sawReviewRejected)
                        {
                            skippedNoCandidate++;
                            SurveillanceBootstrap.Log.LogInfo(
                                $"[LethalCCTV] SURFACE_SKIP tile={sortedIndex} name={tile?.name} reason=all-review-rejected {lastReviewRejectReason}");
                            if (promotedPick)
                                WarnPromotedPickUnmounted(pick, $"all-review-rejected {lastReviewRejectReason}");
                        }
                        else
                        {
                            skippedRedundant++;
                            SurveillanceBootstrap.Log.LogInfo(
                                $"[LethalCCTV] SURFACE_SKIP tile={sortedIndex} name={tile?.name} reason=all-redundant {lastRedundantReason}");
                            if (promotedPick)
                                WarnPromotedPickUnmounted(pick, $"all-redundant {lastRedundantReason}");
                        }
                    }
                    continue;
                }

                int cameraIndex = _spawnedCameras.Count;
                CCTVCamera holder = InstantiateAtPose(
                    tile, cameraIndex, chosen.WorldPos, chosen.WorldRot,
                    chosen.CornerId, chosen.CornerLocal, chosen.MountMode, chosen.RoomHeightM,
                    source: "procedural/" + pick.Reason);
                if (holder == null)
                {
                    skippedNoCandidate++;
                    SurveillanceBootstrap.Log.LogWarning(
                        $"[LethalCCTV] SURFACE_SKIP tile={sortedIndex} name={tile?.name} " +
                        "reason=fixture-reservation-conflict source=surface-candidate");
                    if (promotedPick)
                        WarnPromotedPickUnmounted(pick, "fixture-reservation-conflict");
                    continue;
                }

                CameraPlacementReviewStore.ApplyCandidateDiagnostics(
                    holder, chosen, usingExpandedFallback ? "expanded" : "strict", chosenTier);
                _spawnedCameras.Add(holder);
                occupiedCameraTiles.Add(tile);
                acceptedPoses.Add(new AcceptedPose(chosen.WorldPos, chosen.Forward, tile, pick.Component));
                if (usingExpandedFallback) expandedFallbackAccepted++;

                if (placementDebugLogging || reconLogging)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] SURFACE_ACCEPT cam={cameraIndex} tile={sortedIndex} name={tile?.name} " +
                        $"source={(usingExpandedFallback ? "expanded" : "strict")} tier={chosenTier} " +
                        $"kind={chosen.Kind} mount={chosen.MountMode} score={chosen.Score:F1} " +
                        $"heightAboveFloor={chosen.MountHeightAboveFloorM:F2} centerDist={chosen.CenterUsefulDistM:F2} " +
                        $"frustum(hit/void/near)={chosen.FrustumHits}/{chosen.FrustumVoids}/{chosen.FrustumNearWall} " +
                        $"normal=({chosen.SupportNormal.x:F2},{chosen.SupportNormal.y:F2},{chosen.SupportNormal.z:F2}) " +
                        $"hit={chosen.HitName} layer={chosen.HitLayer} " +
                        $"pos=({chosen.WorldPos.x:F2},{chosen.WorldPos.y:F2},{chosen.WorldPos.z:F2})");
                }
            }

            // #1271: every prop is built here, inside the pass, rather
            // than in each CCTVCameraVisual.LateUpdate after it (LateUpdate stays the retry
            // path). Placement above is finished, so no prop can reach a placement query.
            for (int i = 0; i < _spawnedCameras.Count; i++)
            {
                CCTVCameraVisual.BuildNow(_spawnedCameras[i]);
            }

            int skippedTotal = skippedNoCandidate + skippedRedundant;
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] Spawned {_spawnedCameras.Count} cameras (interior picks={orderedPicks.Count}, " +
                $"skipped {skippedTotal}: {skippedNoCandidate} no-surface, {skippedRedundant} redundant, " +
                $"expandedFallback={expandedFallbackAccepted}/{expandedFallbackAttempts}) " +
                $"of {totalTiles} total tiles");
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] excluded {mineshaft} mineshaft tunnels, {tiny} tiny tiles, {name} name-matched tiles, {entrance} entrance tiles, {fireExit} fire-exit tiles, {corridor} corridors" +
                (other > 0 ? $", {other} other" : string.Empty));

            CamerasReady?.Invoke(_spawnedCameras);
        }

        private static void MarkAuthoredCamerasUnused(
            IReadOnlyList<AuthoredInteriorPlacementPose> poses,
            string reason)
        {
            if (poses == null)
                return;

            for (int i = 0; i < poses.Count; i++)
                AuthoredPlacementRoundReport.RecordResolvedButUnused(poses[i].Record, reason);
        }

        private static CameraMountMode ParseMountMode(string value)
        {
            if (Enum.TryParse(value, out CameraMountMode mode))
                return mode;
            return CameraMountMode.Wall;
        }

        private static bool IsRedundantPose(
            Vector3 pos, Vector3 forward, List<AcceptedPose> kept, CameraPick pick, out string reason)
        {
            reason = null;
            for (int i = 0; i < kept.Count; i++)
            {
                AcceptedPose k = kept[i];
                float d = Vector3.Distance(pos, k.Pos);
                if (d >= RedundancyDistM) continue;
                float dy = Mathf.Abs(pos.y - k.Pos.y);
                if (dy >= RedundancySameFloorYM) continue;
                float dot = Vector3.Dot(forward, k.Fwd);
                if (dot <= RedundancyForwardDot) continue;
                bool sameRoom = ReferenceEquals(pick.Tile, k.Tile)
                    || (pick.Component != null && ReferenceEquals(pick.Component, k.Comp));
                if (!sameRoom) continue;
                reason = $"distance={d:F1} dy={dy:F1} dot={dot:F2}";
                return true;
            }
            return false;
        }

        // T5 — composite-sort the capped picks for stable cameraIndex
        // assignment. Delegates to CompareTiles by projecting each
        // CameraPick to its (Tile, SrcIndex) pair — same eight-key
        // composite the Phase 1 build used; SrcIndex (= dungeon.AllTiles
        // index) is the final tiebreaker and is host/client-deterministic
        // via DunGen seed propagation.
        private static List<CameraPick> SortPicksForInstantiation(IReadOnlyList<CameraPick> picks)
        {
            var list = new List<CameraPick>(picks != null ? picks.Count : 0);
            if (picks == null) return list;
            for (int i = 0; i < picks.Count; i++) list.Add(picks[i]);
            list.Sort(ComparePicksForInstantiation);
            return list;
        }

        private List<CameraPick> TopUpSparseInteriorSelection(
            List<CameraPick> roomPicks,
            IReadOnlyList<Tile> allTiles,
            ISet<Tile> entranceTiles,
            ISet<Tile> fireExitTiles)
        {
            roomPicks ??= new List<CameraPick>(0);

            int cap = _config.NormalizedMaximumCameraCount;
            if (cap <= 0) return roomPicks;
            int targetCount = _config.NormalizedMinimumCameraCount;
            if (roomPicks.Count >= targetCount) return roomPicks;
            if (allTiles == null || allTiles.Count == 0) return roomPicks;

            if (targetCount <= roomPicks.Count) return roomPicks;

            var pickedTiles = new HashSet<Tile>();
            for (int i = 0; i < roomPicks.Count; i++)
            {
                Tile pickedTile = roomPicks[i].Tile;
                if (pickedTile != null) pickedTiles.Add(pickedTile);
            }

            var corridorFallbacks = new List<CameraPick>();
            for (int i = 0; i < allTiles.Count; i++)
            {
                Tile tile = allTiles[i];
                if (tile == null || pickedTiles.Contains(tile)) continue;
                if (_filter.ShouldInclude(tile, out string reason, entranceTiles, fireExitTiles)) continue;
                if (reason != TileExclusionFilter.ReasonCorridor) continue;

                int degree = tile.UsedDoorways != null ? tile.UsedDoorways.Count : 0;
                float footprint = tile.Bounds.size.x * tile.Bounds.size.z;
                bool isOnMainPath = false;
                try { isOnMainPath = tile.Placement != null && tile.Placement.IsOnMainPath; }
                catch { isOnMainPath = false; }

                corridorFallbacks.Add(new CameraPick(
                    tile: tile,
                    component: null,
                    reason: CameraPickReason.CorridorFallback,
                    degree: degree,
                    footprintM2: footprint,
                    isOnMainPath: isOnMainPath,
                    srcIndex: i));
            }

            if (corridorFallbacks.Count == 0) return roomPicks;

            corridorFallbacks.Sort(CameraBudgetCapper.GetCapPriorityComparison(_config));

            int deficit = targetCount - roomPicks.Count;
            if (deficit <= 0) return roomPicks;

            var result = new List<CameraPick>(roomPicks.Count + deficit);
            result.AddRange(roomPicks);
            int added = 0;
            for (int i = 0; i < corridorFallbacks.Count && added < deficit; i++)
            {
                result.Add(corridorFallbacks[i]);
                added++;
            }

            if (added > 0)
            {
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] Sparse interior camera selection detected; added {added} corridor fallback picks " +
                    $"(rooms={roomPicks.Count}, target={targetCount}, availableCorridors={corridorFallbacks.Count}).");
            }

            return result;
        }

        private static int ComparePicksForInstantiation(CameraPick a, CameraPick b)
        {
            return CompareTiles((a.Tile, a.SrcIndex), (b.Tile, b.SrcIndex));
        }

        // Sort comparator. Lexicographic order across the eight composite keys from PLAN §5.
        private static int CompareTiles((Tile tile, int srcIndex) a, (Tile tile, int srcIndex) b)
        {
            TilePlacementData ap = a.tile.Placement;
            TilePlacementData bp = b.tile.Placement;

            // 1. IsOnMainPath: main-path tiles first (true < false in sort, so invert).
            int cmp = (bp.IsOnMainPath ? 1 : 0).CompareTo(ap.IsOnMainPath ? 1 : 0);
            if (cmp != 0) return cmp;

            // 2. PathDepth ascending.
            cmp = ap.PathDepth.CompareTo(bp.PathDepth);
            if (cmp != 0) return cmp;

            // 3. BranchDepth ascending.
            cmp = ap.BranchDepth.CompareTo(bp.BranchDepth);
            if (cmp != 0) return cmp;

            // 4-6. Quantized position: integer decimeters (multiply-round-cast).
            Vector3 ap0 = ap.Position;
            Vector3 bp0 = bp.Position;
            cmp = QuantizeDm(ap0.x).CompareTo(QuantizeDm(bp0.x));
            if (cmp != 0) return cmp;
            cmp = QuantizeDm(ap0.z).CompareTo(QuantizeDm(bp0.z));
            if (cmp != 0) return cmp;
            cmp = QuantizeDm(ap0.y).CompareTo(QuantizeDm(bp0.y));
            if (cmp != 0) return cmp;

            // 7. Tile name (ordinal).
            cmp = string.CompareOrdinal(a.tile.name, b.tile.name);
            if (cmp != 0) return cmp;

            // 8. Pre-sort source index.
            return a.srcIndex.CompareTo(b.srcIndex);
        }

        private static int QuantizeDm(float v) => (int)Mathf.Round(v * 10f);

        // Build the entrance-tile and fire-exit-tile sets via closest-center
        // attribution. Algorithm per 01_phase1_filter_rewrite.md:
        //   - #1283: the dungeon-side pads come from the SpawnSyncedObject markers
        //     (CollectSpawnMarkerInputs), which already skips ship-side scripts
        //     (isEntranceToBuilding == true) and null entrancePoints.
        //   - For each pad, find the tile whose Placement.Bounds.center is
        //     closest to it (deterministic across runs).
        //   - Bounds.Contains is a sanity check only — if the closest tile's
        //     bounds don't contain the pad, log a warning (orphaned teleport)
        //     and skip attribution rather than attribute to a near-miss tile.
        //   - entranceId == 0 → entrance set; otherwise fire-exit set. The
        //     discriminator literal is Cecil-verified against
        //     RoundManager.FindMainEntranceScript IL.
        private (HashSet<Tile> entrance, HashSet<Tile> fireExit) BuildEntranceTileSets(
            IReadOnlyList<Tile> allTiles, IReadOnlyList<DungeonEntrancePad> pads)
        {
            var entrance = new HashSet<Tile>();
            var fireExit = new HashSet<Tile>();
            _mainEntranceTile = null;
            _hasMainEntrancePad = false;
            _mainEntrancePadWorld = Vector3.zero;
            bool reconLogging = _config.ReconLoggingEnabled.Value;
            bool placementDebugLogging = _config.PlacementDebugLoggingEnabled.Value;

            int attributedEntrance = 0;
            int attributedFireExit = 0;
            int skippedOrphan = 0;
            int mainEntranceSrcIndex = -1;

            for (int i = 0; i < pads.Count; i++)
            {
                DungeonEntrancePad pad = pads[i];
                Vector3 padWorld = pad.PadWorld;
                string kind = pad.IsMain ? "entrance" : "fire-exit";

                Tile closestTile = null;
                int closestIndex = -1;
                float closestSqr = float.PositiveInfinity;
                for (int j = 0; j < allTiles.Count; j++)
                {
                    Tile t = allTiles[j];
                    Vector3 center = t.Placement.Bounds.center;
                    float sqr = (center - padWorld).sqrMagnitude;
                    if (sqr < closestSqr)
                    {
                        closestSqr = sqr;
                        closestTile = t;
                        closestIndex = j;
                    }
                }
                if (closestTile == null) continue;

                if (!closestTile.Placement.Bounds.Contains(padWorld))
                {
                    if (placementDebugLogging)
                    {
                        SurveillanceBootstrap.Log.LogWarning(
                            $"[LethalCCTV] EntranceTeleport {kind} pad at " +
                            $"({padWorld.x:F2},{padWorld.y:F2},{padWorld.z:F2}) is outside closest tile " +
                            $"{closestTile.name} bounds; skipping attribution");
                    }
                    skippedOrphan++;
                    continue;
                }

                if (pad.IsMain)
                {
                    entrance.Add(closestTile);
                    attributedEntrance++;
                    // Remember the MAIN entrance for the promotion pass and
                    // the focus-target aim: the camera in this tile must
                    // watch the entrance door itself. #1283: marker order is
                    // undefined, so a second main pad (none in vanilla) never
                    // wins by order: the lowest AllTiles index wins, then the
                    // lowest quantized position (x, z, y).
                    bool better = _mainEntranceTile == null || closestIndex < mainEntranceSrcIndex;
                    if (!better && closestIndex == mainEntranceSrcIndex)
                    {
                        int cmp = QuantizeDm(padWorld.x).CompareTo(QuantizeDm(_mainEntrancePadWorld.x));
                        if (cmp == 0) cmp = QuantizeDm(padWorld.z).CompareTo(QuantizeDm(_mainEntrancePadWorld.z));
                        if (cmp == 0) cmp = QuantizeDm(padWorld.y).CompareTo(QuantizeDm(_mainEntrancePadWorld.y));
                        better = cmp < 0;
                    }
                    if (better)
                    {
                        _mainEntranceTile = closestTile;
                        _mainEntrancePadWorld = padWorld;
                        _hasMainEntrancePad = true;
                        mainEntranceSrcIndex = closestIndex;
                    }
                }
                else
                {
                    fireExit.Add(closestTile);
                    attributedFireExit++;
                }

                if (reconLogging)
                {
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] Attributed {kind} to tile " +
                        $"{closestTile.name} via pad at ({padWorld.x:F2},{padWorld.y:F2},{padWorld.z:F2})");
                }
            }

            if (placementDebugLogging || reconLogging)
            {
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] Entrance attribution: {attributedEntrance} entrance, {attributedFireExit} fire-exit; " +
                    $"skipped {skippedOrphan} orphan (of {pads.Count} marker pads).");
            }

            return (entrance, fireExit);
        }

    }
}
