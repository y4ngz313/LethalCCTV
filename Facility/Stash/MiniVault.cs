using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Security;
namespace Y4NGZCompany.Facility.Stash
{
    public sealed class MiniVault : NetworkBehaviour
    {
        public const int CodeDigits = 4;
        public const int MaxAttemptsPerPlayer = 4;

        private NetworkVariable<int> _assignedCode = new NetworkVariable<int>(-1,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<bool> _isUnlocked = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<int> _globalAttempts = new NetworkVariable<int>(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // #716 E10: player-facing label. "Company Stash" is the one name this fixture goes by;
        // the MiniVault/Vault type names stay as they are.
        private string _displayName = "COMPANY STASH";

        public int AssignedCode => _assignedCode.Value;
        public bool IsUnlocked => _isUnlocked.Value;
        public int GlobalAttempts => _globalAttempts.Value;
        public string DisplayName => string.IsNullOrWhiteSpace(_displayName) ? $"COMPANY STASH {GetInstanceID():X}" : _displayName;

        public void InitializeAssignedCode(int code, string displayName)
        {
            if (code >= 0)
                _assignedCode.Value = code;
            if (!string.IsNullOrWhiteSpace(displayName))
                _displayName = displayName;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer)
            {
                if (_assignedCode.Value < 0)
                {
                    int[] codes = CctvSupportState.HiddenVaultCodes;
                    if (codes != null && codes.Length > 0)
                    {
                        var rng = new System.Random(GetInstanceID() ^ Environment.TickCount);
                        _assignedCode.Value = codes[rng.Next(0, codes.Length)];
                    }
                }
            }
        }

        public bool TrySubmitCodeLocally(int code, out string statusText)
        {
            if (!IsUnlocked && _assignedCode.Value == code)
            {
                statusText = "UNLOCKED";
                return true;
            }
            statusText = "DENIED";
            return false;
        }

        [ServerRpc(RequireOwnership = false)]
        public void SubmitCodeServerRpc(int code, ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (_isUnlocked.Value) return;
            _globalAttempts.Value++;

            if (_assignedCode.Value == code)
            {
                _isUnlocked.Value = true;
                OpenVault();
            }
        }

        private void OpenVault()
        {
            // Placeholder behaviour: print to log and disable colliders.
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Vault '{DisplayName}' unlocked with code {_assignedCode.Value:D4} after {_globalAttempts.Value} attempts.");
            foreach (Collider col in GetComponentsInChildren<Collider>(includeInactive: true))
                col.enabled = false;
        }
    }
}
