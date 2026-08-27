using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Facility.Cameras.Placement;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.ShipSystems.Surveillance;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    public sealed partial class DungeonCameraSpawner
    {
        // Pose snapshot for redundancy suppression. Forward is cached so the
        // similarity test never re-derives it from the rotation.
        private readonly struct AcceptedPose
        {
            public readonly Vector3 Pos;
            public readonly Vector3 Fwd;
            public readonly Tile Tile;
            public readonly RoomComponent Comp;

            public AcceptedPose(Vector3 pos, Vector3 fwd, Tile tile, RoomComponent comp)
            {
                Pos = pos;
                Fwd = fwd;
                Tile = tile;
                Comp = comp;
            }
        }

        private readonly struct LegacyFallbackPose
        {
            public readonly Vector3 WorldPos;
            public readonly Quaternion WorldRot;
            public readonly int CornerId;
            public readonly Vector3 CornerLocal;
            public readonly CameraMountMode MountMode;
            public readonly float DrivingRoomHeightM;
            public readonly SurfaceMount.SafetyGateResult Gate;

            public LegacyFallbackPose(
                Vector3 worldPos, Quaternion worldRot, int cornerId, Vector3 cornerLocal,
                CameraMountMode mountMode, float drivingRoomHeightM,
                SurfaceMount.SafetyGateResult gate)
            {
                WorldPos = worldPos;
                WorldRot = worldRot;
                CornerId = cornerId;
                CornerLocal = cornerLocal;
                MountMode = mountMode;
                DrivingRoomHeightM = drivingRoomHeightM;
                Gate = gate;
            }
        }

        private static bool TryComputeSafeLegacyFallback(
            Tile tile,
            Bounds box,
            int sortedIndex,
            in PlacementParams placementParams,
            in PlacementMasks placementMasks,
            bool debug,
            out LegacyFallbackPose fallback)
        {
            fallback = default;
            if (tile == null || box.size.sqrMagnitude < 1e-6f) return false;

            for (int cornerVariantOffset = 0; cornerVariantOffset < 4; cornerVariantOffset++)
            {
                (Vector3 worldPos, Quaternion worldRot, int cornerId, Vector3 cornerLocal,
                 CameraMountMode mountMode, float drivingRoomHeightM) =
                    TilePlacement.ComputeP2(
                        tile, box, sortedIndex, in placementParams, cornerVariantOffset);

                if (!SurfaceMount.TryValidateLegacyFallbackPose(
                        tile, box, sortedIndex, in placementMasks,
                        worldPos, worldRot, mountMode, cornerId, debug,
                        out SurfaceMount.SafetyGateResult gate))
                {
                    continue;
                }

                fallback = new LegacyFallbackPose(
                    worldPos, worldRot, cornerId, cornerLocal,
                    mountMode, drivingRoomHeightM, gate);
                return true;
            }

            return false;
        }
        private static bool IsRedundant(
            SurfaceMount.Candidate c, List<AcceptedPose> kept, CameraPick pick, out string reason)
        {
            reason = null;
            for (int i = 0; i < kept.Count; i++)
            {
                AcceptedPose k = kept[i];
                float d = Vector3.Distance(c.WorldPos, k.Pos);
                if (d >= RedundancyDistM) continue;
                float dy = Mathf.Abs(c.WorldPos.y - k.Pos.y);
                if (dy >= RedundancySameFloorYM) continue;
                bool sameRoom = ReferenceEquals(pick.Tile, k.Tile)
                    || (pick.Component != null && ReferenceEquals(pick.Component, k.Comp));
                if (d < MinCameraSeparationM && (sameRoom || d < RedundancyCloseAnywayM))
                {
                    reason = $"min-separation vs cam@({k.Pos.x:F1},{k.Pos.y:F1},{k.Pos.z:F1}) " +
                             $"d={d:F2} dy={dy:F2} sameRoom={sameRoom}";
                    return true;
                }
                float dot = Vector3.Dot(c.Forward, k.Fwd);
                if (dot <= RedundancyForwardDot) continue;
                if (sameRoom || d < RedundancyCloseAnywayM)
                {
                    reason = $"vs cam@({k.Pos.x:F1},{k.Pos.y:F1},{k.Pos.z:F1}) " +
                             $"d={d:F2} dot={dot:F2} dy={dy:F2} sameRoom={sameRoom}";
                    return true;
                }
            }
            return false;
        }

        // Spacing-floor check for the legacy fallback pose, which never goes
        // through candidate ranking (and therefore never through IsRedundant).
        // Same rule as IsRedundant's min-separation clause.
        private static bool IsFallbackTooClose(
            Vector3 pos, List<AcceptedPose> kept, CameraPick pick, out string reason)
        {
            reason = null;
            for (int i = 0; i < kept.Count; i++)
            {
                AcceptedPose k = kept[i];
                float d = Vector3.Distance(pos, k.Pos);
                if (d >= MinCameraSeparationM) continue;
                if (Mathf.Abs(pos.y - k.Pos.y) >= RedundancySameFloorYM) continue;
                bool sameRoom = ReferenceEquals(pick.Tile, k.Tile)
                    || (pick.Component != null && ReferenceEquals(pick.Component, k.Comp));
                if (sameRoom || d < RedundancyCloseAnywayM)
                {
                    reason = $"vs cam@({k.Pos.x:F1},{k.Pos.y:F1},{k.Pos.z:F1}) d={d:F2} sameRoom={sameRoom}";
                    return true;
                }
            }
            return false;
        }

        // Tile-local fallback box for degenerate-mesh tiles: the DunGen
        // tile-local LocalBounds. Colliders still exist on such tiles even
        // when no encapsulable MeshFilter does, so a surface probe over this
        // box can still find real walls/ceilings.
        private static Bounds FallbackBox(Tile tile)
        {
            if (tile == null || tile.Placement == null) return new Bounds(Vector3.zero, Vector3.zero);
            return tile.Placement.LocalBounds;
        }

        // Pose-instantiation funnel. It does not choose where the camera goes; on the
        // server it first reserves the selected pose so a delayed camera pass cannot
        // overlap another facility consumer. All placement sources share component setup,
        // field stash, disable-config, and mouselook baseline capture here.
        private CCTVCamera InstantiateAtPose(
            Tile tile, int cameraIndex, Vector3 worldPos, Quaternion worldRot,
            int cornerId, Vector3 cornerLocal, CameraMountMode mountMode,
            float drivingRoomHeightM)
        {
            FixtureReservationHandle cameraReservation = default;
            if (RoundManager.Instance != null && RoundManager.Instance.IsServer)
            {
                var cameraFootprint = new FixtureFootprint(
                    Vector3.forward,
                    Vector3.back,
                    Vector3.up,
                    new Vector3(0.70f, 0.55f, 0.75f),
                    0f,
                    -0.275f,
                    0.35f,
                    0.20f);
                if (!FixtureReservationRegistry.TryReserve(
                        $"CCTV camera {cameraIndex}",
                        "cctv-camera-pipeline",
                        cameraFootprint,
                        worldPos,
                        worldRot,
                        out cameraReservation,
                        out FixtureReservationConflict conflict))
                {
                    SurveillanceBootstrap.Log.LogWarning(
                        $"[LethalCCTV] Camera rejected before construction because its reservation overlaps " +
                        $"an existing fixture. cam={cameraIndex} {conflict.ToDiagnosticString()}.");
                    return null;
                }
            }

            string ownerName = tile != null ? tile.name : "Exterior";
            GameObject go = null;
            try
            {
                go = new GameObject($"LethalCCTVCamera_{cameraIndex}_{ownerName}");
                if (tile != null)
                {
                    go.transform.SetParent(tile.transform, worldPositionStays: false);
                }
                go.transform.SetPositionAndRotation(worldPos, worldRot);

                // Phase 1 deliberately attaches only the Camera component (not
                // HDAdditionalCameraData). With Cam.enabled=false the Camera alone
                // contributes no render work, but adding HDAdditionalCameraData
                // here would register every spawned camera with HDRP's per-frame
                // iteration and leak its (stripped) FrameSettings into HDRP's
                // probe/volume sampling — the cause of the ceiling-brightening
                // artifact observed in Factory testing. Phase 2's Promote() adds
                // HDAdditionalCameraData at the moment the camera is first picked
                // for a monitor slot and applies the Frame Settings strip set then.
                Camera cam = go.AddComponent<Camera>();
                CCTVCamera holder = go.AddComponent<CCTVCamera>();

                holder.CameraIndex = cameraIndex;
                holder.DisplayLabel = "CAM_" + cameraIndex.ToString("D2");
                holder.OwningTile = tile;
                holder.Cam = cam;
                holder.HdrpData = null;
                holder.PlacementCornerLocal = cornerLocal;
                holder.PlacementCornerId = cornerId;
                holder.MountMode = mountMode;
                holder.DrivingRoomHeightM = drivingRoomHeightM;

                holder.ConfigureDisabled();
                // Mouselook baseline — captured AFTER SetPositionAndRotation so the
                // baseline reflects the final spawn rotation. FocusMouselook applies
                // PitchOffsetDeg/YawOffsetDeg on top of this baseline per-frame on
                // the active pane.
                holder.CaptureBaseline();
                CCTVCameraVisual.AttachTo(holder);
                CctvDetectionIndicator.AttachTo(holder);

                return holder;
            }
            catch (Exception exception)
            {
                if (cameraReservation.IsValid)
                    FixtureReservationRegistry.Release(cameraReservation);
                if (go != null)
                    UnityEngine.Object.Destroy(go);

                SurveillanceBootstrap.Log.LogError(
                    $"[LethalCCTV] Camera construction failed after reservation. cam={cameraIndex} exception={exception}");
                return null;
            }
        }

    }
}
