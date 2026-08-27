using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;
namespace Y4NGZCompany.Facility.Security
{
    public readonly struct CctvSecurityConfig
    {
        /// <summary>
        /// Global time-to-detection slowdown (#466). Deliberately a constant rather than a
        /// change to the config defaults: the dialled-in per-tier DetectionSeconds keeps
        /// meaning what it always meant, and this rebalance rides on top of it.
        ///
        /// It is applied once, here, while the struct is built - so it lands after every
        /// config value (flat or per-tier) and, because the Chameleon upgrade instead
        /// stretches the *accumulator* in RunDetectionSweep, after that multiplier too.
        /// Effective time-to-trip is DetectionSeconds x 1.25 x chameleon. Baking it into
        /// the resolved window rather than into the sweep also keeps the lens ramp and the
        /// spotting arc honest, and guarantees it cannot compound across sweep ticks.
        /// </summary>
        private const float GlobalDetectionSlowdownMultiplier = 1.25f;

        public readonly bool Enabled;
        public readonly float ActiveCameraRatio;
        public readonly float ActiveCameraRatioRiskD;
        public readonly float ActiveCameraRatioRiskC;
        public readonly float ActiveCameraRatioRiskB;
        public readonly float ActiveCameraRatioRiskA;
        public readonly float ActiveCameraRatioRiskS;
        public readonly int ActiveCameraMin;
        public readonly int ActiveCameraMax;
        public readonly float RotationSeconds;
        public readonly float DetectionSeconds;
        public readonly float AlarmDurationSeconds;
        public readonly bool ProtocolEventsEnabled;
        public readonly float ProtocolBlackoutSeconds;
        public readonly float AlarmBlackoutSeconds;
        public readonly float LockdownDrillCooldownSeconds;
        public readonly bool BreakableCamerasEnabled;
        public readonly float CameraHealth;
        public readonly float AlarmLightIntensity;
        public readonly float AlarmAudioVolume;
        public readonly float AlarmCooldownSeconds;
        public readonly bool AwarenessPingEnabled;
        public readonly float AwarenessPingRange;
        public readonly float AwarenessPingLoudness;
        public readonly bool AwarenessSecondPingEnabled;
        public readonly float AwarenessSecondPingDelaySeconds;

        public CctvSecurityConfig(
            bool enabled,
            float activeCameraRatio,
            float activeCameraRatioRiskD,
            float activeCameraRatioRiskC,
            float activeCameraRatioRiskB,
            float activeCameraRatioRiskA,
            float activeCameraRatioRiskS,
            int activeCameraMin,
            int activeCameraMax,
            float rotationSeconds,
            float detectionSeconds,
            float alarmDurationSeconds,
            bool protocolEventsEnabled,
            float protocolBlackoutSeconds,
            float alarmBlackoutSeconds,
            float lockdownDrillCooldownSeconds,
            bool breakableCamerasEnabled,
            float cameraHealth,
            float alarmLightIntensity,
            float alarmAudioVolume,
            float alarmCooldownSeconds,
            bool awarenessPingEnabled,
            float awarenessPingRange,
            float awarenessPingLoudness,
            bool awarenessSecondPingEnabled,
            float awarenessSecondPingDelaySeconds)
        {
            Enabled = enabled;
            ActiveCameraRatio = UnityEngine.Mathf.Clamp(activeCameraRatio, 0f, 1f);
            ActiveCameraRatioRiskD = UnityEngine.Mathf.Clamp(activeCameraRatioRiskD, 0f, 1f);
            ActiveCameraRatioRiskC = UnityEngine.Mathf.Clamp(activeCameraRatioRiskC, 0f, 1f);
            ActiveCameraRatioRiskB = UnityEngine.Mathf.Clamp(activeCameraRatioRiskB, 0f, 1f);
            ActiveCameraRatioRiskA = UnityEngine.Mathf.Clamp(activeCameraRatioRiskA, 0f, 1f);
            ActiveCameraRatioRiskS = UnityEngine.Mathf.Clamp(activeCameraRatioRiskS, 0f, 1f);
            ActiveCameraMin = UnityEngine.Mathf.Max(0, activeCameraMin);
            ActiveCameraMax = UnityEngine.Mathf.Max(ActiveCameraMin, activeCameraMax);
            RotationSeconds = UnityEngine.Mathf.Max(1f, rotationSeconds);
            DetectionSeconds = UnityEngine.Mathf.Max(0.1f, detectionSeconds) * GlobalDetectionSlowdownMultiplier;
            AlarmDurationSeconds = UnityEngine.Mathf.Max(1f, alarmDurationSeconds);
            ProtocolEventsEnabled = protocolEventsEnabled;
            ProtocolBlackoutSeconds = UnityEngine.Mathf.Max(0f, protocolBlackoutSeconds);
            AlarmBlackoutSeconds = UnityEngine.Mathf.Max(0f, alarmBlackoutSeconds);
            LockdownDrillCooldownSeconds = UnityEngine.Mathf.Max(5f, lockdownDrillCooldownSeconds);
            BreakableCamerasEnabled = breakableCamerasEnabled;
            CameraHealth = UnityEngine.Mathf.Max(1f, cameraHealth);
            AlarmLightIntensity = UnityEngine.Mathf.Max(0f, alarmLightIntensity);
            AlarmAudioVolume = UnityEngine.Mathf.Clamp01(alarmAudioVolume);
            AlarmCooldownSeconds = UnityEngine.Mathf.Max(0f, alarmCooldownSeconds);
            AwarenessPingEnabled = awarenessPingEnabled;
            AwarenessPingRange = UnityEngine.Mathf.Max(0f, awarenessPingRange);
            AwarenessPingLoudness = UnityEngine.Mathf.Clamp01(awarenessPingLoudness);
            AwarenessSecondPingEnabled = awarenessSecondPingEnabled;
            AwarenessSecondPingDelaySeconds = UnityEngine.Mathf.Max(0.5f, awarenessSecondPingDelaySeconds);
        }

        public static CctvSecurityConfig Current
        {
            get
            {
                char tier = ContractsBridge.GetCurrentRiskTierCached();
                return new CctvSecurityConfig(
                    ResolveEnabledForTier(tier),
                    CctvModuleConfig.CctvSecurityActiveCameraRatio?.Value ?? 0.20f,
                    CctvModuleConfig.CctvSecurityActiveCameraRatioRiskD?.Value ?? 0.10f,
                    CctvModuleConfig.CctvSecurityActiveCameraRatioRiskC?.Value ?? 0.15f,
                    CctvModuleConfig.CctvSecurityActiveCameraRatioRiskB?.Value ?? 0.25f,
                    CctvModuleConfig.CctvSecurityActiveCameraRatioRiskA?.Value ?? 0.25f,
                    CctvModuleConfig.CctvSecurityActiveCameraRatioRiskS?.Value ?? 0.35f,
                    CctvModuleConfig.CctvSecurityActiveCameraMin?.Value ?? 1,
                    CctvModuleConfig.CctvSecurityActiveCameraMax?.Value ?? 4,
                    CctvModuleConfig.CctvSecurityRotationSeconds?.Value ?? 90f,
                    ResolveDetectionSecondsForTier(tier),
                    CctvModuleConfig.CctvSecurityAlarmDurationSeconds?.Value ?? 15f,
                    // #575: unbound fallbacks track the bind defaults.
                    CctvModuleConfig.CctvSecurityProtocolEventsEnabled?.Value ?? false,
                    CctvModuleConfig.CctvSecurityProtocolBlackoutSeconds?.Value ?? 2.5f,
                    CctvModuleConfig.CctvSecurityAlarmBlackoutSeconds?.Value ?? 5f,
                    CctvModuleConfig.CctvSecurityLockdownDrillCooldownSeconds?.Value ?? 300f,
                    SurveillanceBootstrap.Config?.BreakableCameras?.Value ?? true,
                    SurveillanceBootstrap.Config?.CameraHealth?.Value ?? 6f,
                    CctvModuleConfig.CctvSecurityAlarmLightIntensity?.Value ?? 8f,
                    CctvModuleConfig.CctvSecurityAlarmAudioVolume?.Value ?? 0.85f,
                    CctvModuleConfig.CctvSecurityAlarmCooldownSeconds?.Value ?? 60f,
                    CctvModuleConfig.CctvSecurityAlarmAwarenessPingEnabled?.Value ?? true,
                    CctvModuleConfig.CctvSecurityAlarmAwarenessPingRange?.Value ?? 45f,
                    CctvModuleConfig.CctvSecurityAlarmAwarenessPingLoudness?.Value ?? 0.9f,
                    CctvModuleConfig.CctvSecurityAlarmAwarenessSecondPingEnabled?.Value ?? false,
                    CctvModuleConfig.CctvSecurityAlarmAwarenessSecondPingDelaySeconds?.Value ?? 4f);
            }
        }

        /// <summary>
        /// The master switch AND the landed moon's tier switch (#466). Folding the tier into
        /// the same <c>Enabled</c> flag every consumer already reads is what makes "D and C
        /// have no alarms" total: the director's tick early-out, the active-set rotation, the
        /// camera-count maths and the protocol scheduler all gate on it already.
        ///
        /// An unrecognized tier ('?' - no contracts plugin, or a modded moon with an
        /// unparseable risk string) keeps the master value, so a CCTV-only profile is
        /// unchanged.
        /// </summary>
        private static bool ResolveEnabledForTier(char tier)
        {
            bool master = CctvModuleConfig.CctvSecurityEnabled?.Value ?? true;
            if (!master) return false;

            switch (char.ToUpperInvariant(tier))
            {
                // #575: unbound fallbacks track the bind defaults (D and C now on).
                case 'D': return CctvModuleConfig.CctvSecurityEnabledRiskD?.Value ?? true;
                case 'C': return CctvModuleConfig.CctvSecurityEnabledRiskC?.Value ?? true;
                case 'B': return CctvModuleConfig.CctvSecurityEnabledRiskB?.Value ?? true;
                case 'A': return CctvModuleConfig.CctvSecurityEnabledRiskA?.Value ?? true;
                case 'S': return CctvModuleConfig.CctvSecurityEnabledRiskS?.Value ?? true;
                default: return true;
            }
        }

        private static float ResolveDetectionSecondsForTier(char tier)
        {
            float fallback = CctvModuleConfig.CctvSecurityDetectionSeconds?.Value ?? 1.5f;
            switch (char.ToUpperInvariant(tier))
            {
                case 'D': return CctvModuleConfig.CctvSecurityDetectionSecondsRiskD?.Value ?? fallback;
                case 'C': return CctvModuleConfig.CctvSecurityDetectionSecondsRiskC?.Value ?? fallback;
                case 'B': return CctvModuleConfig.CctvSecurityDetectionSecondsRiskB?.Value ?? fallback;
                case 'A': return CctvModuleConfig.CctvSecurityDetectionSecondsRiskA?.Value ?? fallback;
                case 'S': return CctvModuleConfig.CctvSecurityDetectionSecondsRiskS?.Value ?? fallback;
                default: return fallback;
            }
        }
    }
}
