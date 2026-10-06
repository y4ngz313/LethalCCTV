using System;
using System.Collections.Generic;
using DunGen;
using UnityEngine;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // Round-local snapshot of dungeon structure. No NavMesh, player settings,
    // spawned props, renderer visibility flags, or network timing decide membership.
    internal static partial class CameraPlacementSafety
    {
        internal const float VerticalFov = 75f;
        internal const float Aspect = 4f / 3f;
        private const float FloorStep = 0.5f;
        private const int MaxFloorPatches = 192;
        // TryFloor's body samples above a patch.
        private const float BodyLowest = 0.15f, BodySpacing = 0.55f;
        private const int BodySamples = 3;
        private readonly struct Solid
        {
            internal readonly Collider Collider;
            internal readonly Bounds Bounds;
            internal readonly BroadBox Broad;
            internal Solid(Collider collider) { Collider = collider; Bounds = collider.bounds; Broad = new BroadBox(Bounds); }
        }
        private readonly struct Hazard
        {
            internal readonly Collider Collider;
            internal readonly bool Water;
            // ClosestPoint on these shapes stays inside collider.bounds.
            internal readonly bool Primitive;
            internal Hazard(Collider collider, bool water)
            {
                Collider = collider; Water = water;
                Primitive = collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider;
            }
        }
        private sealed class Room
        {
            internal Tile Tile;
            internal Bounds Bounds;
            internal BroadBox Broad;
            internal Solid[] Solids;
            internal readonly List<Hazard> Hazards = new List<Hazard>();
            internal readonly List<Vector3> Floor = new List<Vector3>();
            internal bool FloorReady;
        }
        private static readonly List<Room> Rooms = new List<Room>();
        private static readonly Dictionary<Tile, Room> ByTile = new Dictionary<Tile, Room>();
        // Every kept room's hazards, in room order then hazard order.
        private static readonly List<Hazard> AllHazards = new List<Hazard>();
        // A useful patch has foot support, not a single lucky ray on a railing.
        private static readonly Vector3[] FootOffsets = { Vector3.right * 0.18f, Vector3.left * 0.18f,
            Vector3.forward * 0.18f, Vector3.back * 0.18f };
        private static float _probeDistance = 120f;
        internal static float ProbeDistance => _probeDistance;

        internal static void Clear() { Rooms.Clear(); ByTile.Clear(); AllHazards.Clear(); _gridCellsX = 0; _probeDistance = 120f; }

        internal static void Prepare(IEnumerable<Tile> tiles)
        {
            Clear();
            Bounds dungeon = default;
            bool haveBounds = false;
            var solids = new List<Solid>();
            foreach (Tile tile in tiles)
            {
                if (tile == null) continue;
                var room = new Room { Tile = tile };
                solids.Clear();
                bool first = true;
                foreach (Collider collider in tile.GetComponentsInChildren<Collider>(true))
                {
                    if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                    QuicksandTrigger sinking = collider.GetComponentInParent<QuicksandTrigger>();
                    bool kill = collider.GetComponentInParent<KillLocalPlayer>() != null;
                    if (kill || (sinking != null && (!sinking.isWater || sinking.isInsideWater)))
                    { room.Hazards.Add(new Hazard(collider, !kill && sinking != null && sinking.isWater)); continue; }
                    if (collider.isTrigger || collider.attachedRigidbody != null ||
                        collider.GetComponentInParent<Unity.Netcode.NetworkObject>() != null ||
                        collider.GetComponentInParent<GrabbableObject>() != null) continue;
                    var solid = new Solid(collider);
                    solids.Add(solid);
                    if (first) { room.Bounds = solid.Bounds; first = false; }
                    else room.Bounds.Encapsulate(solid.Bounds);
                }
                if (!first)
                {
                    room.Solids = solids.ToArray();
                    room.Broad = new BroadBox(room.Bounds);
                    Rooms.Add(room); ByTile[tile] = room;
                    AllHazards.AddRange(room.Hazards);
                    if (!haveBounds) { dungeon = room.Bounds; haveBounds = true; }
                    else dungeon.Encapsulate(room.Bounds);
                }
            }
            // A large interior is not void merely because its wall is beyond 40m.
            if (haveBounds) _probeDistance = Mathf.Max(120f, dungeon.size.magnitude + 2f);
            BuildGrid();
        }

        internal static void PrepareFloor(Tile tile)
        {
            if (tile == null || !ByTile.TryGetValue(tile, out Room room) || room.FloorReady) return;
            ReadHazardBounds();
            var pending = new Queue<Vector3>();
            var visited = new HashSet<Vector3Int>();
            if (tile.UsedDoorways != null)
            foreach (Doorway door in tile.UsedDoorways)
            {
                if (door == null || door.ConnectedDoorway == null) continue;
                // Doorway floor bands seed only a bounded local downward probe.
                // A pit bottom several metres below a door can never seed a patch.
                Vector3 intoRoom = -door.transform.forward;
                intoRoom.y = 0f;
                Vector3 seed = door.transform.position + intoRoom.normalized * 0.6f;
                if (TryFloor(room, seed, out Vector3 floor)) pending.Enqueue(floor);
            }
            Vector3 right = tile.transform.right; right.y = 0f; right.Normalize();
            Vector3 forward = tile.transform.forward; forward.y = 0f; forward.Normalize();
            Vector3[] directions = { right, -right, forward, -forward };
            while (pending.Count > 0 && room.Floor.Count < MaxFloorPatches)
            {
                Vector3 floor = pending.Dequeue();
                Vector3 local = tile.transform.InverseTransformPoint(floor);
                var key = new Vector3Int(Mathf.RoundToInt(local.x / FloorStep),
                    Mathf.RoundToInt(local.y / FloorStep), Mathf.RoundToInt(local.z / FloorStep));
                if (!visited.Add(key)) continue;
                room.Floor.Add(floor);
                foreach (Vector3 direction in directions)
                {
                    Vector3 next = floor + direction * FloorStep;
                    if (next.x < room.Bounds.min.x || next.x > room.Bounds.max.x ||
                        next.z < room.Bounds.min.z || next.z > room.Bounds.max.z) continue;
                    if (!TryFloor(room, next, out Vector3 supported)) continue;
                    if (!TryFloor(room, (floor + supported) * 0.5f, out _)) continue;
                    Vector3 from = floor + Vector3.up * 0.65f;
                    Vector3 to = supported + Vector3.up * 0.65f;
                    if (Raycast(from, (to - from).normalized, (to - from).magnitude, out _)) continue;
                    pending.Enqueue(supported);
                }
            }
            room.FloorReady = true;
        }

        // Called only inside PrepareFloor, after ReadHazardBounds.
        private static bool TryFloor(Room room, Vector3 near, out Vector3 floor)
        {
            floor = default;
            if (!Raycast(near + Vector3.up * 0.55f, Vector3.down, 1.05f, out RaycastHit hit) || hit.normal.y < 0.55f)
                return false;
            floor = hit.point;
            for (int o = 0; o < FootOffsets.Length; o++)
                if (!Raycast(floor + FootOffsets[o] + Vector3.up * 0.35f, Vector3.down, 0.65f, out RaycastHit edge) || edge.normal.y < 0.55f)
                    return false;
            for (int h = 0; h < AllHazards.Count; h++)
            {
                Hazard hazard = AllHazards[h];
                if (hazard.Collider == null || HazardClear(h, floor)) continue;
                for (int i = 0; i < BodySamples; i++)
                {
                    // Shallow water with solid footing is usable. Water becomes
                    // a drowning veto at head height; pits/quicksand veto feet too.
                    if (hazard.Water && i < 2) continue;
                    Vector3 body = floor + Vector3.up * (BodyLowest + i * BodySpacing);
                    if ((hazard.Collider.ClosestPoint(body) - body).sqrMagnitude < 0.22f * 0.22f) return false;
                }
            }
            return true;
        }

        // One Raycast's state: the exact ray, the caller's direction for the
        // back-face test, the broad-phase segment and the nearest front hit.
        private struct Cast
        {
            internal readonly Ray Ray;
            internal readonly Vector3 Direction;
            internal Probe Probe;
            internal float Nearest;
            internal bool Found;
            internal RaycastHit Closest;

            internal Cast(Vector3 origin, Vector3 direction, float distance)
            {
                Ray = new Ray(origin, direction);
                Direction = direction;
                Probe = new Probe(origin, Ray.direction, distance);
                Nearest = distance;
                Found = false;
                Closest = default;
            }
        }

        internal static bool Raycast(Vector3 origin, Vector3 direction, float distance, out RaycastHit closest)
        {
            var cast = new Cast(origin, direction, distance);
            if (!VisitGridRooms(ref cast))
                for (int r = 0; r < Rooms.Count; r++) Visit(Rooms[r], ref cast);
            closest = cast.Closest;
            return cast.Found;
        }

        private static void Visit(Room room, ref Cast cast)
        {
            if (!cast.Probe.MayHit(in room.Broad)) return;
            if (!room.Bounds.IntersectRay(cast.Ray, out float entry) || entry > cast.Nearest) return;
            Solid[] solids = room.Solids;
            for (int s = 0; s < solids.Length; s++)
            {
                if (!cast.Probe.MayHit(in solids[s].Broad)) continue;
                Collider collider = solids[s].Collider;
                if (collider == null || !solids[s].Bounds.IntersectRay(cast.Ray, out float colliderEntry) ||
                    colliderEntry > cast.Nearest) continue;
                if (!collider.Raycast(cast.Ray, out RaycastHit hit, cast.Nearest)) continue;
                // An interior shell's back face is not evidence of a rendered wall.
                if (Vector3.Dot(hit.normal, cast.Direction) >= -0.001f) continue;
                if (!cast.Found || hit.distance < cast.Nearest)
                {
                    cast.Closest = hit; cast.Nearest = hit.distance; cast.Found = true;
                    cast.Probe.Reach(cast.Nearest);
                }
            }
        }

        internal static bool HasPlayableView(Tile tile, Vector3 origin, Quaternion rotation)
        {
            if (tile == null || !ByTile.TryGetValue(tile, out Room room) || !room.FloorReady) return false;
            float tanY = Mathf.Tan(VerticalFov * 0.5f * Mathf.Deg2Rad);
            Quaternion inverse = Quaternion.Inverse(rotation);
            foreach (Vector3 patch in room.Floor)
            {
                Vector3 target = patch + Vector3.up * 0.6f;
                Vector3 local = inverse * (target - origin);
                if (local.z <= 1f || Mathf.Abs(local.x) > local.z * tanY * Aspect * 0.9f ||
                    Mathf.Abs(local.y) > local.z * tanY * 0.9f) continue;
                float distance = Vector3.Distance(origin, target);
                if (!Raycast(origin, (target - origin).normalized, distance - 0.1f, out _)) return true;
            }
            return false;
        }

        internal static Vector3 AimAtPlayablePatch(Tile tile, Vector3 origin, Vector3 preferred)
        {
            if (tile == null || !ByTile.TryGetValue(tile, out Room room)) return preferred;
            Vector3 best = preferred;
            float score = -1f;
            foreach (Vector3 patch in room.Floor)
            {
                Vector3 delta = patch + Vector3.up * 0.6f - origin;
                float candidate = Vector3.Dot(preferred.normalized, delta.normalized);
                if (delta.magnitude < 2f || candidate <= score ||
                    Raycast(origin, delta.normalized, delta.magnitude - 0.1f, out _)) continue;
                score = candidate; best = delta;
            }
            return best;
        }

        internal static bool ValidateView(Vector3 origin, Quaternion rotation, float paddingDegrees,
            out float requiredDistance, out string reason)
            => ValidateView(origin, rotation, paddingDegrees, out requiredDistance, out _, out reason);

        internal static bool ValidateView(Vector3 origin, Quaternion rotation, float paddingDegrees,
            out float requiredDistance, out int nearWalls, out string reason)
        {
            requiredDistance = 0f;
            nearWalls = 0;
            float tanY = Mathf.Tan((VerticalFov * 0.5f + paddingDegrees) * Mathf.Deg2Rad);
            // 5x5 projection samples include all four full-feed corners and edges.
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                Vector3 direction = rotation * new Vector3(x * 0.5f * tanY * Aspect, y * 0.5f * tanY, 1f).normalized;
                if (!Raycast(origin, direction, _probeDistance, out RaycastHit hit))
                { reason = $"frustum-escape sample={x},{y} direction={direction:F3}"; return false; }
                if (hit.distance < 0.08f)
                { reason = $"lens-embedded collider={hit.collider.name} layer={hit.collider.gameObject.layer}"; return false; }
                if (hit.distance < 2f) nearWalls++;
                requiredDistance = Mathf.Max(requiredDistance, hit.distance + 1f);
            }
            reason = null;
            return true;
        }

        internal static bool TryEnvelope(Tile tile, Vector3 origin, Quaternion rotation,
            out CameraPanEnvelope envelope, out string reason)
        {
            envelope = null;
            if (!HasPlayableView(tile, origin, rotation)) { reason = "no-connected-safe-floor-in-view"; return false; }
            if (!ValidateView(origin, rotation, 0f, out float distance, out reason)) return false;
            envelope = new CameraPanEnvelope(tile, origin, rotation, distance);
            return true;
        }

        internal static Quaternion LevelRotation(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            return Quaternion.Euler(-Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg,
                Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 0f);
        }

        internal static string DescribeFloor(Tile tile)
        {
            if (tile == null || !ByTile.TryGetValue(tile, out Room room)) return "no-static-structure";
            return $"patches={room.Floor.Count} solids={room.Solids.Length} hazards={room.Hazards.Count} ready={room.FloorReady}";
        }
    }

    internal sealed class CameraPanEnvelope
    {
        private readonly Vector3 _origin;
        private readonly Tile _tile;
        private readonly float _baseYaw, _basePitch;
        private readonly Dictionary<Vector2Int, bool> _sectors = new Dictionary<Vector2Int, bool>();
        // #1139: exact-pose validation cached at 0.5° resolution. Value >= 0
        // is the validated view distance; -1f is invalid. An idle sweep makes
        // zero raycasts once every visited cell is known.
        private readonly Dictionary<Vector2Int, float> _exactPoses = new Dictionary<Vector2Int, float>();
        private const int MaxValidationsPerFrame = 2;
        private float _yaw, _pitch;
        internal float RequiredDistance { get; private set; }

        internal CameraPanEnvelope(Tile tile, Vector3 origin, Quaternion rotation, float distance)
        {
            _tile = tile;
            _origin = origin;
            Vector3 forward = rotation * Vector3.forward;
            _baseYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            _basePitch = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            RequiredDistance = distance;
        }

        internal void Constrain(ref float yaw, ref float pitch)
        {
            yaw = Mathf.Clamp(yaw, -90f, 90f);
            pitch = Mathf.Clamp(_basePitch + pitch, -5f, 75f) - _basePitch;
            float fromYaw = _yaw, fromPitch = _pitch;
            if (Mathf.Abs(yaw - fromYaw) + Mathf.Abs(pitch - fromPitch) < 0.0001f)
            { yaw = _yaw; pitch = _pitch; return; }
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(yaw - fromYaw), Mathf.Abs(pitch - fromPitch)) / 2f));
            int budget = MaxValidationsPerFrame;
            for (int i = 1; i <= steps; i++)
            {
                float y = Mathf.Lerp(fromYaw, yaw, i / (float)steps);
                float p = Mathf.Lerp(fromPitch, pitch, i / (float)steps);
                var cell = new Vector2Int(Mathf.RoundToInt(y / 2f), Mathf.RoundToInt(p / 2f));
                if (!_sectors.TryGetValue(cell, out bool valid))
                {
                    if (budget <= 0) break;
                    budget--;
                    Quaternion rotation = Quaternion.Euler(_basePitch + cell.y * 2f, _baseYaw + cell.x * 2f, 0f);
                    valid = CameraPlacementSafety.ValidateView(_origin, rotation, 2f, out float distance, out _) &&
                        CameraPlacementSafety.HasPlayableView(_tile, _origin, rotation);
                    _sectors[cell] = valid;
                    if (valid) RequiredDistance = Mathf.Max(RequiredDistance, distance);
                }
                if (!valid) break;
                // #1139: wider sample rays (2°) are not a superset of
                // narrower rays — a doorway can fall between them. The
                // exact-pose result is cached at 0.5° cells so an idle sweep
                // makes zero raycasts once every visited cell is known.
                var exactCell = new Vector2Int(Mathf.RoundToInt(y / 0.5f), Mathf.RoundToInt(p / 0.5f));
                if (!_exactPoses.TryGetValue(exactCell, out float exactResult))
                {
                    if (budget <= 0) break;
                    budget--;
                    Quaternion actual = Quaternion.Euler(_basePitch + p, _baseYaw + y, 0f);
                    bool exactValid = CameraPlacementSafety.ValidateView(_origin, actual, 0f, out float exactDistance, out _) &&
                        CameraPlacementSafety.HasPlayableView(_tile, _origin, actual);
                    _exactPoses[exactCell] = exactValid ? exactDistance : -1f;
                    if (exactValid)
                    {
                        RequiredDistance = Mathf.Max(RequiredDistance, exactDistance);
                    }
                    else
                    {
                        break;
                    }
                }
                else if (exactResult < 0f)
                {
                    break;
                }
                // exactResult >=0f: cached valid; RequiredDistance already grew on first validation.
                _yaw = y; _pitch = p;
            }
            yaw = _yaw; pitch = _pitch;
        }
    }
}
