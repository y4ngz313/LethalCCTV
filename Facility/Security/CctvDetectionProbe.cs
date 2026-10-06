using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.Facility.Security
{
    internal static class CctvDetectionProbe
    {
        private const float DetectionRangeM = 28f;
        private const float DetectionHalfAngleDeg = 35f;

        internal static bool TryFindVisiblePlayer(CctvSecurityCameraState camera, out PlayerControllerB player)
        {
            return TryFindVisiblePlayer(camera, out player, out _);
        }

        /// <summary>
        /// <paramref name="player"/> is the nearest visible crew member — the one the
        /// camera tracks and the alarm is attributed to. <paramref name="sawLocalPlayer"/>
        /// answers the separate question the personal spotting alert needs: was *this*
        /// client's player in the cone at all, even standing behind someone closer. The
        /// loop already tests every player, so this costs no extra casts.
        /// </summary>
        internal static bool TryFindVisiblePlayer(
            CctvSecurityCameraState camera,
            out PlayerControllerB player,
            out bool sawLocalPlayer)
        {
            player = null;
            sawLocalPlayer = false;
            if (camera == null || camera.Transform == null || !camera.IsSecurityActive || camera.IsBroken) return false;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null) return false;
            PlayerControllerB local = StartOfRound.Instance.localPlayerController;

            float bestSqr = float.PositiveInfinity;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB candidate = players[i];
                if (!IsValidPlayer(candidate)) continue;
                if (!CanSeePlayer(camera.Transform, candidate, out float sqr)) continue;
                if (candidate == local) sawLocalPlayer = true;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                player = candidate;
            }
            return player != null;
        }

        // #1159: the single per-player gate the whole detection sweep passes through, so the
        // debug mob-ignore is applied here instead of at the alarm, the tracker and the
        // spotting alert separately. It is tested last: the vanilla flags are field reads and
        // the ignore lookup is a latched delegate that is null on every profile without
        // Y4NGZDebugTools.
        private static bool IsValidPlayer(PlayerControllerB player)
        {
            return player != null &&
                   player.isPlayerControlled &&
                   !player.isPlayerDead &&
                   !player.isInElevator &&
                   !player.isInHangarShipRoom &&
                   !MobIgnoreBridge.IsIgnored(player.actualClientId);
        }

        private static bool CanSeePlayer(Transform camera, PlayerControllerB player, out float distanceSqr)
        {
            Vector3 target = player.transform.position + Vector3.up * 0.8f;
            Vector3 offset = target - camera.position;
            distanceSqr = offset.sqrMagnitude;
            if (distanceSqr > DetectionRangeM * DetectionRangeM) return false;

            Vector3 dir = offset.normalized;
            float dot = Vector3.Dot(camera.forward, dir);
            if (dot < Mathf.Cos(DetectionHalfAngleDeg * Mathf.Deg2Rad)) return false;

            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (Physics.Linecast(camera.position, target, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
            {
                PlayerControllerB hitPlayer = hit.collider != null ? hit.collider.GetComponentInParent<PlayerControllerB>() : null;
                if (hitPlayer != player && (hit.transform == null || !hit.transform.IsChildOf(player.transform)))
                    return false;
            }

            return true;
        }
    }
}
