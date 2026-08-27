using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Cameras;
namespace Y4NGZCompany.Facility.Security
{
    // Server-authoritative per-camera shutdown. Cameras are plain MonoBehaviours
    // spawned deterministically from the shared map seed, so CameraIndex is the
    // stable identifier on every client. Periodic snapshots also converge late
    // joiners and cameras whose visuals register after the message arrives.
    internal static class CctvCameraShutdownSync
    {
        private const string SnapshotMessage = "Y4NGZCompany.CctvCameraShutdown.v2";
        private const string BreakRequestMessage = "Y4NGZCompany.CctvCameraBreakRequest.v1";
        private const float SnapshotIntervalSeconds = 5f;

        private static readonly HashSet<int> DisabledIndices = new HashSet<int>();
        private static readonly HashSet<int> BrokenIndices = new HashSet<int>();
        private static readonly HashSet<int> RejectionLoggedIndices = new HashSet<int>();
        private static NetworkManager _registeredNetworkManager;
        private static float _nextSnapshotAt;

        internal static bool IsCameraDisabled(int cameraIndex)
        {
            return DisabledIndices.Contains(cameraIndex) || BrokenIndices.Contains(cameraIndex);
        }

        internal static bool TryDisableCamera(int cameraIndex)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return false;

            CctvSecurityCameraState state = FindStateByIndex(cameraIndex);
            if (state == null)
                return false;

            if (DisabledIndices.Add(cameraIndex))
            {
                ApplyShutdownStates();
                SendSnapshot();
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] Camera {state.Label} remotely disabled for the round.");
            }

            return true;
        }

        internal static bool RequestPhysicalBreak(int cameraIndex)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.CustomMessagingManager == null)
                return RegisterPhysicalBreak(cameraIndex);

            EnsureNetworkRegistration(nm);
            if (nm.IsServer)
                return RegisterPhysicalBreak(cameraIndex);

            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(cameraIndex);
                nm.CustomMessagingManager.SendNamedMessage(
                    BreakRequestMessage,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
            return true;
        }

        internal static void ResetRound()
        {
            DisabledIndices.Clear();
            BrokenIndices.Clear();
            RejectionLoggedIndices.Clear();
            // The per-round hitboxes die with the interior; drop their index map too so a
            // recycled CameraIndex cannot resolve to a destroyed breakable.
            CctvBreakableCamera.ResetRound();
        }

        internal static void Tick()
        {
            ApplyShutdownStates();

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || nm.CustomMessagingManager == null)
                return;

            EnsureNetworkRegistration(nm);
            if (nm.IsServer
                && (DisabledIndices.Count > 0 || BrokenIndices.Count > 0)
                && Time.realtimeSinceStartup >= _nextSnapshotAt)
            {
                _nextSnapshotAt = Time.realtimeSinceStartup + SnapshotIntervalSeconds;
                SendSnapshot();
            }
        }

        private static void EnsureNetworkRegistration(NetworkManager nm)
        {
            if (ReferenceEquals(nm, _registeredNetworkManager))
                return;

            if (_registeredNetworkManager?.CustomMessagingManager != null)
            {
                try { _registeredNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(SnapshotMessage); } catch { }
                try { _registeredNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(BreakRequestMessage); } catch { }
            }

            nm.CustomMessagingManager.RegisterNamedMessageHandler(SnapshotMessage, OnSnapshotMessage);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(BreakRequestMessage, OnBreakRequestMessage);
            _registeredNetworkManager = nm;
            _nextSnapshotAt = 0f;
        }

        private static bool RegisterPhysicalBreak(int cameraIndex)
        {
            CctvSecurityCameraState state = FindStateByIndex(cameraIndex);
            if (state == null || !CctvSecurityConfig.Current.BreakableCamerasEnabled)
            {
                // Reachable from the network handler and therefore client-driven: a
                // client that keeps swinging would otherwise spam this every hit.
                if (RejectionLoggedIndices.Add(cameraIndex))
                {
                    CctvModuleConfig.Log?.LogWarning(
                        $"[MoonContracts.CctvSecurity] Physical break rejected index={cameraIndex}; reason=" +
                        (state == null ? "no registered camera state" : "breakable cameras disabled") + ".");
                }
                return false;
            }

            if (BrokenIndices.Add(cameraIndex))
            {
                ApplyShutdownStates();
                SendSnapshot();
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] Replicated physical break camera={state.Label} index={cameraIndex}.");
            }
            return true;
        }

        private static void ApplyShutdownStates()
        {
            if (DisabledIndices.Count == 0 && BrokenIndices.Count == 0)
                return;

            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            for (int i = 0; i < cameras.Count; i++)
            {
                CctvSecurityCameraState state = cameras[i];
                if (state == null)
                    continue;

                if (DisabledIndices.Contains(state.CameraIndex))
                    ApplyRemoteDisable(state);
                if (BrokenIndices.Contains(state.CameraIndex))
                    ApplyPhysicalBreak(state);
            }
        }

        private static void ApplyRemoteDisable(CctvSecurityCameraState state)
        {
            if (!state.IsBroken)
                CctvSecurityDirector.OnCameraRemotelyDisabled(state);

            if (state.CameraComponent is CCTVCamera holder && !holder.SecurityRemotelyDisabled)
            {
                holder.MarkSecurityRemotelyDisabled();
                QuadCameraAssignment.OnCameraBroken(holder);
            }
        }

        private static void ApplyPhysicalBreak(CctvSecurityCameraState state)
        {
            if (!(state.CameraComponent is CCTVCamera holder))
                return;

            if (CctvBreakableCamera.ApplyReplicatedBreak(holder))
                return;

            // The state snapshot can arrive before the visual/breakable component.
            // Apply gameplay state immediately; Tick retries until the visual exists.
            if (!holder.IsSecurityBroken)
                holder.MarkSecurityBroken();
            if (!state.IsBroken)
                CctvSecurityCameraRegistry.MarkCameraBroken(holder);
            QuadCameraAssignment.OnCameraBroken(holder);
        }

        private static CctvSecurityCameraState FindStateByIndex(int cameraIndex)
        {
            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            for (int i = 0; i < cameras.Count; i++)
            {
                if (cameras[i] != null && cameras[i].CameraIndex == cameraIndex)
                    return cameras[i];
            }
            return null;
        }

        private static void SendSnapshot()
        {
            NetworkManager nm = NetworkManager.Singleton;
            CustomMessagingManager manager = nm?.CustomMessagingManager;
            if (manager == null || nm.IsServer != true)
                return;

            int valueCount = DisabledIndices.Count + BrokenIndices.Count + 2;
            using (var writer = new FastBufferWriter(sizeof(int) * valueCount, Allocator.Temp))
            {
                writer.WriteValueSafe(DisabledIndices.Count);
                foreach (int index in DisabledIndices)
                    writer.WriteValueSafe(index);
                writer.WriteValueSafe(BrokenIndices.Count);
                foreach (int index in BrokenIndices)
                    writer.WriteValueSafe(index);
                manager.SendNamedMessageToAll(
                    SnapshotMessage,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnBreakRequestMessage(ulong sender, FastBufferReader reader)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || sender == NetworkManager.ServerClientId)
                return;

            reader.ReadValueSafe(out int cameraIndex);
            RegisterPhysicalBreak(cameraIndex);
        }

        private static void OnSnapshotMessage(ulong sender, FastBufferReader reader)
        {
            if (sender != NetworkManager.ServerClientId || NetworkManager.Singleton?.IsServer == true)
                return;

            reader.ReadValueSafe(out int disabledCount);
            DisabledIndices.Clear();
            for (int i = 0; i < disabledCount; i++)
            {
                reader.ReadValueSafe(out int index);
                DisabledIndices.Add(index);
            }

            reader.ReadValueSafe(out int brokenCount);
            BrokenIndices.Clear();
            for (int i = 0; i < brokenCount; i++)
            {
                reader.ReadValueSafe(out int index);
                BrokenIndices.Add(index);
            }

            ApplyShutdownStates();
        }
    }
}
