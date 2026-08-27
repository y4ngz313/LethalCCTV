using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using LethalNetworkAPI;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [Serializable]
    public sealed class CCTVOperatorAnimMessage
    {
        public byte Kind;
        public int PlayerId;
        public sbyte JoyX;
        public sbyte JoyY;
        public byte Arg;
        public int Yaw;
    }

    /// <summary>
    /// Network mirror for operator animation state. One-shot messages for
    /// enter/exit/button/slot/control/radar; the joystick deflection is
    /// quantized to sbyte and throttled to ~12 Hz while the operator is
    /// actively steering a camera (never per-frame).
    /// Remote clients apply the messages to an OperatorAnimSession on that
    /// player so everyone sees the operator lean in and work the console.
    /// </summary>
    internal static class CCTVOperatorAnimSync
    {
        private const string CHANNEL = "y4ngz_cctv_anim_v1";
        private const float JoystickSendInterval = 1f / 12f;
        private const float JoystickHeartbeatSeconds = 1f;
        private const float JoystickDiagnosticInterval = 1f;
        private const string MainframeDefaultStopTrigger = "SA_stopAnimation";

        private const byte KindEnter = 1;
        private const byte KindExit = 2;
        private const byte KindCameraControl = 3;
        private const byte KindButtonPress = 4;
        private const byte KindSelectCamera = 5;
        private const byte KindRadarLook = 6;
        private const byte KindJoystick = 7;
        private const byte KindMainframeEnter = 8;
        private const byte KindMainframeExit = 9;

        private static LNetworkMessage<CCTVOperatorAnimMessage> _message;
        private static readonly Dictionary<int, OperatorAnimSession> _remoteSessions =
            new Dictionary<int, OperatorAnimSession>();
        private static readonly HashSet<int> _remoteMainframePlayers = new HashSet<int>();
        private static readonly List<int> _endedSessionIds = new List<int>();
        private static readonly Dictionary<int, float> _nextRemoteJoystickDiagnosticAt =
            new Dictionary<int, float>();
        private static readonly HashSet<int> _remoteJoystickDiagnosticLogged = new HashSet<int>();
        private static float _nextJoystickSendAt;
        private static float _nextJoystickHeartbeatAt;
        private static float _nextLocalJoystickDiagnosticAt;
        private static sbyte _lastSentJoyX = sbyte.MaxValue;
        private static sbyte _lastSentJoyY = sbyte.MaxValue;
        private static bool _localJoystickDiagnosticLogged;

        internal static void Initialize()
        {
            if (_message != null) return;
            try
            {
                _message = LNetworkMessage<CCTVOperatorAnimMessage>.Connect(
                    CHANNEL,
                    onServerReceived: OnServerReceived,
                    onClientReceived: OnClientReceived);
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Operator anim sync channel '{CHANNEL}' connected.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Operator anim sync unavailable (animations stay local): {ex.Message}");
                _message = null;
            }
        }

        internal static void Shutdown()
        {
            foreach (KeyValuePair<int, OperatorAnimSession> pair in _remoteSessions)
                pair.Value?.EndImmediate("sync-shutdown");
            _remoteSessions.Clear();

            foreach (int playerId in new List<int>(_remoteMainframePlayers))
                ApplyRemoteMainframeExit(playerId, ResolvePlayer(playerId), "sync-shutdown");
            _remoteMainframePlayers.Clear();

            _nextRemoteJoystickDiagnosticAt.Clear();
            _remoteJoystickDiagnosticLogged.Clear();

            _message?.ClearSubscriptions();
            _message = null;
        }

        internal static void Tick()
        {
            if (_remoteSessions.Count == 0)
                return;

            _endedSessionIds.Clear();
            foreach (KeyValuePair<int, OperatorAnimSession> pair in _remoteSessions)
            {
                OperatorAnimSession session = pair.Value;
                if (session == null)
                {
                    _endedSessionIds.Add(pair.Key);
                    continue;
                }
                session.Tick();
                if (!session.IsActive && !session.IsWindingDown)
                    _endedSessionIds.Add(pair.Key);
            }
            for (int i = 0; i < _endedSessionIds.Count; i++)
                _remoteSessions.Remove(_endedSessionIds[i]);
        }

        /// <summary>Throttled joystick deflection sync for the local operator.</summary>
        internal static void TickLocalJoystickSend(OperatorAnimSession localSession)
        {
            if (_message == null || localSession == null || !localSession.IsActive)
                return;
            if (!localSession.CameraControlActive)
                return;
            if (Time.unscaledTime < _nextJoystickSendAt)
                return;

            sbyte x = QuantizeAxis(localSession.JoystickCommand.x);
            sbyte y = QuantizeAxis(localSession.JoystickCommand.y);
            bool changed = x != _lastSentJoyX || y != _lastSentJoyY;
            bool heartbeatDue = Time.unscaledTime >= _nextJoystickHeartbeatAt;
            if (!changed && !heartbeatDue)
                return;

            _nextJoystickSendAt = Time.unscaledTime + JoystickSendInterval;
            _nextJoystickHeartbeatAt = Time.unscaledTime + JoystickHeartbeatSeconds;
            _lastSentJoyX = x;
            _lastSentJoyY = y;
            Send(new CCTVOperatorAnimMessage
            {
                Kind = KindJoystick,
                PlayerId = GetPlayerId(localSession.Player),
                JoyX = x,
                JoyY = y,
            });
        }

        internal static void SendEnter(PlayerControllerB player, int activeSlot)
        {
            _lastSentJoyX = sbyte.MaxValue;
            _lastSentJoyY = sbyte.MaxValue;
            _localJoystickDiagnosticLogged = false;
            _nextLocalJoystickDiagnosticAt = 0f;
            Send(new CCTVOperatorAnimMessage { Kind = KindEnter, PlayerId = GetPlayerId(player), Arg = (byte)Mathf.Clamp(activeSlot, 0, 255) });
            ReportOperatorNoiseToBundy(player);
        }

        internal static void SendExit(PlayerControllerB player)
        {
            Send(new CCTVOperatorAnimMessage { Kind = KindExit, PlayerId = GetPlayerId(player) });
        }

        internal static void SendCameraControl(PlayerControllerB player, bool controlling)
        {
            Send(new CCTVOperatorAnimMessage { Kind = KindCameraControl, PlayerId = GetPlayerId(player), Arg = controlling ? (byte)1 : (byte)0 });
        }

        internal static void SendButtonPress(PlayerControllerB player, int buttonId)
        {
            Send(new CCTVOperatorAnimMessage { Kind = KindButtonPress, PlayerId = GetPlayerId(player), Arg = (byte)Mathf.Clamp(buttonId, 0, 255) });
        }

        internal static void SendSelectCamera(PlayerControllerB player, int slot)
        {
            Send(new CCTVOperatorAnimMessage { Kind = KindSelectCamera, PlayerId = GetPlayerId(player), Arg = (byte)Mathf.Clamp(slot, 0, 255) });
        }

        internal static void SendRadarLook(PlayerControllerB player, bool looking)
        {
            Send(new CCTVOperatorAnimMessage { Kind = KindRadarLook, PlayerId = GetPlayerId(player), Arg = looking ? (byte)1 : (byte)0 });
        }

        internal static void SendMainframeEnter(PlayerControllerB player, short yaw)
        {
            Send(new CCTVOperatorAnimMessage { Kind = KindMainframeEnter, PlayerId = GetPlayerId(player), Yaw = yaw });
            ReportOperatorNoiseToBundy(player);
        }

        /// <summary>
        /// Bundy noise bus (3b follow-up). Sitting down at the surveillance cluster is an audible,
        /// position-fixed event -- chair, buttons, mainframe chatter -- and this class is the one
        /// place that sees every operator: the local player via the Send* pair above, remote
        /// players via the Apply* pair below (the host receives those like any client). The bus
        /// discards the report on every client that does not own the Bundy AI, and its rate gate
        /// absorbs a monitor enter and a mainframe enter landing together.
        /// </summary>
        private static void ReportOperatorNoiseToBundy(PlayerControllerB player)
        {
            if (player == null)
                return;

            Y4NGZCompany.Core.Compat.BundyWorldNoiseBridge.Report(
                player.transform.position, Y4NGZCompany.Core.Compat.BundyWorldNoiseBridge.CctvStationUse, player);
        }

        internal static void SendMainframeExit(PlayerControllerB player)
        {
            Send(new CCTVOperatorAnimMessage { Kind = KindMainframeExit, PlayerId = GetPlayerId(player) });
        }

        private static void Send(CCTVOperatorAnimMessage message)
        {
            if (message == null)
                return;
            if (message.PlayerId < 0)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Operator anim sync send skipped: invalid player id for {KindName(message.Kind)}.");
                return;
            }

            LogLocalSend(message, _message != null);
            if (_message == null)
                return;

            try
            {
                _message.SendServer(message);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Operator anim message stayed local: {ex.Message}");
            }
        }

        private static int GetPlayerId(PlayerControllerB player)
        {
            return player != null ? (int)player.playerClientId : -1;
        }

        private static PlayerControllerB ResolvePlayer(int playerId)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.allPlayerScripts == null)
                return null;
            if (playerId < 0 || playerId >= sor.allPlayerScripts.Length)
                return null;
            return sor.allPlayerScripts[playerId];
        }

        private static sbyte QuantizeAxis(float value)
        {
            return (sbyte)Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * 127f);
        }

        private static void OnServerReceived(CCTVOperatorAnimMessage data, ulong _)
        {
            try
            {
                _message?.SendClients(data);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Operator anim server broadcast failed: {ex.Message}");
            }
        }

        private static void OnClientReceived(CCTVOperatorAnimMessage data)
        {
            if (data == null) return;

            PlayerControllerB localPlayer = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (localPlayer != null && data.PlayerId == (int)localPlayer.playerClientId)
                return; // own loop-back

            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.allPlayerScripts == null) return;
            if (data.PlayerId < 0 || data.PlayerId >= sor.allPlayerScripts.Length) return;

            PlayerControllerB player = sor.allPlayerScripts[data.PlayerId];
            if (player == null) return;

            LogRemoteReceive(data);
            switch (data.Kind)
            {
                case KindEnter:
                    ApplyRemoteEnter(data.PlayerId, player, data.Arg);
                    break;
                case KindExit:
                    if (_remoteSessions.TryGetValue(data.PlayerId, out OperatorAnimSession exiting))
                        exiting.BeginGracefulEnd("remote-exit");
                    _nextRemoteJoystickDiagnosticAt.Remove(data.PlayerId);
                    _remoteJoystickDiagnosticLogged.Remove(data.PlayerId);
                    break;
                case KindCameraControl:
                    GetRemoteSession(data.PlayerId)?.SetCameraControl(data.Arg != 0);
                    break;
                case KindButtonPress:
                    GetRemoteSession(data.PlayerId)?.PressButton(data.Arg, isStandUp: false);
                    break;
                case KindSelectCamera:
                    GetRemoteSession(data.PlayerId)?.SelectCamera(data.Arg);
                    break;
                case KindRadarLook:
                    GetRemoteSession(data.PlayerId)?.SetRadarLook(data.Arg != 0);
                    break;
                case KindJoystick:
                    GetRemoteSession(data.PlayerId)?.SetJoystickTargetAbsolute(
                        new Vector2(data.JoyX / 127f, data.JoyY / 127f));
                    break;
                case KindMainframeEnter:
                    ApplyRemoteMainframeEnter(data.PlayerId, player, data.Yaw);
                    break;
                case KindMainframeExit:
                    ApplyRemoteMainframeExit(data.PlayerId, player, "remote-exit");
                    break;
            }
        }

        private static void ApplyRemoteEnter(int playerId, PlayerControllerB player, int activeSlot)
        {
            if (_remoteSessions.TryGetValue(playerId, out OperatorAnimSession existing))
            {
                existing?.EndImmediate("remote-reenter");
                _remoteSessions.Remove(playerId);
            }
            _nextRemoteJoystickDiagnosticAt.Remove(playerId);
            _remoteJoystickDiagnosticLogged.Remove(playerId);

            // Remote operators get the enter flourish too: the third-person
            // lean-in plus button-press reach reads on the visible body.
            var session = new OperatorAnimSession(player, isLocal: false);
            if (!session.Begin(Y4NGZPlayerAnimationBridge.ResolveControllerShared(), enterFlourish: true))
                return;

            session.SetActiveSlotQuiet(activeSlot);
            _remoteSessions[playerId] = session;
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Remote operator anim session started for player #{playerId}.");
            ReportOperatorNoiseToBundy(player);
        }

        private static void ApplyRemoteMainframeEnter(int playerId, PlayerControllerB player, int yaw)
        {
            if (player == null)
                return;

            ResolveVanillaTerminalAnimation(out string enterTrigger, out _);
            short clampedYaw = (short)Mathf.RoundToInt(Mathf.Repeat(yaw, 360f));

            try
            {
                player.UpdateSpecialAnimationValue(true, clampedYaw);
                player.inSpecialInteractAnimation = true;
                player.enteringSpecialAnimation = false;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Remote mainframe enter failed for player #{playerId}: {ex.Message}");
            }

            TryTriggerPlayerAnimator(player, enterTrigger, "mainframe-enter");
            _remoteMainframePlayers.Add(playerId);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeInteract] Remote mainframe TypeOnTerminal stance entered for player #{playerId} yaw={clampedYaw}.");
            ReportOperatorNoiseToBundy(player);
        }

        private static void ApplyRemoteMainframeExit(int playerId, PlayerControllerB player, string reason)
        {
            if (player != null)
            {
                ResolveVanillaTerminalAnimation(out _, out string stopTrigger);
                TryTriggerPlayerAnimator(player, stopTrigger, "mainframe-exit");

                try
                {
                    player.UpdateSpecialAnimationValue(false, 0);
                    player.inSpecialInteractAnimation = false;
                    player.enteringSpecialAnimation = false;
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Remote mainframe exit failed for player #{playerId}: {ex.Message}");
                }
            }

            _remoteMainframePlayers.Remove(playerId);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeInteract] Remote mainframe TypeOnTerminal stance exited for player #{playerId} reason='{reason ?? "unspecified"}'.");
        }

        private static void ResolveVanillaTerminalAnimation(out string enterTrigger, out string stopTrigger)
        {
            enterTrigger = null;
            stopTrigger = MainframeDefaultStopTrigger;

            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                InteractTrigger terminalTrigger = terminal != null ? terminal.GetComponent<InteractTrigger>() : null;
                if (terminalTrigger == null)
                    return;

                if (!string.IsNullOrWhiteSpace(terminalTrigger.animationString))
                    enterTrigger = terminalTrigger.animationString;
                if (!string.IsNullOrWhiteSpace(terminalTrigger.stopAnimationString))
                    stopTrigger = terminalTrigger.stopAnimationString;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Remote vanilla terminal animation probe failed: {ex.Message}");
            }
        }

        private static void TryTriggerPlayerAnimator(PlayerControllerB player, string trigger, string phase)
        {
            if (player == null || player.playerBodyAnimator == null || string.IsNullOrWhiteSpace(trigger))
                return;

            try
            {
                if (!AnimatorHasTrigger(player.playerBodyAnimator, trigger))
                    return;
                player.playerBodyAnimator.ResetTrigger(trigger);
                player.playerBodyAnimator.SetTrigger(trigger);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeInteract] Remote animator {phase} trigger '{trigger}' failed: {ex.Message}");
            }
        }

        private static bool AnimatorHasTrigger(Animator animator, string trigger)
        {
            if (animator == null || string.IsNullOrWhiteSpace(trigger))
                return false;

            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].type == AnimatorControllerParameterType.Trigger &&
                        string.Equals(parameters[i].name, trigger, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static OperatorAnimSession GetRemoteSession(int playerId)
        {
            return _remoteSessions.TryGetValue(playerId, out OperatorAnimSession session) && session.IsActive
                ? session
                : null;
        }

        private static void LogLocalSend(CCTVOperatorAnimMessage message, bool channelReady)
        {
            if (message.Kind == KindJoystick && !ShouldLogLocalJoystick(message))
                return;

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Operator anim sync SEND {FormatMessage(message)} channelReady={channelReady}.");
        }

        private static bool ShouldLogLocalJoystick(CCTVOperatorAnimMessage message)
        {
            if (message.Kind != KindJoystick)
                return true;

            if (!_localJoystickDiagnosticLogged || Time.unscaledTime >= _nextLocalJoystickDiagnosticAt)
            {
                _localJoystickDiagnosticLogged = true;
                _nextLocalJoystickDiagnosticAt = Time.unscaledTime + JoystickDiagnosticInterval;
                return true;
            }

            return false;
        }

        private static void LogRemoteReceive(CCTVOperatorAnimMessage message)
        {
            if (message.Kind == KindJoystick)
            {
                bool seen = _remoteJoystickDiagnosticLogged.Contains(message.PlayerId);
                float nextAt;
                _nextRemoteJoystickDiagnosticAt.TryGetValue(message.PlayerId, out nextAt);
                if (seen && Time.unscaledTime < nextAt)
                    return;

                _remoteJoystickDiagnosticLogged.Add(message.PlayerId);
                _nextRemoteJoystickDiagnosticAt[message.PlayerId] = Time.unscaledTime + JoystickDiagnosticInterval;
            }

            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Operator anim sync RECV {FormatMessage(message)}.");
        }

        private static string FormatMessage(CCTVOperatorAnimMessage message)
        {
            if (message == null)
                return "<null>";

            if (message.Kind == KindJoystick)
            {
                return $"{KindName(message.Kind)} player=#{message.PlayerId} " +
                       $"joy=({message.JoyX / 127f:F2},{message.JoyY / 127f:F2})";
            }

            if (message.Kind == KindMainframeEnter)
                return $"{KindName(message.Kind)} player=#{message.PlayerId} yaw={message.Yaw}";

            return $"{KindName(message.Kind)} player=#{message.PlayerId} arg={message.Arg}";
        }

        private static string KindName(byte kind)
        {
            switch (kind)
            {
                case KindEnter: return "Enter";
                case KindExit: return "Exit";
                case KindCameraControl: return "CameraControl";
                case KindButtonPress: return "ButtonPress";
                case KindSelectCamera: return "SelectCamera";
                case KindRadarLook: return "RadarLook";
                case KindJoystick: return "Joystick";
                case KindMainframeEnter: return "MainframeEnter";
                case KindMainframeExit: return "MainframeExit";
                default: return "Unknown(" + kind + ")";
            }
        }
    }

    /// <summary>
    /// Vanilla syncs animator parameters by index to other clients; while the
    /// operator wears the CCTV controller those indices describe a different
    /// parameter set, so the sync is suppressed and CCTVOperatorAnimSync owns
    /// the remote animation instead.
    /// </summary>
    [HarmonyPatch(typeof(PlayerControllerB), "UpdatePlayerAnimationsToOtherClients")]
    internal static class CCTVOperatorAnimSyncSuppressPatch
    {
        private static bool Prefix(PlayerControllerB __instance)
        {
            return !Y4NGZPlayerAnimationBridge.IsLocalSessionActive(__instance);
        }
    }
}
