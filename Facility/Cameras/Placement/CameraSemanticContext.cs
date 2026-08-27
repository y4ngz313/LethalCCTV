using System.Collections.Generic;
using DunGen;
using UnityEngine;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // CameraTileShape moved to Y4NGZCore alongside TileShapeClassifier.

    internal readonly struct DoorwayFact
    {
        public readonly Vector3 WorldPosition;
        public readonly Vector3 WorldForward;
        public readonly Tile ConnectedTile;

        public DoorwayFact(Vector3 worldPosition, Vector3 worldForward, Tile connectedTile)
        {
            WorldPosition = worldPosition;
            WorldForward = SafeNormal(worldForward, Vector3.forward);
            ConnectedTile = connectedTile;
        }

        private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
        {
            if (value.sqrMagnitude < 1e-6f) value = fallback;
            return value.sqrMagnitude < 1e-6f ? Vector3.forward : value.normalized;
        }
    }

    internal readonly struct CameraSemanticContext
    {
        public readonly CameraTileShape Shape;
        public readonly Vector3 RoomCenter;
        public readonly Vector3 LongAxis;
        public readonly IReadOnlyList<DoorwayFact> Doorways;
        // Optional point of interest the tile's camera should watch when it
        // has line of sight (the main-entrance door, a fixture, …). Aim
        // profiles treat this as the highest-priority target.
        public readonly Vector3 FocusTarget;
        public readonly bool HasFocusTarget;

        public CameraSemanticContext(
            CameraTileShape shape,
            Vector3 roomCenter,
            Vector3 longAxis,
            IReadOnlyList<DoorwayFact> doorways)
            : this(shape, roomCenter, longAxis, doorways, Vector3.zero, hasFocusTarget: false)
        {
        }

        public CameraSemanticContext(
            CameraTileShape shape,
            Vector3 roomCenter,
            Vector3 longAxis,
            IReadOnlyList<DoorwayFact> doorways,
            Vector3 focusTarget,
            bool hasFocusTarget)
        {
            Shape = shape;
            RoomCenter = roomCenter;
            LongAxis = SafeNormal(longAxis, Vector3.forward);
            Doorways = doorways ?? s_emptyDoorways;
            FocusTarget = focusTarget;
            HasFocusTarget = hasFocusTarget;
        }

        private static readonly DoorwayFact[] s_emptyDoorways = new DoorwayFact[0];

        private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
        {
            if (value.sqrMagnitude < 1e-6f) value = fallback;
            return value.sqrMagnitude < 1e-6f ? Vector3.forward : value.normalized;
        }
    }

    internal static class CameraSemanticContextBuilder
    {
        public static CameraSemanticContext Build(Tile tile, Bounds box)
        {
            return Build(tile, box, null);
        }

        public static CameraSemanticContext Build(Tile tile, Bounds box, Vector3? focusTarget)
        {
            Vector3 focus = focusTarget ?? Vector3.zero;
            bool hasFocus = focusTarget.HasValue;
            if (tile == null || tile.Placement == null)
            {
                return new CameraSemanticContext(
                    CameraTileShape.Room,
                    box.center,
                    LongAxisFromBounds(box, Quaternion.identity),
                    null,
                    focus,
                    hasFocus);
            }

            TilePlacementData placement = tile.Placement;
            Vector3 roomCenter = placement.Position + placement.Rotation * box.center;
            Vector3 longAxis = LongAxisFromBounds(box, placement.Rotation);
            var doorways = new List<DoorwayFact>();

            if (tile.UsedDoorways != null)
            {
                for (int i = 0; i < tile.UsedDoorways.Count; i++)
                {
                    Doorway doorway = tile.UsedDoorways[i];
                    if (doorway == null) continue;
                    Vector3 forward = doorway.transform != null ? doorway.transform.forward : longAxis;
                    Tile connected = doorway.ConnectedDoorway != null ? doorway.ConnectedDoorway.Tile : null;
                    doorways.Add(new DoorwayFact(doorway.transform.position, forward, connected));
                }
            }

            CameraTileShape shape = ClassifyShape(doorways, placement, box);
            return new CameraSemanticContext(shape, roomCenter, longAxis, doorways, focus, hasFocus);
        }

        private static CameraTileShape ClassifyShape(List<DoorwayFact> doorways, TilePlacementData placement, Bounds box)
        {
            if (doorways == null || doorways.Count < 2)
                return CameraTileShape.Room;

            Matrix4x4 worldToLocal = Matrix4x4.TRS(
                placement.Position, placement.Rotation, Vector3.one).inverse;
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity;
            float maxZ = float.NegativeInfinity;

            for (int i = 0; i < doorways.Count; i++)
            {
                Vector3 local = worldToLocal.MultiplyPoint3x4(doorways[i].WorldPosition);
                if (local.x < minX) minX = local.x;
                if (local.x > maxX) maxX = local.x;
                if (local.z < minZ) minZ = local.z;
                if (local.z > maxZ) maxZ = local.z;
            }

            float spanX = Mathf.Max(0f, maxX - minX);
            float spanZ = Mathf.Max(0f, maxZ - minZ);
            return TileShapeClassifier.ClassifyByDoorwaySpans(spanX, spanZ);
        }

        private static Vector3 LongAxisFromBounds(Bounds box, Quaternion rotation)
        {
            Vector3 local = box.size.x >= box.size.z ? Vector3.right : Vector3.forward;
            Vector3 world = rotation * local;
            world.y = 0f;
            return world.sqrMagnitude < 1e-6f ? Vector3.forward : world.normalized;
        }
    }
}
