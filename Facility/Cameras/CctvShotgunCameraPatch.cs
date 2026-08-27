using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// #563: lets a shotgun break a physical CCTV camera.
    ///
    /// Vanilla's <see cref="ShotgunItem.ShootGun"/> can never reach one on its own. Its
    /// damage pass spherecasts layer 19 only (Enemies) and then requires an
    /// <c>EnemyAICollisionDetect</c> on the hit transform before it will even look for an
    /// <c>IHittable</c>; the camera hitbox is a Props-layer trigger with no enemy
    /// component, so it fails both tests. The shovel reaches it because
    /// <c>Shovel.HitShovel</c> casts a mask that includes Props and resolves IHittable
    /// directly. This postfix gives the shotgun the equivalent pass, scoped to CCTV
    /// cameras only, and leaves every other vanilla interaction untouched.
    ///
    /// The pass iterates the breakable-camera registry directly rather than physics
    /// sweeping the Props layer: every piece of loose scrap lives on Props too, and a
    /// NonAlloc sphere cast returns UNORDERED results truncated at its buffer size, so
    /// in a looted corridor the camera hitbox could silently fall off the end of the
    /// buffer and the shell would do nothing. A facility holds a handful of cameras, so
    /// testing each against the shot envelope is both exact and cheaper than the cast.
    ///
    /// Multiplayer: ShootGun runs on every client (the owner calls it directly, everyone
    /// else through ShootGunClientRpc), so the postfix runs everywhere too. It acts only
    /// for the item's owner, which is exactly the gate vanilla puts on its own damage pass
    /// (<c>if (!base.IsOwner) return;</c>) and the same one client the shovel path runs on
    /// (HitShovel is owner-only). That matters because <see cref="CctvBreakableCamera"/>
    /// tracks health locally and replicates only the resulting break, through
    /// <c>CctvCameraShutdownSync.RequestPhysicalBreak</c> - the host registers it and
    /// broadcasts a snapshot that every client applies. Letting every client run its own
    /// damage pass would instead decrement four separate health counters off one shell and
    /// break the camera at a quarter of its configured health.
    ///
    /// A nutcracker's shotgun also fires through ShootGun (server-owned, so the owner
    /// gate passes exactly once, on the host, with <c>playerHeldBy == null</c> - which
    /// <see cref="CctvBreakableCamera.Hit"/> tolerates). Stray enemy fire wrecking a
    /// camera near the crew is deliberate: the shell does not care what it hits.
    /// </summary>
    [HarmonyPatch(typeof(ShotgunItem), "ShootGun")]
    internal static class CctvShotgunCameraPatch
    {
        // Mirrors the shot envelope vanilla uses for its own falloff tiers: 30m of reach
        // and the 30-degree spread cone it tests the local player against.
        private const float ShotRangeM = 30f;
        private const float ConeHalfAngleDeg = 30f;
        // Two tiers, matching the spirit of vanilla's 5/3/2 enemy falloff without copying
        // its exact thresholds: point blank destroys a default-health camera outright.
        private const float CloseRangeM = 6f;
        private const int CloseRangeForce = 3;
        private const int LongRangeForce = 2;

        private static readonly List<CctvBreakableCamera> Candidates = new List<CctvBreakableCamera>(8);

        [HarmonyPostfix]
        private static void Postfix(ShotgunItem __instance, Vector3 shotgunPosition, Vector3 shotgunForward)
        {
            try
            {
                TryHitCameras(__instance, shotgunPosition, shotgunForward);
            }
            catch (System.Exception ex)
            {
                // A throwing postfix would take the whole shot with it; a shotgun that
                // cannot break a camera is a far smaller failure than a shotgun that
                // cannot fire.
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Shotgun camera pass failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void TryHitCameras(ShotgunItem shotgun, Vector3 muzzlePosition, Vector3 muzzleForward)
        {
            if (shotgun == null || !shotgun.IsOwner)
                return;
            Vector3 forward = muzzleForward;
            if (forward.sqrMagnitude < 1e-4f)
                return;

            StartOfRound round = StartOfRound.Instance;
            if (round == null)
                return;

            forward = forward.normalized;
            CctvBreakableCamera.CollectLive(Candidates);
            for (int i = 0; i < Candidates.Count; i++)
            {
                CctvBreakableCamera breakable = Candidates[i];
                if (breakable == null)
                    continue;

                Vector3 point = breakable.WorldHitPoint;
                Vector3 toTarget = point - muzzlePosition;
                float distance = toTarget.magnitude;
                if (distance > ShotRangeM)
                    continue;
                if (distance > 0.01f && Vector3.Angle(forward, toTarget) > ConeHalfAngleDeg)
                    continue;

                // Same occlusion test vanilla runs before damaging an enemy: a wall
                // between the muzzle and the camera stops the shell. Triggers are
                // ignored, so the camera's own hitbox can never shadow itself.
                if (Physics.Linecast(
                        muzzlePosition,
                        point,
                        round.collidersAndRoomMaskAndDefault,
                        QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                int force = distance < CloseRangeM ? CloseRangeForce : LongRangeForce;
                breakable.Hit(force, forward, shotgun.playerHeldBy, playHitSFX: true);
            }

            Candidates.Clear();
        }
    }
}
