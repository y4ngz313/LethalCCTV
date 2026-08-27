using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Facility.Cameras.Placement;
using Y4NGZCompany.ShipSystems.Surveillance;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    public sealed partial class DungeonCameraSpawner
    {
        private const float EntranceTeleportTimeoutSeconds = 1.0f;

        private readonly LethalCCTVConfig _config;
        private readonly TileExclusionFilter _filter;
        private readonly MonoBehaviour _coroutineHost;
        private readonly List<CCTVCamera> _spawnedCameras = new List<CCTVCamera>();

        private RoundManager _subscribedTo;
        private bool _boundsProbeDone;
        private Coroutine _pendingSpawnCoroutine;

        // MAIN entrance (entranceId == 0) captured during BuildEntranceTileSets:
        // the promotion pass guarantees this tile a camera and the mount pass
        // aims that camera at the entrance door itself.
        private Tile _mainEntranceTile;
        private Vector3 _mainEntrancePadWorld;
        private bool _hasMainEntrancePad;

        // Vanilla apparatus resolved after DunGen finishes. Its room receives a
        // post-cap promotion and its camera aims at the apparatus instead of a generic
        // doorway, making the guarantee useful rather than merely co-located.
        private Tile _apparatusTile;
        private Vector3 _apparatusWorldPosition;
        private bool _hasApparatusTarget;

        private static readonly string[] s_roomNameHints = new[]
        {
            "Room", "Bedroom", "Study", "Cellar", "Storage", "Kitchen",
            "Dining", "Library", "Ballroom", "Office", "Bathroom",
        };

        public DungeonCameraSpawner(LethalCCTVConfig config, TileExclusionFilter filter, MonoBehaviour coroutineHost)
        {
            _config = config;
            _filter = filter;
            _coroutineHost = coroutineHost;
        }

        public IReadOnlyList<CCTVCamera> SpawnedCameras => _spawnedCameras;
        public event Action<IReadOnlyList<CCTVCamera>> CamerasReady;

        public void Subscribe(RoundManager roundManager)
        {
            if (roundManager == null) return;
            if (_subscribedTo == roundManager) return;
            roundManager.OnFinishedGeneratingDungeon += OnDungeonFinished;
            _subscribedTo = roundManager;
        }

        public void Unsubscribe()
        {
            if (_subscribedTo == null) return;
            if (_subscribedTo)
            {
                _subscribedTo.OnFinishedGeneratingDungeon -= OnDungeonFinished;
            }
            _subscribedTo = null;
        }

        private void OnDungeonFinished()
        {
            // The spawn pipeline is now deferred behind a coroutine that waits for the
            // dungeon-side EntranceTeleport scripts to spawn (they appear after this
            // event fires; see 01_phase1_filter_rewrite.md for the timing analysis).
            // If a previous coroutine is still running (back-to-back dungeon loads
            // before the previous coroutine's wake/timeout), stop it — the new event
            // supersedes it.
            if (_pendingSpawnCoroutine != null && _coroutineHost != null)
            {
                _coroutineHost.StopCoroutine(_pendingSpawnCoroutine);
                _pendingSpawnCoroutine = null;
            }
            if (_coroutineHost == null)
            {
                // Defensive fallback for tests or callers that didn't supply a host.
                // Run the pipeline inline; entrance attribution may produce an empty
                // set if dungeon-side scripts haven't spawned, which is the same
                // degraded behaviour as the coroutine timing out.
                RunSpawnPipelineSafe();
                return;
            }
            _pendingSpawnCoroutine = _coroutineHost.StartCoroutine(SpawnPipelineCoroutine());
        }

        private IEnumerator SpawnPipelineCoroutine()
        {
            float startTime = Time.realtimeSinceStartup;
            while (!IsEntranceTeleportReady() &&
                   (Time.realtimeSinceStartup - startTime) < EntranceTeleportTimeoutSeconds)
            {
                yield return null;
            }
            _pendingSpawnCoroutine = null;
            RunSpawnPipelineSafe();
        }

        // True when at least one EntranceTeleport with isEntranceToBuilding == false
        // exists in the scene. The dungeon-side scripts are the ones we need to
        // attribute to tiles via entrancePoint position; the ship-side scripts
        // (isEntranceToBuilding == true) are visible from chainload and don't help
        // with tile attribution.
        private static bool IsEntranceTeleportReady()
        {
            EntranceTeleport[] all = UnityEngine.Object.FindObjectsByType<EntranceTeleport>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].isEntranceToBuilding) return true;
            }
            return false;
        }

        private void RunSpawnPipelineSafe()
        {
            try
            {
                RunSpawnPipeline();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogError($"[LethalCCTV] DungeonCameraSpawner threw: {ex}");
            }
            finally
            {
                AuthoredPlacementRoundReport.NotifyCameraPassComplete(
                    RoundManager.Instance != null && RoundManager.Instance.IsServer);
            }
        }

        private static Dungeon ResolveDungeonOrNull()
        {
            RoundManager rm = RoundManager.Instance;
            RuntimeDungeon rtd = rm != null ? rm.dungeonGenerator : null;
            DungeonGenerator gen = rtd != null ? rtd.Generator : null;
            return gen != null ? gen.CurrentDungeon : null;
        }

        private void ClearPriorRunIfAny()
        {
            // Interior cameras are usually scene-unload destroyed before the
            // next roll, so this loop is defensive: explicitly destroy any
            // spawned camera object still alive before resetting the list.
            for (int i = 0; i < _spawnedCameras.Count; i++)
            {
                CCTVCamera camera = _spawnedCameras[i];
                if (camera != null)
                {
                    UnityEngine.Object.Destroy(camera.gameObject);
                }
            }
            _spawnedCameras.Clear();
        }

        // 0.0.36 — placement constants live as hardcoded values (not config)
        // so they are identical on host and client by construction, satisfying
        // the "any knob that affects camera count/selection must be synced, or
        // hardcoded" rule. Redundancy suppression affects the kept-camera set.
        //
        // A new candidate is redundant with an already-placed camera iff it is
        // physically close, on (roughly) the same floor, and points in a very
        // similar direction — AND they cover the same room (same tile/component)
        // or are close enough that they would plainly duplicate the view. Two
        // close cameras that look into DIFFERENT rooms/components (e.g. through
        // a shared wall) are NOT redundant and both survive.
        private const float RedundancyDistM = 6.0f;
        private const float RedundancyForwardDot = 0.86f;   // ~31° cone
        private const float RedundancySameFloorYM = 4.0f;
        private const float RedundancyCloseAnywayM = 3.0f;
        // Hard spacing floor, facing-independent: two cameras this close on
        // the same floor read as clutter even when they cover different
        // angles. Promoted picks (entrance/mainframe/objective) bypass it.
        private const float MinCameraSeparationM = 5.0f;

    }
}
