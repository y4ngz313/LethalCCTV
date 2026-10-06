using System;
using System.Diagnostics;
using UnityEngine;
using Y4NGZCore.Modules;
using Y4NGZCompany.Facility.Cameras;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// #1219 G4. The only caller of <c>Camera.Render()</c> in LethalCCTV. Producers state
    /// what they want with <see cref="Request"/>; <see cref="Pump"/> runs once per frame at
    /// the end of <c>SurveillanceBootstrap.LateUpdateCore</c> and performs at most one
    /// manual render per <see cref="Time.frameCount"/>, across every producer.
    ///
    /// Cadence: a request is eligible once <c>cadenceSeconds</c> have passed since that
    /// producer's last render. A bypass request (dirty content, a switch flash, an outline
    /// fade, an interactive turret) shortens the wait to
    /// <see cref="DirtyBypassMinIntervalSeconds"/> but never below it, so animation cannot
    /// turn into a per-frame render.
    ///
    /// Fairness: eligible requests are served oldest-eligible first, ties by
    /// <see cref="CctvRenderClient"/> order, so with four producers every eligible request is
    /// served within four frames. A request stays pending until it is served or cancelled,
    /// so a delayed snapshot still lands; producers keep their own dirty flags until their
    /// render callback actually runs.
    ///
    /// The render callbacks are fixed static methods held in one array, so requesting and
    /// pumping allocate nothing.
    /// </summary>
    internal static class CctvRenderScheduler
    {
        internal const float DirtyBypassMinIntervalSeconds = 1f / 30f;
        private const int ClientCount = 4;
        private const int NotEligible = int.MaxValue;

        private struct ClientState
        {
            internal bool Pending;
            internal bool Bypass;
            internal float CadenceSeconds;
            internal float LastRenderAt;
            internal int EligibleSinceFrame;
            internal long Renders;
        }

        private static readonly ClientState[] _clients = CreateClients();
        private static readonly string[] RenderErrorKeys =
        {
            "cctv.render.snapshot",
            "cctv.render.turret",
            "cctv.render.left",
            "cctv.render.right",
        };

        // Each returns true when it issued a Camera.Render, false when its target has gone
        // (no camera, page no longer shown) and the request should simply be dropped.
        private static readonly Func<bool>[] _renderers =
        {
            QuadCameraAssignment.RenderScheduledFeedSnapshot,
            ShipTurretController.RenderScheduledView,
            CCTVVanillaMonitorDisplay.RenderScheduledLeftCompositor,
            CCTVVanillaMonitorDisplay.RenderScheduledRightCompositor,
        };

        private static int _lastPumpFrame = -1;
        private static int _lastRenderFrame = -1;
        private static int _rendersInLastRenderFrame;

        internal static long TotalRenders { get; private set; }
        /// <summary>Highest number of manual renders observed in one frame; the contract is 1.</summary>
        internal static int MaxRendersInOneFrame { get; private set; }
        internal static int LastRenderFrame => _lastRenderFrame;
        internal static long LastPumpElapsedTicks { get; private set; }
        internal static long WorstPumpElapsedTicks { get; private set; }

        internal static long GetRenderCount(CctvRenderClient client) => _clients[(int)client].Renders;
        internal static bool IsPending(CctvRenderClient client) => _clients[(int)client].Pending;

        /// <summary>
        /// Asks for one render of <paramref name="client"/>. Repeating the request while it
        /// is pending only refreshes the cadence; a bypass stays set until served.
        /// </summary>
        internal static void Request(CctvRenderClient client, float cadenceSeconds, bool bypassCadence)
        {
            ref ClientState state = ref _clients[(int)client];
            if (!state.Pending)
            {
                state.Pending = true;
                state.EligibleSinceFrame = NotEligible;
            }
            state.CadenceSeconds = Mathf.Max(0f, cadenceSeconds);
            state.Bypass |= bypassCadence;
        }

        internal static void Cancel(CctvRenderClient client)
        {
            ref ClientState state = ref _clients[(int)client];
            state.Pending = false;
            state.Bypass = false;
            state.EligibleSinceFrame = NotEligible;
        }

        internal static void CancelAll()
        {
            for (int i = 0; i < ClientCount; i++)
                Cancel((CctvRenderClient)i);
        }

        internal static void Pump()
        {
            int frame = Time.frameCount;
            if (frame == _lastPumpFrame)
                return;
            _lastPumpFrame = frame;

            long startedAt = Stopwatch.GetTimestamp();
            float now = Time.unscaledTime;
            try
            {
                for (int i = 0; i < ClientCount; i++)
                    MarkEligible(ref _clients[i], now, frame);

                // A callback that finds nothing to render does not spend the frame's
                // render, so the next-oldest request gets it. Bounded by ClientCount.
                for (int attempt = 0; attempt < ClientCount; attempt++)
                {
                    int chosen = SelectOldestEligible();
                    if (chosen < 0)
                        break;
                    if (Serve(chosen, now, frame))
                        break;
                }
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - startedAt;
                LastPumpElapsedTicks = elapsed;
                if (elapsed > WorstPumpElapsedTicks)
                    WorstPumpElapsedTicks = elapsed;
            }
        }

        internal static void ResetStats()
        {
            TotalRenders = 0;
            MaxRendersInOneFrame = 0;
            LastPumpElapsedTicks = 0;
            WorstPumpElapsedTicks = 0;
            _lastRenderFrame = -1;
            _rendersInLastRenderFrame = 0;
            for (int i = 0; i < ClientCount; i++)
                _clients[i].Renders = 0;
        }

        private static void MarkEligible(ref ClientState state, float now, int frame)
        {
            if (!state.Pending || state.EligibleSinceFrame != NotEligible)
                return;
            float wait = state.Bypass
                ? DirtyBypassMinIntervalSeconds
                : state.CadenceSeconds;
            if (now - state.LastRenderAt >= wait)
                state.EligibleSinceFrame = frame;
        }

        private static int SelectOldestEligible()
        {
            int chosen = -1;
            int oldest = NotEligible;
            for (int i = 0; i < ClientCount; i++)
            {
                ref ClientState state = ref _clients[i];
                if (!state.Pending || state.EligibleSinceFrame >= oldest)
                    continue;
                oldest = state.EligibleSinceFrame;
                chosen = i;
            }
            return chosen;
        }

        private static bool Serve(int index, float now, int frame)
        {
            ref ClientState state = ref _clients[index];
            state.Pending = false;
            state.Bypass = false;
            state.EligibleSinceFrame = NotEligible;

            bool rendered = false;
            try
            {
                rendered = _renderers[index]();
            }
            catch (Exception ex)
            {
                // A throwing callback still spent its render attempt; count it so the
                // one-per-frame bound holds even on the failure path.
                rendered = true;
                if (!ModuleDiagnostics.HasWarned(RenderErrorKeys[index]))
                {
                    ModuleDiagnostics.WarnOnce(RenderErrorKeys[index],
                        $"[LethalCCTV] Scheduled {(CctvRenderClient)index} render failed: {ex.Message}");
                }
            }

            if (!rendered)
                return false;

            state.LastRenderAt = now;
            state.Renders++;
            TotalRenders++;
            _rendersInLastRenderFrame = _lastRenderFrame == frame ? _rendersInLastRenderFrame + 1 : 1;
            _lastRenderFrame = frame;
            if (_rendersInLastRenderFrame > MaxRendersInOneFrame)
                MaxRendersInOneFrame = _rendersInLastRenderFrame;
            return true;
        }

        private static ClientState[] CreateClients()
        {
            var clients = new ClientState[ClientCount];
            for (int i = 0; i < ClientCount; i++)
            {
                clients[i].LastRenderAt = float.NegativeInfinity;
                clients[i].EligibleSinceFrame = NotEligible;
            }
            return clients;
        }
    }
}
