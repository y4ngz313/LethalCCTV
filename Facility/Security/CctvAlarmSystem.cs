using System.Collections.Generic;
using UnityEngine;

using Y4NGZCompany.Facility.Mainframe;
namespace Y4NGZCompany.Facility.Security
{
    public static class CctvAlarmSystem
    {
        private static readonly List<EnemyAI> InteriorEnemyBuffer = new List<EnemyAI>(32);
        private static bool _alarmWasActive;
        private static float _nextPulseAt;

        internal static void ResetRunState()
        {
            InteriorEnemyBuffer.Clear();
            _alarmWasActive = false;
            _nextPulseAt = 0f;
        }

        public static void Tick()
        {
            MainframeSupport active = MainframeSupport.Active;
            bool alarmActive = active != null && active.IsAlarmOn;
            if (!alarmActive)
            {
                _alarmWasActive = false;
                _nextPulseAt = 0f;
                return;
            }

            float now = Time.unscaledTime;
            if (!_alarmWasActive)
            {
                _alarmWasActive = true;
                _nextPulseAt = now + EnemyLocationPulseService.PulseIntervalSeconds;
                PulseAllInteriorEnemies(now);
                return;
            }

            if (now + 0.001f < _nextPulseAt)
                return;

            _nextPulseAt = now + EnemyLocationPulseService.PulseIntervalSeconds;
            PulseAllInteriorEnemies(now);
        }

        private static void PulseAllInteriorEnemies(float now)
        {
            InteriorEnemyBuffer.Clear();
            List<EnemyAI> spawnedEnemies = RoundManager.Instance?.SpawnedEnemies;
            if (spawnedEnemies == null)
                return;

            for (int i = 0; i < spawnedEnemies.Count; i++)
            {
                EnemyAI enemy = spawnedEnemies[i];
                if (EnemyLocationPulseService.IsValidInteriorEnemy(enemy))
                    InteriorEnemyBuffer.Add(enemy);
            }

            EnemyLocationPulseService.PulseEnemies(InteriorEnemyBuffer, now);
        }
    }
}
