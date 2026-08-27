using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using DunGen;
using DunGen.Graph;
using Y4NGZCompany.Facility.Cameras.Placement;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;

namespace Y4NGZCompany.Facility.Cameras
{
    internal enum CameraPlacementReviewRating
    {
        Perfect,
        TooLow,
        Floating,
        TooHigh,
        BlockedView,
        LookingAtWall,
        PoorCoverage,
        SkyboxVisible,
        Duplicate,
        ImportantRoom,
    }

    /// <summary>
    /// Stable buckets for why a reviewed pose was turned down. Kept separate from the detailed
    /// reason string (which carries measurements) so the round report can count them without
    /// parsing or allocating.
    /// </summary>
    internal static class ReviewedPoseRejectCategories
    {
        public const string TooLow = "too-low";
        public const string Blocked = "blocked";
        public const string PoorFrustum = "poor-frustum";
        public const string Embedded = "embedded";
        public const string SurfaceContactLost = "surface-contact-lost";
        public const string SurfaceContactUnverifiable = "surface-contact-unverifiable";
    }

    internal static class CameraPlacementReviewTags
    {
        public const string Perfect = "perfect";
        public const string TooLow = "too-low";
        public const string Floating = "floating";
        public const string TooHigh = "too-high";
        public const string BlockedView = "blocked-view";
        public const string LookingAtWall = "looking-at-wall";
        public const string PoorCoverage = "poor-coverage";
        public const string SkyboxVisible = "skybox-visible";
        public const string Duplicate = "duplicate";
        public const string ImportantRoom = "important-room";
        public const string DoorwayVisible = "doorway-visible";
        public const string DoorwayNotVisible = "doorway-not-visible";
        public const string FramesRoomWell = "frames-room-well";
        public const string DoesNotFrameRoom = "does-not-frame-room";
        public const string StuckInFurniture = "stuck-in-furniture";
        public const string PipeObstructed = "pipe-obstructed";
        public const string CoversMainframe = "covers-mainframe";
        public const string GoodHeight = "good-height";
        public const string GoodCorner = "good-corner";
        public const string MainframeRoom = "mainframe-room";
        public const string CompanyStashRoom = "company-stash-room";
        public const string BadMainframeRoom = "bad-mainframe-room";
        public const string BadCompanyStashRoom = "bad-company-stash-room";
    }

    internal readonly struct CameraPlacementTileHint
    {
        public readonly bool HasAnyReview;
        public readonly bool ImportantRoom;
        public readonly bool MainframeCandidate;
        public readonly bool CompanyStashCandidate;
        public readonly float ObjectiveScore;
        public readonly float CameraScoreBonus;
        public readonly int PositiveCount;
        public readonly int HardRejectCount;
        public readonly int SkyboxRejectCount;
        public readonly int FurnitureRejectCount;
        public readonly int MainframeRejectCount;
        public readonly int CompanyStashRejectCount;

        public CameraPlacementTileHint(
            bool hasAnyReview,
            bool importantRoom,
            bool mainframeCandidate,
            bool companyStashCandidate,
            float objectiveScore,
            float cameraScoreBonus,
            int positiveCount,
            int hardRejectCount,
            int skyboxRejectCount,
            int furnitureRejectCount,
            int mainframeRejectCount,
            int companyStashRejectCount)
        {
            HasAnyReview = hasAnyReview;
            ImportantRoom = importantRoom;
            MainframeCandidate = mainframeCandidate;
            CompanyStashCandidate = companyStashCandidate;
            ObjectiveScore = objectiveScore;
            CameraScoreBonus = cameraScoreBonus;
            PositiveCount = positiveCount;
            HardRejectCount = hardRejectCount;
            SkyboxRejectCount = skyboxRejectCount;
            FurnitureRejectCount = furnitureRejectCount;
            MainframeRejectCount = mainframeRejectCount;
            CompanyStashRejectCount = companyStashRejectCount;
        }
    }

    internal readonly struct ReviewedCameraPose
    {
        public readonly Vector3 WorldPosition;
        public readonly Quaternion WorldRotation;
        public readonly CameraPlacementReviewRecord Record;
        public readonly CameraReviewMetrics Metrics;

        public ReviewedCameraPose(
            Vector3 worldPosition,
            Quaternion worldRotation,
            CameraPlacementReviewRecord record,
            CameraReviewMetrics metrics)
        {
            WorldPosition = worldPosition;
            WorldRotation = worldRotation;
            Record = record;
            Metrics = metrics;
        }
    }

    [Serializable]
    internal sealed class CameraPlacementReviewProfile
    {
        public int schemaVersion = 1;
        public List<CameraPlacementReviewRecord> reviews = new List<CameraPlacementReviewRecord>();
    }

    [Serializable]
    internal sealed class CameraPlacementReviewRecord
    {
        public string rating;
        public List<string> tags = new List<string>();
        public bool objectivePlacementCandidate;
        public bool mainframePlacementCandidate;
        public bool companyStashPlacementCandidate;
        public float tileObjectiveScore;
        public float doorwayVisibilityScore;
        public float roomFramingScore;
        public string aimProfile;
        public string reason;
        public string timestampUtc;
        public string flowName;
        public string dungeonName;
        public string dungeonFingerprint;
        public string tileName;
        public Vector3 tileBoundsSize;
        public int tileDoorwayCount;
        public int cameraIndex;
        public string displayLabel;
        public string mountMode;
        public string placementSource;
        public string surfaceKind;
        public int placementCornerId;
        public Vector3 tileLocalPosition;
        public Vector3 tileLocalEuler;
        public Vector3 tileLocalSurfaceNormal;
        public float heightAboveFloor;
        public float centerSightDistance;
        public int frustumHits;
        public int frustumVoids;
        public int frustumNearWall;
        public string forwardHitName;
        public int forwardHitLayer;
        public float forwardHitDistance;
        public float placementScore;
    }

    internal readonly struct CameraReviewMetrics
    {
        public readonly float HeightAboveFloor;
        public readonly float CenterSightDistance;
        public readonly int FrustumHits;
        public readonly int FrustumVoids;
        public readonly int FrustumNearWall;
        public readonly string ForwardHitName;
        public readonly int ForwardHitLayer;
        public readonly float ForwardHitDistance;

        public CameraReviewMetrics(
            float heightAboveFloor,
            float centerSightDistance,
            int frustumHits,
            int frustumVoids,
            int frustumNearWall,
            string forwardHitName,
            int forwardHitLayer,
            float forwardHitDistance)
        {
            HeightAboveFloor = heightAboveFloor;
            CenterSightDistance = centerSightDistance;
            FrustumHits = frustumHits;
            FrustumVoids = frustumVoids;
            FrustumNearWall = frustumNearWall;
            ForwardHitName = forwardHitName ?? "none";
            ForwardHitLayer = forwardHitLayer;
            ForwardHitDistance = forwardHitDistance;
        }
    }

    internal static class CameraPlacementReviewStore
    {
        private const string ProfileFileName = "camera-placement-reviews.json";
        private const string LocalFolderName = Core.Y4NGZCompanyPaths.LocalDataDirName;
        private const string LocalFileName = "camera-placement-reviews.local.json";
        private const float VoidProbeDistanceM = 40f;
        private const float NearWallDistanceM = 2f;

        /// <summary>
        /// Reach for the fallback mount probe used when a review record saved no surface normal.
        /// Comfortably clears the offset between a mount point and its surface, while still
        /// rejecting a pose sitting in open air in the middle of a room.
        /// </summary>
        private const float UnverifiedMountProbeRadius = 0.6f;

        private static readonly Vector2[] FrustumOffsets =
        {
            new Vector2(0f, 0f),
            new Vector2(25f, 0f), new Vector2(-25f, 0f),
            new Vector2(0f, 18f), new Vector2(0f, -18f),
            new Vector2(25f, 18f), new Vector2(25f, -18f),
            new Vector2(-25f, 18f), new Vector2(-25f, -18f),
        };

        private static readonly List<CameraPlacementReviewRecord> PackReviews = new List<CameraPlacementReviewRecord>();
        private static CameraPlacementReviewProfile _localProfile = new CameraPlacementReviewProfile();
        private static bool _loaded;

        internal static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            PackReviews.Clear();
            string packPath = PackProfilePath;
            if (File.Exists(packPath))
            {
                LoadProfile(packPath, PackReviews);
            }

            string localPath = LocalProfilePath;
            if (File.Exists(localPath))
            {
                var localReviews = new List<CameraPlacementReviewRecord>();
                LoadProfile(localPath, localReviews);
                _localProfile.reviews = localReviews;
            }

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Camera review profiles loaded: pack={PackReviews.Count}, local={_localProfile.reviews.Count}. localPath='{localPath}'");
            PublishProfileSizes();
            WarnAboutUnverifiablePerfectReviews();
        }

        /// <summary>
        /// A <c>Perfect</c> review with no saved <c>tileLocalSurfaceNormal</c> cannot have its
        /// mount surface verified precisely, so it falls back to the proximity probe in
        /// <see cref="ReviewedPoseStillValid"/>. Worth reporting once at load: these records are
        /// the ones that can place a camera badly, and re-reviewing them fixes the data at source.
        /// </summary>
        private static void WarnAboutUnverifiablePerfectReviews()
        {
            int perfect = 0;
            int missingNormal = 0;
            foreach (CameraPlacementReviewRecord record in EnumerateReviews())
            {
                if (!IsRating(record, CameraPlacementReviewRating.Perfect)) continue;
                perfect++;
                if (record.tileLocalSurfaceNormal.sqrMagnitude <= 0.1f)
                    missingNormal++;
            }

            if (missingNormal <= 0) return;

            SurveillanceBootstrap.Log?.LogWarning(
                $"[Y4NGZ.PlacementReview] REVIEW_NO_SURFACE_NORMAL {missingNormal}/{perfect} Perfect reviews saved no tileLocalSurfaceNormal; their mount surface cannot be verified precisely and they fall back to a {UnverifiedMountProbeRadius:F2}m proximity probe. Re-review those cameras to record a normal.");
        }

        /// <summary>
        /// Keeps the per-round review diagnostic (Phase 3e) able to close its books against the
        /// real profile sizes. Called on load and after every local save.
        /// </summary>
        private static void PublishProfileSizes()
        {
            PlacementReviewRoundReport.NoteProfileSizes(
                _localProfile?.reviews?.Count ?? 0,
                PackReviews.Count);
        }

        internal static bool RecordReview(CCTVCamera camera, CameraPlacementReviewRating rating, out string message)
        {
            return RecordReview(camera, rating, null, out message);
        }

        internal static bool RecordReview(
            CCTVCamera camera,
            CameraPlacementReviewRating rating,
            IReadOnlyList<string> extraTags,
            out string message)
        {
            Load();
            message = string.Empty;
            if (camera == null || camera.OwningTile == null)
            {
                message = "NO CAMERA/TILE";
                return false;
            }

            Tile tile = camera.OwningTile;
            if (tile.Placement == null)
            {
                message = "NO TILE PLACEMENT";
                return false;
            }

            CameraReviewMetrics metrics = ComputeMetrics(camera.transform.position, camera.transform.rotation, tile, PlacementMask.Resolve(false));
            Quaternion localRot = Quaternion.Inverse(tile.Placement.Rotation) * camera.transform.rotation;
            List<string> tags = BuildReviewTags(rating, extraTags);
            bool important = tags.Contains(CameraPlacementReviewTags.ImportantRoom);
            bool mainframeRoom = important || tags.Contains(CameraPlacementReviewTags.MainframeRoom);
            bool companyStashRoom = important || tags.Contains(CameraPlacementReviewTags.CompanyStashRoom);
            bool badMainframeRoom = tags.Contains(CameraPlacementReviewTags.BadMainframeRoom);
            bool badCompanyStashRoom = tags.Contains(CameraPlacementReviewTags.BadCompanyStashRoom);
            float doorwayVisibilityScore = camera.PlacementDoorwayVisibilityScore;
            if (doorwayVisibilityScore == 0f && tags.Contains(CameraPlacementReviewTags.DoorwayVisible))
                doorwayVisibilityScore = 1f;
            float roomFramingScore = camera.PlacementRoomFramingScore;
            if (roomFramingScore == 0f && tags.Contains(CameraPlacementReviewTags.FramesRoomWell))
                roomFramingScore = 1f;
            float tileObjectiveScore = 0f;
            if (important) tileObjectiveScore += 1000f;
            if (tags.Contains(CameraPlacementReviewTags.MainframeRoom)) tileObjectiveScore += 700f;
            if (tags.Contains(CameraPlacementReviewTags.CompanyStashRoom)) tileObjectiveScore += 700f;
            if (badMainframeRoom || badCompanyStashRoom) tileObjectiveScore -= 350f;

            var record = new CameraPlacementReviewRecord
            {
                rating = rating.ToString(),
                reason = ReasonForRating(rating),
                tags = tags,
                objectivePlacementCandidate = (mainframeRoom && !badMainframeRoom) || (companyStashRoom && !badCompanyStashRoom),
                mainframePlacementCandidate = mainframeRoom && !badMainframeRoom,
                companyStashPlacementCandidate = companyStashRoom && !badCompanyStashRoom,
                tileObjectiveScore = tileObjectiveScore,
                doorwayVisibilityScore = doorwayVisibilityScore,
                roomFramingScore = roomFramingScore,
                aimProfile = camera.PlacementAimProfile ?? "unknown",
                timestampUtc = DateTime.UtcNow.ToString("o"),
                flowName = ResolveFlowName(),
                dungeonName = ResolveDungeonName(),
                dungeonFingerprint = BuildDungeonFingerprint(),
                tileName = tile.name ?? "<unnamed>",
                tileBoundsSize = tile.Bounds.size,
                tileDoorwayCount = tile.UsedDoorways != null ? tile.UsedDoorways.Count : 0,
                cameraIndex = camera.CameraIndex,
                displayLabel = camera.ResolvedLabel,
                mountMode = camera.MountMode.ToString(),
                placementSource = camera.PlacementSource ?? "unknown",
                surfaceKind = camera.PlacementSurfaceKind ?? "unknown",
                placementCornerId = camera.PlacementCornerId,
                tileLocalPosition = WorldToTileLocal(tile, camera.transform.position),
                tileLocalEuler = localRot.eulerAngles,
                tileLocalSurfaceNormal = WorldDirectionToTileLocal(tile, camera.PlacementSurfaceNormal),
                heightAboveFloor = metrics.HeightAboveFloor,
                centerSightDistance = metrics.CenterSightDistance,
                frustumHits = metrics.FrustumHits,
                frustumVoids = metrics.FrustumVoids,
                frustumNearWall = metrics.FrustumNearWall,
                forwardHitName = metrics.ForwardHitName,
                forwardHitLayer = metrics.ForwardHitLayer,
                forwardHitDistance = metrics.ForwardHitDistance,
                placementScore = camera.PlacementScore,
            };

            _localProfile.reviews.Add(record);
            SaveLocalProfile();
            message = $"{rating} {record.displayLabel} {record.tileName} tags={string.Join(",", tags.ToArray())}";
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] CAMERA_REVIEW rating={record.rating} tags={string.Join(",", tags.ToArray())} flow='{record.flowName}' tile='{record.tileName}' label='{record.displayLabel}' localPos=({record.tileLocalPosition.x:F2},{record.tileLocalPosition.y:F2},{record.tileLocalPosition.z:F2}) localEuler=({record.tileLocalEuler.x:F1},{record.tileLocalEuler.y:F1},{record.tileLocalEuler.z:F1}) height={record.heightAboveFloor:F2} centerDist={record.centerSightDistance:F2} frustum={record.frustumHits}/{record.frustumVoids}/{record.frustumNearWall} source={record.placementSource}");
            return true;
        }

        internal static string TagForRating(CameraPlacementReviewRating rating)
        {
            switch (rating)
            {
                case CameraPlacementReviewRating.Perfect: return CameraPlacementReviewTags.Perfect;
                case CameraPlacementReviewRating.TooLow: return CameraPlacementReviewTags.TooLow;
                case CameraPlacementReviewRating.Floating: return CameraPlacementReviewTags.Floating;
                case CameraPlacementReviewRating.TooHigh: return CameraPlacementReviewTags.TooHigh;
                case CameraPlacementReviewRating.BlockedView: return CameraPlacementReviewTags.BlockedView;
                case CameraPlacementReviewRating.LookingAtWall: return CameraPlacementReviewTags.LookingAtWall;
                case CameraPlacementReviewRating.PoorCoverage: return CameraPlacementReviewTags.PoorCoverage;
                case CameraPlacementReviewRating.SkyboxVisible: return CameraPlacementReviewTags.SkyboxVisible;
                case CameraPlacementReviewRating.Duplicate: return CameraPlacementReviewTags.Duplicate;
                case CameraPlacementReviewRating.ImportantRoom: return CameraPlacementReviewTags.ImportantRoom;
                default: return rating.ToString().ToLowerInvariant();
            }
        }

        internal static CameraPlacementTileHint GetTileHint(Tile tile)
        {
            Load();
            bool hasAnyReview = false;
            bool importantRoom = false;
            bool mainframeCandidate = false;
            bool companyStashCandidate = false;
            float objectiveScore = 0f;
            float cameraScoreBonus = 0f;
            int positiveCount = 0;
            int hardRejectCount = 0;
            int skyboxRejectCount = 0;
            int furnitureRejectCount = 0;
            int mainframeRejectCount = 0;
            int companyStashRejectCount = 0;

            foreach (CameraPlacementReviewRecord record in EnumerateReviews())
            {
                if (!MatchesTile(record, tile)) continue;
                hasAnyReview = true;

                bool important = HasTag(record, CameraPlacementReviewTags.ImportantRoom);
                bool goodMainframe = important ||
                                     HasTag(record, CameraPlacementReviewTags.MainframeRoom) ||
                                     record.mainframePlacementCandidate;
                bool goodStash = important ||
                                 HasTag(record, CameraPlacementReviewTags.CompanyStashRoom) ||
                                 record.companyStashPlacementCandidate;
                bool badMainframe = HasTag(record, CameraPlacementReviewTags.BadMainframeRoom);
                bool badStash = HasTag(record, CameraPlacementReviewTags.BadCompanyStashRoom);

                if (important)
                {
                    importantRoom = true;
                    objectiveScore += 1000f + Math.Max(0f, record.tileObjectiveScore);
                    cameraScoreBonus += 260f;
                    positiveCount++;
                }

                if (goodMainframe && !badMainframe)
                {
                    mainframeCandidate = true;
                    if (!important)
                    {
                        objectiveScore += 700f + Math.Max(0f, record.tileObjectiveScore);
                        positiveCount++;
                    }
                }

                if (goodStash && !badStash)
                {
                    companyStashCandidate = true;
                    if (!important)
                    {
                        objectiveScore += 700f + Math.Max(0f, record.tileObjectiveScore);
                        positiveCount++;
                    }
                }

                if (badMainframe)
                {
                    mainframeRejectCount++;
                    objectiveScore -= 350f;
                }

                if (badStash)
                {
                    companyStashRejectCount++;
                    objectiveScore -= 350f;
                }

                if (HasTag(record, CameraPlacementReviewTags.Perfect) ||
                    HasTag(record, CameraPlacementReviewTags.DoorwayVisible) ||
                    HasTag(record, CameraPlacementReviewTags.FramesRoomWell) ||
                    HasTag(record, CameraPlacementReviewTags.GoodCorner))
                {
                    cameraScoreBonus += 160f;
                    positiveCount++;
                }

                if (HasTag(record, CameraPlacementReviewTags.SkyboxVisible))
                {
                    hardRejectCount++;
                    skyboxRejectCount++;
                }

                if (HasTag(record, CameraPlacementReviewTags.StuckInFurniture))
                {
                    hardRejectCount++;
                    furnitureRejectCount++;
                }
            }

            return new CameraPlacementTileHint(
                hasAnyReview,
                importantRoom,
                mainframeCandidate,
                companyStashCandidate,
                objectiveScore,
                cameraScoreBonus,
                positiveCount,
                hardRejectCount,
                skyboxRejectCount,
                furnitureRejectCount,
                mainframeRejectCount,
                companyStashRejectCount);
        }

        internal static bool TryResolveReviewedPose(Tile tile, PlacementMasks masks, out ReviewedCameraPose pose, out string rejectReason)
        {
            Load();
            pose = default;
            rejectReason = null;
            if (tile == null || tile.Placement == null) return false;

            CameraPlacementReviewRecord best = null;
            float bestScore = float.NegativeInfinity;
            float bestBoundsPenalty = 0f;
            foreach (CameraPlacementReviewRecord record in EnumerateReviews())
            {
                if (!IsRating(record, CameraPlacementReviewRating.Perfect)) continue;
                if (!MatchesTile(record, tile, out float boundsPenalty)) continue;

                Vector3 worldPos = TileLocalToWorld(tile, record.tileLocalPosition);
                Quaternion worldRot = tile.Placement.Rotation * Quaternion.Euler(record.tileLocalEuler);
                CameraReviewMetrics metrics = ComputeMetrics(worldPos, worldRot, tile, masks);
                if (!ReviewedPoseStillValid(tile, worldPos, worldRot, record, metrics, masks, out string reason, out string category))
                {
                    rejectReason = reason;
                    PlacementReviewRoundReport.NoteReviewedPoseReject(category);
                    continue;
                }

                // Phase 3e: bounds mismatch is a penalty, not a gate (§4 point 5) — a
                // bounds-compatible record on the same tile always outranks a mismatched one.
                float score = 10000f + metrics.CenterSightDistance * 8f + metrics.FrustumHits * 40f
                              - metrics.FrustumVoids * 80f - boundsPenalty;
                if (score <= bestScore) continue;
                bestScore = score;
                best = record;
                bestBoundsPenalty = boundsPenalty;
                pose = new ReviewedCameraPose(worldPos, worldRot, record, metrics);
            }

            // Mirrors the canonical store's RESOLVE_BOUNDS_MISMATCH: warn only when the
            // bounds-mismatched candidate is the one actually selected.
            if (best != null && bestBoundsPenalty > 0f)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[Y4NGZ.PlacementReview] REVIEW_BOUNDS_MISMATCH savedTile='{best.tileName}' matchedTile='{tile.name}' savedSize={best.tileBoundsSize} currentSize={tile.Bounds.size} — reviewed pose accepted on a bounds-mismatched tile (pre-3e this record was dropped).");
            }

            return best != null;
        }

        internal static bool ShouldRejectCandidate(Tile tile, SurfaceMount.Candidate candidate, out string reason)
        {
            if (!ShouldRejectCandidateCore(tile, candidate, out reason))
                return false;

            PlacementReviewRoundReport.NoteEngineBVeto(reason);
            return true;
        }

        private static bool ShouldRejectCandidateCore(Tile tile, SurfaceMount.Candidate candidate, out string reason)
        {
            Load();
            reason = null;
            foreach (CameraPlacementReviewRecord record in EnumerateReviews())
            {
                if (!MatchesTile(record, tile)) continue;

                Vector3 local = WorldToTileLocal(tile, candidate.WorldPos);
                float localDistance = Vector3.Distance(local, record.tileLocalPosition);

                if (HasTag(record, CameraPlacementReviewTags.SkyboxVisible))
                {
                    if (candidate.FrustumVoids >= Mathf.Max(2, record.frustumVoids) ||
                        candidate.CenterUsefulDistM >= 32f)
                    {
                        reason = "review-skybox-pattern";
                        return true;
                    }

                    if (tile?.Placement != null)
                    {
                        Quaternion reviewedWorldRot = tile.Placement.Rotation * Quaternion.Euler(record.tileLocalEuler);
                        if (localDistance < 1.75f &&
                            Quaternion.Angle(candidate.WorldRot, reviewedWorldRot) < 45f)
                        {
                            reason = "review-skybox-local-pose";
                            return true;
                        }
                    }
                }

                if (HasTag(record, CameraPlacementReviewTags.StuckInFurniture))
                {
                    if (localDistance < 2.0f)
                    {
                        reason = "review-furniture-local-pose";
                        return true;
                    }

                    if (!string.IsNullOrEmpty(record.forwardHitName) &&
                        string.Equals(record.forwardHitName, candidate.HitName, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "review-furniture-hit-name";
                        return true;
                    }
                }

                if (HasTag(record, CameraPlacementReviewTags.DoorwayNotVisible) &&
                    candidate.DoorwayVisibilityScore <= 0f)
                {
                    reason = "review-doorway-not-visible";
                    return true;
                }
            }
            return false;
        }

        internal static float AdjustCandidateScore(Tile tile, SurfaceMount.Candidate candidate, bool usingExpandedFallback)
        {
            Load();
            float adjustment = 0f;
            foreach (CameraPlacementReviewRecord record in EnumerateReviews())
            {
                if (!MatchesTile(record, tile)) continue;
                if (IsRating(record, CameraPlacementReviewRating.Perfect))
                {
                    Vector3 local = WorldToTileLocal(tile, candidate.WorldPos);
                    float localDistance = Vector3.Distance(local, record.tileLocalPosition);
                    if (localDistance < 2.0f) adjustment += 180f - localDistance * 60f;
                }

                if (IsRating(record, CameraPlacementReviewRating.TooLow) && candidate.MountHeightAboveFloorM <= Mathf.Max(2.2f, record.heightAboveFloor + 0.65f))
                    adjustment -= 900f;
                if (IsRating(record, CameraPlacementReviewRating.TooHigh) && candidate.MountHeightAboveFloorM >= Mathf.Max(3.0f, record.heightAboveFloor - 0.5f))
                    adjustment -= 520f;
                if (HasTag(record, CameraPlacementReviewTags.BlockedView) &&
                    (candidate.CenterUsefulDistM <= Mathf.Max(3.0f, record.centerSightDistance + 2.0f) ||
                     candidate.FrustumNearWall >= 3))
                    adjustment -= 750f;
                if (HasTag(record, CameraPlacementReviewTags.LookingAtWall) &&
                    (candidate.CenterUsefulDistM <= Mathf.Max(3.0f, record.centerSightDistance + 1.5f) ||
                     candidate.FrustumNearWall >= 2))
                    adjustment -= 950f;
                if (HasTag(record, CameraPlacementReviewTags.PoorCoverage) &&
                    (candidate.FrustumHits <= 4 || candidate.RoomFramingScore <= 0.2f))
                    adjustment -= 620f;
                if (HasTag(record, CameraPlacementReviewTags.DoorwayNotVisible) &&
                    candidate.DoorwayVisibilityScore <= 0f)
                    adjustment -= 850f;
                if (HasTag(record, CameraPlacementReviewTags.DoesNotFrameRoom) &&
                    candidate.RoomFramingScore <= 0.25f)
                    adjustment -= 720f;
                if (HasTag(record, CameraPlacementReviewTags.PipeObstructed))
                    adjustment -= candidate.CenterUsefulDistM < 4.0f ? 900f : 180f;
                if (IsRating(record, CameraPlacementReviewRating.SkyboxVisible))
                {
                    int voidThreshold = Math.Max(1, Math.Min(4, record.frustumVoids > 0 ? record.frustumVoids - 1 : 2));
                    if (candidate.FrustumVoids >= voidThreshold)
                        adjustment -= 1200f + candidate.FrustumVoids * 140f;
                    if (candidate.FrustumVoids >= 3 || candidate.CenterUsefulDistM >= 32f)
                        adjustment -= 500f;
                    if (usingExpandedFallback)
                        adjustment -= 900f;

                    if (tile.Placement != null)
                    {
                        Vector3 local = WorldToTileLocal(tile, candidate.WorldPos);
                        float localDistance = Vector3.Distance(local, record.tileLocalPosition);
                        Quaternion reviewedWorldRot = tile.Placement.Rotation * Quaternion.Euler(record.tileLocalEuler);
                        float angle = Quaternion.Angle(candidate.WorldRot, reviewedWorldRot);
                        if (localDistance < 1.75f && angle < 45f)
                            adjustment -= 2200f - localDistance * 500f - angle * 12f;
                    }
                }
                if (IsRating(record, CameraPlacementReviewRating.Duplicate))
                    adjustment -= 140f;
                if (IsRating(record, CameraPlacementReviewRating.Floating) && usingExpandedFallback)
                    adjustment -= 1000f;
                if (IsRating(record, CameraPlacementReviewRating.ImportantRoom))
                    adjustment += 260f;
                if (HasTag(record, CameraPlacementReviewTags.DoorwayVisible) &&
                    candidate.DoorwayVisibilityScore > 0f)
                    adjustment += 420f;
                if (HasTag(record, CameraPlacementReviewTags.FramesRoomWell) &&
                    candidate.RoomFramingScore > 0.35f)
                    adjustment += 360f;
                if (HasTag(record, CameraPlacementReviewTags.GoodCorner) &&
                    candidate.Kind == SurfaceMount.SurfaceKind.Corner)
                    adjustment += 300f;
                if (HasTag(record, CameraPlacementReviewTags.GoodHeight) &&
                    candidate.MountHeightAboveFloorM >= 2.4f)
                    adjustment += 180f;
            }
            return candidate.Score + adjustment;
        }

        internal static bool ShouldAvoidLegacyFallback(Tile tile)
        {
            Load();
            foreach (CameraPlacementReviewRecord record in EnumerateReviews())
            {
                if (!MatchesTile(record, tile)) continue;
                if (IsRating(record, CameraPlacementReviewRating.Floating) ||
                    IsRating(record, CameraPlacementReviewRating.TooLow) ||
                    IsRating(record, CameraPlacementReviewRating.LookingAtWall) ||
                    IsRating(record, CameraPlacementReviewRating.SkyboxVisible))
                    return true;
            }
            return false;
        }

        internal static void ApplyCandidateDiagnostics(CCTVCamera camera, SurfaceMount.Candidate candidate, string source)
        {
            if (camera == null) return;
            camera.PlacementSource = source;
            camera.PlacementSurfaceKind = candidate.Kind.ToString();
            camera.PlacementScore = candidate.Score;
            camera.PlacementHeightAboveFloorM = candidate.MountHeightAboveFloorM;
            camera.PlacementCenterSightDistanceM = candidate.CenterUsefulDistM;
            camera.PlacementFrustumHits = candidate.FrustumHits;
            camera.PlacementFrustumVoids = candidate.FrustumVoids;
            camera.PlacementFrustumNearWall = candidate.FrustumNearWall;
            camera.PlacementDoorwayVisibilityScore = candidate.DoorwayVisibilityScore;
            camera.PlacementRoomFramingScore = candidate.RoomFramingScore;
            camera.PlacementAimProfile = candidate.AimProfile;
            camera.PlacementSurfaceNormal = candidate.SupportNormal;
            camera.PlacementHitName = candidate.HitName;
            camera.PlacementHitLayer = candidate.HitLayer;
            camera.FromReviewedPlacementProfile = false;
        }

        internal static void ApplyReviewedDiagnostics(CCTVCamera camera, ReviewedCameraPose reviewed)
        {
            if (camera == null) return;
            camera.PlacementSource = "reviewed-perfect";
            camera.PlacementSurfaceKind = reviewed.Record.surfaceKind ?? "reviewed";
            camera.PlacementScore = 10000f;
            camera.PlacementHeightAboveFloorM = reviewed.Metrics.HeightAboveFloor;
            camera.PlacementCenterSightDistanceM = reviewed.Metrics.CenterSightDistance;
            camera.PlacementFrustumHits = reviewed.Metrics.FrustumHits;
            camera.PlacementFrustumVoids = reviewed.Metrics.FrustumVoids;
            camera.PlacementFrustumNearWall = reviewed.Metrics.FrustumNearWall;
            camera.PlacementDoorwayVisibilityScore = DoorwayVisibilityScoreForReview(reviewed.Record);
            camera.PlacementRoomFramingScore = RoomFramingScoreForReview(reviewed.Record);
            camera.PlacementAimProfile = string.IsNullOrEmpty(reviewed.Record.aimProfile)
                ? "reviewed"
                : reviewed.Record.aimProfile;
            camera.PlacementSurfaceNormal = TileLocalDirectionToWorld(camera.OwningTile, reviewed.Record.tileLocalSurfaceNormal);
            camera.PlacementHitName = reviewed.Metrics.ForwardHitName;
            camera.PlacementHitLayer = reviewed.Metrics.ForwardHitLayer;
            camera.FromReviewedPlacementProfile = true;
        }

        internal static CameraReviewMetrics ComputeMetrics(Vector3 worldPosition, Quaternion worldRotation, Tile tile, PlacementMasks masks)
        {
            Vector3 forward = worldRotation * Vector3.forward;
            float height = EstimateHeightAboveFloor(worldPosition, tile);
            float centerDist = VoidProbeDistanceM;
            string hitName = "none";
            int hitLayer = -1;
            float hitDistance = VoidProbeDistanceM;
            if (Physics.Raycast(worldPosition, forward, out RaycastHit centerHit, VoidProbeDistanceM, masks.MountMask, QueryTriggerInteraction.Ignore))
            {
                centerDist = centerHit.distance;
                hitDistance = centerHit.distance;
                hitName = centerHit.collider != null ? centerHit.collider.name : "none";
                hitLayer = centerHit.collider != null ? centerHit.collider.gameObject.layer : -1;
            }

            int hits = 0;
            int voids = 0;
            int nearWall = 0;
            for (int i = 0; i < FrustumOffsets.Length; i++)
            {
                Vector2 off = FrustumOffsets[i];
                Vector3 dir = (worldRotation * Quaternion.Euler(off.y, off.x, 0f)) * Vector3.forward;
                if (Physics.Raycast(worldPosition, dir, out RaycastHit hit, VoidProbeDistanceM, masks.MountMask, QueryTriggerInteraction.Ignore))
                {
                    hits++;
                    if (hit.distance < NearWallDistanceM) nearWall++;
                }
                else
                {
                    voids++;
                }
            }

            return new CameraReviewMetrics(height, centerDist, hits, voids, nearWall, hitName, hitLayer, hitDistance);
        }

        private static bool ReviewedPoseStillValid(Tile tile, Vector3 worldPos, Quaternion worldRot, CameraPlacementReviewRecord record, CameraReviewMetrics metrics, PlacementMasks masks, out string reason, out string category)
        {
            reason = null;
            category = null;
            if (metrics.HeightAboveFloor < 1.65f)
            {
                reason = $"too-low height={metrics.HeightAboveFloor:F2}";
                category = ReviewedPoseRejectCategories.TooLow;
                return false;
            }

            if (metrics.CenterSightDistance < 2.25f)
            {
                reason = $"blocked centerDist={metrics.CenterSightDistance:F2}";
                category = ReviewedPoseRejectCategories.Blocked;
                return false;
            }

            if (metrics.FrustumVoids > 4 || metrics.FrustumHits < 4)
            {
                reason = $"poor-frustum hits={metrics.FrustumHits} voids={metrics.FrustumVoids}";
                category = ReviewedPoseRejectCategories.PoorFrustum;
                return false;
            }

            if (Physics.CheckSphere(worldPos, 0.16f, masks.SolidMask, QueryTriggerInteraction.Ignore))
            {
                reason = "embedded";
                category = ReviewedPoseRejectCategories.Embedded;
                return false;
            }

            Vector3 normal = TileLocalDirectionToWorld(tile, record.tileLocalSurfaceNormal);
            if (normal.sqrMagnitude > 0.1f)
            {
                Vector3 n = normal.normalized;
                if (!Physics.Raycast(worldPos + n * 0.05f, -n, 0.75f, masks.MountMask, QueryTriggerInteraction.Ignore))
                {
                    reason = "surface-contact-lost";
                    category = ReviewedPoseRejectCategories.SurfaceContactLost;
                    return false;
                }

                return true;
            }

            // No saved surface normal (13 of 29 local Perfect records are like this, including a
            // CloverTile one). This check used to be skipped entirely when the normal was missing,
            // so nothing verified the camera was attached to anything - a review applies to every
            // tile instance sharing its name, so on a CloverTile-dense flow one such record placed
            // cameras floating in mid-air. Fall back to a direction-agnostic proximity probe: any
            // genuinely mounted pose has mount geometry within arm's reach, a mid-room pose does
            // not. Rejecting here just falls through to procedural placement, which mounts properly.
            if (!Physics.CheckSphere(worldPos, UnverifiedMountProbeRadius, masks.MountMask, QueryTriggerInteraction.Ignore))
            {
                reason = $"surface-contact-unverifiable no-normal probe={UnverifiedMountProbeRadius:F2}m";
                category = ReviewedPoseRejectCategories.SurfaceContactUnverifiable;
                return false;
            }

            return true;
        }

        private static IEnumerable<CameraPlacementReviewRecord> EnumerateReviews()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < PackReviews.Count; i++)
            {
                CameraPlacementReviewRecord record = PackReviews[i];
                if (record == null) continue;
                if (seen.Add(ReviewIdentity(record)))
                    yield return record;
            }
            if (_localProfile?.reviews == null) yield break;
            for (int i = 0; i < _localProfile.reviews.Count; i++)
            {
                CameraPlacementReviewRecord record = _localProfile.reviews[i];
                if (record == null) continue;
                if (seen.Add(ReviewIdentity(record)))
                    yield return record;
            }
        }

        private static string ReviewIdentity(CameraPlacementReviewRecord record)
        {
            Vector3 pos = record.tileLocalPosition;
            Vector3 euler = record.tileLocalEuler;
            return string.Join("|",
                record.timestampUtc ?? string.Empty,
                record.flowName ?? string.Empty,
                record.tileName ?? string.Empty,
                record.rating ?? string.Empty,
                pos.x.ToString("F3"), pos.y.ToString("F3"), pos.z.ToString("F3"),
                euler.x.ToString("F2"), euler.y.ToString("F2"), euler.z.ToString("F2"));
        }

        private static bool MatchesTile(CameraPlacementReviewRecord record, Tile tile)
        {
            return MatchesTile(record, tile, out _);
        }

        /// <summary>
        /// Phase 3e: matches a review to a tile using the canonical rules from
        /// INTERIOR_PLACEMENT_SYSTEM.md §4 instead of the old ordinal/hard-bounds pair.
        ///
        /// - Name comparison goes through <see cref="AuthoredInteriorPlacementStore.NormalizeTileNameForMatch"/>
        ///   (repeated <c>(Clone)</c> stripping, <c>(1)</c> / <c>_02</c> instance suffixes,
        ///   case-insensitive) — the pre-3e <see cref="StringComparison.Ordinal"/> equality let
        ///   records silently go dormant the moment a runtime name gained a suffix.
        /// - Bounds are a scoring penalty, not an eligibility gate (§4 point 5). A mismatch
        ///   reports <paramref name="boundsPenalty"/> so a compatible same-name tile outranks it,
        ///   but the record is no longer dropped outright.
        ///
        /// Reviews still deliberately apply to <em>every</em> tile instance sharing a name, unlike
        /// authored records which resolve to a single instance (§8).
        /// </summary>
        private static bool MatchesTile(CameraPlacementReviewRecord record, Tile tile, out float boundsPenalty)
        {
            boundsPenalty = 0f;
            if (record == null || tile == null) return false;

            // Before the flow gate, so the count answers "were tiles queried at all" rather
            // than "did anything match" — those diverge exactly when every review belongs to
            // another flow, which is when the report is most likely to be misread.
            PlacementReviewRoundReport.NoteTileQueried(tile.GetInstanceID());

            string flow = ResolveFlowName();
            if (!string.IsNullOrEmpty(record.flowName) &&
                !string.Equals(record.flowName, flow, StringComparison.OrdinalIgnoreCase))
            {
                PlacementReviewRoundReport.NoteOtherFlow(record);
                return false;
            }

            PlacementReviewRoundReport.NoteFlowEligible(record);

            string savedName = record.tileName ?? string.Empty;
            string runtimeName = tile.name ?? string.Empty;
            // A record with no saved tile name is unmatchable; normalizing both sides would
            // otherwise let it match an unnamed tile, which the pre-3e ordinal gate never did.
            if (savedName.Length == 0) return false;

            if (!string.Equals(
                    AuthoredInteriorPlacementStore.NormalizeTileNameForMatch(savedName),
                    AuthoredInteriorPlacementStore.NormalizeTileNameForMatch(runtimeName),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool boundsCompatible = AuthoredInteriorPlacementStore.TileBoundsCompatible(
                record.tileBoundsSize, tile.Bounds, out _, out _, out _);
            if (!boundsCompatible)
                boundsPenalty = AuthoredInteriorPlacementStore.BoundsMismatchScorePenalty;

            PlacementReviewRoundReport.NoteResolved(
                record,
                nameRescued: !string.Equals(savedName, runtimeName, StringComparison.Ordinal),
                boundsPenalized: !boundsCompatible);
            return true;
        }

        private static bool IsRating(CameraPlacementReviewRecord record, CameraPlacementReviewRating rating)
        {
            return HasTag(record, TagForRating(rating));
        }

        private static float DoorwayVisibilityScoreForReview(CameraPlacementReviewRecord record)
        {
            if (record == null) return 0f;
            if (record.doorwayVisibilityScore != 0f) return record.doorwayVisibilityScore;
            return HasTag(record, CameraPlacementReviewTags.DoorwayVisible) ? 1f : 0f;
        }

        private static float RoomFramingScoreForReview(CameraPlacementReviewRecord record)
        {
            if (record == null) return 0f;
            if (record.roomFramingScore != 0f) return record.roomFramingScore;
            return HasTag(record, CameraPlacementReviewTags.FramesRoomWell) ? 1f : 0f;
        }

        private static List<string> BuildReviewTags(CameraPlacementReviewRating rating, IReadOnlyList<string> extraTags)
        {
            var tags = new List<string>();
            AddTag(tags, TagForRating(rating));
            if (extraTags == null) return tags;

            for (int i = 0; i < extraTags.Count; i++)
            {
                AddTag(tags, extraTags[i]);
            }

            return tags;
        }

        private static void AddTag(List<string> tags, string tag)
        {
            if (tags == null || string.IsNullOrWhiteSpace(tag)) return;
            string normalized = TagForRatingName(tag.Trim()).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized)) return;

            for (int i = 0; i < tags.Count; i++)
            {
                if (string.Equals(tags[i], normalized, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            tags.Add(normalized);
        }

        private static bool HasTag(CameraPlacementReviewRecord record, string tag)
        {
            if (record == null || string.IsNullOrWhiteSpace(tag)) return false;
            if (string.Equals(record.rating, tag, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(TagForRatingName(record.rating), tag, StringComparison.OrdinalIgnoreCase)) return true;
            if (record.tags == null) return false;
            for (int i = 0; i < record.tags.Count; i++)
            {
                if (string.Equals(record.tags[i], tag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string TagForRatingName(string rating)
        {
            if (Enum.TryParse(rating, ignoreCase: true, out CameraPlacementReviewRating parsed))
                return TagForRating(parsed);
            return rating ?? string.Empty;
        }

        private static void LoadProfile(string path, List<CameraPlacementReviewRecord> destination)
        {
            try
            {
                string json = File.ReadAllText(path);
                CameraPlacementReviewProfile profile = ReadProfile(json);
                if (profile?.reviews == null) return;
                destination.AddRange(profile.reviews);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to load camera review profile '{path}': {ex.Message}");
            }
        }

        private static void SaveLocalProfile()
        {
            try
            {
                string path = LocalProfilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, WriteProfile(_localProfile));
                PublishProfileSizes();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to save local camera review profile: {ex.Message}");
            }
        }

        /// <summary>
        /// #591 — explicit mapping in place of JsonUtility, which drops every one
        /// of these fields when FixPluginTypesSerialization is not active (the
        /// shipped review pack loaded zero records on a real Thunderstore
        /// profile). Field names and order match the previous output, so the
        /// packs already on disk keep loading.
        /// </summary>
        private static CameraPlacementReviewProfile ReadProfile(string json)
        {
            Core.Y4NGZJsonObject root = Core.Y4NGZJson.Parse(json);
            if (root == null) return null;

            CameraPlacementReviewProfile profile = new CameraPlacementReviewProfile
            {
                schemaVersion = root.GetInt("schemaVersion", 1),
            };

            Core.Y4NGZJsonArray reviews = root.GetArray("reviews");
            if (reviews == null) return profile;

            for (int i = 0; i < reviews.Count; i++)
            {
                Core.Y4NGZJsonObject entry = reviews.GetObject(i);
                if (entry == null) continue;

                profile.reviews.Add(new CameraPlacementReviewRecord
                {
                    rating = entry.GetString("rating"),
                    tags = entry.GetStringList("tags"),
                    objectivePlacementCandidate = entry.GetBool("objectivePlacementCandidate"),
                    mainframePlacementCandidate = entry.GetBool("mainframePlacementCandidate"),
                    companyStashPlacementCandidate = entry.GetBool("companyStashPlacementCandidate"),
                    tileObjectiveScore = entry.GetFloat("tileObjectiveScore"),
                    doorwayVisibilityScore = entry.GetFloat("doorwayVisibilityScore"),
                    roomFramingScore = entry.GetFloat("roomFramingScore"),
                    aimProfile = entry.GetString("aimProfile"),
                    reason = entry.GetString("reason"),
                    timestampUtc = entry.GetString("timestampUtc"),
                    flowName = entry.GetString("flowName"),
                    dungeonName = entry.GetString("dungeonName"),
                    dungeonFingerprint = entry.GetString("dungeonFingerprint"),
                    tileName = entry.GetString("tileName"),
                    tileBoundsSize = entry.GetVector3("tileBoundsSize"),
                    tileDoorwayCount = entry.GetInt("tileDoorwayCount"),
                    cameraIndex = entry.GetInt("cameraIndex"),
                    displayLabel = entry.GetString("displayLabel"),
                    mountMode = entry.GetString("mountMode"),
                    placementSource = entry.GetString("placementSource"),
                    surfaceKind = entry.GetString("surfaceKind"),
                    placementCornerId = entry.GetInt("placementCornerId"),
                    tileLocalPosition = entry.GetVector3("tileLocalPosition"),
                    tileLocalEuler = entry.GetVector3("tileLocalEuler"),
                    tileLocalSurfaceNormal = entry.GetVector3("tileLocalSurfaceNormal"),
                    heightAboveFloor = entry.GetFloat("heightAboveFloor"),
                    centerSightDistance = entry.GetFloat("centerSightDistance"),
                    frustumHits = entry.GetInt("frustumHits"),
                    frustumVoids = entry.GetInt("frustumVoids"),
                    frustumNearWall = entry.GetInt("frustumNearWall"),
                    forwardHitName = entry.GetString("forwardHitName"),
                    forwardHitLayer = entry.GetInt("forwardHitLayer"),
                    forwardHitDistance = entry.GetFloat("forwardHitDistance"),
                    placementScore = entry.GetFloat("placementScore"),
                });
            }

            return profile;
        }

        private static string WriteProfile(CameraPlacementReviewProfile profile)
        {
            Core.Y4NGZJsonObject root = new Core.Y4NGZJsonObject();
            root.SetInt("schemaVersion", profile?.schemaVersion ?? 1);
            Core.Y4NGZJsonArray reviews = root.SetArray("reviews");
            if (profile?.reviews == null) return Core.Y4NGZJson.Write(root);

            for (int i = 0; i < profile.reviews.Count; i++)
            {
                CameraPlacementReviewRecord record = profile.reviews[i];
                if (record == null) continue;

                Core.Y4NGZJsonObject entry = reviews.AddObject();
                entry.SetString("rating", record.rating);
                entry.SetStringList("tags", record.tags);
                entry.SetBool("objectivePlacementCandidate", record.objectivePlacementCandidate);
                entry.SetBool("mainframePlacementCandidate", record.mainframePlacementCandidate);
                entry.SetBool("companyStashPlacementCandidate", record.companyStashPlacementCandidate);
                entry.SetFloat("tileObjectiveScore", record.tileObjectiveScore);
                entry.SetFloat("doorwayVisibilityScore", record.doorwayVisibilityScore);
                entry.SetFloat("roomFramingScore", record.roomFramingScore);
                entry.SetString("aimProfile", record.aimProfile);
                entry.SetString("reason", record.reason);
                entry.SetString("timestampUtc", record.timestampUtc);
                entry.SetString("flowName", record.flowName);
                entry.SetString("dungeonName", record.dungeonName);
                entry.SetString("dungeonFingerprint", record.dungeonFingerprint);
                entry.SetString("tileName", record.tileName);
                entry.SetVector3("tileBoundsSize", record.tileBoundsSize);
                entry.SetInt("tileDoorwayCount", record.tileDoorwayCount);
                entry.SetInt("cameraIndex", record.cameraIndex);
                entry.SetString("displayLabel", record.displayLabel);
                entry.SetString("mountMode", record.mountMode);
                entry.SetString("placementSource", record.placementSource);
                entry.SetString("surfaceKind", record.surfaceKind);
                entry.SetInt("placementCornerId", record.placementCornerId);
                entry.SetVector3("tileLocalPosition", record.tileLocalPosition);
                entry.SetVector3("tileLocalEuler", record.tileLocalEuler);
                entry.SetVector3("tileLocalSurfaceNormal", record.tileLocalSurfaceNormal);
                entry.SetFloat("heightAboveFloor", record.heightAboveFloor);
                entry.SetFloat("centerSightDistance", record.centerSightDistance);
                entry.SetInt("frustumHits", record.frustumHits);
                entry.SetInt("frustumVoids", record.frustumVoids);
                entry.SetInt("frustumNearWall", record.frustumNearWall);
                entry.SetString("forwardHitName", record.forwardHitName);
                entry.SetInt("forwardHitLayer", record.forwardHitLayer);
                entry.SetFloat("forwardHitDistance", record.forwardHitDistance);
                entry.SetFloat("placementScore", record.placementScore);
            }

            return Core.Y4NGZJson.Write(root);
        }

        private static string ReasonForRating(CameraPlacementReviewRating rating)
        {
            switch (rating)
            {
                case CameraPlacementReviewRating.Perfect: return "replay this tile-local pose first";
                case CameraPlacementReviewRating.TooLow: return "raise minimum mount height for this tile";
                case CameraPlacementReviewRating.Floating: return "avoid unsupported fallback/source for this tile";
                case CameraPlacementReviewRating.TooHigh: return "cap mount height for this tile";
                case CameraPlacementReviewRating.BlockedView: return "penalize short center sight and blocked frustum";
                case CameraPlacementReviewRating.LookingAtWall: return "reject near-wall forward view";
                case CameraPlacementReviewRating.PoorCoverage: return "prefer wider doorway/room coverage";
                case CameraPlacementReviewRating.SkyboxVisible: return "penalize void/skybox frustum leaks";
                case CameraPlacementReviewRating.Duplicate: return "strengthen redundancy suppression";
                case CameraPlacementReviewRating.ImportantRoom: return "mark this tile as important for camera coverage and mainframe/company stash placement";
                default: return rating.ToString();
            }
        }

        private static float EstimateHeightAboveFloor(Vector3 position, Tile tile)
        {
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;
            if (Physics.Raycast(position + Vector3.up * 1.0f, Vector3.down, out RaycastHit hit, 12f, mask, QueryTriggerInteraction.Ignore))
                return position.y - hit.point.y;
            return tile != null ? position.y - tile.Bounds.min.y : 0f;
        }

        private static Vector3 WorldToTileLocal(Tile tile, Vector3 world)
        {
            if (tile?.Placement == null) return world;
            return Quaternion.Inverse(tile.Placement.Rotation) * (world - tile.Placement.Position);
        }

        private static Vector3 WorldDirectionToTileLocal(Tile tile, Vector3 worldDirection)
        {
            if (tile?.Placement == null || worldDirection.sqrMagnitude < 1e-6f) return Vector3.zero;
            return Quaternion.Inverse(tile.Placement.Rotation) * worldDirection.normalized;
        }

        private static Vector3 TileLocalToWorld(Tile tile, Vector3 local)
        {
            return tile.Placement.Position + tile.Placement.Rotation * local;
        }

        private static Vector3 TileLocalDirectionToWorld(Tile tile, Vector3 localDirection)
        {
            if (tile?.Placement == null || localDirection.sqrMagnitude < 1e-6f) return Vector3.zero;
            return (tile.Placement.Rotation * localDirection).normalized;
        }

        private static string ResolveFlowName()
        {
            try
            {
                DungeonFlow flow = RoundManager.Instance?.dungeonGenerator?.Generator?.DungeonFlow;
                if (flow != null && !string.IsNullOrEmpty(flow.name)) return flow.name;
            }
            catch
            {
            }
            return "unknown-flow";
        }

        private static string ResolveDungeonName()
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            return dungeon != null && !string.IsNullOrEmpty(dungeon.name) ? dungeon.name : "unknown-dungeon";
        }

        private static string BuildDungeonFingerprint()
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null) return "tiles0";
            int tileCount = 0;
            int doorways = 0;
            for (int i = 0; i < dungeon.AllTiles.Count; i++)
            {
                Tile tile = dungeon.AllTiles[i];
                if (tile == null) continue;
                tileCount++;
                doorways += tile.UsedDoorways != null ? tile.UsedDoorways.Count : 0;
            }
            return $"tiles{tileCount}_doors{doorways}";
        }

        private static string PackProfilePath
        {
            get
            {
                string dllDir = Path.GetDirectoryName(typeof(SurveillanceBootstrap).Assembly.Location);
                return Path.Combine(dllDir ?? Paths.PluginPath, ProfileFileName);
            }
        }

        private static string LocalProfilePath => Path.Combine(Paths.ConfigPath, LocalFolderName, LocalFileName);
    }
}
