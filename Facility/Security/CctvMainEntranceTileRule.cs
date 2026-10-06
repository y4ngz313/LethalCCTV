using System.Collections.Generic;
using System.Reflection;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Security
{
    /// <summary>
    /// A camera in the facility's main-entrance tile is never security-active (#735).
    ///
    /// The spawner deliberately guarantees that tile a camera aimed at the entrance door, so
    /// without this rule the first camera the crew meets is the one most likely to be hunting
    /// them as they walk in. The camera still spawns, streams and aims as before; it is simply
    /// never drawn into the detection rotation and never counts toward the active quota.
    ///
    /// The tile is resolved the way <c>DungeonCameraSpawner.BuildEntranceTileSets</c> does it:
    /// the facility-side <see cref="EntranceTeleport"/> with entranceId 0, its pad attributed
    /// to the closest tile whose bounds contain it. Cameras expose their DunGen tile through an
    /// <c>OwningTile</c> property; cameras without one (supplementary injections) fall back to
    /// the tile whose bounds contain their position. Unconditional by design - no config.
    /// </summary>
    internal static class CctvMainEntranceTileRule
    {
        internal const string ExclusionReason = "main-entrance-tile";

        private static Tile _mainEntranceTile;
        private static bool _resolved;
        private static int _resolveAttempts;
        private static readonly HashSet<CctvSecurityCameraState> Evaluated = new HashSet<CctvSecurityCameraState>();
        private const int MaxResolveAttempts = 8;

        internal static void ResetRound()
        {
            _mainEntranceTile = null;
            _resolved = false;
            _resolveAttempts = 0;
            Evaluated.Clear();
        }

        /// <summary>
        /// Marks every not-yet-evaluated camera owned by the main-entrance tile as excluded.
        /// Cheap after the first call: the tile is cached per round and each camera is
        /// evaluated once. Safe to call from every rotation.
        /// </summary>
        internal static void Apply(IReadOnlyList<CctvSecurityCameraState> cameras)
        {
            if (cameras == null || cameras.Count == 0)
                return;

            if (!_resolved && !TryResolveMainEntranceTile())
                return;
            if (_mainEntranceTile == null)
                return;

            for (int i = 0; i < cameras.Count; i++)
            {
                CctvSecurityCameraState state = cameras[i];
                if (state == null || state.CameraComponent == null || !Evaluated.Add(state))
                    continue;

                Tile owning = ReadOwningTile(state.CameraComponent) ?? FindTileContaining(state.Transform);
                if (!ReferenceEquals(owning, _mainEntranceTile))
                    continue;

                state.SecurityExclusionReason = ExclusionReason;
                state.ClearActive();
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] Camera '{state.Label}' sits in the main-entrance tile '{owning.name}'; excluded from the security rotation.");
            }
        }

        private static bool TryResolveMainEntranceTile()
        {
            if (_resolveAttempts >= MaxResolveAttempts)
            {
                // Give up quietly for the round rather than rescanning every rotation.
                _resolved = true;
                return true;
            }
            _resolveAttempts++;

            IReadOnlyList<Tile> tiles = TryGetAllTiles();
            if (tiles == null || tiles.Count == 0)
                return false;

            EntranceTeleport[] teleports = Object.FindObjectsByType<EntranceTeleport>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < teleports.Length; i++)
            {
                EntranceTeleport et = teleports[i];
                if (et == null || et.isEntranceToBuilding || et.entranceId != 0 || et.entrancePoint == null)
                    continue;

                Vector3 pad = et.entrancePoint.position;
                Tile closest = null;
                float closestSqr = float.PositiveInfinity;
                for (int j = 0; j < tiles.Count; j++)
                {
                    Tile t = tiles[j];
                    if (t == null || t.Placement == null) continue;
                    float sqr = (t.Placement.Bounds.center - pad).sqrMagnitude;
                    if (sqr < closestSqr)
                    {
                        closestSqr = sqr;
                        closest = t;
                    }
                }

                if (closest != null && closest.Placement.Bounds.Contains(pad))
                {
                    _mainEntranceTile = closest;
                    _resolved = true;
                    return true;
                }
            }

            return false;
        }

        private static Tile FindTileContaining(Transform transform)
        {
            if (transform == null) return null;
            IReadOnlyList<Tile> tiles = TryGetAllTiles();
            if (tiles == null) return null;
            Vector3 position = transform.position;
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile t = tiles[i];
                if (t != null && t.Placement != null && t.Placement.Bounds.Contains(position))
                    return t;
            }
            return null;
        }

        private static Tile ReadOwningTile(Component camera)
        {
            try
            {
                PropertyInfo prop = camera.GetType().GetProperty("OwningTile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return prop != null ? prop.GetValue(camera) as Tile : null;
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<Tile> TryGetAllTiles()
        {
            try
            {
                RoundManager round = RoundManager.Instance;
                if (round == null || round.dungeonGenerator == null) return null;
                Dungeon dungeon = round.dungeonGenerator.Generator?.CurrentDungeon;
                return dungeon != null ? dungeon.AllTiles : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
