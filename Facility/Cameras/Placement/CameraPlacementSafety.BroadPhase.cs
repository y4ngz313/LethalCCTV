using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Y4NGZCompany.Facility.Cameras.Placement
{
    // Conservative culls in front of the exact checks. Each one skips only work
    // whose exact outcome is already known, so Raycast reaches the same
    // survivors in the same order and TryFloor vetoes the same patches.
    internal static partial class CameraPlacementSafety
    {
        // Unity's float ray/box error is well under a millimetre at dungeon
        // coordinates, so a box grown by 5 cm only rejects what
        // Bounds.IntersectRay also rejects.
        private const float BroadPhaseMargin = 0.05f;
        private const float GridPadding = 0.01f;
        private const float GridCellMin = 4f;
        private const int GridCellsPerAxis = 64;
        private const int GridCellsPerRay = 64;
        // TryFloor's hazard veto radius plus a centimetre for float error.
        private const float HazardClearance = 0.23f;

        private static ulong[] _grid = Array.Empty<ulong>();
        private static ulong[] _gridHits = Array.Empty<ulong>();
        private static int _gridRooms, _gridWords, _gridCellsX, _gridCellsZ;
        private static float _gridMinX, _gridMinZ, _gridInverseCell;
        private static Bounds[] _hazardBounds = Array.Empty<Bounds>();

        // Snapshot bounds grown by BroadPhaseMargin.
        private readonly struct BroadBox
        {
            internal readonly float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            internal BroadBox(Bounds bounds)
            {
                Vector3 min = bounds.min, max = bounds.max;
                MinX = min.x - BroadPhaseMargin; MinY = min.y - BroadPhaseMargin; MinZ = min.z - BroadPhaseMargin;
                MaxX = max.x + BroadPhaseMargin; MaxY = max.y + BroadPhaseMargin; MaxZ = max.z + BroadPhaseMargin;
            }
        }

        // The segment origin + t * direction, t in [0, nearest + margin], tested
        // against broad boxes in managed code. Degenerate input disables it and
        // every box survives.
        private struct Probe
        {
            private readonly bool _enabled;
            private readonly float _x, _y, _z, _dirX, _dirY, _dirZ, _invX, _invY, _invZ;
            private readonly bool _parallelX, _parallelY, _parallelZ;
            private float _reach, _loX, _loY, _loZ, _hiX, _hiY, _hiZ;

            internal Probe(Vector3 origin, Vector3 unitDirection, float distance)
            {
                _x = origin.x; _y = origin.y; _z = origin.z;
                _dirX = unitDirection.x; _dirY = unitDirection.y; _dirZ = unitDirection.z;
                _enabled = distance > 0f && Finite(distance) &&
                    Finite(_x) && Finite(_y) && Finite(_z) &&
                    Finite(_dirX) && Finite(_dirY) && Finite(_dirZ) &&
                    unitDirection.sqrMagnitude > 0.5f;
                // A near-zero component moves the segment far less than the
                // margin along that axis; the segment bounds alone test it.
                _parallelX = Mathf.Abs(_dirX) < 1e-20f;
                _parallelY = Mathf.Abs(_dirY) < 1e-20f;
                _parallelZ = Mathf.Abs(_dirZ) < 1e-20f;
                _invX = _parallelX ? 0f : 1f / _dirX;
                _invY = _parallelY ? 0f : 1f / _dirY;
                _invZ = _parallelZ ? 0f : 1f / _dirZ;
                _reach = _loX = _loY = _loZ = _hiX = _hiY = _hiZ = 0f;
                Reach(distance);
            }

            internal bool Enabled => _enabled;
            internal float LoX => _loX;
            internal float HiX => _hiX;
            internal float LoZ => _loZ;
            internal float HiZ => _hiZ;

            internal void Reach(float nearest)
            {
                _reach = nearest + BroadPhaseMargin;
                float endX = _x + _dirX * _reach, endY = _y + _dirY * _reach, endZ = _z + _dirZ * _reach;
                _loX = Mathf.Min(_x, endX); _hiX = Mathf.Max(_x, endX);
                _loY = Mathf.Min(_y, endY); _hiY = Mathf.Max(_y, endY);
                _loZ = Mathf.Min(_z, endZ); _hiZ = Mathf.Max(_z, endZ);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool MayHit(in BroadBox box)
            {
                if (!_enabled) return true;
                if (box.MaxX < _loX || box.MinX > _hiX || box.MaxY < _loY || box.MinY > _hiY ||
                    box.MaxZ < _loZ || box.MinZ > _hiZ) return false;
                float tMin = 0f, tMax = _reach;
                return (_parallelX || Clip(box.MinX, box.MaxX, _x, _invX, ref tMin, ref tMax)) &&
                    (_parallelY || Clip(box.MinY, box.MaxY, _y, _invY, ref tMin, ref tMax)) &&
                    (_parallelZ || Clip(box.MinZ, box.MaxZ, _z, _invZ, ref tMin, ref tMax));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool Clip(float min, float max, float origin, float inverse, ref float tMin, ref float tMax)
            {
                float t0 = (min - origin) * inverse, t1 = (max - origin) * inverse;
                if (t0 > t1) { float swap = t0; t0 = t1; t1 = swap; }
                if (t0 > tMin) tMin = t0;
                if (t1 < tMax) tMax = t1;
                return tMin <= tMax;
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // XZ grid over the rooms' broad boxes. Each cell holds a bitset of the
        // rooms whose padded broad box overlaps it. A room in none of the
        // segment's cells fails Probe.MayHit's bounds test, so visiting the
        // grid rooms in index order equals the full scan.
        private static void BuildGrid()
        {
            _gridCellsX = _gridCellsZ = 0;
            int count = Rooms.Count;
            if (count == 0) return;
            float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            for (int r = 0; r < count; r++)
            {
                BroadBox box = Rooms[r].Broad;
                minX = Mathf.Min(minX, box.MinX - GridPadding); maxX = Mathf.Max(maxX, box.MaxX + GridPadding);
                minZ = Mathf.Min(minZ, box.MinZ - GridPadding); maxZ = Mathf.Max(maxZ, box.MaxZ + GridPadding);
            }
            float extent = Mathf.Max(maxX - minX, maxZ - minZ);
            if (!Finite(extent)) return;
            _gridMinX = minX; _gridMinZ = minZ;
            _gridInverseCell = 1f / Mathf.Max(GridCellMin, extent / GridCellsPerAxis);
            int cellsX = (int)((maxX - minX) * _gridInverseCell) + 1;
            int cellsZ = (int)((maxZ - minZ) * _gridInverseCell) + 1;
            _gridWords = (count + 63) >> 6;
            int size = cellsX * cellsZ * _gridWords;
            if (_grid.Length < size) _grid = new ulong[size];
            else Array.Clear(_grid, 0, size);
            if (_gridHits.Length < _gridWords) _gridHits = new ulong[_gridWords];
            for (int r = 0; r < count; r++)
            {
                BroadBox box = Rooms[r].Broad;
                int x0 = GridCell(box.MinX - GridPadding, minX, cellsX), x1 = GridCell(box.MaxX + GridPadding, minX, cellsX);
                int z0 = GridCell(box.MinZ - GridPadding, minZ, cellsZ), z1 = GridCell(box.MaxZ + GridPadding, minZ, cellsZ);
                ulong bit = 1UL << (r & 63);
                for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    _grid[(z * cellsX + x) * _gridWords + (r >> 6)] |= bit;
            }
            _gridCellsX = cellsX; _gridCellsZ = cellsZ; _gridRooms = count;
        }

        // False when the grid cannot answer; the caller then scans every room.
        private static bool VisitGridRooms(ref Cast cast)
        {
            if (_gridCellsX == 0 || _gridRooms != Rooms.Count || !cast.Probe.Enabled) return false;
            int x0 = GridCell(cast.Probe.LoX, _gridMinX, _gridCellsX), x1 = GridCell(cast.Probe.HiX, _gridMinX, _gridCellsX);
            int z0 = GridCell(cast.Probe.LoZ, _gridMinZ, _gridCellsZ), z1 = GridCell(cast.Probe.HiZ, _gridMinZ, _gridCellsZ);
            if ((x1 - x0 + 1) * (z1 - z0 + 1) > GridCellsPerRay) return false;
            Array.Clear(_gridHits, 0, _gridWords);
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                int cell = (z * _gridCellsX + x) * _gridWords;
                for (int w = 0; w < _gridWords; w++) _gridHits[w] |= _grid[cell + w];
            }
            for (int w = 0; w < _gridWords; w++)
            {
                ulong bits = _gridHits[w];
                for (int r = w << 6; bits != 0; r++, bits >>= 1)
                    if ((bits & 1UL) != 0) Visit(Rooms[r], ref cast);
            }
            return true;
        }

        // Rooms and segments share this one mapping, so overlapping extents
        // always share a cell. Out-of-grid values clamp to the edge cells.
        private static int GridCell(float value, float min, int cells)
        {
            float cell = (value - min) * _gridInverseCell;
            if (!(cell > 0f)) return 0;
            return cell >= cells - 1 ? cells - 1 : (int)cell;
        }

        // Live hazard bounds for one PrepareFloor call. Only this class's
        // queries run inside it, so the bounds stay current for the call.
        private static void ReadHazardBounds()
        {
            int count = AllHazards.Count;
            if (_hazardBounds.Length < count) _hazardBounds = new Bounds[count];
            for (int h = 0; h < count; h++)
            {
                Collider collider = AllHazards[h].Collider;
                _hazardBounds[h] = AllHazards[h].Primitive && collider != null ? collider.bounds : default;
            }
        }

        // A primitive's closest point lies inside its bounds, so body samples
        // farther than the veto radius from the bounds cannot veto. Empty
        // bounds (not a primitive, disabled or inactive) keep the exact check.
        private static bool HazardClear(int hazard, Vector3 floor)
        {
            Bounds bounds = _hazardBounds[hazard];
            Vector3 extents = bounds.extents;
            if (extents.x == 0f && extents.y == 0f && extents.z == 0f) return false;
            Vector3 min = bounds.min, max = bounds.max;
            float lowest = floor.y + BodyLowest, highest = floor.y + BodyLowest + (BodySamples - 1) * BodySpacing;
            float dx = Mathf.Max(0f, Mathf.Max(min.x - floor.x, floor.x - max.x));
            float dy = Mathf.Max(0f, Mathf.Max(min.y - highest, lowest - max.y));
            float dz = Mathf.Max(0f, Mathf.Max(min.z - floor.z, floor.z - max.z));
            return dx * dx + dy * dy + dz * dz >= HazardClearance * HazardClearance;
        }
    }
}
