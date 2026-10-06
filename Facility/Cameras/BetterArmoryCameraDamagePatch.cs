using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// Optional BetterArmory compatibility. Native Y4NGZ firearms trace solid world with
    /// QueryTriggerInteraction.Ignore, which is correct for impacts but necessarily skips the
    /// trigger-only CCTV hitbox. This postfix reconstructs the exact deterministic pellet rays
    /// and applies the weapon's enemy-force falloff on the server only.
    /// </summary>
    [HarmonyPatch]
    internal static class BetterArmoryCameraDamagePatch
    {
        private const string BallisticsTypeName = "Y4NGZUpgrades.Weapons.Y4NGZWeaponBallistics";
        private const float MinimumImpactDistance = 0.05f;

        private static Type _configType;
        private static FieldInfo _pelletCount;
        private static FieldInfo _maxRangeMeters;
        private static FieldInfo _enemyDamageClose;
        private static FieldInfo _enemyDamageFar;
        private static bool _configShapeWarningLogged;
        private static bool _shotWarningLogged;

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            Type ballistics = AccessTools.TypeByName(BallisticsTypeName);
            return ballistics != null ? AccessTools.Method(ballistics, "ResolveShot") : null;
        }

        [HarmonyPostfix]
        private static void Postfix(
            object config,
            Vector3 origin,
            Vector3 __3,
            uint seed,
            float effectiveSpreadDegrees,
            PlayerControllerB firedBy)
        {
            try
            {
                Vector3 shotForward = __3;
                // #716 G5: this used to read "network != null && !network.IsServer", so a null
                // NetworkManager fell through as if this client were the server and applied
                // camera damage it had no authority for. CctvNetworkRole answers false when it
                // cannot show otherwise.
                if (!CctvNetworkRole.IsServer())
                    return;
                if (!(SurveillanceBootstrap.Config?.BreakableCameras?.Value ?? true))
                    return;
                if (config == null || shotForward.sqrMagnitude < 1e-6f)
                    return;
                if (!TryResolveConfigFields(config.GetType()))
                    return;

                int pellets = Mathf.Max(1, Convert.ToInt32(_pelletCount.GetValue(config)));
                float range = Mathf.Max(1f, Convert.ToSingle(_maxRangeMeters.GetValue(config)));
                int closeForce = Mathf.Max(0, Convert.ToInt32(_enemyDamageClose.GetValue(config)));
                int farForce = Mathf.Max(0, Convert.ToInt32(_enemyDamageFar.GetValue(config)));
                float spread = Mathf.Max(0f, effectiveSpreadDegrees);
                int worldMask = StartOfRound.Instance != null
                    ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                    : ~0;

                for (int pellet = 0; pellet < pellets; pellet++)
                {
                    Vector3 direction = ApplySpread(shotForward, spread, seed, pellet);
                    bool rayHit = Physics.Raycast(
                        origin,
                        direction,
                        out RaycastHit worldHit,
                        range,
                        worldMask,
                        QueryTriggerInteraction.Ignore);
                    float reach = rayHit && worldHit.distance >= MinimumImpactDistance
                        ? worldHit.distance
                        : range;

                    if (!CctvBreakableCamera.TryRaycastNearest(
                            origin,
                            direction,
                            reach,
                            out CctvBreakableCamera camera,
                            out float distance))
                    {
                        continue;
                    }

                    float t = Mathf.Clamp01(distance / range);
                    int force = Mathf.Max(0, Mathf.RoundToInt(Mathf.Lerp(closeForce, farForce, t)));
                    if (force > 0)
                        camera.Hit(force, direction, firedBy, playHitSFX: true);
                }
            }
            catch (Exception exception)
            {
                if (_shotWarningLogged)
                    return;
                _shotWarningLogged = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] BetterArmory camera damage pass failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private static bool TryResolveConfigFields(Type configType)
        {
            if (_configType == configType)
                return _pelletCount != null
                    && _maxRangeMeters != null
                    && _enemyDamageClose != null
                    && _enemyDamageFar != null;

            _configType = configType;
            _pelletCount = AccessTools.Field(configType, "PelletCount");
            _maxRangeMeters = AccessTools.Field(configType, "MaxRangeMeters");
            _enemyDamageClose = AccessTools.Field(configType, "EnemyDamageClose");
            _enemyDamageFar = AccessTools.Field(configType, "EnemyDamageFar");

            bool valid = _pelletCount != null
                && _maxRangeMeters != null
                && _enemyDamageClose != null
                && _enemyDamageFar != null;
            if (!valid && !_configShapeWarningLogged)
            {
                _configShapeWarningLogged = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] BetterArmory WeaponConfig fields changed; native weapon camera damage is disabled for this session.");
            }
            return valid;
        }

        private static Vector3 ApplySpread(Vector3 directionBase, float spreadDegrees, uint seed, int pelletIndex)
        {
            Vector3 axis = Vector3.Normalize(directionBase);
            if (spreadDegrees <= 0f)
                return axis;

            uint hash = Hash(seed, (uint)pelletIndex);
            float angle01 = (hash & 0xFFFFu) / 65535f;
            float radius01 = ((hash >> 16) & 0xFFFFu) / 65535f;
            float theta = angle01 * Mathf.PI * 2f;
            float spread = Mathf.Sqrt(radius01) * spreadDegrees;
            Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.95f
                ? Vector3.right
                : Vector3.up;
            Vector3 right = Vector3.Normalize(Vector3.Cross(reference, axis));
            Vector3 up = Vector3.Cross(axis, right);
            Vector3 offsetAxis = right * Mathf.Cos(theta) + up * Mathf.Sin(theta);
            return Quaternion.AngleAxis(spread, offsetAxis) * axis;
        }

        private static uint Hash(uint a, uint b)
        {
            unchecked
            {
                uint hash = a ^ (b * 0x9E3779B9u);
                hash ^= hash >> 15;
                hash *= 0x2C1B3C6Du;
                hash ^= hash >> 12;
                hash *= 0x297A2D39u;
                hash ^= hash >> 15;
                return hash;
            }
        }
    }
}
