using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using LethalNetworkAPI;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Cameras;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [Serializable]
    public sealed class CctvDeviceCommandMessage
    {
        public bool IsResult;
        public string RequestId, Code, Command, Status;
        public ulong TargetId, ClientId;
        public int CameraIndex, MapSeed;
        public Vector3 CameraPosition, Direction;
    }

    /// <summary>Local selection; host validation and vanilla device execution.</summary>
    internal static class CctvDeviceCommands
    {
        private static LNetworkMessage<CctvDeviceCommandMessage> _channel;
        private static readonly Dictionary<ulong, float> NextRequest = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, string> LastRequest = new Dictionary<ulong, string>();

        internal static void Initialize()
        {
            if (_channel != null) return;
            try
            {
                _channel = LNetworkMessage<CctvDeviceCommandMessage>.Connect("y4ngz_cctv_device_v1",
                    onServerReceived: ReceiveRequest, onClientReceived: ReceiveResult);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Coded device channel unavailable: " + ex.Message);
            }
        }

        internal static void Reset()
        {
            NextRequest.Clear();
            LastRequest.Clear();
            CctvDeviceCommandLine.Close(clearStatus: true);
        }

        internal static void Shutdown()
        {
            Reset();
            _channel?.ClearSubscriptions();
            _channel = null;
            CctvDeviceCommandLine.Shutdown();
        }

        internal static Turret TurretFor(TerminalAccessibleObject device)
        {
            if (device == null || device.isBigDoor) return null;
            return device.GetComponent<Turret>() ?? device.GetComponentInParent<Turret>() ?? device.GetComponentInChildren<Turret>();
        }

        internal static bool TryDescribe(TerminalAccessibleObject device, out string label)
        {
            label = null;
            if (device == null || !device.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(device.objectCode)) return false;
            bool hasCodeDisplay = false;
            if (device.codeMaterials != null)
                foreach (MeshRenderer renderer in device.codeMaterials)
                    if (renderer != null && renderer.gameObject.activeInHierarchy) { hasCodeDisplay = true; break; }
            if (!hasCodeDisplay) return false;
            if (!device.isBigDoor && TurretFor(device) == null) return false;
            label = (device.isBigDoor ? "DOOR " : "TURRET ") + device.objectCode.ToUpperInvariant();
            return true;
        }

        internal static TerminalAccessibleObject Hover(Camera camera)
            => camera == null ? null : RayTarget(camera.transform.position, camera.transform.forward);

        private static TerminalAccessibleObject RayTarget(Vector3 origin, Vector3 direction)
        {
            if (!CctvSquadPing.TryRayHit(origin, direction, out RaycastHit hit) || hit.distance > 60f) return null;
            TerminalAccessibleObject device = hit.collider.GetComponentInParent<TerminalAccessibleObject>();
            if (device == null)
            {
                Turret turret = hit.collider.GetComponentInParent<Turret>();
                if (turret != null) device = turret.GetComponent<TerminalAccessibleObject>() ?? turret.GetComponentInChildren<TerminalAccessibleObject>();
            }
            return TryDescribe(device, out _) ? device : null;
        }

        internal static string Submit(TerminalAccessibleObject target, CCTVCamera camera, string command)
        {
            if (_channel == null || target == null || target.NetworkObject == null || !target.NetworkObject.IsSpawned || camera?.Cam == null)
                return null;
            var request = new CctvDeviceCommandMessage
            {
                RequestId = Guid.NewGuid().ToString("N"), TargetId = target.NetworkObjectId,
                Code = target.objectCode, Command = command, CameraIndex = camera.CameraIndex,
                CameraPosition = camera.Cam.transform.position, Direction = camera.Cam.transform.forward,
                MapSeed = StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : 0
            };
            // Set the pending identity before send: a host may receive its own result synchronously.
            CctvDeviceCommandLine.SetPending(request.RequestId, camera);
            _channel.SendServer(request);
            return request.RequestId;
        }

        private static void ReceiveRequest(CctvDeviceCommandMessage request, ulong sender)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer || request == null || request.IsResult) return;
            if (string.IsNullOrEmpty(request.RequestId) || request.RequestId.Length > 40) return;
            if (LastRequest.TryGetValue(sender, out string last) && last == request.RequestId) return;
            string status;
            if (NextRequest.TryGetValue(sender, out float next) && Time.unscaledTime < next) status = "PLEASE WAIT";
            else
            {
                NextRequest[sender] = Time.unscaledTime + 0.4f;
                LastRequest[sender] = request.RequestId;
                try { status = Execute(request, sender); }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Coded device command failed: " + ex.Message);
                    status = "COMMAND FAILED";
                }
            }
            _channel?.SendClients(new CctvDeviceCommandMessage { IsResult = true, ClientId = sender, RequestId = request.RequestId, Status = status });
        }

        private static string Execute(CctvDeviceCommandMessage request, ulong sender)
        {
            StartOfRound round = StartOfRound.Instance;
            if (round == null || round.inShipPhase || round.shipIsLeaving || request.MapSeed != round.randomMapSeed) return "FEED UNAVAILABLE";
            PlayerControllerB player = null;
            foreach (PlayerControllerB candidate in round.allPlayerScripts)
                if (candidate != null && candidate.actualClientId == sender) { player = candidate; break; }
            if (player == null || player.isPlayerDead || !player.isPlayerControlled ||
                CCTVMonitorFeedSync.OperatorId != (int)player.playerClientId || !CCTVMonitorFeedSync.IsFeedCctv)
                return "OPERATOR REQUIRED";
            Transform station = CCTVOperatorStation.OperatorPoseAnchor;
            if (station == null || Vector3.Distance(player.transform.position, station.position) > 5f) return "OPERATOR OUT OF RANGE";
            if (!CCTVShipSystemsBridge.CanUseRemoteOperations(out string blocked)) return blocked;
            if (SurveillanceBootstrap.Config?.AllowTargetScanning?.Value == false) return "TARGET COMMANDS DISABLED BY HOST";
            if (!CCTVShipSystemsBridge.IsShipPowerOnline()) return "NO SHIP POWER";
            CCTVCamera camera = null;
            if (QuadCameraAssignment.AllAssignedCameras != null)
                foreach (CCTVCamera candidate in QuadCameraAssignment.AllAssignedCameras)
                    if (candidate != null && candidate.CameraIndex == request.CameraIndex) { camera = candidate; break; }
            if (camera?.Cam == null || camera.IsSecurityBroken ||
                !Finite(request.CameraPosition) || !Finite(request.Direction) ||
                Vector3.Distance(camera.Cam.transform.position, request.CameraPosition) > 0.5f ||
                request.Direction.sqrMagnitude < 0.9f || request.Direction.sqrMagnitude > 1.1f) return "FEED LOST";
            Vector3 direction = request.Direction.normalized;
            float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (camera.AllowsOperatorRotation)
            {
                if (Mathf.Abs(Mathf.DeltaAngle(camera.BaseYawDeg, yaw)) > (SurveillanceBootstrap.Config?.YawClampDeg ?? 80f) + 1f ||
                    pitch < -6f || pitch > (SurveillanceBootstrap.Config?.PitchClampDeg ?? 80f) + 1f) return "OUTSIDE CAMERA VIEW";
            }
            else if (Vector3.Angle(camera.Cam.transform.forward, direction) > 1f) return "OUTSIDE CAMERA VIEW";
            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(request.TargetId, out NetworkObject obj)) return "TARGET LOST";
            TerminalAccessibleObject target = RayTarget(camera.Cam.transform.position, direction);
            if (!TryDescribe(target, out _) || target.NetworkObject != obj || target.objectCode != request.Code)
                return "TARGET NOT VISIBLE";
            if (request.Command == null || request.Command.Length > 24) return "INVALID COMMAND";
            Turret turret = TurretFor(target);
            CctvDeviceAction action = CctvDeviceCommandPolicy.Decide(request.Command, target.isBigDoor,
                target.isDoorOpen, target.isPoweredOn, target.inCooldown, turret != null && turret.turretActive);
            if (action == CctvDeviceAction.Invalid) return target.isBigDoor ? "USE UNLOCK" : "USE DISABLE";
            if (action == CctvDeviceAction.Unpowered) return "DOOR UNPOWERED";
            if (action == CctvDeviceAction.AlreadyOpen) return "ALREADY OPEN";
            if (action == CctvDeviceAction.Cooldown) return "DEVICE IN COOLDOWN";
            if (target.isBigDoor)
            {
                if (!CCTVShipSystemsBridge.TrySpendRemoteOperationPower()) return "INSUFFICIENT POWER";
                target.SetDoorLocalClient(true);
                return target.isDoorOpen ? "DOOR OPEN" : "DOOR DID NOT OPEN";
            }
            if (turret == null || target.codeAccessCooldownTimer <= 0f) return "DEVICE UNAVAILABLE";
            if (!CCTVShipSystemsBridge.TrySpendRemoteOperationPower()) return "INSUFFICIENT POWER";
            target.CallFunctionFromTerminal();
            return !turret.turretActive && target.inCooldown ? "TURRET TEMPORARILY DISABLED" : "DEVICE DID NOT RESPOND";
        }

        private static bool Finite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) &&
            !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private static void ReceiveResult(CctvDeviceCommandMessage result)
        {
            if (result == null || !result.IsResult || NetworkManager.Singleton == null ||
                result.ClientId != NetworkManager.Singleton.LocalClientId) return;
            CctvDeviceCommandLine.ReceiveResult(result.RequestId, result.Status);
        }
    }
}
