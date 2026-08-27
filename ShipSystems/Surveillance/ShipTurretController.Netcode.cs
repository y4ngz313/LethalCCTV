using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using GameNetcodeStuff;
using Y4NGZCompany.Core.Compat;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class ShipTurretController
    {
        private static void EnsureNetworkHandlers()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsListening) return;
            if (_networkHandlersRegistered && ReferenceEquals(_registeredNetworkManager, nm)) return;
            if (_networkHandlersRegistered) UnregisterNetworkHandlers();

            try
            {
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgShotRequest, OnReceiveShotRequest);
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgShotResult, OnReceiveShotResult);
                _networkHandlersRegistered = true;
                _registeredNetworkManager = nm;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] Network handler registration failed: {ex.Message}");
            }
        }

        private static void UnregisterNetworkHandlers()
        {
            if (!_networkHandlersRegistered || _registeredNetworkManager == null || _registeredNetworkManager.CustomMessagingManager == null)
            {
                _networkHandlersRegistered = false;
                _registeredNetworkManager = null;
                return;
            }

            try { _registeredNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MsgShotRequest); } catch { }
            try { _registeredNetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MsgShotResult); } catch { }
            _networkHandlersRegistered = false;
            _registeredNetworkManager = null;
        }

        private static void OnReceiveShotRequest(ulong senderClientId, FastBufferReader reader)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            try
            {
                Vector3 origin = ReadVector(ref reader);
                Vector3 direction = ReadVector(ref reader).normalized;
                ProcessShotRequest(senderClientId, origin, direction);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] Malformed shot request: {ex.Message}");
            }
        }

        private static void ProcessShotRequest(ulong senderClientId, Vector3 origin, Vector3 direction)
        {
            bool accepted = false;
            bool hit = false;
            bool enemyHit = false;
            Vector3 hitPoint = origin + direction * ShotDistance;
            Vector3 hitNormal = Vector3.up;
            float now = Time.realtimeSinceStartup;

            if (!IsUnlocked)
            {
                BroadcastShotResult(senderClientId, false, false, false, origin, direction, hitPoint, hitNormal);
                return;
            }

            if (ServerNextShotAt.TryGetValue(senderClientId, out float nextAllowed) && now < nextAllowed)
            {
                BroadcastShotResult(senderClientId, false, false, false, origin, direction, hitPoint, hitNormal);
                return;
            }

            if (!CCTVShipSystemsBridge.TrySpendTurretShotPower())
            {
                BroadcastShotResult(senderClientId, false, false, false, origin, direction, hitPoint, hitNormal);
                return;
            }

            accepted = true;
            ServerNextShotAt[senderClientId] = now + ShotCooldownSeconds;

            // Bundy noise bus (3b follow-up). An accepted shot proves a player is seated at the
            // CCTV console at this exact moment, so the report carries the operator's position,
            // not the muzzle -- the shot is how Bundy learns where the SHOOTER is.
            PlayerControllerB shotOperator = FindPlayerByClientId(senderClientId);
            if (shotOperator != null)
            {
                BundyWorldNoiseBridge.Report(
                    shotOperator.transform.position, BundyWorldNoiseBridge.ShipTurretFire, shotOperator);
            }

            if (TryResolveShotHit(origin, direction, out RaycastHit rayHit, out EnemyAI enemy))
            {
                hit = true;
                hitPoint = rayHit.point;
                hitNormal = rayHit.normal.sqrMagnitude > 0.001f ? rayHit.normal : -direction;
                if (enemy != null)
                {
                    enemyHit = TryDamageEnemy(rayHit.collider, enemy, direction);
                }
            }

            BroadcastShotResult(senderClientId, accepted, hit, enemyHit, origin, direction, hitPoint, hitNormal);
        }

        private static PlayerControllerB FindPlayerByClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && (player.actualClientId == clientId || player.playerClientId == clientId))
                    return player;
            }

            return null;
        }

        private static bool TryResolveShotHit(Vector3 origin, Vector3 direction, out RaycastHit bestHit, out EnemyAI enemy)
        {
            bestHit = default;
            enemy = null;
            RaycastHit firstWorldHit = default;
            bool hasWorldHit = false;
            RaycastHit firstEnemyHit = default;
            EnemyAI firstEnemy = null;

            int rayCount = Physics.RaycastNonAlloc(origin, direction, ShotHits, ShotDistance, ~0, QueryTriggerInteraction.Collide);
            ConsiderShotHits(ShotHits, rayCount, ref hasWorldHit, ref firstWorldHit, ref firstEnemyHit, ref firstEnemy);

            int sphereCount = Physics.SphereCastNonAlloc(origin, EnemyShotRadius, direction, ShotSphereHits, ShotDistance, ~0, QueryTriggerInteraction.Collide);
            ConsiderShotHits(ShotSphereHits, sphereCount, ref hasWorldHit, ref firstWorldHit, ref firstEnemyHit, ref firstEnemy);

            if (firstEnemy != null && (!hasWorldHit || firstEnemyHit.distance <= firstWorldHit.distance + EnemyPriorityLeeway))
            {
                bestHit = firstEnemyHit;
                enemy = firstEnemy;
                return true;
            }

            if (hasWorldHit)
            {
                bestHit = firstWorldHit;
                return true;
            }

            return false;
        }

        private static void ConsiderShotHits(
            RaycastHit[] hits,
            int count,
            ref bool hasWorldHit,
            ref RaycastHit firstWorldHit,
            ref RaycastHit firstEnemyHit,
            ref EnemyAI firstEnemy)
        {
            if (hits == null || count <= 0)
                return;

            Array.Sort(hits, 0, count, RaycastHitDistanceComparer.Instance);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = hits[i];
                hits[i] = default;
                Collider collider = hit.collider;
                if (collider == null) continue;
                if (_root != null && collider.transform.IsChildOf(_root.transform)) continue;
                if (collider.GetComponentInParent<PlayerControllerB>() != null) continue;

                EnemyAI hitEnemy = ResolveEnemyFromCollider(collider);
                if (hitEnemy != null && !hitEnemy.isEnemyDead)
                {
                    if (firstEnemy == null || hit.distance < firstEnemyHit.distance)
                    {
                        firstEnemy = hitEnemy;
                        firstEnemyHit = hit;
                    }
                    continue;
                }

                if (!hasWorldHit || hit.distance < firstWorldHit.distance)
                {
                    hasWorldHit = true;
                    firstWorldHit = hit;
                }
            }
        }

        private static EnemyAI ResolveEnemyFromCollider(Collider collider)
        {
            if (collider == null)
                return null;

            EnemyAICollisionDetect detect = collider.GetComponent<EnemyAICollisionDetect>() ?? collider.GetComponentInParent<EnemyAICollisionDetect>();
            if (detect != null && detect.mainScript != null)
                return detect.mainScript;

            return collider.GetComponentInParent<EnemyAI>();
        }

        private static bool TryDamageEnemy(Collider collider, EnemyAI enemy, Vector3 direction)
        {
            if (enemy == null || enemy.isEnemyDead) return false;

            try
            {
                bool applied = false;
                EnemyAICollisionDetect detect = collider != null
                    ? collider.GetComponent<EnemyAICollisionDetect>() ?? collider.GetComponentInParent<EnemyAICollisionDetect>()
                    : null;
                if (detect != null)
                {
                    IHittable hittable = detect;
                    applied = hittable.Hit(ShotDamage, direction, null, playHitSFX: true, TurretHitId);
                }

                if (!applied)
                    enemy.HitEnemy(ShotDamage, null, playHitSFX: true, TurretHitId);
                return true;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] Enemy damage failed: {ex.Message}");
                return false;
            }
        }

        private static void BroadcastShotResult(
            ulong requester,
            bool accepted,
            bool hit,
            bool enemyHit,
            Vector3 origin,
            Vector3 direction,
            Vector3 hitPoint,
            Vector3 hitNormal)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer) return;

            FastBufferWriter writer = new FastBufferWriter(160, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(requester);
                writer.WriteValueSafe(accepted);
                writer.WriteValueSafe(hit);
                writer.WriteValueSafe(enemyHit);
                WriteVector(ref writer, origin);
                WriteVector(ref writer, direction);
                WriteVector(ref writer, hitPoint);
                WriteVector(ref writer, hitNormal);
                nm.CustomMessagingManager.SendNamedMessageToAll(MsgShotResult, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnReceiveShotResult(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                ulong requester;
                bool accepted;
                bool hit;
                bool enemyHit;
                reader.ReadValueSafe(out requester);
                reader.ReadValueSafe(out accepted);
                reader.ReadValueSafe(out hit);
                reader.ReadValueSafe(out enemyHit);
                Vector3 origin = ReadVector(ref reader);
                Vector3 direction = ReadVector(ref reader);
                Vector3 hitPoint = ReadVector(ref reader);
                Vector3 hitNormal = ReadVector(ref reader);

                NetworkManager nm = NetworkManager.Singleton;
                bool localRequest = nm != null && requester == nm.LocalClientId;
                if (!accepted)
                {
                    if (localRequest)
                    {
                        _nextLocalShotAt = Mathf.Min(_nextLocalShotAt, Time.realtimeSinceStartup + 0.4f);
                        SetStatus("NO POWER / LOCKED", 1.2f);
                    }
                    return;
                }

                PlayFireAudio();
                CCTVShipSystemsBridge.NoteTurretShotAccepted();
                TriggerRecoil();
                SpawnMuzzleEffects(origin, direction, hit ? hitPoint : origin + direction * ShotDistance);
                if (hit)
                {
                    SpawnImpact(hitPoint, hitNormal);
                    if (localRequest)
                        MonitorFocus.ShowTurretHitmarker(enemyHit);
                }
                if (localRequest)
                    SetStatus(hit ? (enemyHit ? "TARGET HIT" : "IMPACT") : "FIRED", 1.0f);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] Malformed shot result: {ex.Message}");
            }
        }

        private static void WriteVector(ref FastBufferWriter writer, Vector3 value)
        {
            writer.WriteValueSafe(value.x);
            writer.WriteValueSafe(value.y);
            writer.WriteValueSafe(value.z);
        }

        private static Vector3 ReadVector(ref FastBufferReader reader)
        {
            float x;
            float y;
            float z;
            reader.ReadValueSafe(out x);
            reader.ReadValueSafe(out y);
            reader.ReadValueSafe(out z);
            return new Vector3(x, y, z);
        }

        private sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
        {
            internal static readonly RaycastHitDistanceComparer Instance = new RaycastHitDistanceComparer();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

    }
}
