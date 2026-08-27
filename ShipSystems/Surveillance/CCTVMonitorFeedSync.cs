using System;
using GameNetcodeStuff;
using LethalNetworkAPI;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [Serializable]
    public sealed class CCTVFeedStateMessage
    {
        public byte Kind;
        public int PlayerId;
        public byte FeedCctv;
        public int OperatorId;
        public int ActiveSlot = -1;
    }

    /// <summary>
    /// Shared monitor feed state (vanilla map vs CCTV) plus the operator
    /// occupancy lock, synced over LethalNetworkAPI with the same
    /// server-rebroadcast pattern as CCTVOperatorAnimSync.
    ///
    /// Rules (2026-07-02 spec):
    /// - The monitor shows the vanilla display until a player enters CCTV;
    ///   entering switches the shared feed to CCTV.
    /// - Exiting leaves the CCTV feed up; the grey monitor button cycles
    ///   vanilla&lt;-&gt;CCTV while nobody is operating.
    /// - Only one operator at a time: entry is claim-based, and the grey
    ///   button is inert while the claim is held.
    /// - Ship leave / orbit resets the feed to vanilla and clears the claim.
    ///
    /// Late-join needs no special handling: players can only join in orbit,
    /// where the state is always the default (vanilla feed, no operator).
    /// </summary>
    internal static class CCTVMonitorFeedSync
    {
        private const string CHANNEL = "y4ngz_cctv_feed_v1";

        private const byte KindRequestClaim = 1;
        private const byte KindRequestRelease = 2;
        private const byte KindRequestToggle = 3;
        private const byte KindState = 4;
        private const byte KindRequestActiveSlot = 5;

        // If the state broadcast for our claim never arrives (host without the
        // channel, packet loss), fail open into the pre-lock behaviour instead
        // of leaving the button dead.
        private const float ClaimTimeoutSeconds = 1.5f;

        private static LNetworkMessage<CCTVFeedStateMessage> _message;
        private static bool _initialized;

        // Client-visible applied state.
        private static bool _feedCctv;
        private static int _operatorId = -1;

        // Server-authoritative state; meaningful on the host only.
        private static bool _serverFeedCctv;
        private static int _serverOperatorId = -1;
        private static int _serverActiveSlot = -1;

        // Pending local entry claim.
        private static PlayerControllerB _claimPlayer;
        private static Action _claimConfirmed;
        private static Action<string> _claimDenied;
        private static float _claimTimeoutAt;

        internal static bool IsFeedCctv => _feedCctv;
        internal static bool HasOperator => _operatorId >= 0;
        internal static int OperatorId => _operatorId;
        internal static bool IsClaimPending => _claimPlayer != null;

        // The operator's currently-selected quad slot, replicated so observer
        // clients know which facility camera to keep awake (CCTVCameraThrottle).
        // It is retained after the operator releases the station (#562), so the
        // wall keeps its last feed and CCTVCameraThrottle knows which single
        // camera to idle-render while unmanned. -1 = no known slot yet (fresh
        // round, orbit reset, or a claim whose operator has not reported one).
        private static int _remoteActiveSlot = -1;
        internal static int RemoteActiveSlot => _remoteActiveSlot;
        private static int _lastSentActiveSlot = -1;

        internal static bool IsLocalOperator
        {
            get
            {
                PlayerControllerB lp = GameNetworkManager.Instance != null
                    ? GameNetworkManager.Instance.localPlayerController
                    : null;
                return lp != null && _operatorId == (int)lp.playerClientId;
            }
        }

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                _message = LNetworkMessage<CCTVFeedStateMessage>.Connect(
                    CHANNEL,
                    onServerReceived: OnServerReceived,
                    onClientReceived: OnClientReceived);
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Monitor feed sync channel '{CHANNEL}' connected.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Monitor feed sync unavailable (feed state stays local): {ex.Message}");
                _message = null;
            }

            // ChairExited fires at the tail of every focus cleanup (normal exit,
            // death, dungeon regen, ship phase), so the claim can never leak.
            CCTVStationEvents.ChairEntered += OnLocalChairEntered;
            CCTVStationEvents.ChairExited += OnLocalChairExited;
        }

        internal static void Shutdown()
        {
            if (!_initialized) return;
            _initialized = false;

            CCTVStationEvents.ChairEntered -= OnLocalChairEntered;
            CCTVStationEvents.ChairExited -= OnLocalChairExited;
            _message?.ClearSubscriptions();
            _message = null;
            ResetLocal("shutdown");
        }

        internal static void Tick()
        {
            if (_claimPlayer == null) return;
            if (Time.unscaledTime < _claimTimeoutAt) return;

            SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Feed sync claim timed out; failing open into local entry.");
            PlayerControllerB player = _claimPlayer;
            Action confirmed = _claimConfirmed;
            ClearPendingClaim();
            ApplyState(feedCctv: true, operatorId: GetPlayerId(player), "claim-timeout-failopen");
            confirmed?.Invoke();
        }

        /// <summary>
        /// True when the red monitor button should offer CCTV entry to the local player.
        /// </summary>
        internal static bool IsEntryAvailable(out string unavailableReason)
        {
            if (!CCTVTerminalUnlockable.IsPurchased())
            {
                unavailableReason = "Requires CCTV terminal upgrade";
                return false;
            }
            if (!IsShipLandedForCctv())
            {
                unavailableReason = "Comes online once the ship has landed";
                return false;
            }
            if (HasOperator && !IsLocalOperator)
            {
                unavailableReason = "In use by another employee";
                return false;
            }
            if (MonitorFocus.IsFocused || MonitorFocus.IsEntryPoseSettleActive || IsClaimPending)
            {
                unavailableReason = string.Empty;
                return false;
            }

            unavailableReason = null;
            return true;
        }

        /// <summary>
        /// True when the grey monitor button should cycle the feed. Inert while
        /// unpurchased, while an operator holds the claim, and in orbit (the
        /// cameras despawn with the level, so there is nothing to cycle to).
        /// </summary>
        internal static bool IsFeedToggleAvailable()
        {
            return CCTVTerminalUnlockable.IsPurchased() &&
                   !HasOperator &&
                   !IsClaimPending &&
                   !MonitorFocus.IsFocused &&
                   IsShipLandedForCctv();
        }

        internal static bool IsShipLandedForCctv()
        {
            StartOfRound sor = StartOfRound.Instance;
            return sor != null && sor.shipHasLanded && !sor.shipIsLeaving && !sor.inShipPhase;
        }

        /// <summary>
        /// Claim the operator slot for the local player, then invoke exactly one
        /// of the callbacks. Offline (or host) resolution is synchronous.
        /// </summary>
        internal static void RequestEntry(PlayerControllerB player, Action onConfirmed, Action<string> onDenied)
        {
            if (player == null) return;
            if (_claimPlayer != null)
                return; // a claim is already in flight

            if (HasOperator && !IsLocalOperator)
            {
                onDenied?.Invoke("Another employee is on the cameras.");
                return;
            }

            int playerId = GetPlayerId(player);
            if (playerId < 0)
            {
                onDenied?.Invoke("Player not ready.");
                return;
            }

            if (_message == null)
            {
                // No network channel: single source of truth is local.
                ApplyState(feedCctv: true, operatorId: playerId, "offline-claim");
                onConfirmed?.Invoke();
                return;
            }

            _claimPlayer = player;
            _claimConfirmed = onConfirmed;
            _claimDenied = onDenied;
            _claimTimeoutAt = Time.unscaledTime + ClaimTimeoutSeconds;
            SendRequest(new CCTVFeedStateMessage { Kind = KindRequestClaim, PlayerId = playerId });
        }

        /// <summary>
        /// Called by MonitorFocus whenever the local operator's active quad slot
        /// changes while it holds the operator claim, so observer clients can keep
        /// the corresponding facility camera awake (see RemoteActiveSlot /
        /// CCTVCameraThrottle). No-ops for non-operators and off-network play.
        /// </summary>
        internal static void NotifyLocalActiveSlotChanged(int slot)
        {
            if (!IsLocalOperator) return;
            if (slot == _lastSentActiveSlot) return;
            _lastSentActiveSlot = slot;

            if (_message == null)
            {
                // Offline: no observers to replicate to, but keep the local mirror
                // consistent in case something reads RemoteActiveSlot directly.
                _remoteActiveSlot = slot;
                return;
            }

            try
            {
                _message.SendServer(new CCTVFeedStateMessage
                {
                    Kind = KindRequestActiveSlot,
                    PlayerId = _operatorId,
                    ActiveSlot = slot,
                });
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Active-slot sync stayed local: {ex.Message}");
            }
        }

        /// <summary>Grey button: cycle vanilla&lt;-&gt;CCTV while unoccupied.</summary>
        internal static void RequestFeedToggle(PlayerControllerB player)
        {
            if (!IsFeedToggleAvailable())
                return;

            int playerId = GetPlayerId(player);
            if (_message == null)
            {
                ApplyState(!_feedCctv, _operatorId, "offline-toggle");
                return;
            }

            SendRequest(new CCTVFeedStateMessage { Kind = KindRequestToggle, PlayerId = playerId });
        }

        /// <summary>
        /// Ship left the moon / lobby ended: everyone deterministically resets to
        /// the default state (vanilla feed, no operator). Runs on every client
        /// from the same StartOfRound hooks, so no broadcast is required.
        /// </summary>
        internal static void ResetForOrbit(string reason)
        {
            _serverFeedCctv = false;
            _serverOperatorId = -1;
            _serverActiveSlot = -1;
            ClearPendingClaim();
            ApplyState(feedCctv: false, operatorId: -1, reason, activeSlot: -1);
        }

        private static void OnLocalChairEntered(PlayerControllerB player)
        {
            // Safety net for entry paths that bypass RequestEntry (debug tools,
            // the standalone-terminal interactable): claim late so the grey
            // button still locks and other clients still learn the feed state.
            int playerId = GetPlayerId(player);
            if (playerId < 0 || _operatorId == playerId) return;

            if (_message == null)
            {
                ApplyState(feedCctv: true, operatorId: playerId, "late-claim-offline");
                return;
            }
            SendRequest(new CCTVFeedStateMessage { Kind = KindRequestClaim, PlayerId = playerId });
        }

        private static void OnLocalChairExited(PlayerControllerB player)
        {
            ReleaseLocalEntryClaim(player, "chair-exited");
        }

        internal static void ReleaseLocalEntryClaim(PlayerControllerB player, string reason)
        {
            int playerId = GetPlayerId(player);
            if (playerId < 0 && IsLocalOperator)
                playerId = _operatorId;

            ClearPendingClaim();
            if (playerId < 0)
                return;

            SurveillanceBootstrap.Log?.LogDebug(
                $"[LethalCCTV] Releasing local CCTV entry claim ({reason ?? "unspecified"}) for player #{playerId}.");
            if (_message == null)
            {
                if (_operatorId == playerId)
                    ApplyState(_feedCctv, operatorId: -1, "offline-release-" + (reason ?? "unspecified"));
                return;
            }
            SendRequest(new CCTVFeedStateMessage { Kind = KindRequestRelease, PlayerId = playerId });
        }

        private static void SendRequest(CCTVFeedStateMessage message)
        {
            try
            {
                _message.SendServer(message);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Feed sync request stayed local: {ex.Message}");
                // Fail open locally so solo/offline behaviour matches pre-lock behaviour.
                switch (message.Kind)
                {
                    case KindRequestClaim:
                        PlayerControllerB player = _claimPlayer;
                        Action confirmed = _claimConfirmed;
                        ClearPendingClaim();
                        ApplyState(feedCctv: true, operatorId: message.PlayerId, "send-failed-claim");
                        confirmed?.Invoke();
                        break;
                    case KindRequestRelease:
                        if (_operatorId == message.PlayerId)
                            ApplyState(_feedCctv, operatorId: -1, "send-failed-release");
                        break;
                    case KindRequestToggle:
                        ApplyState(!_feedCctv, _operatorId, "send-failed-toggle");
                        break;
                }
            }
        }

        private static void OnServerReceived(CCTVFeedStateMessage data, ulong _)
        {
            if (data == null) return;

            switch (data.Kind)
            {
                case KindRequestClaim:
                    if (_serverOperatorId < 0 || _serverOperatorId == data.PlayerId)
                    {
                        _serverOperatorId = data.PlayerId;
                        _serverFeedCctv = true;
                        // New operator: forget the previous one's slot until the
                        // fresh operator reports its own via KindRequestActiveSlot.
                        _serverActiveSlot = -1;
                    }
                    break;
                case KindRequestRelease:
                    if (_serverOperatorId == data.PlayerId)
                    {
                        // #562 — feed AND slot stay as-is per spec. The wall keeps
                        // showing the camera the last operator left on it, and the
                        // throttle keeps that one camera rendering at the unmanned
                        // idle rate instead of freezing on its final frame.
                        _serverOperatorId = -1;
                    }
                    break;
                case KindRequestToggle:
                    if (_serverOperatorId < 0)
                        _serverFeedCctv = !_serverFeedCctv;
                    break;
                case KindRequestActiveSlot:
                    if (_serverOperatorId == data.PlayerId)
                        _serverActiveSlot = data.ActiveSlot;
                    break;
                default:
                    return;
            }

            BroadcastState();
        }

        private static void BroadcastState()
        {
            try
            {
                _message?.SendClients(new CCTVFeedStateMessage
                {
                    Kind = KindState,
                    PlayerId = -1,
                    FeedCctv = _serverFeedCctv ? (byte)1 : (byte)0,
                    OperatorId = _serverOperatorId,
                    ActiveSlot = _serverActiveSlot,
                });
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Feed sync broadcast failed: {ex.Message}");
            }
        }

        private static void OnClientReceived(CCTVFeedStateMessage data)
        {
            if (data == null || data.Kind != KindState) return;
            ApplyState(data.FeedCctv != 0, data.OperatorId, "network-state", activeSlot: data.ActiveSlot);
        }

        private static void ApplyState(bool feedCctv, int operatorId, string reason, int? activeSlot = null)
        {
            bool feedChanged = _feedCctv != feedCctv;
            bool operatorChanged = _operatorId != operatorId;
            _feedCctv = feedCctv;
            _operatorId = operatorId;

            if (activeSlot.HasValue)
            {
                _remoteActiveSlot = activeSlot.Value;
            }
            // #562 — a valid slot with no operator is NOT "unmanned, nothing to
            // show": it is "the last feed left on the display". Only the explicit
            // resets (orbit, new claim, local reset) clear the slot; consumers
            // that need "is somebody seated" must read HasOperator instead.
            if (operatorId < 0)
                _lastSentActiveSlot = -1;

            if (feedChanged || operatorChanged)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Monitor feed state applied ({reason}): feed={(feedCctv ? "cctv" : "vanilla")} operator={(operatorId < 0 ? "none" : "#" + operatorId)}.");
            }

            ApplyFeedToDisplay();
            ResolvePendingClaim();
        }

        /// <summary>
        /// Push the current feed state into the display hijack. Also the reapply
        /// hook for round lifecycle events (cameras ready, monitor respawn).
        /// </summary>
        internal static void ApplyFeedToDisplay()
        {
            bool wantCctv = _feedCctv && CCTVTerminalUnlockable.IsPurchased();
            // While the local enter animation is mid-press, the claim broadcast
            // must not flip the display early — MonitorFocus completes the flip
            // at press contact (and flushes it on any early exit).
            if (wantCctv && MonitorFocus.IsStationFeedFlipPending)
                return;
            CCTVVanillaMonitorDisplay.SetCctvModeActive(wantCctv, announce: false);
            if (wantCctv)
                CCTVVanillaMonitorDisplay.PrimeMonitorOwnershipNow(renderNow: false);
        }

        private static void ResolvePendingClaim()
        {
            if (_claimPlayer == null) return;

            int claimId = GetPlayerId(_claimPlayer);
            if (_operatorId == claimId && claimId >= 0)
            {
                Action confirmed = _claimConfirmed;
                ClearPendingClaim();
                confirmed?.Invoke();
            }
            else if (HasOperator)
            {
                Action<string> denied = _claimDenied;
                ClearPendingClaim();
                denied?.Invoke("Another employee is on the cameras.");
            }
            // No operator at all yet: state broadcast raced ahead of our claim;
            // keep waiting for the claim's own broadcast (or the timeout).
        }

        private static void ClearPendingClaim()
        {
            _claimPlayer = null;
            _claimConfirmed = null;
            _claimDenied = null;
            _claimTimeoutAt = 0f;
        }

        private static void ResetLocal(string reason)
        {
            _serverFeedCctv = false;
            _serverOperatorId = -1;
            _serverActiveSlot = -1;
            ClearPendingClaim();
            _feedCctv = false;
            _operatorId = -1;
            _remoteActiveSlot = -1;
            _lastSentActiveSlot = -1;
            SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Monitor feed state reset locally ({reason}).");
        }

        private static int GetPlayerId(PlayerControllerB player)
        {
            return player != null ? (int)player.playerClientId : -1;
        }
    }
}
