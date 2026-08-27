using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Security
{
    /// <summary>
    /// The alarm's monster-awareness ping (#466).
    ///
    /// Design constraint from the owner, verbatim in intent: "more of a one time awareness
    /// ping, they shouldn't just all walk to that location no matter what, especially if
    /// they're further away, but if they're close by they should reach the location... I
    /// don't want anything hard coded, I just want to assist the monsters in getting to that
    /// location in a light weight way."
    ///
    /// So this deliberately owns no AI. It raises one vanilla audible-noise event at the
    /// tripped camera and stops. <c>RoundManager.PlayAudibleNoise</c> (verified against the
    /// game assembly) does exactly two things: it halves the range if the noise is inside a
    /// closed ship, then <c>Physics.OverlapSphereNonAlloc</c>s the enemy layer at that radius
    /// and hands every <c>INoiseListener</c> it finds the position and the raw loudness. It
    /// applies no falloff of its own.
    ///
    /// That is precisely why this is the right mechanism and why the two dials are what they
    /// are. The radius is the whole "close by, not far away" rule - an enemy outside it is
    /// never told anything happened. Inside it, each species' own <c>DetectNoise</c> decides:
    /// its own distance and line-of-sight checks, its own loudness threshold, whether it is
    /// already chasing someone, whether it is asleep, whether it hears at all. Per-species
    /// deafness and "busy with something better" come free and correct, and no code here
    /// knows the name of a single enemy type.
    ///
    /// What this must never become: <c>SetDestinationToPosition</c>, a per-enemy loop, a
    /// species allow-list, or a repeating lure. Any of those is the hard-coded pathing the
    /// design rejected.
    ///
    /// Multiplayer: enemy AI is host-authoritative in Lethal Company - clients run
    /// presentation-only copies whose destinations are replicated from the host - so noise
    /// only changes behaviour when the host's instances hear it. The ping is therefore raised
    /// on the host only (its single call site, <see cref="CctvSecurityDirector.RefreshAlarmTimer"/>,
    /// is already behind an IsServerRuntime gate) and needs no replication of its own.
    /// Clients are not missing a cue: the alarm siren and lights they already hear and see
    /// are the player-facing half of the same event.
    /// </summary>
    internal static class CctvAlarmAwarenessPing
    {
        /// <summary>
        /// Vanilla's generic "something made a noise here" id. Deliberately not one of the
        /// special-cased ids (the boombox and shout ids some species branch on), because the
        /// point is an ordinary investigable noise rather than a species-specific reaction.
        /// </summary>
        private const int GenericNoiseId = 0;

        /// <summary>
        /// Range and loudness scale for the optional follow-up ping. Smaller on purpose: the
        /// second ping is a nudge for anything that started moving, not a second summons.
        /// </summary>
        /// <remarks>
        /// Config defaults for the first ping are 45m / 0.9, chosen against the vanilla call
        /// sites read out of Assembly-CSharp: the ship alarm cord runs 30m/0.8, the boombox
        /// 16m/0.9, a turret 15m/0.9, a jetpack 25m/0.85, a radar-booster ping 12m/0.8, and
        /// the item dropship - the loudest thing in the game - 60m/1.3. 45m/0.9 therefore
        /// sits above every handheld noisemaker and above the ship's own alarm, below the
        /// dropship, which is the "similar or slightly larger than a vanilla loud event"
        /// band the design asked for. The high loudness matters as much as the radius:
        /// several species gate DetectNoise on a loudness threshold, so a quiet ping inside
        /// the radius is ignored by exactly the enemies worth waking.
        /// </remarks>
        private const float SecondPingRangeScale = 0.6f;
        private const float SecondPingLoudnessScale = 0.7f;

        private static float _secondPingAt;
        private static Vector3 _secondPingPosition;
        private static float _secondPingRange;
        private static float _secondPingLoudness;

        internal static void ResetRound()
        {
            _secondPingAt = 0f;
        }

        /// <summary>
        /// Fires the one-shot ping for an alarm engage. Host-only by contract; the caller
        /// guarantees this runs exactly once per alarm episode, never on a refresh.
        /// </summary>
        internal static void EmitForAlarmEngage(Vector3 position, string cameraLabel, CctvSecurityConfig config)
        {
            _secondPingAt = 0f;
            if (!config.AwarenessPingEnabled || config.AwarenessPingRange <= 0f || config.AwarenessPingLoudness <= 0f)
                return;

            if (!Emit(position, config.AwarenessPingRange, config.AwarenessPingLoudness))
                return;

            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.CctvSecurity] AWARENESS_PING emitted at camera='{cameraLabel}' "
                + $"range={config.AwarenessPingRange:0.#}m loudness={config.AwarenessPingLoudness:0.00}.");

            if (!config.AwarenessSecondPingEnabled)
                return;

            _secondPingPosition = position;
            _secondPingRange = config.AwarenessPingRange * SecondPingRangeScale;
            _secondPingLoudness = config.AwarenessPingLoudness * SecondPingLoudnessScale;
            _secondPingAt = CctvSecurityDirector.SecurityTimeSeconds() + config.AwarenessSecondPingDelaySeconds;
        }

        /// <summary>
        /// Drives the optional follow-up ping off the director's existing host tick rather
        /// than a coroutine, so it shares the round lifecycle and cannot outlive a reset.
        /// </summary>
        internal static void Tick(float now)
        {
            if (_secondPingAt <= 0f || now < _secondPingAt)
                return;

            float range = _secondPingRange;
            float loudness = _secondPingLoudness;
            Vector3 position = _secondPingPosition;
            _secondPingAt = 0f;

            if (Emit(position, range, loudness))
            {
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.CctvSecurity] AWARENESS_PING follow-up emitted range={range:0.#}m loudness={loudness:0.00}.");
            }
        }

        private static bool Emit(Vector3 position, float range, float loudness)
        {
            RoundManager round = RoundManager.Instance;
            if (round == null || range <= 0f || loudness <= 0f)
                return false;

            try
            {
                // Signature verified against the game assembly:
                // PlayAudibleNoise(Vector3 noisePosition, float noiseRange, float noiseLoudness,
                //                  int timesPlayedInSameSpot, bool noiseIsInsideClosedShip, int noiseID)
                // timesPlayedInSameSpot stays 0: it exists so a repeating emitter can tell
                // enemies to stop re-reacting, and a one-shot ping is the opposite case.
                // noiseIsInsideClosedShip is false - the camera is in the facility.
                round.PlayAudibleNoise(position, range, Mathf.Clamp01(loudness), 0, false, GenericNoiseId);
                return true;
            }
            catch (System.Exception ex)
            {
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CctvSecurity] Awareness ping failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
