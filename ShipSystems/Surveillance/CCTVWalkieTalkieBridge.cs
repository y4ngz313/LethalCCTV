using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using LethalNetworkAPI;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    [Serializable]
    public sealed class CCTVWalkieMessage
    {
        public int Id;
        public int PlayerId;
        public bool Speaking;
    }

    internal static class CCTVWalkieTalkieBridge
    {
        private const string CHANNEL = "cctv_walkie_v1";
        // Vanilla plays the transmission clicks off the walkie's own AudioSource at unit volume,
        // which is already attenuated by distance. This is the 2D stand-in for an operator who is
        // not holding one, so it is trimmed to roughly what a held unit sounds like in-hand.
        private const float OperatorTransmissionVolume = 0.6f;

        private static LNetworkMessage<CCTVWalkieMessage> _message;
        private static readonly HashSet<int> _seenIds = new HashSet<int>();
        private static readonly HashSet<int> _activeVirtualSpeakers = new HashSet<int>();
        private static readonly Dictionary<WalkieTalkie, bool> _priorSpeakerStates = new Dictionary<WalkieTalkie, bool>();
        private static int _nextId;
        private static PlayerControllerB _listeningPlayer;
        private static bool _priorHoldingWalkieTalkie;
        private static bool _localSpeaking;
        private static AudioClip[] _startTransmissionClips;
        private static AudioClip[] _stopTransmissionClips;

        internal static bool IsTransmitting => _localSpeaking;
        internal static bool IsListening => _listeningPlayer != null;

        internal static void Initialize()
        {
            if (_message != null) return;
            _message = LNetworkMessage<CCTVWalkieMessage>.Connect(
                CHANNEL,
                onServerReceived: OnServerReceived,
                onClientReceived: OnClientReceived);
        }

        internal static void Shutdown()
        {
            ExitFocus();
            _message?.ClearSubscriptions();
            _message = null;
            _seenIds.Clear();
            _activeVirtualSpeakers.Clear();
            _priorSpeakerStates.Clear();
            // The cached arrays belong to a walkie prefab from the session being torn down.
            _startTransmissionClips = null;
            _stopTransmissionClips = null;
        }

        internal static void EnterFocus(PlayerControllerB player)
        {
            if (player == null) return;
            Initialize();

            if (_listeningPlayer != player)
            {
                ExitFocus();
                _listeningPlayer = player;
                _priorHoldingWalkieTalkie = player.holdingWalkieTalkie;
            }

            player.holdingWalkieTalkie = true;
            UpdateVoiceEffects();
        }

        internal static void ExitFocus()
        {
            SetLocalSpeaking(false);

            if (_listeningPlayer != null)
            {
                _listeningPlayer.holdingWalkieTalkie = _priorHoldingWalkieTalkie;
                _listeningPlayer = null;
            }
            _priorHoldingWalkieTalkie = false;
            UpdateVoiceEffects();
        }

        internal static void SetLocalSpeaking(bool speaking)
        {
            if (speaking && SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowWalkieTalkie.Value)
                return;

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null)
            {
                _localSpeaking = false;
                return;
            }

            if (!speaking && !_localSpeaking)
            {
                return;
            }
            if (speaking && _localSpeaking && player.speakingToWalkieTalkie)
            {
                return;
            }

            if (speaking && _listeningPlayer == null)
            {
                EnterFocus(player);
            }

            _localSpeaking = speaking;
            var data = new CCTVWalkieMessage
            {
                Id = NextId(),
                PlayerId = (int)player.playerClientId,
                Speaking = speaking,
            };

            if (ApplySpeaking(data, localSender: true))
                PlayLocalOperatorTransmissionSfx(speaking);

            if (_message == null) return;
            try
            {
                _message.SendServer(data);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV walkie stayed local: {ex.Message}");
            }
        }

        private static int NextId()
        {
            unchecked
            {
                int client = GameNetworkManager.Instance?.localPlayerController != null
                    ? (int)GameNetworkManager.Instance.localPlayerController.playerClientId
                    : 0;
                _nextId++;
                return (client << 20) ^ _nextId ^ Mathf.RoundToInt(Time.realtimeSinceStartup * 1000f);
            }
        }

        private static void OnServerReceived(CCTVWalkieMessage data, ulong _)
        {
            if (data.Speaking && SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowWalkieTalkie.Value)
                return;

            try
            {
                _message?.SendClients(data);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV walkie server broadcast failed: {ex.Message}");
            }
        }

        private static void OnClientReceived(CCTVWalkieMessage data)
        {
            ApplySpeaking(data, localSender: false);
        }

        /// <summary>Returns whether the change was actually applied; a duplicate id or an
        /// unresolvable player means nothing happened and callers must not react as if it had.</summary>
        private static bool ApplySpeaking(CCTVWalkieMessage data, bool localSender)
        {
            if (data == null) return false;
            if (_seenIds.Contains(data.Id)) return false;
            _seenIds.Add(data.Id);

            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.allPlayerScripts == null) return false;
            if (data.PlayerId < 0 || data.PlayerId >= sor.allPlayerScripts.Length) return false;

            PlayerControllerB player = sor.allPlayerScripts[data.PlayerId];
            if (player == null) return false;

            player.speakingToWalkieTalkie = data.Speaking;
            if (localSender)
            {
                player.activatingItem = data.Speaking;
                if (player.playerBodyAnimator != null)
                {
                    player.playerBodyAnimator.SetBool("walkieTalkie", data.Speaking);
                }
            }

            if (data.Speaking)
            {
                _activeVirtualSpeakers.Add(data.PlayerId);
            }
            else
            {
                _activeVirtualSpeakers.Remove(data.PlayerId);
            }
            SetVirtualSpeakerState(_activeVirtualSpeakers.Count > 0);
            PlayTransmissionSfx(data.PlayerId, data.Speaking);
            UpdateVoiceEffects();
            return true;
        }

        private static void SetVirtualSpeakerState(bool speaking)
        {
            if (WalkieTalkie.allWalkieTalkies == null) return;
            if (!speaking)
            {
                foreach (KeyValuePair<WalkieTalkie, bool> pair in _priorSpeakerStates)
                {
                    if (pair.Key != null)
                    {
                        pair.Key.clientIsHoldingAndSpeakingIntoThis = pair.Value;
                    }
                }
                _priorSpeakerStates.Clear();
                return;
            }

            for (int i = 0; i < WalkieTalkie.allWalkieTalkies.Count; i++)
            {
                WalkieTalkie walkie = WalkieTalkie.allWalkieTalkies[i];
                if (walkie == null) continue;
                if (!walkie.isBeingUsed) continue;
                if (!_priorSpeakerStates.ContainsKey(walkie))
                {
                    _priorSpeakerStates[walkie] = walkie.clientIsHoldingAndSpeakingIntoThis;
                }
                walkie.clientIsHoldingAndSpeakingIntoThis = speaking;
            }
        }

        private static void PlayTransmissionSfx(int playerId, bool speaking)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || WalkieTalkie.allWalkieTalkies == null) return;

            PlayerControllerB speakingPlayer = playerId >= 0 && playerId < sor.allPlayerScripts.Length
                ? sor.allPlayerScripts[playerId]
                : null;

            for (int i = 0; i < WalkieTalkie.allWalkieTalkies.Count; i++)
            {
                WalkieTalkie walkie = WalkieTalkie.allWalkieTalkies[i];
                if (walkie == null || !walkie.isBeingUsed || walkie.thisAudio == null) continue;
                if (speakingPlayer != null && walkie.playerHeldBy == speakingPlayer) continue;
                if (IsPocketedHeldWalkie(walkie)) continue;

                AudioClip[] clips = speaking ? walkie.startTransmissionSFX : walkie.stopTransmissionSFX;
                if (clips == null || clips.Length == 0) continue;
                RoundManager.PlayRandomClip(walkie.thisAudio, clips);
            }
        }

        /// <summary>
        /// #578 — the transmitting end used to key up in silence. PlayTransmissionSfx deliberately
        /// skips the speaking player's own walkie, so nothing sounded the click for the operator.
        /// This plays the vanilla start/stop clips locally in 2D.
        ///
        /// It runs unconditionally, holding a walkie included, and that cannot double up: vanilla's
        /// click rides the clientIsHoldingAndSpeakingIntoThis setter, which SetVirtualSpeakerState
        /// writes to directly rather than through, and SetLocalSpeaking is only ever reached from
        /// bridge paths (station focus and the mainframe intercom) — never from vanilla ItemActivate.
        /// </summary>
        private static void PlayLocalOperatorTransmissionSfx(bool speaking)
        {
            AudioSource ui = HUDManager.Instance != null ? HUDManager.Instance.UIAudio : null;
            if (ui == null) return;

            AudioClip[] clips = ResolveTransmissionClips(speaking);
            if (clips == null || clips.Length == 0) return;

            ui.PlayOneShot(clips[UnityEngine.Random.Range(0, clips.Length)], OperatorTransmissionVolume);
        }

        /// <summary>Sources the clip arrays from any loaded walkie, falling back to the item prefab
        /// in StartOfRound's item list for a round where nobody has one spawned. The arrays are the
        /// same shared assets on every walkie, so they are cached once resolved — but a failed
        /// resolve is never cached, so a round that loads the item later still gets its clicks.</summary>
        private static AudioClip[] ResolveTransmissionClips(bool speaking)
        {
            if (_startTransmissionClips != null && _stopTransmissionClips != null)
                return speaking ? _startTransmissionClips : _stopTransmissionClips;

            if (WalkieTalkie.allWalkieTalkies != null)
            {
                for (int i = 0; i < WalkieTalkie.allWalkieTalkies.Count; i++)
                {
                    if (TryCacheTransmissionClips(WalkieTalkie.allWalkieTalkies[i]))
                        return speaking ? _startTransmissionClips : _stopTransmissionClips;
                }
            }

            StartOfRound sor = StartOfRound.Instance;
            List<Item> items = sor != null && sor.allItemsList != null
                ? sor.allItemsList.itemsList
                : null;
            if (items == null) return null;

            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null || item.spawnPrefab == null) continue;
                if (TryCacheTransmissionClips(item.spawnPrefab.GetComponent<WalkieTalkie>()))
                    return speaking ? _startTransmissionClips : _stopTransmissionClips;
            }
            return null;
        }

        private static bool TryCacheTransmissionClips(WalkieTalkie walkie)
        {
            if (walkie == null) return false;
            AudioClip[] start = walkie.startTransmissionSFX;
            AudioClip[] stop = walkie.stopTransmissionSFX;
            if (start == null || start.Length == 0 || stop == null || stop.Length == 0) return false;

            _startTransmissionClips = start;
            _stopTransmissionClips = stop;
            return true;
        }

        private static bool IsPocketedHeldWalkie(WalkieTalkie walkie)
        {
            if (walkie == null || walkie.playerHeldBy == null) return false;
            GrabbableObject held = walkie.playerHeldBy.currentlyHeldObjectServer;
            if (held == null) return false;
            if (held.GetComponent<WalkieTalkie>() == null) return false;
            return walkie.isPocketed;
        }

        private static void UpdateVoiceEffects()
        {
            if (StartOfRound.Instance == null) return;
            try
            {
                StartOfRound.Instance.UpdatePlayerVoiceEffects();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV walkie voice update skipped: {ex.Message}");
            }
        }
    }
}
