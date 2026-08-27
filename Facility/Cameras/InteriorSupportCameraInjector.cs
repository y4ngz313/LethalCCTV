using System;
using System.Collections;
using System.Collections.Generic;
using DunGen;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Cameras.Placement;
using Y4NGZCompany.ShipSystems.Surveillance;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// Public surface the contracts HUD uses to force a CCTV camera into
    /// specific dungeon rooms — currently the rooms that own the global
    /// support fixtures (mainframe, alarm box, mini-vaults). The
    /// injector runs after the main <see cref="DungeonCameraSpawner"/> pass,
    /// reuses the same surface-mount placement and QuadCameraAssignment
    /// integration, and returns the freshly-spawned holders so the caller
    /// can log them.
    /// </summary>
    public static class InteriorSupportCameraInjector
    {
        private const float TargetEndpointToleranceM = 0.55f;
        private const float EmergencySurfaceInsetM = 0.28f;
        private const float EmergencyMountClearRadiusM = 0.16f;
        private const float EmergencyWallMountHeightM = 2.35f;
        private const float EmergencyWallCastDistanceM = 18f;
        private const float EmergencyCeilingCastDistanceM = 14f;

        public readonly struct SpawnResult
        {
            public Tile Tile { get; }
            public CCTVCamera Camera { get; }
            public string FailureReason { get; }
            public string PoseSource { get; }

            public SpawnResult(Tile tile, CCTVCamera camera, string failureReason)
                : this(tile, camera, failureReason, null)
            {
            }

            public SpawnResult(Tile tile, CCTVCamera camera, string failureReason, string poseSource)
            {
                Tile = tile;
                Camera = camera;
                FailureReason = failureReason;
                PoseSource = poseSource;
            }

            public bool Succeeded => Camera != null;
        }

        public static List<SpawnResult> EnsureCamerasInTiles(System.Collections.IEnumerable tiles)
        {
            var results = new List<SpawnResult>();
            if (tiles == null) return results;

            var typedTiles = new List<Tile>();
            foreach (object entry in tiles)
            {
                if (entry is Tile tile)
                    typedTiles.Add(tile);
            }
            if (typedTiles.Count == 0) return results;

            SurveillanceBootstrap plugin = SurveillanceBootstrap.Instance;
            if (plugin == null) return results;
            LethalCCTVConfig config = SurveillanceBootstrap.Config;
            if (config == null) return results;
            if (!CCTVTerminalUnlockable.IsPurchased()) return results;

            PlacementParams placementParams = new PlacementParams(
                heightFraction: config.PlacementHeightFraction,
                horizontalFraction: config.PlacementHorizontalFraction,
                wallMountMinRoomHeightM: config.WallMountMinRoomHeightM,
                wallMountHeightM: config.WallMountHeightM);
            PlacementMasks masks = PlacementMask.Resolve(false);
            if (!masks.Valid)
            {
                for (int i = 0; i < typedTiles.Count; i++)
                    results.Add(new SpawnResult(typedTiles[i], null, "placement-mask-invalid"));
                return results;
            }
            PlacementMasks fallbackMasks = PlacementMask.ResolveExpandedMountFallback(masks, false);

            List<CCTVCamera> cameraSnapshot = BuildCameraSnapshot();
            int baseIndex = NextCameraIndex(cameraSnapshot);

            for (int i = 0; i < typedTiles.Count; i++)
            {
                Tile tile = typedTiles[i];
                if (tile == null) continue;

                if (TileAlreadyHasCamera(tile, cameraSnapshot))
                {
                    results.Add(new SpawnResult(tile, null, "already-served"));
                    continue;
                }

                Bounds box = DungeonCameraSpawner.ComputeWorldAabbLocal(tile);
                if (box.size.sqrMagnitude < 1e-6f) box = FallbackBox(tile);
                CameraSemanticContext semantic = CameraSemanticContextBuilder.Build(tile, box);

                SurfaceMount.Candidate chosen = default;
                bool haveChoice = false;
                bool usingExpandedFallback = false;

                List<SurfaceMount.Candidate> candidates = SurfaceMount.Compute(
                    tile, box, sortedIndex: i, in placementParams, in masks, in semantic,
                    relaxed: false, debug: false);
                if (candidates != null && candidates.Count > 0)
                {
                    chosen = candidates[0];
                    haveChoice = true;
                }
                else if (fallbackMasks.Valid)
                {
                    candidates = SurfaceMount.Compute(
                        tile, box, sortedIndex: i, in placementParams, in fallbackMasks, in semantic,
                        relaxed: true, debug: false);
                    if (candidates != null && candidates.Count > 0)
                    {
                        chosen = candidates[0];
                        haveChoice = true;
                        usingExpandedFallback = true;
                    }
                }

                if (!haveChoice)
                {
                    results.Add(new SpawnResult(tile, null, "no-mount-candidate"));
                    continue;
                }

                int cameraIndex = baseIndex++;
                CCTVCamera holder = InstantiateAtPose(
                    tile, cameraIndex, chosen.WorldPos, chosen.WorldRot,
                    chosen.CornerId, chosen.CornerLocal, chosen.MountMode, chosen.RoomHeightM);
                CameraPlacementReviewStore.ApplyCandidateDiagnostics(
                    holder, chosen, usingExpandedFallback ? "support-expanded" : "support-strict");
                QuadCameraAssignment.RegisterSupplementaryCamera(holder);
                Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry.RegisterSupplementaryCamera(holder);
                cameraSnapshot.Add(holder);
                results.Add(new SpawnResult(tile, holder, null));
            }

            return results;
        }

        public static List<SpawnResult> EnsureCamerasForSupportPoses(System.Collections.IEnumerable supportPoses)
        {
            var results = new List<SpawnResult>();
            IEnumerator routine = EnsureCamerasForSupportPosesBudgeted(supportPoses, result =>
            {
                if (result is SpawnResult spawnResult)
                    results.Add(spawnResult);
            }, null);
            while (routine.MoveNext())
            {
            }

            return results;
        }

        public static IEnumerator EnsureCamerasForSupportPosesBudgeted(System.Collections.IEnumerable supportPoses, Action<object> onResult, Func<bool> shouldYield)
        {
            var results = new List<SpawnResult>();
            void Emit(SpawnResult result)
            {
                results.Add(result);
                onResult?.Invoke(result);
            }

            if (supportPoses == null) yield break;

            var poses = new List<PlacementPose>();
            foreach (object entry in supportPoses)
            {
                if (entry is PlacementPose pose && pose.Tile != null)
                    poses.Add(pose);
            }
            if (poses.Count == 0) yield break;

            SurveillanceBootstrap plugin = SurveillanceBootstrap.Instance;
            if (plugin == null) yield break;
            LethalCCTVConfig config = SurveillanceBootstrap.Config;
            if (config == null) yield break;
            if (!CCTVTerminalUnlockable.IsPurchased()) yield break;

            PlacementParams placementParams = new PlacementParams(
                heightFraction: config.PlacementHeightFraction,
                horizontalFraction: config.PlacementHorizontalFraction,
                wallMountMinRoomHeightM: config.WallMountMinRoomHeightM,
                wallMountHeightM: config.WallMountHeightM);
            PlacementMasks masks = PlacementMask.Resolve(false);
            if (!masks.Valid)
            {
                for (int i = 0; i < poses.Count; i++)
                    Emit(new SpawnResult(poses[i].Tile, null, "placement-mask-invalid", poses[i].Source));
                yield break;
            }
            PlacementMasks fallbackMasks = PlacementMask.ResolveExpandedMountFallback(masks, false);

            List<CCTVCamera> cameraSnapshot = BuildCameraSnapshot();
            int baseIndex = NextCameraIndex(cameraSnapshot);
            var servedTiles = new HashSet<Tile>();
            for (int i = 0; i < poses.Count; i++)
            {
                PlacementPose supportPose = poses[i];
                Tile tile = supportPose.Tile;
                if (tile == null) continue;
                if (servedTiles.Contains(tile))
                {
                    Emit(new SpawnResult(tile, null, "tile-already-served-this-call", supportPose.Source));
                    if (shouldYield != null && shouldYield())
                        yield return null;
                    continue;
                }

                CCTVCamera existing = FindCameraForTile(tile, cameraSnapshot);
                if (existing != null)
                {
                    AimCameraAtSupport(existing, supportPose);
                    if (CameraHasSupportLineOfSight(existing, supportPose, out string existingLosReason))
                    {
                        existing.PlacementSource = "support-retarget";
                        servedTiles.Add(tile);
                        Emit(new SpawnResult(tile, existing, "retargeted-existing", supportPose.Source));
                        SurveillanceBootstrap.Log.LogInfo(
                            $"[LethalCCTV] SUPPORT_CAMERA_RETARGET existing='{existing.name}' tile='{tile.name}' target=({supportPose.InteractionPoint.x:F2},{supportPose.InteractionPoint.y:F2},{supportPose.InteractionPoint.z:F2}) los=clear rolePoseSource={supportPose.Source}");
                        if (shouldYield != null && shouldYield())
                            yield return null;
                        continue;
                    }

                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] SUPPORT_CAMERA_RETARGET_REJECT existing='{existing.name}' tile='{tile.name}' reason={existingLosReason} target=({supportPose.InteractionPoint.x:F2},{supportPose.InteractionPoint.y:F2},{supportPose.InteractionPoint.z:F2}) rolePoseSource={supportPose.Source}");
                }

                Bounds box = DungeonCameraSpawner.ComputeWorldAabbLocal(tile);
                if (box.size.sqrMagnitude < 1e-6f) box = FallbackBox(tile);
                CameraSemanticContext semantic = CameraSemanticContextBuilder.Build(tile, box);

                SurfaceMount.Candidate chosen;
                bool usingExpandedFallback = false;
                bool usingEmergencyFallback = false;
                bool usingForcedEmergencyFallback = false;
                string emergencyRejectReason = null;
                bool haveChoice = TryChooseSupportCameraCandidate(tile, box, i, in placementParams, in masks, in semantic, supportPose, out chosen);
                if (!haveChoice && fallbackMasks.Valid)
                {
                    haveChoice = TryChooseSupportCameraCandidate(tile, box, i, in placementParams, in fallbackMasks, in semantic, supportPose, out chosen);
                    usingExpandedFallback = haveChoice;
                }
                if (!haveChoice)
                {
                    haveChoice = TryChooseEmergencySupportCameraCandidate(
                        tile, box, in masks, supportPose, out chosen, out emergencyRejectReason, out usingForcedEmergencyFallback);
                    usingEmergencyFallback = haveChoice;
                }
                if (!haveChoice)
                {
                    string reason = "no-support-los-mount-candidate";
                    if (!string.IsNullOrEmpty(emergencyRejectReason))
                        reason += "/" + emergencyRejectReason;
                    Emit(new SpawnResult(tile, null, reason, supportPose.Source));
                    if (shouldYield != null && shouldYield())
                        yield return null;
                    continue;
                }

                if (usingForcedEmergencyFallback
                    && !string.IsNullOrEmpty(supportPose.Source)
                    && supportPose.Source.IndexOf("mainframe", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Emit(new SpawnResult(tile, null, "mainframe-forced-emergency-rejected", supportPose.Source));
                    if (shouldYield != null && shouldYield())
                        yield return null;
                    continue;
                }

                Quaternion aimedRot = RotationToward(chosen.WorldPos, supportPose.InteractionPoint, chosen.WorldRot);
                int cameraIndex = baseIndex++;
                CCTVCamera holder = InstantiateAtPose(
                    tile, cameraIndex, chosen.WorldPos, aimedRot,
                    chosen.CornerId, chosen.CornerLocal, chosen.MountMode, chosen.RoomHeightM);
                string supportSource = usingEmergencyFallback
                    ? (usingForcedEmergencyFallback ? "support-emergency-forced" : "support-emergency")
                    : (usingExpandedFallback ? "support-expanded" : "support-strict");
                CameraPlacementReviewStore.ApplyCandidateDiagnostics(
                    holder, chosen, supportSource);
                QuadCameraAssignment.RegisterSupplementaryCamera(holder);
                Y4NGZCompany.Facility.Security.CctvSecurityCameraRegistry.RegisterSupplementaryCamera(holder);
                cameraSnapshot.Add(holder);
                servedTiles.Add(tile);
                Emit(new SpawnResult(tile, holder, null, supportPose.Source));
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] SUPPORT_CAMERA_ACCEPT cam={cameraIndex} tile='{tile.name}' target=({supportPose.InteractionPoint.x:F2},{supportPose.InteractionPoint.y:F2},{supportPose.InteractionPoint.z:F2}) candidate={chosen.Kind} source={supportSource} los={(usingForcedEmergencyFallback ? "forced" : "clear")} pos=({chosen.WorldPos.x:F2},{chosen.WorldPos.y:F2},{chosen.WorldPos.z:F2})");
                if (shouldYield != null && shouldYield())
                    yield return null;
            }
        }

        private static bool TileAlreadyHasCamera(Tile tile, IReadOnlyList<CCTVCamera> cameraSnapshot)
        {
            return FindCameraForTile(tile, cameraSnapshot) != null;
        }

        private static CCTVCamera FindCameraForTile(Tile tile, IReadOnlyList<CCTVCamera> cameraSnapshot)
        {
            if (tile == null) return null;
            if (cameraSnapshot == null) return null;
            for (int i = 0; i < cameraSnapshot.Count; i++)
            {
                CCTVCamera cam = cameraSnapshot[i];
                if (cam == null) continue;
                if (cam.OwningTile == tile) return cam;
                if (cam.transform != null && tile.transform != null && cam.transform.IsChildOf(tile.transform))
                    return cam;
            }
            return null;
        }

        private static int NextCameraIndex(IReadOnlyList<CCTVCamera> cameraSnapshot)
        {
            int max = -1;
            if (cameraSnapshot == null)
                return 0;

            for (int i = 0; i < cameraSnapshot.Count; i++)
            {
                if (cameraSnapshot[i] != null && cameraSnapshot[i].CameraIndex > max) max = cameraSnapshot[i].CameraIndex;
            }
            return max + 1;
        }

        private static List<CCTVCamera> BuildCameraSnapshot()
        {
            return new List<CCTVCamera>(UnityEngine.Object.FindObjectsOfType<CCTVCamera>(includeInactive: true));
        }

        // The spawner's InstantiateAtPose is private. We can't replicate every
        // field-stash inlined here, but the supported-camera setup is small
        // enough to reproduce inline at parity with what the spawner does.
        private static CCTVCamera InstantiateAtPose(
            Tile tile, int cameraIndex, Vector3 worldPos, Quaternion worldRot,
            int cornerId, Vector3 cornerLocal, CameraMountMode mountMode,
            float drivingRoomHeightM)
        {
            string ownerName = tile != null ? tile.name : "Support";
            var go = new GameObject($"LethalCCTVCamera_{cameraIndex}_Support_{ownerName}");
            if (tile != null)
                go.transform.SetParent(tile.transform, worldPositionStays: false);
            go.transform.SetPositionAndRotation(worldPos, worldRot);

            Camera cam = go.AddComponent<Camera>();
            CCTVCamera holder = go.AddComponent<CCTVCamera>();
            holder.CameraIndex = cameraIndex;
            holder.DisplayLabel = "CAM_" + cameraIndex.ToString("D2") + "S";
            holder.OwningTile = tile;
            holder.Cam = cam;
            holder.HdrpData = null;
            holder.PlacementCornerLocal = cornerLocal;
            holder.PlacementCornerId = cornerId;
            holder.MountMode = mountMode;
            holder.DrivingRoomHeightM = drivingRoomHeightM;
            holder.ConfigureDisabled();
            holder.CaptureBaseline();
            CCTVCameraVisual.AttachTo(holder);
            CctvDetectionIndicator.AttachTo(holder);
            return holder;
        }

        private static bool TryChooseSupportCameraCandidate(
            Tile tile,
            Bounds box,
            int sortedIndex,
            in PlacementParams placementParams,
            in PlacementMasks masks,
            in CameraSemanticContext semantic,
            PlacementPose supportPose,
            out SurfaceMount.Candidate chosen)
        {
            chosen = default;
            List<SurfaceMount.Candidate> candidates = SurfaceMount.Compute(
                tile, box, sortedIndex, in placementParams, in masks, in semantic,
                relaxed: false, debug: false);
            if (candidates == null || candidates.Count == 0)
            {
                candidates = SurfaceMount.Compute(
                    tile, box, sortedIndex, in placementParams, in masks, in semantic,
                    relaxed: true, debug: false);
            }
            if (candidates == null || candidates.Count == 0)
                return false;

            int bestIndex = -1;
            float bestScore = float.NegativeInfinity;
            string lastReject = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                SurfaceMount.Candidate candidate = candidates[i];
                Vector3 toTarget = supportPose.InteractionPoint - candidate.WorldPos;
                if (toTarget.sqrMagnitude < 0.5f) continue;
                float distance = toTarget.magnitude;
                Vector3 dir = toTarget / distance;
                Quaternion supportRot = RotationToward(candidate.WorldPos, supportPose.InteractionPoint, candidate.WorldRot);
                Vector3 supportForward = supportRot * Vector3.forward;
                if (!HasSupportLineOfSight(candidate.WorldPos, supportForward, supportPose, out string losReason))
                {
                    lastReject = losReason;
                    continue;
                }
                float aimDot = Vector3.Dot(supportForward, dir);
                float score = candidate.Score + 650f + aimDot * 180f - Mathf.Abs(distance - 8f) * 6f;
                if (candidate.Kind == SurfaceMount.SurfaceKind.Corner) score += 320f;
                if (candidate.MountHeightAboveFloorM >= 2.35f) score += 180f;
                if (candidate.DoorwayVisibilityScore > 0f) score += 120f;
                if (candidate.FrustumVoids > 2) score -= 500f;
                if (score <= bestScore) continue;
                bestScore = score;
                bestIndex = i;
            }

            if (bestIndex < 0)
            {
                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] SUPPORT_CAMERA_CANDIDATE_REJECT tile='{tile?.name}' reason=no-clear-los last={lastReject ?? "none"} target=({supportPose.InteractionPoint.x:F2},{supportPose.InteractionPoint.y:F2},{supportPose.InteractionPoint.z:F2})");
                return false;
            }
            chosen = candidates[bestIndex];
            return true;
        }

        private static bool TryChooseEmergencySupportCameraCandidate(
            Tile tile,
            Bounds box,
            in PlacementMasks masks,
            PlacementPose supportPose,
            out SurfaceMount.Candidate chosen,
            out string rejectReason,
            out bool forcedEmergency)
        {
            chosen = default;
            rejectReason = null;
            forcedEmergency = false;
            if (tile == null || !masks.Valid)
            {
                rejectReason = "emergency-invalid-input";
                return false;
            }

            Vector3 forward = SafeHorizontal(supportPose.Forward, tile.transform != null ? tile.transform.forward : Vector3.forward);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();
            float floorY = EstimateFloorY(tile, supportPose.Position, in masks);
            Vector3 target = ResolveSupportAimPoint(supportPose);

            if (TryChooseEmergencyWallCandidate(tile, in masks, supportPose, target, forward, right, floorY, out chosen, out rejectReason))
                return true;

            if (TryChooseEmergencyCeilingCandidate(tile, in masks, supportPose, target, forward, right, floorY, out chosen, out rejectReason))
                return true;

            if (TryChooseEmergencyInspectionCandidate(tile, box, in masks, supportPose, target, forward, right, floorY, out chosen, out rejectReason, out forcedEmergency))
                return true;

            if (string.IsNullOrEmpty(rejectReason))
                rejectReason = "emergency-no-clear-pose";
            return false;
        }

        private static bool TryChooseEmergencyWallCandidate(
            Tile tile,
            in PlacementMasks masks,
            PlacementPose supportPose,
            Vector3 target,
            Vector3 forward,
            Vector3 right,
            float floorY,
            out SurfaceMount.Candidate chosen,
            out string rejectReason)
        {
            chosen = default;
            rejectReason = null;
            Vector3 originBase = supportPose.Position + forward * 0.45f;
            originBase.y = floorY + EmergencyWallMountHeightM;
            Vector3[] castDirs =
            {
                forward,
                (forward + right * 0.45f).normalized,
                (forward - right * 0.45f).normalized,
                right,
                -right,
                -forward,
            };

            for (int i = 0; i < castDirs.Length; i++)
            {
                Vector3 dir = SafeHorizontal(castDirs[i], forward);
                if (!Physics.Raycast(originBase, dir, out RaycastHit hit, EmergencyWallCastDistanceM, masks.MountMask, QueryTriggerInteraction.Ignore))
                {
                    rejectReason = "emergency-wall-no-hit";
                    continue;
                }

                if (hit.distance < 1.0f)
                {
                    rejectReason = "emergency-wall-too-close";
                    continue;
                }

                Vector3 normalH = new Vector3(hit.normal.x, 0f, hit.normal.z);
                if (normalH.sqrMagnitude < 1e-6f || Mathf.Abs(hit.normal.y) >= 0.55f)
                {
                    rejectReason = "emergency-wall-not-vertical";
                    continue;
                }
                normalH.Normalize();

                Vector3 mountPos = hit.point + hit.normal.normalized * EmergencySurfaceInsetM;
                if (!ContainsTileXZ(tile.Bounds, mountPos, 0.2f))
                {
                    rejectReason = "emergency-wall-outside-tile";
                    continue;
                }
                if (!HasMountClearance(mountPos, in masks))
                {
                    rejectReason = "emergency-wall-embedded";
                    continue;
                }

                Quaternion rotation = RotationToward(mountPos, target, Quaternion.LookRotation(-normalH, Vector3.up));
                if (!HasSupportLineOfSight(mountPos, rotation * Vector3.forward, supportPose, out string losReason))
                {
                    rejectReason = "emergency-wall-" + losReason;
                    continue;
                }

                chosen = BuildEmergencyCandidate(
                    tile, in masks, mountPos, rotation, CameraMountMode.Wall,
                    SurfaceMount.SurfaceKind.Wall, hit.normal.normalized,
                    hit.collider != null ? hit.collider.name : "emergency-wall",
                    hit.collider != null ? hit.collider.gameObject.layer : -1,
                    cornerId: 970 + i);
                return true;
            }

            return false;
        }

        private static bool TryChooseEmergencyCeilingCandidate(
            Tile tile,
            in PlacementMasks masks,
            PlacementPose supportPose,
            Vector3 target,
            Vector3 forward,
            Vector3 right,
            float floorY,
            out SurfaceMount.Candidate chosen,
            out string rejectReason)
        {
            chosen = default;
            rejectReason = null;
            Vector3[] offsets =
            {
                forward * 1.4f,
                forward * 2.2f,
                forward * 1.6f + right * 0.75f,
                forward * 1.6f - right * 0.75f,
                Vector3.zero,
            };

            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 origin = supportPose.Position + offsets[i];
                origin.y = floorY + 1.25f;
                if (!ContainsTileXZ(tile.Bounds, origin, 0.3f))
                    origin = ClampTileXZ(tile.Bounds, origin, 0.35f);

                if (!Physics.Raycast(origin, Vector3.up, out RaycastHit hit, EmergencyCeilingCastDistanceM, masks.MountMask, QueryTriggerInteraction.Ignore))
                {
                    rejectReason = "emergency-ceiling-no-hit";
                    continue;
                }
                if (hit.distance < 1.0f || hit.normal.y > -0.5f)
                {
                    rejectReason = "emergency-ceiling-not-ceiling";
                    continue;
                }

                Vector3 mountPos = hit.point + hit.normal.normalized * EmergencySurfaceInsetM;
                if (!HasMountClearance(mountPos, in masks))
                {
                    rejectReason = "emergency-ceiling-embedded";
                    continue;
                }

                Quaternion rotation = RotationToward(mountPos, target, Quaternion.LookRotation(Vector3.down, Vector3.up));
                if (!HasSupportLineOfSight(mountPos, rotation * Vector3.forward, supportPose, out string losReason))
                {
                    rejectReason = "emergency-ceiling-" + losReason;
                    continue;
                }

                chosen = BuildEmergencyCandidate(
                    tile, in masks, mountPos, rotation, CameraMountMode.Ceiling,
                    SurfaceMount.SurfaceKind.Ceiling, hit.normal.normalized,
                    hit.collider != null ? hit.collider.name : "emergency-ceiling",
                    hit.collider != null ? hit.collider.gameObject.layer : -1,
                    cornerId: 980 + i);
                return true;
            }

            return false;
        }

        private static bool TryChooseEmergencyInspectionCandidate(
            Tile tile,
            Bounds box,
            in PlacementMasks masks,
            PlacementPose supportPose,
            Vector3 target,
            Vector3 forward,
            Vector3 right,
            float floorY,
            out SurfaceMount.Candidate chosen,
            out string rejectReason,
            out bool forcedEmergency)
        {
            chosen = default;
            rejectReason = null;
            forcedEmergency = false;
            float roomHeight = box.size.y > 0.1f ? box.size.y : tile.Bounds.size.y;
            float cameraHeight = floorY + Mathf.Clamp(Mathf.Min(roomHeight - 0.6f, 2.25f), 1.65f, 2.35f);
            float[] distances = { 2.8f, 2.1f, 1.45f, 1.05f };
            float[] lateralOffsets = { 0f, 0.55f, -0.55f };

            for (int d = 0; d < distances.Length; d++)
            {
                for (int l = 0; l < lateralOffsets.Length; l++)
                {
                    Vector3 mountPos = supportPose.Position + forward * distances[d] + right * lateralOffsets[l];
                    mountPos.y = cameraHeight;
                    mountPos = ClampTileXZ(tile.Bounds, mountPos, 0.45f);
                    if (!HasMountClearance(mountPos, in masks))
                    {
                        rejectReason = "emergency-inspection-embedded";
                        continue;
                    }

                    Quaternion rotation = RotationToward(mountPos, target, Quaternion.LookRotation(-forward, Vector3.up));
                    if (!HasSupportLineOfSight(mountPos, rotation * Vector3.forward, supportPose, out string losReason))
                    {
                        rejectReason = "emergency-inspection-" + losReason;
                        continue;
                    }

                    chosen = BuildEmergencyCandidate(
                        tile, in masks, mountPos, rotation, CameraMountMode.Wall,
                        SurfaceMount.SurfaceKind.Wall, -forward,
                        "emergency-inspection", -1,
                        cornerId: 990 + d * 3 + l);
                    return true;
                }
            }

            float[] forcedDistances = { 1.2f, 0.95f, 1.45f };
            float[] forcedOffsets = { 0f, 0.45f, -0.45f };
            Vector3 forcedPos = supportPose.Position + forward * forcedDistances[0];
            forcedPos.y = cameraHeight;
            forcedPos = ClampTileXZ(tile.Bounds, forcedPos, 0.35f);
            for (int d = 0; d < forcedDistances.Length; d++)
            {
                for (int l = 0; l < forcedOffsets.Length; l++)
                {
                    Vector3 candidatePos = supportPose.Position + forward * forcedDistances[d] + right * forcedOffsets[l];
                    candidatePos.y = cameraHeight;
                    candidatePos = ClampTileXZ(tile.Bounds, candidatePos, 0.35f);
                    forcedPos = candidatePos;
                    if (HasMountClearance(candidatePos, in masks))
                    {
                        d = forcedDistances.Length;
                        break;
                    }
                }
            }

            Quaternion forcedRotation = RotationToward(forcedPos, target, Quaternion.LookRotation(-forward, Vector3.up));
            chosen = BuildEmergencyCandidate(
                tile, in masks, forcedPos, forcedRotation, CameraMountMode.Wall,
                SurfaceMount.SurfaceKind.Wall, -forward,
                "emergency-inspection-forced", -1,
                cornerId: 999);
            rejectReason = "emergency-inspection-forced";
            forcedEmergency = true;
            return true;
        }

        private static SurfaceMount.Candidate BuildEmergencyCandidate(
            Tile tile,
            in PlacementMasks masks,
            Vector3 mountPos,
            Quaternion rotation,
            CameraMountMode mountMode,
            SurfaceMount.SurfaceKind kind,
            Vector3 supportNormal,
            string hitName,
            int hitLayer,
            int cornerId)
        {
            Vector3 forward = rotation * Vector3.forward;
            CameraReviewMetrics metrics = CameraPlacementReviewStore.ComputeMetrics(mountPos, rotation, tile, masks);
            return new SurfaceMount.Candidate(
                mountPos, rotation, forward, mountMode, kind, 9000f,
                metrics.HeightAboveFloor, metrics.CenterSightDistance,
                metrics.FrustumHits, metrics.FrustumVoids, metrics.FrustumNearWall,
                supportNormal, hitName, hitLayer, cornerId,
                tile != null ? tile.Bounds.size.y : 0f,
                0f, 0f, "default");
        }

        private static Vector3 ResolveSupportAimPoint(PlacementPose supportPose)
        {
            Vector3 forward = supportPose.Forward.sqrMagnitude > 1e-6f ? supportPose.Forward.normalized : Vector3.forward;
            return supportPose.Position + Vector3.up * 1.05f + forward * 0.18f;
        }

        private static float EstimateFloorY(Tile tile, Vector3 near, in PlacementMasks masks)
        {
            Vector3 origin = near + Vector3.up * 4f;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 10f, masks.MountMask, QueryTriggerInteraction.Ignore))
                return hit.point.y;
            return tile != null ? tile.Bounds.min.y : near.y - 1.6f;
        }

        private static bool HasMountClearance(Vector3 mountPos, in PlacementMasks masks)
        {
            return !Physics.CheckSphere(mountPos, EmergencyMountClearRadiusM, masks.SolidMask, QueryTriggerInteraction.Ignore);
        }

        private static Vector3 SafeHorizontal(Vector3 value, Vector3 fallback)
        {
            value.y = 0f;
            if (value.sqrMagnitude < 1e-6f)
            {
                value = fallback;
                value.y = 0f;
            }
            if (value.sqrMagnitude < 1e-6f)
                value = Vector3.forward;
            return value.normalized;
        }

        private static bool ContainsTileXZ(Bounds bounds, Vector3 position, float inset)
        {
            return position.x >= bounds.min.x + inset &&
                   position.x <= bounds.max.x - inset &&
                   position.z >= bounds.min.z + inset &&
                   position.z <= bounds.max.z - inset;
        }

        private static Vector3 ClampTileXZ(Bounds bounds, Vector3 position, float inset)
        {
            if (bounds.size.x > inset * 2f)
                position.x = Mathf.Clamp(position.x, bounds.min.x + inset, bounds.max.x - inset);
            if (bounds.size.z > inset * 2f)
                position.z = Mathf.Clamp(position.z, bounds.min.z + inset, bounds.max.z - inset);
            return position;
        }

        private static void AimCameraAtSupport(CCTVCamera camera, PlacementPose supportPose)
        {
            if (camera == null || camera.transform == null) return;
            Quaternion rotation = RotationToward(camera.transform.position, supportPose.InteractionPoint, camera.transform.rotation);
            camera.transform.rotation = rotation;
            if (camera.Cam != null)
                camera.Cam.transform.rotation = rotation;
            camera.CaptureBaseline();
        }

        private static Quaternion RotationToward(Vector3 cameraPosition, Vector3 target, Quaternion fallback)
        {
            Vector3 direction = target - cameraPosition;
            if (direction.sqrMagnitude < 1e-6f) return fallback;
            return Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        private static bool CameraHasSupportLineOfSight(CCTVCamera camera, PlacementPose supportPose, out string reason)
        {
            reason = "no-camera";
            if (camera == null || camera.transform == null) return false;
            return HasSupportLineOfSight(camera.transform.position, camera.transform.forward, supportPose, out reason);
        }

        private static bool HasSupportLineOfSight(Vector3 cameraPosition, Vector3 cameraForward, PlacementPose supportPose, out string reason)
        {
            Vector3 forward = supportPose.Forward.sqrMagnitude > 1e-6f ? supportPose.Forward.normalized : Vector3.forward;
            Vector3 lensForward = cameraForward.sqrMagnitude > 1e-6f ? cameraForward.normalized : Vector3.zero;
            Vector3 lensPosition = cameraPosition + lensForward * 0.28f + Vector3.up * 0.05f;
            Vector3[] targets =
            {
                supportPose.InteractionPoint,
                supportPose.Position + Vector3.up * 1.05f + forward * 0.22f,
                supportPose.Position + Vector3.up * 0.55f + forward * 0.18f,
            };

            reason = null;
            for (int i = 0; i < targets.Length; i++)
            {
                if (HasClearSight(lensPosition, targets[i], supportPose, out reason))
                    return true;
            }

            return false;
        }

        private static bool HasClearSight(Vector3 cameraPosition, Vector3 target, PlacementPose supportPose, out string reason)
        {
            reason = null;
            StartOfRound sor = StartOfRound.Instance;
            int mask = sor != null ? sor.collidersAndRoomMaskAndDefault : ~0;
            Vector3 a = cameraPosition + Vector3.up * 0.05f;
            Vector3 b = target;
            Vector3 delta = b - a;
            if (delta.sqrMagnitude < 0.25f)
            {
                reason = "target-too-close";
                return false;
            }

            if (!Physics.Linecast(a, b, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
                return true;

            if (IsFixtureEndpointHit(hit, target, supportPose))
                return true;

            reason = hit.collider != null
                ? $"blocked-by={hit.collider.name} layer={hit.collider.gameObject.layer} dist={hit.distance:F2}"
                : "blocked";
            return false;
        }

        private static bool IsFixtureEndpointHit(RaycastHit hit, Vector3 target, PlacementPose supportPose)
        {
            if (hit.collider == null) return false;
            float targetDistance = Vector3.Distance(hit.point, target);
            if (targetDistance <= TargetEndpointToleranceM) return true;
            float fixtureDistance = Vector3.Distance(hit.point, supportPose.Position);
            return fixtureDistance <= Mathf.Max(TargetEndpointToleranceM, supportPose.InteractionPoint.y - supportPose.Position.y + 0.25f);
        }

        // The spawner uses an internal fallback when the rendered-geometry
        // AABB is degenerate. Replicate the same shape here so the surface
        // mount has something to cast against.
        private static Bounds FallbackBox(Tile tile)
        {
            if (tile == null) return new Bounds(Vector3.zero, Vector3.one);
            Bounds local = tile.Placement != null ? tile.Placement.LocalBounds : tile.Bounds;
            if (local.size.sqrMagnitude < 1e-6f) local = tile.Bounds;
            return local;
        }
    }
}
