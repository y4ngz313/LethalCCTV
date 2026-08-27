using System.Collections.Generic;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Security;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    internal static class CctvCameraSweepMotor
    {
        private const float DefaultPitchOffset = 0f;
        private const float ManualControlIdleSeconds = 3f;

        internal static void NotifyManualControl(CCTVCamera camera)
        {
            if (camera == null) return;
            camera.ManualSweepHoldUntil = Time.unscaledTime + ManualControlIdleSeconds;
        }

        internal static void TickAll(IReadOnlyList<CCTVCamera> cameras)
        {
            if (SurveillanceBootstrap.Config == null || !SurveillanceBootstrap.Config.SecuritySweepEnabled.Value) return;
            if (cameras == null || cameras.Count == 0) return;

            for (int i = 0; i < cameras.Count; i++)
                TickCamera(cameras[i], Time.unscaledDeltaTime);
        }

        private static void TickCamera(CCTVCamera camera, float deltaTime)
        {
            if (camera == null || camera.IsSecurityBroken || camera.SecuritySweepSuppressed) return;
            if (Time.unscaledTime < camera.ManualSweepHoldUntil) return;

            CctvSecurityCameraState state = CctvSecurityCameraRegistry.Find(camera);
            PlayerControllerB trackedPlayer = state?.TrackedPlayerForPresentation;
            float yaw;
            float pitch = DefaultPitchOffset;
            // #563: only a camera that can actually detect tracks a player. A suppressed
            // camera keeps its idle sweep (the else branch) rather than locking on.
            if (state != null && state.IsDetectionLive && trackedPlayer != null && IsPlayerVisibleOnFeed(camera, trackedPlayer))
            {
                yaw = MoveYawTowardPlayer(camera, trackedPlayer, deltaTime);
            }
            else
            {
                yaw = SweepYaw(camera, deltaTime);
            }

            camera.ApplySecurityAngles(yaw, pitch);
        }

        private static float SweepYaw(CCTVCamera camera, float deltaTime)
        {
            float seconds = Mathf.Max(1f, SurveillanceBootstrap.Config.SecuritySweepSeconds.Value);
            float maxYaw = Mathf.Min(
                Mathf.Abs(SurveillanceBootstrap.Config.SecuritySweepYawDegrees.Value),
                Mathf.Abs(SurveillanceBootstrap.Config.YawClampDeg));
            camera.SecuritySweepPhase += Mathf.Max(0f, deltaTime) / seconds;
            float wave = Mathf.Sin(camera.SecuritySweepPhase * Mathf.PI * 2f);
            float target = wave * maxYaw;
            float configuredSweepDegreesPerSecond = Mathf.Max(1f, maxYaw * Mathf.PI * 2f / seconds);
            float trackCap = Mathf.Max(1f, SurveillanceBootstrap.Config.SecurityTrackDegreesPerSecond.Value);
            float sweepStep = Mathf.Min(configuredSweepDegreesPerSecond, trackCap) * Mathf.Max(0f, deltaTime);
            return Mathf.MoveTowards(camera.YawOffsetDeg, target, sweepStep);
        }

        private static float MoveYawTowardPlayer(CCTVCamera camera, PlayerControllerB player, float deltaTime)
        {
            Vector3 toPlayer = player.transform.position + Vector3.up * 0.8f - camera.transform.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f)
                return camera.YawOffsetDeg;

            float worldYaw = Mathf.Atan2(toPlayer.x, toPlayer.z) * Mathf.Rad2Deg;
            float desiredOffset = Mathf.DeltaAngle(camera.BaseYawDeg, worldYaw);
            float clamp = Mathf.Abs(SurveillanceBootstrap.Config.YawClampDeg);
            desiredOffset = Mathf.Clamp(desiredOffset, -clamp, clamp);
            float maxStep = Mathf.Max(1f, SurveillanceBootstrap.Config.SecurityTrackDegreesPerSecond.Value) * Mathf.Max(0f, deltaTime);
            return Mathf.MoveTowards(camera.YawOffsetDeg, desiredOffset, maxStep);
        }

        private static bool IsPlayerVisibleOnFeed(CCTVCamera camera, PlayerControllerB player)
        {
            if (camera == null || player == null || camera.Cam == null) return false;
            Vector3 viewport = camera.Cam.WorldToViewportPoint(player.transform.position + Vector3.up * 0.8f);
            return viewport.z > 0f && viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
        }
    }
}
