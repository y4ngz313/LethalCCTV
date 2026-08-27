using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Y4NGZCompany.Facility.Cameras;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// CCTV-focus frame-time instrumentation. SurveillanceBootstrap.RunLateUpdateStep feeds every
    /// mod tick through RecordStep while focused; a compact summary is logged every
    /// 5 seconds so the actual hot subsystem is identifiable from LogOutput.log.
    /// Costs two Stopwatch.GetTimestamp calls per step while focused and nothing
    /// while not focused; the camera/MCR census runs only at summary time.
    /// </summary>
    internal static class FocusPerfProbe
    {
        private const float SummaryIntervalSeconds = 5f;

        private sealed class StepStat
        {
            public long TotalTicks;
            public long MaxTicks;
            public int Calls;
        }

        private static readonly Dictionary<string, StepStat> _steps = new Dictionary<string, StepStat>(24);
        private static readonly StringBuilder _builder = new StringBuilder(1024);
        private static float _windowStartedAt = -1f;
        private static int _frames;
        private static float _frameTimeSum;

        internal static void RecordStep(string stepName, long elapsedTicks)
        {
            if (!_steps.TryGetValue(stepName, out StepStat stat))
            {
                stat = new StepStat();
                _steps[stepName] = stat;
            }

            stat.TotalTicks += elapsedTicks;
            stat.Calls++;
            if (elapsedTicks > stat.MaxTicks)
                stat.MaxTicks = elapsedTicks;
        }

        /// <summary>Called once per LateUpdate while focused, after all steps ran.</summary>
        internal static void OnFocusedFrame()
        {
            float now = Time.unscaledTime;
            if (_windowStartedAt < 0f)
                _windowStartedAt = now;

            _frames++;
            _frameTimeSum += Time.unscaledDeltaTime;

            if (now - _windowStartedAt < SummaryIntervalSeconds)
                return;

            LogSummary(now - _windowStartedAt);
            Reset();
            _windowStartedAt = now;
        }

        /// <summary>Drops the window when focus ends so idle frames never dilute it.</summary>
        internal static void OnFocusEnded()
        {
            Reset();
            _windowStartedAt = -1f;
        }

        private static void Reset()
        {
            _steps.Clear();
            _frames = 0;
            _frameTimeSum = 0f;
        }

        private static void LogSummary(float windowSeconds)
        {
            if (_frames == 0)
                return;

            double ticksToMs = 1000.0 / Stopwatch.Frequency;
            float fps = _frameTimeSum > 0f ? _frames / _frameTimeSum : 0f;

            _builder.Length = 0;
            _builder.Append("[LethalCCTV][FocusPerf] ")
                .Append(_frames).Append(" frames / ").Append(windowSeconds.ToString("F1")).Append("s, avg ")
                .Append(fps.ToString("F1")).Append(" fps (")
                .Append((_frameTimeSum * 1000f / _frames).ToString("F1")).Append(" ms/frame). Mod LateUpdate steps (avg ms/frame | worst single call ms):");
            SurveillanceBootstrap.Log?.LogInfo(_builder.ToString());

            foreach (KeyValuePair<string, StepStat> entry in _steps)
            {
                StepStat stat = entry.Value;
                double avgMsPerFrame = stat.TotalTicks * ticksToMs / _frames;
                double maxMs = stat.MaxTicks * ticksToMs;
                // Skip the noise floor so the summary stays readable.
                if (avgMsPerFrame < 0.01 && maxMs < 0.5)
                    continue;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][FocusPerf]   {entry.Key}: {avgMsPerFrame:F3} | {maxMs:F2} ({stat.Calls} calls)");
            }

            LogRenderCensus();
        }

        private static void LogRenderCensus()
        {
            try
            {
                Camera[] cameras = Camera.allCameras;
                int toRenderTexture = 0;
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i] != null && cameras[i].targetTexture != null)
                        toRenderTexture++;
                }

                int activeCctv = 0;
                int boundCctv = 0;
                for (int slot = 0; slot < 4; slot++)
                {
                    CCTVCamera bound = QuadCameraAssignment.GetBoundCamera(slot);
                    if (bound == null || bound.Cam == null)
                        continue;
                    boundCctv++;
                    if (bound.Cam.enabled)
                        activeCctv++;
                }

                ManualCameraRenderer[] mcrs = Object.FindObjectsOfType<ManualCameraRenderer>();
                int enabledMcrs = 0;
                int enabledMcrCams = 0;
                for (int i = 0; i < mcrs.Length; i++)
                {
                    if (mcrs[i] == null)
                        continue;
                    if (mcrs[i].enabled)
                        enabledMcrs++;
                    if (mcrs[i].cam != null && mcrs[i].cam.enabled)
                        enabledMcrCams++;
                }

                _builder.Length = 0;
                _builder.Append("[LethalCCTV][FocusPerf]   render census: enabledCams=").Append(cameras.Length)
                    .Append(" (toRT=").Append(toRenderTexture)
                    .Append("), cctvBound=").Append(boundCctv)
                    .Append(" cctvEnabledNow=").Append(activeCctv)
                    .Append(", MCRs=").Append(mcrs.Length)
                    .Append(" enabled=").Append(enabledMcrs)
                    .Append(" mcrCamsEnabled=").Append(enabledMcrCams);
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i] == null)
                        continue;
                    _builder.Append(" | ").Append(cameras[i].name);
                    if (cameras[i].targetTexture != null)
                        _builder.Append("->").Append(cameras[i].targetTexture.width).Append('x').Append(cameras[i].targetTexture.height);
                }
                SurveillanceBootstrap.Log?.LogInfo(_builder.ToString());
            }
            catch (System.Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV][FocusPerf] render census failed: {ex.Message}");
            }
        }
    }
}
