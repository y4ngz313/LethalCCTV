using System.Diagnostics;
using System.Text;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Focus-ENTRY frame attribution (#291). Steady-state focused timing (the
    /// LateUpdate slow-pass report) buries the one-off ~230ms entry frame; this
    /// probe times the entry tick step by step and emits ONE line per entry so
    /// the buckets sum to the measured TickFrame total.
    ///
    /// Cost model: every hook is a single bool test while not capturing, so
    /// ordinary ticks (focused or not) pay nothing measurable. Capture is armed
    /// only on frames that can complete a focus entry (pose settle live but not
    /// yet focused) plus the frame after entry, and all buffers are preallocated
    /// so a captured frame allocates nothing beyond the summary string itself.
    /// </summary>
    internal static partial class MonitorFocus
    {
        private const int ENTRY_PERF_MAX_STEPS = 96;
        private const int ENTRY_PERF_FOLLOWUP_FRAMES = 1;
        private const double ENTRY_PERF_MIN_REPORTED_MS = 0.05;

        private static readonly string[] _entryPerfStepNames = new string[ENTRY_PERF_MAX_STEPS];
        private static readonly long[] _entryPerfStepTicks = new long[ENTRY_PERF_MAX_STEPS];
        private static readonly StringBuilder _entryPerfBuilder = new StringBuilder(1024);
        private static int _entryPerfStepCount;
        private static bool _entryPerfCapturing;
        private static bool _entryPerfWasFocusedAtFrameStart;
        private static bool _entryPerfFollowupFrame;
        private static int _entryPerfFollowupFramesRemaining;
        private static long _entryPerfFrameStartedAt;
        private static long _entryPerfLastMarkAt;
        private static long _entryPerfOverflowTicks;
        private static int _entryPerfOverflowSteps;

        /// <summary>
        /// Arms capture for this TickFrame when the frame can plausibly own a
        /// focus entry. Returns true only for the caller that armed it, which is
        /// then responsible for EndEntryPerfFrame in a finally.
        /// </summary>
        private static bool BeginEntryPerfFrame()
        {
            if (_entryPerfCapturing)
                return false;

            // Entry candidate: the pose settle owns the player and may hand off to
            // EnterFocus this tick. Follow-up: the first focused ticks after entry,
            // where deferred/lazy work can still land.
            bool entryCandidate = _stationPoseSettleActive && !IsFocused;
            bool followupFrame = !entryCandidate && _entryPerfFollowupFramesRemaining > 0;
            if (!entryCandidate && !followupFrame)
                return false;

            _entryPerfCapturing = true;
            _entryPerfFollowupFrame = followupFrame;
            _entryPerfWasFocusedAtFrameStart = IsFocused;
            _entryPerfStepCount = 0;
            _entryPerfOverflowTicks = 0;
            _entryPerfOverflowSteps = 0;
            _entryPerfFrameStartedAt = Stopwatch.GetTimestamp();
            _entryPerfLastMarkAt = _entryPerfFrameStartedAt;
            return true;
        }

        /// <summary>
        /// Closes the bucket that has been open since the previous mark. Named
        /// steps therefore tile the whole frame — nothing can hide between them.
        /// </summary>
        private static void EntryPerfMark(string stepName)
        {
            if (!_entryPerfCapturing)
                return;

            long now = Stopwatch.GetTimestamp();
            long elapsed = now - _entryPerfLastMarkAt;
            _entryPerfLastMarkAt = now;

            if (_entryPerfStepCount < ENTRY_PERF_MAX_STEPS)
            {
                _entryPerfStepNames[_entryPerfStepCount] = stepName;
                _entryPerfStepTicks[_entryPerfStepCount] = elapsed;
                _entryPerfStepCount++;
                return;
            }

            _entryPerfOverflowTicks += elapsed;
            _entryPerfOverflowSteps++;
        }

        private static void EndEntryPerfFrame()
        {
            if (!_entryPerfCapturing)
                return;

            EntryPerfMark("tick-tail");
            _entryPerfCapturing = false;

            bool enteredThisFrame = !_entryPerfWasFocusedAtFrameStart && IsFocused;
            bool followupFrame = _entryPerfFollowupFrame;
            _entryPerfFollowupFrame = false;
            if (followupFrame && _entryPerfFollowupFramesRemaining > 0)
                _entryPerfFollowupFramesRemaining--;

            if (enteredThisFrame)
                _entryPerfFollowupFramesRemaining = ENTRY_PERF_FOLLOWUP_FRAMES;
            else if (!followupFrame)
                return; // ordinary settle tick: nothing entered, nothing to report

            try
            {
                LogEntryPerfFrame(enteredThisFrame ? "entry" : "entry+1");
            }
            catch
            {
                // Instrumentation must never break the entry path.
            }
        }

        private static void LogEntryPerfFrame(string phase)
        {
            double ticksToMs = 1000.0 / Stopwatch.Frequency;
            double totalMs = (_entryPerfLastMarkAt - _entryPerfFrameStartedAt) * ticksToMs;

            _entryPerfBuilder.Length = 0;
            _entryPerfBuilder.Append("[LethalCCTV][EntryPerf] phase=").Append(phase)
                .Append(" frame=").Append(Time.frameCount)
                .Append(" total=").Append(totalMs.ToString("F1")).Append("ms");

            double belowFloorMs = 0.0;
            for (int i = 0; i < _entryPerfStepCount; i++)
            {
                double ms = _entryPerfStepTicks[i] * ticksToMs;
                if (ms < ENTRY_PERF_MIN_REPORTED_MS)
                {
                    belowFloorMs += ms;
                    continue;
                }

                _entryPerfBuilder.Append(' ').Append(_entryPerfStepNames[i])
                    .Append('=').Append(ms.ToString("F2"));
            }

            if (_entryPerfOverflowSteps > 0)
            {
                _entryPerfBuilder.Append(" overflow(").Append(_entryPerfOverflowSteps).Append(" steps)=")
                    .Append((_entryPerfOverflowTicks * ticksToMs).ToString("F2"));
            }
            if (belowFloorMs >= ENTRY_PERF_MIN_REPORTED_MS)
            {
                _entryPerfBuilder.Append(" sub-floor-steps=").Append(belowFloorMs.ToString("F2"));
            }

            // LogWarning, not LogInfo: the profiling profile's BepInEx.cfg drops
            // Info, and this line is the whole point of the entry-frame probe.
            SurveillanceBootstrap.Log?.LogWarning(_entryPerfBuilder.ToString());
        }
    }
}
