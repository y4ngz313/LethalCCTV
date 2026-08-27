using System;
using DunGen;
using Y4NGZCompany.Facility.Cameras.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using UnityEngine;

// Compatibility facade for callers that still enter through the historical
// LethalCCTV API. All persistence and matching is owned by the canonical
// Y4NGZCompany authored-placement store.
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    public static class InteriorAuthoredPlacementStore
    {
        internal static void Load()
        {
            AuthoredInteriorPlacementStore.Load();
        }

        internal static bool RecordPlacement(
            Transform transform,
            Tile tile,
            string objectKind,
            string objectId,
            out string message)
        {
            string kind = AuthoredInteriorPlacementKinds.Normalize(objectKind);
            string metadata = AuthoredInteriorPlacementKinds.IsCctvCamera(kind)
                ? AuthoredInteriorPlacementMetadata.CctvCamera(objectId, "cctv-system")
                : string.Empty;
            return AuthoredInteriorPlacementStore.RecordPlacement(
                transform,
                kind,
                objectId,
                metadata,
                out message);
        }

        public static bool TryResolveAuthoredPose(
            object tileObject,
            string objectKind,
            out Vector3 worldPosition,
            out Quaternion worldRotation,
            out string source)
        {
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            source = null;

            Tile targetTile = tileObject as Tile;
            if (targetTile == null ||
                !AuthoredInteriorPlacementStore.TryResolvePoses(
                    objectKind,
                    out System.Collections.Generic.List<AuthoredInteriorPlacementPose> poses))
            {
                return false;
            }

            for (int i = 0; i < poses.Count; i++)
            {
                AuthoredInteriorPlacementPose pose = poses[i];
                if (!ReferenceEquals(pose.Tile, targetTile))
                    continue;

                worldPosition = pose.WorldPosition;
                worldRotation = pose.WorldRotation;
                source = pose.Source;
                return true;
            }

            return false;
        }

        internal static void ApplyCameraDiagnostics(CCTVCamera camera, Tile tile, string source)
        {
            if (camera == null || tile == null)
                return;

            CameraReviewMetrics metrics = CameraPlacementReviewStore.ComputeMetrics(
                camera.transform.position,
                camera.transform.rotation,
                tile,
                PlacementMask.Resolve(false));
            camera.PlacementSource = source ?? "authored-camera";
            camera.PlacementSurfaceKind = "authored";
            camera.PlacementScore = 12000f;
            camera.PlacementHeightAboveFloorM = metrics.HeightAboveFloor;
            camera.PlacementCenterSightDistanceM = metrics.CenterSightDistance;
            camera.PlacementFrustumHits = metrics.FrustumHits;
            camera.PlacementFrustumVoids = metrics.FrustumVoids;
            camera.PlacementFrustumNearWall = metrics.FrustumNearWall;
            camera.PlacementDoorwayVisibilityScore = 0f;
            camera.PlacementRoomFramingScore = 0f;
            camera.PlacementAimProfile = "authored";
            camera.PlacementHitName = metrics.ForwardHitName;
            camera.PlacementHitLayer = metrics.ForwardHitLayer;
            camera.FromReviewedPlacementProfile = true;
        }

        internal static bool RecordCameraDeletion(CCTVCamera camera, out string message)
        {
            Load();
            if (camera == null || camera.transform == null || camera.OwningTile == null)
            {
                message = "NO CAMERA/TILE";
                return false;
            }

            string authoredId = camera.AuthoredPlacementId;
            bool authored = !string.IsNullOrWhiteSpace(authoredId);
            bool saved = AuthoredInteriorPlacementStore.RecordCameraSuppression(
                camera.transform.position,
                camera.transform.rotation,
                authored,
                authoredId,
                out message);
            if (saved)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] AUTHORED_CAMERA_DELETE authored={authored} id='{(authored ? authoredId : "auto")}' tile='{camera.OwningTile.name}' kind={(authored ? AuthoredInteriorPlacementKinds.CameraDeleted : AuthoredInteriorPlacementKinds.CameraDisabled)}");
            }

            return saved;
        }

        internal static bool TryGetAuthoredCameraDeletion(
            AuthoredInteriorPlacementRecord cameraRecord,
            Tile cameraTile,
            out AuthoredInteriorPlacementRecord suppressionRecord,
            out string source)
        {
            return AuthoredInteriorPlacementStore.TryGetAuthoredCameraDeletion(
                cameraRecord,
                cameraTile,
                out suppressionRecord,
                out source);
        }

        internal static bool TryGetCameraTileSuppression(
            Tile tile,
            out AuthoredInteriorPlacementRecord reportRecord,
            out string source)
        {
            return AuthoredInteriorPlacementStore.TryGetCameraTileSuppression(
                tile,
                out reportRecord,
                out source);
        }
    }
}
