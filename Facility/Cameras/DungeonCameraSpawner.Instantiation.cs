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
        // server it claims the selected pose for later facility consumers through
        // CameraReservationQueue, which holds the claim until the host support pass settles.
        // Reservation conflicts never change the peer-local camera list.
        // All placement sources share safety validation, component setup,
        // field stash, disable-config, and mouselook baseline capture here.
        private CCTVCamera InstantiateAtPose(
            Tile tile, int cameraIndex, Vector3 worldPos, Quaternion worldRot,
            int cornerId, Vector3 cornerLocal, CameraMountMode mountMode,
            float drivingRoomHeightM, string source = "procedural")
        {
            // ApplyOffsets reconstructs a level yaw/pitch rotation. Validate and
            // spawn exactly that rotation, including saved poses with authored roll.
            worldRot = CameraPlacementSafety.LevelRotation(worldRot);
            CameraPanEnvelope safetyEnvelope = null;
            if (!CameraPlacementSafety.TryEnvelope(tile, worldPos, worldRot,
                out safetyEnvelope, out string safetyReason))
            {
                SurveillanceBootstrap.Log?.LogMessage(
                    $"[LethalCCTV][PlacementSafety] reject cam={cameraIndex} tile='{tile?.name}' " +
                    $"source={source} corner={cornerId} seed={StartOfRound.Instance?.randomMapSeed} " +
                    $"lens={worldPos:F3} euler={worldRot.eulerAngles:F2} reason={safetyReason} " +
                    CameraPlacementSafety.DescribeFloor(tile));
                return null;
            }
            CameraReservationQueue.Claim cameraReservation = null;
            if (RoundManager.Instance != null && RoundManager.Instance.IsServer)
                cameraReservation = CameraReservationQueue.Reserve(cameraIndex, worldPos, worldRot);

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
                holder.SafetyEnvelope = safetyEnvelope;
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
                CameraReservationQueue.Release(cameraReservation);
                if (go != null)
                    UnityEngine.Object.Destroy(go);

                SurveillanceBootstrap.Log.LogError(
                    $"[LethalCCTV] Camera construction failed after reservation. cam={cameraIndex} exception={exception}");
                return null;
            }
        }

    }
}
