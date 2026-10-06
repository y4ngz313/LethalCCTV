using System;
using System.Collections.Generic;
using System.Text;
using DunGen;
using UnityEngine;
using Y4NGZCompany.Facility.Cameras.Placement;
using Y4NGZCompany.ShipSystems.Surveillance;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Y4NGZCompany.Bootstrap;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Y4NGZCompany.Facility.Cameras
{
    public sealed partial class DungeonCameraSpawner
    {
        private readonly LethalCCTVConfig _config;
        private readonly TileExclusionFilter _filter;
        private readonly List<CCTVCamera> _spawnedCameras = new List<CCTVCamera>();

        private RoundManager _subscribedTo;
        private bool _boundsProbeDone;

        // #1283 — per-pass inputs read from the dungeon's SpawnSyncedObject markers
        // (CollectSpawnMarkerInputs), reused across passes. The two prefab lists are scratch
        // space for one marker prefab's components at a time.
        private static readonly List<DungeonEntrancePad> s_markerPads = new List<DungeonEntrancePad>(4);
        private static readonly List<Vector3> s_markerApparatus = new List<Vector3>(1);
        private static readonly List<EntranceTeleport> s_prefabTeleports = new List<EntranceTeleport>(2);
        private static readonly List<LungProp> s_prefabApparatus = new List<LungProp>(1);

        private static bool s_spawnPipelineRunning;

        /// <summary>True while the placement pass runs. #1283: the pass completes inside the
        /// dungeon-finished event, so only code it calls (the CamerasReady handlers) can see
        /// true. Anything that snapshots the live CCTVCamera set to derive a free camera index
        /// (see InteriorSupportCameraInjector) consults this to avoid racing the pipeline.</summary>
        public static bool SpawnPipelineInProgress => s_spawnPipelineRunning;

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

        public DungeonCameraSpawner(LethalCCTVConfig config, TileExclusionFilter filter)
        {
            _config = config;
            _filter = filter;
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

        // #1283. Raised inside vanilla RoundManager.FinishGeneratingLevel, after the NavMesh
        // bake and before FinishedGeneratingLevelServerRpc: under the loading screen and before
        // "Players finished generating the new floor". The whole pass (placement, props,
        // CamerasReady and its inline follow-ups) completes inside this call, so its cost lands
        // in the loading freeze instead of a hitch after it. Nothing may escape: an exception
        // here would skip the ServerRpc and hang the level load for every player.
        private void OnDungeonFinished()
        {
            try
            {
                RunSpawnPipelineSafe();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogError($"[LethalCCTV] DungeonCameraSpawner dungeon-finished handler threw: {ex}");
            }
        }

        // Runs the pass to completion. An exception inside it is logged once and ends the pass
        // without CamerasReady; the in-progress flag, the perf line and the authored placement
        // report complete either way.
        private void RunSpawnPipelineSafe()
        {
            // #1271: the pass raycasts inside this frame, before a physics step would sync the
            // transforms of objects moved or spawned since the last one.
            Physics.SyncTransforms();
            Stopwatch total = Stopwatch.StartNew();
            s_spawnPipelineRunning = true;
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
                s_spawnPipelineRunning = false;
            }

            // #716 — one Info line per round. The total includes the CamerasReady follow-ups
            // that run inside the pass.
            total.Stop();
            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV.Perf] camera-spawn total={total.Elapsed.TotalMilliseconds:0.00}ms " +
                $"cameras={_spawnedCameras.Count} mode=load");
            AuthoredPlacementRoundReport.NotifyCameraPassComplete(
                RoundManager.Instance != null && RoundManager.Instance.IsServer);
        }

        // #1283 — a dungeon-side EntranceTeleport's pad where its spawned prop will hold it.
        // IsMain is entranceId == 0; any other id is a fire exit.
        private readonly struct DungeonEntrancePad
        {
            internal readonly bool IsMain;
            internal readonly Vector3 PadWorld;

            internal DungeonEntrancePad(bool isMain, Vector3 padWorld)
            {
                IsMain = isMain;
                PadWorld = padWorld;
            }
        }

        // #1283. The dungeon-side main entrance (EntranceTeleportA), the fire exits
        // (EntranceTeleportB) and the apparatus are props that RoundManager.SpawnSyncedProps
        // instantiates from the active SpawnSyncedObject markers after this event (clients
        // network-spawn them later still). The markers are DunGen tile children generated from
        // the seed, so the host and every client read the same set here. Each child's world
        // position is where vanilla's Instantiate(spawnPrefab, marker.position, marker.rotation,
        // mapPropsContainer.transform) puts it. The serialized entranceId is enough: SetExitIDs
        // later renumbers fire exits only (1 -> 1..n), and placement only tells 0 from non-zero.
        private static void CollectSpawnMarkerInputs(List<DungeonEntrancePad> pads, List<Vector3> apparatusPositions)
        {
            pads.Clear();
            apparatusPositions.Clear();
            // Vanilla assigns mapPropsContainer in GenerateNewLevelClientRpc, before generation.
            RoundManager round = RoundManager.Instance;
            Vector3 parentScale = round != null && round.mapPropsContainer != null
                ? round.mapPropsContainer.transform.lossyScale
                : Vector3.one;
            SpawnSyncedObject[] markers = UnityEngine.Object.FindObjectsByType<SpawnSyncedObject>(
                FindObjectsSortMode.None);
            for (int i = 0; i < markers.Length; i++)
            {
                GameObject prefab = markers[i].spawnPrefab;
                if (prefab == null) continue;
                Transform prefabRoot = prefab.transform;
                Transform marker = markers[i].transform;
                Matrix4x4 spawned = Matrix4x4.TRS(
                    marker.position, marker.rotation, Vector3.Scale(parentScale, prefabRoot.localScale));

                prefab.GetComponentsInChildren(true, s_prefabTeleports);
                for (int t = 0; t < s_prefabTeleports.Count; t++)
                {
                    EntranceTeleport teleport = s_prefabTeleports[t];
                    if (teleport.isEntranceToBuilding || teleport.entrancePoint == null) continue;
                    pads.Add(new DungeonEntrancePad(
                        teleport.entranceId == 0,
                        spawned.MultiplyPoint3x4(prefabRoot.InverseTransformPoint(teleport.entrancePoint.position))));
                }

                prefab.GetComponentsInChildren(true, s_prefabApparatus);
                for (int a = 0; a < s_prefabApparatus.Count; a++)
                {
                    apparatusPositions.Add(spawned.MultiplyPoint3x4(
                        prefabRoot.InverseTransformPoint(s_prefabApparatus[a].transform.position)));
                }
            }
            s_prefabTeleports.Clear();
            s_prefabApparatus.Clear();
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
            CameraPlacementSafety.Clear();
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
