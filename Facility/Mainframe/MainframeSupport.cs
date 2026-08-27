using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Security;
namespace Y4NGZCompany.Facility.Mainframe
{
    public sealed class MainframeSupport : CctvCommandableFixture
    {
        public static MainframeSupport Active { get; private set; }

        private NetworkVariable<bool> _isHacked = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private NetworkVariable<float> _lockoutEndTime = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Alarm state was previously owned by AlarmBoxSupport; consolidated here so the
        // mainframe is the single fixture (no separate alarm box asset). CctvAlarmSystem
        // and CctvSupportApi.IsAlarmActive read MainframeSupport.Active.IsAlarmOn.
        private NetworkVariable<bool> _alarmOn = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private NetworkVariable<float> _securityAlarmEndTime = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private NetworkVariable<int> _securityAlarmReasonCode = new NetworkVariable<int>(NoSecurityAlarmReason,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private const int NoSecurityAlarmReason = 0;
        private const int GenericSecurityAlarmReason = 3;
        private static readonly string[] SecurityAlarmReasonTable =
        {
            string.Empty,
            "SECURITY CAMERA",
            "CAMERA SABOTAGE",
            "SECURITY EVENT",
        };

        public override string CctvDisplayName => "MAINFRAME";
        public override string CctvCommandHint => "hack";
        public override Vector3 CctvFocusPosition => transform.position + Vector3.up * 0.6f;
        public override float CctvFocusRadius => 1.1f;

        public bool IsHacked => _isHacked.Value;
        public bool IsLockedOut => Time.unscaledTime < _lockoutEndTime.Value && !IsHacked;
        public float LockoutRemaining => Mathf.Max(0f, _lockoutEndTime.Value - Time.unscaledTime);
        public bool IsAlarmOn => _alarmOn.Value;
        public bool IsSecurityAlarmActive => _securityAlarmEndTime.Value > 0f && SecurityTimeSeconds() < _securityAlarmEndTime.Value;
        public float SecurityAlarmRemaining => Mathf.Max(0f, _securityAlarmEndTime.Value - SecurityTimeSeconds());
        public string SecurityAlarmReason => DecodeSecurityAlarmReason(_securityAlarmReasonCode.Value);

        protected override void OnEnable()
        {
            base.OnEnable();
            if (Active == null) Active = this;
        }

        protected override void OnDisable()
        {
            if (Active == this) Active = null;
            base.OnDisable();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (Active == null) Active = this;
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();
            if (Active == this) Active = null;
        }

        public override CctvCommandExecutionResult ExecuteCctvCommand(string command)
        {
            string normalized = (command ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized != "hack")
                return CctvCommandExecutionResult.Fail("USE HACK");

            if (IsHacked)
                return CctvCommandExecutionResult.Succeed("ALREADY HACKED");
            if (IsLockedOut)
                return CctvCommandExecutionResult.Fail($"LOCKOUT {LockoutRemaining:0}s");

            return CctvCommandExecutionResult.Succeed("HACK STARTED");
        }

        [ServerRpc(RequireOwnership = false)]
        public void MarkHackedServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowRemoteHacking.Value)
                return;
            _isHacked.Value = true;
            _lockoutEndTime.Value = 0f;
            Y4NGZCompany.Facility.Security.CctvSecurityDirector.OnMainframeHacked();
        }

        [ServerRpc(RequireOwnership = false)]
        public void MarkLockoutServerRpc(float seconds = CctvSupportState.MainframeLockoutSeconds, ServerRpcParams rpcParams = default)
        {
            if (!IsServer || _isHacked.Value) return;

            float duration = seconds > 0f ? seconds : CctvSupportState.MainframeLockoutSeconds;
            _lockoutEndTime.Value = Mathf.Max(_lockoutEndTime.Value, Time.unscaledTime + duration);

            // Failed/locked-out hack trips the consolidated mainframe alarm.
            Y4NGZCompany.Facility.Security.CctvSecurityDirector.ReleaseMainframeAlarmOwnership();
            _alarmOn.Value = true;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] MAINFRAME_ALARM toggled on=true via lockout (lockout {duration:0}s).");
        }

        [ServerRpc(RequireOwnership = false)]
        public void SetAlarmServerRpc(bool on, ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (!_isHacked.Value) return;

            if (on)
            {
                Y4NGZCompany.Facility.Security.CctvSecurityDirector.ReleaseMainframeAlarmOwnership();
            }
            else
            {
                Y4NGZCompany.Facility.Security.CctvSecurityDirector.SilenceSecurityAlarm();
            }

            if (_alarmOn.Value == on) return;
            _alarmOn.Value = on;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] MAINFRAME_ALARM toggled on={on} via mainframe RPC.");
        }

        [ServerRpc(RequireOwnership = false)]
        public void SilenceSecurityAlarmServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;
            Y4NGZCompany.Facility.Security.CctvSecurityDirector.SilenceSecurityAlarm();
        }

        internal void SetAlarmServerSide(bool on)
        {
            if (!IsServer) return;
            _alarmOn.Value = on;
        }

        internal void SetSecurityAlarmServerSide(float alarmEndsAt, string reason)
        {
            if (!IsServer) return;
            _securityAlarmEndTime.Value = Mathf.Max(0f, alarmEndsAt);
            _securityAlarmReasonCode.Value = EncodeSecurityAlarmReason(reason);
        }

        internal void ClearSecurityAlarmServerSide()
        {
            if (!IsServer) return;
            _securityAlarmEndTime.Value = 0f;
            _securityAlarmReasonCode.Value = NoSecurityAlarmReason;
        }

        private static int EncodeSecurityAlarmReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return NoSecurityAlarmReason;

            string value = reason.Trim();
            for (int i = 1; i < SecurityAlarmReasonTable.Length; i++)
            {
                if (string.Equals(value, SecurityAlarmReasonTable[i], System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return GenericSecurityAlarmReason;
        }

        private static string DecodeSecurityAlarmReason(int code)
        {
            if (code <= NoSecurityAlarmReason || code >= SecurityAlarmReasonTable.Length)
                return string.Empty;

            return SecurityAlarmReasonTable[code];
        }

        private static float SecurityTimeSeconds()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening)
                return (float)manager.ServerTime.Time;
            return Time.unscaledTime;
        }
    }
}
