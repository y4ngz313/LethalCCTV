using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using LethalNetworkAPI;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.ShipSystems.Surveillance.OutlineEffect;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [Serializable]
    public sealed class CctvSquadPingMessage
    {
        public bool Result, HasNetworkRoot;
        public int CameraIndex, MapSeed, Channel;
        public Vector3 Origin, Direction, RootPosition;
        public ulong NetworkId;
        public string RootPath;
        public double ExpiresAt;
    }

    /// <summary>Host-selected, short-lived silhouette pings independent of Upgrades ownership.</summary>
    internal static class CctvSquadPing
    {
        internal const float Lifetime = 4f;
        private static LNetworkMessage<CctvSquadPingMessage> _channel;
        private static readonly Dictionary<ulong, float> NextRequest = new Dictionary<ulong, float>();
        private static readonly Dictionary<GameObject, double> Active = new Dictionary<GameObject, double>();
        private static readonly List<GameObject> Expired = new List<GameObject>();
        internal static bool HasActivePings => Active.Count > 0;

        internal static void Initialize()
        {
            if (_channel == null)
                _channel = LNetworkMessage<CctvSquadPingMessage>.Connect("y4ngz_cctv_squad_ping_v1",
                    onServerReceived: ReceiveRequest, onClientReceived: ReceiveResult);
        }

        internal static void Shutdown()
        {
            Clear();
            _channel?.ClearSubscriptions();
            _channel = null;
        }

        internal static void Clear()
        {
            foreach (GameObject root in Active.Keys) CctvOutlineEffectBridge.ClearSquadPing(root);
            Active.Clear();
            NextRequest.Clear();
            CctvOutlineEffectBridge.ClearSquadCameras();
        }

        internal static bool Request(CCTVCamera camera)
        {
            if (_channel == null || camera?.Cam == null || StartOfRound.Instance == null ||
                NetworkManager.Singleton == null || !CCTVMonitorFeedSync.IsLocalOperator) return false;
            if (!TryRayHit(camera.Cam.transform.position, camera.Cam.transform.forward, out RaycastHit hit) ||
                ResolveRoot(hit.collider) == null) return false;
            _channel.SendServer(new CctvSquadPingMessage
            {
                CameraIndex = camera.CameraIndex, MapSeed = StartOfRound.Instance.randomMapSeed,
                Origin = camera.Cam.transform.position, Direction = camera.Cam.transform.forward
            });
            return true;
        }

        internal static bool TryRayHit(Vector3 origin, Vector3 direction, out RaycastHit hit)
        {
            RaycastHit[] hits = Physics.RaycastAll(origin + direction * 0.08f, direction, 80f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit candidate in hits)
            {
                Collider collider = candidate.collider;
                if (collider == null) continue;
                // Ignore broad room/hazard volumes, but retain actual target hitboxes.
                if (collider.isTrigger && collider.GetComponentInParent<EnemyAI>() == null &&
                    collider.GetComponentInParent<GrabbableObject>() == null &&
                    collider.GetComponentInParent<PlayerControllerB>() == null &&
                    collider.GetComponentInParent<DoorLock>() == null &&
                    collider.GetComponentInParent<TerminalAccessibleObject>() == null &&
                    collider.GetComponentInParent<Landmine>() == null &&
                    collider.GetComponentInParent<EntranceTeleport>() == null &&
                    !CctvCommandTargetBridge.TryDescribeTarget(collider.transform, out _)) continue;
                hit = candidate;
                return true; // An opaque first hit blocks targets behind it.
            }
            hit = default;
            return false;
        }

        private static GameObject ResolveRoot(Collider collider)
        {
            if (collider == null) return null;
            Component target = collider.GetComponentInParent<EnemyAI>();
            if (target == null) target = collider.GetComponentInParent<PlayerControllerB>();
            if (target == null) target = collider.GetComponentInParent<GrabbableObject>();
            if (target == null) target = collider.GetComponentInParent<DoorLock>();
            if (target == null) target = collider.GetComponentInParent<TerminalAccessibleObject>();
            if (target == null) target = collider.GetComponentInParent<EntranceTeleport>();
            if (target != null) return target.gameObject;
            // Limit arbitrary scenery to the struck renderable object. Never
            // climb to the dungeon/tile root and outline an entire room.
            Renderer renderer = collider.GetComponent<Renderer>() ?? collider.GetComponentInChildren<Renderer>();
            return renderer != null ? renderer.gameObject : null;
        }

        private static void ReceiveRequest(CctvSquadPingMessage message, ulong sender)
        {
            NetworkManager network = NetworkManager.Singleton;
            StartOfRound round = StartOfRound.Instance;
            if (network == null || !network.IsServer || round == null || message == null || message.Result ||
                round.inShipPhase || round.shipIsLeaving || message.MapSeed != round.randomMapSeed ||
                SurveillanceBootstrap.Config?.AllowCameraPings.Value == false ||
                !CCTVMonitorFeedSync.IsFeedCctv || !CCTVShipSystemsBridge.IsShipPowerOnline()) return;
            PlayerControllerB player = null;
            foreach (PlayerControllerB candidate in round.allPlayerScripts)
                if (candidate != null && candidate.actualClientId == sender) { player = candidate; break; }
            if (player == null || player.isPlayerDead || !player.isPlayerControlled ||
                CCTVMonitorFeedSync.OperatorId != (int)player.playerClientId ||
                CCTVOperatorStation.OperatorPoseAnchor == null ||
                Vector3.Distance(player.transform.position, CCTVOperatorStation.OperatorPoseAnchor.position) > 5f) return;
            if (NextRequest.TryGetValue(sender, out float next) && Time.unscaledTime < next) return;
            NextRequest[sender] = Time.unscaledTime + 0.15f;
            CCTVCamera camera = null;
            if (QuadCameraAssignment.AllAssignedCameras != null)
                foreach (CCTVCamera candidate in QuadCameraAssignment.AllAssignedCameras)
                    if (candidate != null && candidate.CameraIndex == message.CameraIndex) { camera = candidate; break; }
            if (camera?.Cam == null || camera.IsSecurityBroken || !Finite(message.Origin) || !Finite(message.Direction) ||
                Vector3.Distance(message.Origin, camera.Cam.transform.position) > 0.5f ||
                message.Direction.sqrMagnitude < 0.9f || message.Direction.sqrMagnitude > 1.1f) return;
            Vector3 direction = message.Direction.normalized;
            float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (camera.AllowsOperatorRotation)
            {
                if (Mathf.Abs(Mathf.DeltaAngle(camera.BaseYawDeg, yaw)) > (SurveillanceBootstrap.Config?.YawClampDeg ?? 80f) + 1f ||
                    pitch < -6f || pitch > (SurveillanceBootstrap.Config?.PitchClampDeg ?? 80f) + 1f) return;
            }
            else if (Vector3.Angle(camera.Cam.transform.forward, direction) > 1f) return;
            if (!TryRayHit(camera.Cam.transform.position, direction, out RaycastHit hit)) return;
            GameObject root = ResolveRoot(hit.collider);
            if (root == null) return;
            NetworkObject owner = root.GetComponentInParent<NetworkObject>();
            bool networked = owner != null && owner.IsSpawned;
            _channel.SendClients(new CctvSquadPingMessage
            {
                Result = true, MapSeed = round.randomMapSeed, HasNetworkRoot = networked,
                NetworkId = networked ? owner.NetworkObjectId : 0,
                RootPath = CctvPingTargetPath.Capture(root.transform, networked ? owner.transform : null),
                RootPosition = root.transform.position,
                Channel = root.GetComponent<EnemyAI>() != null ? 0 : root.GetComponent<PlayerControllerB>() != null ? 2 : 1,
                ExpiresAt = network.ServerTime.Time + Lifetime
            });
        }

        private static void ReceiveResult(CctvSquadPingMessage message)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || message == null || !message.Result || StartOfRound.Instance == null ||
                message.MapSeed != StartOfRound.Instance.randomMapSeed || message.RootPath == null ||
                message.RootPath.Length > 2048) return;
            float remaining = Mathf.Min(Lifetime, (float)(message.ExpiresAt - network.ServerTime.Time));
            if (remaining <= 0f || float.IsNaN(remaining)) return;
            GameObject root = null;
            if (message.HasNetworkRoot)
            {
                if (!network.SpawnManager.SpawnedObjects.TryGetValue(message.NetworkId, out NetworkObject owner)) return;
                root = CctvPingTargetPath.Resolve(message.RootPath, owner.transform, message.RootPosition)?.gameObject;
            }
            else
            {
                if (!Finite(message.RootPosition)) return;
                root = CctvPingTargetPath.Resolve(message.RootPath, null, message.RootPosition)?.gameObject;
            }
            if (root == null) return;
            Camera camera = GameNetworkManager.Instance?.localPlayerController?.gameplayCamera;
            if (camera == null) return;
            if (!Active.ContainsKey(root) && Active.Count >= 24) return;
            Active[root] = network.ServerTime.Time + remaining;
            if (!CctvOutlineEffectBridge.ShowSquadPing(camera, root,
                    (CctvOutlineChannel)Mathf.Clamp(message.Channel, 0, 2), remaining))
            {
                CctvOutlineEffectBridge.ClearSquadPing(root);
                Active.Remove(root);
            }
            Tick();
            if (Active.Count == 0) CctvOutlineEffectBridge.ClearSquadCameras();
        }

        internal static void Tick()
        {
            if (Active.Count == 0) return;
            double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : double.MaxValue;
            Expired.Clear();
            foreach (var pair in Active) if (pair.Key == null || now >= pair.Value) Expired.Add(pair.Key);
            foreach (GameObject root in Expired) { CctvOutlineEffectBridge.ClearSquadPing(root); Active.Remove(root); }
            if (Active.Count == 0) { CctvOutlineEffectBridge.ClearSquadCameras(); return; }
            CctvOutlineEffectBridge.PrepareSquadCamera(GameNetworkManager.Instance?.localPlayerController?.gameplayCamera);
            CctvOutlineEffectBridge.PrepareSquadCamera(QuadCameraAssignment.GetBoundCamera(0)?.Cam);
        }

        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
