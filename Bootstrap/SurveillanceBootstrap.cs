using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.ShipSystems.Surveillance;
using Y4NGZCompany.Facility.Security;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Y4NGZCompany.Facility.Stash;
using UnityEngine;

namespace Y4NGZCompany.Bootstrap
{
    public class SurveillanceBootstrap : MonoBehaviour
    {
        // Hosted on LethalCCTVPlugin's own "LethalCCTV_Host" GameObject, which is both
        // DontDestroyOnLoad'd and stamped HideFlags.HideAndDontSave so it survives the boot
        // scene transition (#393 — see LethalCCTVPlugin.Awake for why borrowing BepInEx's
        // manager object was not safe). Expose the instance so MonitorFocus can parent its
        // overlay Canvas here and inherit that persistence via the parent chain. Required
        // because self-DDOL on a freshly-created root GO did not keep the overlay alive
        // across the main-menu -> moon scene transition (Phase 1.3 deploy data).
        internal static SurveillanceBootstrap Instance;

        /// <summary>Set once the process is genuinely shutting down, so an OnDestroy that fires
        /// for any other reason is reported as the fault it is rather than swallowed.</summary>
        private static bool _applicationQuitting;

        internal static ManualLogSource Log;
        // `new` hides BaseUnityPlugin.Config (a ConfigFile) â€” intentional, matches SKILL.md
        // "Plugin Structure" example.
        internal static LethalCCTVConfig Config;
        internal static SoftDependencies SoftDeps;
        internal static DungeonCameraSpawner CameraSpawner;

        private Harmony _harmony;
        private Coroutine _subscribeCoroutine;
        private float _nextMonitorSpawnProbeTime;
        private float _nextIdleCompatTickAt;
        private bool _lastKnownShipPhase = true;
        private readonly Dictionary<string, float> _lateUpdateErrorLogTimes = new Dictionary<string, float>();
        private const float IdleCompatTickIntervalSeconds = 0.5f;

        internal void Initialize(ConfigFile configFile, ManualLogSource logger)
        {
            Instance = this;
            Log = logger;
            logger.LogInfo($"[LethalCCTV] Awake starting. v{SurveillancePluginInfo.PLUGIN_VERSION}");
            try
            {
            CctvModuleConfig.TryImportLegacyConfig(configFile, logger);
            CctvConfigSurfaceMigration.Migrate(configFile, logger);
            CctvModuleConfig.Bind(configFile, logger);
            Config = new LethalCCTVConfig(configFile);
            SoftDeps = new SoftDependencies();
            OpenBodyCamsCompat.Initialize();
            CCTVTerminalUnlockable.Register();
            // #579 - the controls sticky note. Registered here beside the other
            // network prefab so it is in allItemsList before any save loads.
            CCTVStickyNoteItem.Register();

            // Harmony bootstrap â€” must run before anything that depends on patched
            // behaviour. Phase 1.5a Item 1 introduces the first Harmony patch in this
            // project: QuickMenuPatch suppresses vanilla pause-menu open while focus
            // mode is active.
            ApplyHarmonyPatchesSafe();

            Log.LogInfo($"[LethalCCTV] Plugin loaded. v{SurveillancePluginInfo.PLUGIN_VERSION}");
            Log.LogInfo($"[LethalCCTV] Soft-dep OpenBodyCams: {SoftDeps.OpenBodyCams}");
            Log.LogInfo($"[LethalCCTV] Soft-dep LethalLevelLoader: {SoftDeps.LethalLevelLoader}");
            Log.LogInfo($"[LethalCCTV] Soft-dep FacilityMeltdown: {SoftDeps.FacilityMeltdown}");
            Log.LogInfo($"[LethalCCTV] Soft-dep Y4NGZUpgrades: {SoftDeps.Y4NGZUpgrades}");
            // #393: the interior support spawner (mainframe + Company Stash) came across with
            // its fixtures, so this plugin now owns the three lifecycle calls ContractsBootstrap
            // and MoonContractState used to make on its behalf: prefab warm-up here, the
            // per-frame tick and the round reset in CctvRoundLifecyclePatches.
            InteriorSupportSpawner.WarmUpRuntimePrefabs();

            // Y4NGZCompany raises this before it grants a companion-mod placement request, so
            // an already-spawned mainframe's footprint is reserved before anything else can be
            // placed on top of it.
            InteriorFixtureHooks.BeforeExternalPlacementRequest +=
                InteriorSupportSpawner.RegisterExistingMainframeBeforePlacementRequests;
            InteriorFixtureHooks.FixturePreviewFactory = InteriorSupportSpawner.CreatePlacementPreview;

            // #582 — the CCTV sticky note is authored through the F9 Y4NGZDebugTools
            // ghost editor, which reaches Y4NGZCompany's authored-placement debug API
            // by reflection; that API routes ship-scoped kinds here. The note's pose
            // is ship-local (anchor under the station root), so it persists in this
            // plugin's own station placement profile, never in the tile-local store.
            ShipScopedPlacementHooks.SaveHandler = CCTVOperatorStation.SaveShipScopedPlacement;
            ShipScopedPlacementHooks.SavedPoseResolver = CCTVOperatorStation.TryGetShipScopedSavedPose;
            ShipScopedPlacementHooks.DeleteHandler = CCTVOperatorStation.DeleteShipScopedPlacement;

            // The keypad hardware lives in Y4NGZCore and cannot open an AssetBundle of its
            // own. Register only if nothing else has: with Y4NGZCompany installed it claims
            // the slot first from the same bundle, and re-reading it would gain nothing.
            if (CompanyStashKeypadInteractor.BundledAudioClipProvider == null)
                CompanyStashKeypadInteractor.BundledAudioClipProvider = CctvFixtureAssets.GetBundledAudioClip;

            CameraPlacementReviewStore.Load();
            AuthoredInteriorPlacementStore.Load();
            InteriorAuthoredPlacementStore.Load();

            // LethalLevelLoader may claim bundles before this module initializes.
            LethalLevelLoaderCompat.Initialize();

            // Registers the meltdown-start listener once for the process lifetime;
            // MeltdownAPI exposes no unregister, so this must not be per-round. The compat
            // bridge itself moved to Y4NGZCore in #393 because Defuse (Y4NGZCompany) starts
            // meltdowns through it, so the CCTV-specific reaction is subscribed here rather
            // than hard-coded inside the bridge.
            FacilityMeltdownCompat.MeltdownStarted += CctvSecurityDirector.OnFacilityMeltdownStarted;
            FacilityMeltdownCompat.Initialize();

            var filter = new TileExclusionFilter(Config);
            CameraSpawner = new DungeonCameraSpawner(Config, filter, this);
            CameraSpawner.CamerasReady += OnCamerasReady;

            // Phase 1.7 â€” display-side night-vision hot reload. SettingChanged on either
            // of the two new entries pushes fresh values into every live quad material.
            Config.NightVisionParamsChanged += OnNightVisionParamsChanged;

            // Screen-space overlay Canvas + LcInputActions wiring. Constructed once
            // at plugin load; persists across scene transitions via DontDestroyOnLoad.
            MonitorFocus.Initialize();
            Y4NGZPlayerAnimationBridge.Initialize();
            CCTVMonitorFeedSync.Initialize();
            ShipTurretController.Initialize();
            CctvOutlineManager.Initialize();
            if (Config.ReconLoggingEnabled.Value)
                Log.LogWarning($"[LethalCCTV] CCTV outline diagnostics armed. build={SurveillancePluginInfo.PLUGIN_VERSION}");
            CCTVMarkerManager.Initialize();
            CCTVScanGlowManager.Initialize();
            CCTVWalkieTalkieBridge.Initialize();

            // RoundManager.OnFinishedGeneratingDungeon is an instance event on the
            // RoundManager singleton, which does not exist yet at chainload. Defer the
            // subscription via a coroutine that polls until RoundManager.Instance is up.
            _subscribeCoroutine = StartCoroutine(SubscribeWhenRoundManagerReady());
            }
            catch (System.Exception ex)
            {
                logger.LogError($"[LethalCCTV] Awake failed before camera spawner startup completed: {ex}");
            }
        }

        private void ApplyHarmonyPatchesSafe()
        {
            _harmony = new Harmony(SurveillancePluginInfo.PLUGIN_GUID);
            TryPatch(typeof(QuickMenuPatch));
            TryPatch(typeof(CameraOperatorOpenMenuPerformedPatch));
            TryPatch(typeof(CameraOperatorPingScanPatch));
            TryPatch(typeof(CameraOperatorScanNodesPatch));
            TryPatch(typeof(CameraOperatorActivateItemPatch));
            TryPatch(typeof(CameraOperatorActivateItemCancelPatch));
            TryPatch(typeof(CameraOperatorJumpPerformedPatch));
            TryPatch(typeof(CameraOperatorPlayerUpdatePatch));
            TryPatch(typeof(CameraOperatorPlayerLookInputPatch));
            TryPatch(typeof(CCTVOperatorAnimSyncSuppressPatch));
            TryPatch(typeof(CCTVMonitorModeButtonPatch));
            TryPatch(typeof(CCTVMonitorModeShipLeavePatch));
            TryPatch(typeof(CCTVMonitorOwnershipManualCameraRendererUpdatePatch));
            TryPatch(typeof(CCTVMonitorOwnershipStartOfRoundLateUpdatePatch));
            TryPatch(typeof(CctvSupportSessionPatch));
            // #393: this plugin resets its own round state now that MoonContractState no
            // longer reaches in to do it.
            TryPatch(typeof(CctvRoundLifecyclePatches));
            TryPatch(typeof(CctvInteriorSupportTickPatch));
            // #563: vanilla's shotgun damage pass is enemy-only, so a camera can only be
            // shot if we add the pass ourselves.
            TryPatch(typeof(Y4NGZCompany.Facility.Cameras.CctvShotgunCameraPatch));
            // 1.1.0: BetterArmory ignores the trigger collider CCTV needs for its hitbox;
            // this optional reflection patch reproduces its authoritative pellet traces.
            TryPatch(typeof(Y4NGZCompany.Facility.Cameras.BetterArmoryCameraDamagePatch));
            TryPatch(typeof(CctvAlarmLockdownTeleportPatch));
            // #393: only a Contracted-less install sells the CCTV terminal through the
            // vanilla store, and only that route needs the prefab woken across
            // StartOfRound.SpawnUnlockable. With Contracted present the patch is never
            // applied, so that configuration keeps its exact pre-#393 call graph.
            if (CCTVTerminalUnlockable.SoldInVanillaStore)
                TryPatch(typeof(CctvTerminalStoreSpawnPatch));
        }

        private void TryPatch(System.Type patchType)
        {
            try
            {
                _harmony.PatchAll(patchType);
                Log.LogInfo($"[LethalCCTV] Harmony patch applied: {patchType.Name}");
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"[LethalCCTV] Harmony patch skipped: {patchType.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private IEnumerator SubscribeWhenRoundManagerReady()
        {
            while (RoundManager.Instance == null)
            {
                yield return null;
            }
            CameraSpawner.Subscribe(RoundManager.Instance);
            _subscribeCoroutine = null;
        }

        private void OnCamerasReady(IReadOnlyList<CCTVCamera> cameras)
        {
            CCTVOperatorStation.Ensure();

            // Dungeon regen destroys the previous round's CCTVCamera GOs.
            // If a player is mid-focus when this fires, their active-pane
            // holder is about to become a destroyed-Unity-object; force-exit
            // first so disableLookInput stash-restore runs cleanly before
            // the new round's rebind.
            MonitorFocus.ForceExit("dungeon-regen");
            MonitorFocus.ClearZoomState();
            CCTVMarkerManager.ClearAll();
            CCTVScanGlowManager.ClearAll();
            CctvTargetCache.Clear();

            CctvSecurityCameraRegistry.RegisterCameras(cameras);
            InteriorAlarmSpawner.SpawnForRegisteredCameras();

            if (!CCTVTerminalUnlockable.IsPurchased())
            {
                QuadCameraAssignment.Unassign();
                QuadMonitor.Despawn();
                Log.LogInfo("[LethalCCTV] CCTV terminal not purchased; camera feeds remain offline.");
                return;
            }

            // Spawn idempotent across rounds: reuses the existing monitor + RTs.
            // Assign clears any prior round's binds via its own Unassign() call.
            QuadMonitor.Spawn();
            QuadCameraAssignment.Assign(cameras);
            // The monitor keeps the vanilla display until a player actually
            // enters CCTV (2026-07-02 spec); reassert whatever the shared feed
            // state currently says instead of forcing CCTV mode on.
            CCTVMonitorFeedSync.ApplyFeedToDisplay();
        }

        private void OnNightVisionParamsChanged()
        {
            QuadMonitor.ApplyNightVisionParams();
        }

        /// <summary>Diagnostic timing for the 2026-07-31 regression hunt: profiler hitches show
        /// single SurveillanceBootstrap.LateUpdate calls at 41-86 ms late-round, but the method
        /// total cannot say which subsystem owns it. Every step is now timed every frame (one QPC
        /// read per step); when a full pass exceeds the threshold, the breakdown below names the
        /// culprit — including an "unattributed" remainder covering the work that runs outside
        /// RunLateUpdateStep (camera-spawner subscribe, monitor spawn probe, feed clears).</summary>
        private const double SlowPassLogThresholdMilliseconds = 5.0;
        private const float SlowPassLogIntervalSeconds = 5f;
        /// <summary>Severity escape hatch for the 5s throttle. A 473.92ms pass was recorded at
        /// 20:56:00 on 2026-07-31 and its per-step breakdown was computed and then thrown away,
        /// because an ordinary 5-10ms pass had armed the throttle moments earlier. Passes this
        /// far above budget are rare enough to log unconditionally, and they are precisely the
        /// ones worth reading (y4ngz313/Y4NGZCompany#197).</summary>
        private const double ExtremePassLogThresholdMilliseconds = 60.0;
        private static readonly Dictionary<string, double> _frameStepMilliseconds = new Dictionary<string, double>(32);
        private static readonly Dictionary<string, double> _frameNestedStepMilliseconds = new Dictionary<string, double>(16);
        private static float _nextSlowPassLogAt;
        private static double _harnessMilliseconds;

        private void LateUpdate()
        {
            // #613 task 2.2 (V2). This host must pump the shared registry too, for the same reason
            // the Contracted and Ship Systems hosts do: the registry holds subscribers that belong
            // to no one plugin - the cross-plugin module handshake is registered by Y4NGZCore - and
            // on a profile with LethalCCTV installed but neither of the other two, nothing else
            // would ever call it and the handshake would silently never run.
            //
            // Every host pumps the WHOLE list, not its own subscribers, so the ModuleTickOrder
            // sequence survives having several hosts; ModuleTickRegistry.Tick(int) is frame-scoped,
            // so whichever host Unity reaches first that frame does the work and the others return
            // 0. Outside the try/finally on purpose: this pump has its own per-subscriber isolation
            // and its own fault log, and it must not be attributed to CCTV's timing report.
            Y4NGZCore.Lifecycle.ModuleTickRegistry.Tick(Time.frameCount);

            _frameStepMilliseconds.Clear();
            _frameNestedStepMilliseconds.Clear();
            _harnessMilliseconds = 0.0;
            long passStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                LateUpdateCore();
            }
            finally
            {
                ReportSlowPass(TicksToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - passStart));
            }
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void ReportSlowPass(double passMilliseconds)
        {
            if (passMilliseconds < SlowPassLogThresholdMilliseconds)
                return;

            bool extreme = passMilliseconds >= ExtremePassLogThresholdMilliseconds;
            if (!extreme && Time.unscaledTime < _nextSlowPassLogAt)
                return;

            _nextSlowPassLogAt = Time.unscaledTime + SlowPassLogIntervalSeconds;
            _frameStepMilliseconds["RunLateUpdateStep.Harness"] = _harnessMilliseconds;

            double attributed = 0.0;
            var line = new System.Text.StringBuilder(320);
            line.Append(extreme
                ? "[LethalCCTV.Timing] EXTREME LateUpdate pass "
                : "[LethalCCTV.Timing] Slow LateUpdate pass ");
            line.Append(passMilliseconds.ToString("0.00"));
            line.Append("ms (cameras=");
            line.Append(CameraSpawner != null ? CameraSpawner.SpawnedCameras.Count : -1);
            line.Append(", focused=");
            line.Append(MonitorFocus.IsFocused);
            line.Append("):");
            foreach (KeyValuePair<string, double> step in _frameStepMilliseconds)
            {
                attributed += step.Value;
                if (step.Value < 0.5)
                    continue;
                line.Append(' ');
                line.Append(step.Key);
                line.Append('=');
                line.Append(step.Value.ToString("0.00"));
                line.Append("ms");
            }

            // Nested timings explain an opaque outer step without contributing to the
            // top-level attribution sum (which would otherwise double-count their time).
            foreach (KeyValuePair<string, double> step in _frameNestedStepMilliseconds)
            {
                if (step.Value < 0.5)
                    continue;
                line.Append(' ');
                line.Append(step.Key);
                line.Append('=');
                line.Append(step.Value.ToString("0.00"));
                line.Append("ms");
            }

            double unattributed = passMilliseconds - attributed;
            if (unattributed >= 0.5)
            {
                line.Append(" unattributed=");
                line.Append(unattributed.ToString("0.00"));
                line.Append("ms");
            }

            Log?.LogWarning(line.ToString());
        }

        internal static void RecordLateUpdateNestedStep(string stepName, long elapsedTicks)
        {
            _frameNestedStepMilliseconds.TryGetValue(stepName, out double accumulated);
            _frameNestedStepMilliseconds[stepName] = accumulated + TicksToMilliseconds(elapsedTicks);
            if (MonitorFocus.IsFocused)
                FocusPerfProbe.RecordStep(stepName, elapsedTicks);
        }

        // Cached delegates for the steps that wrap instance methods or need a closure. Allocating
        // these per frame would show up as GC pressure in the very profile this instrumentation
        // exists to read.
        private System.Action _stepCameraSpawnerSubscribe;
        private System.Action _stepTerminalNotPurchasedTeardown;
        private System.Action _stepEnsurePersistentMonitor;
        private System.Action _stepClearFeedsWhenShipPhaseBegins;
        private System.Action _stepReadFocusState;
        private System.Action _stepFocusPerfProbe;
        private System.Action _stepSweepMotorTickAll;
        private bool _focusedThisFrame;

        // #502 — the unpurchased teardown used to run all four of its shutdown
        // calls every frame the terminal was not purchased. Latched: it runs
        // once per unpurchased transition and re-arms as soon as the terminal
        // is purchased again (the purchased branch below clears the latch).
        private bool _unpurchasedTeardownDone;

        private void EnsureLateUpdateStepDelegates()
        {
            if (_stepCameraSpawnerSubscribe != null) return;

            _stepCameraSpawnerSubscribe = SubscribeCameraSpawner;
            _stepTerminalNotPurchasedTeardown = TearDownForUnpurchasedTerminal;
            _stepEnsurePersistentMonitor = EnsurePersistentMonitor;
            _stepClearFeedsWhenShipPhaseBegins = ClearFeedsWhenShipPhaseBegins;
            _stepReadFocusState = ReadFocusState;
            _stepFocusPerfProbe = TickFocusPerfProbe;
            _stepSweepMotorTickAll = TickCameraSweepMotors;
        }

        // Re-subscribe every frame (reference-compare no-op when unchanged):
        // RoundManager is recreated when the player quits to the menu and
        // re-hosts, and the one-shot Awake coroutine only ever saw the first
        // instance — cameras silently never spawned again that app run
        // (found 2026-07-11 via a no-cameras log with zero pipeline output).
        private void SubscribeCameraSpawner()
        {
            if (RoundManager.Instance != null)
                CameraSpawner?.Subscribe(RoundManager.Instance);
        }

        private void TickCameraSweepMotors()
        {
            CctvCameraSweepMotor.TickAll(CameraSpawner != null
                ? CameraSpawner.SpawnedCameras
                : QuadCameraAssignment.AllAssignedCameras);
        }

        /// <summary>MonitorFocus.IsFocused is read several times per pass and gates most of the
        /// focused-only work; timing the read separately keeps it out of "unattributed".</summary>
        private void ReadFocusState()
        {
            _focusedThisFrame = MonitorFocus.IsFocused;
        }

        private void TickFocusPerfProbe()
        {
            if (MonitorFocus.IsFocused)
                FocusPerfProbe.OnFocusedFrame();
            else if (_focusedThisFrame)
                FocusPerfProbe.OnFocusEnded();
        }

        private void TearDownForUnpurchasedTerminal()
        {
            // Latch: nothing below re-creates state while unpurchased, so a
            // second pass is pure waste. The latch is cleared when the terminal
            // becomes purchased (LateUpdateCore) and is bypassed here whenever
            // live state reappeared anyway (a monitor spawned, focus entered),
            // which covers a new round starting in an unpurchased state.
            bool hasLiveState = QuadMonitor.MonitorRoot != null || MonitorFocus.IsFocused;
            if (_unpurchasedTeardownDone && !hasLiveState)
                return;
            _unpurchasedTeardownDone = true;

            if (MonitorFocus.IsFocused)
                MonitorFocus.ForceExit("terminal-not-purchased");
            if (QuadMonitor.MonitorRoot != null)
            {
                QuadCameraAssignment.Unassign();
                QuadMonitor.Despawn();
            }
            CCTVMarkerManager.ClearAll();
            CCTVScanGlowManager.ClearAll();
            CCTVVanillaMonitorDisplay.Shutdown();
            CCTVFocusControlsOverlay.Shutdown();
        }

        private void LateUpdateCore()
        {
            EnsureLateUpdateStepDelegates();
            RunLateUpdateStep("CameraSpawner.Subscribe", _stepCameraSpawnerSubscribe);
            RunLateUpdateStep("CCTVOperatorStation.Ensure", CCTVOperatorStation.Ensure);
            RunLateUpdateStep("CCTVOperatorStation.TickStation", CCTVOperatorStation.TickStation);
            RunLateUpdateStep("Y4NGZPlayerAnimationBridge.Tick", Y4NGZPlayerAnimationBridge.Tick);
            RunLateUpdateStep("CctvCameraSweepMotor.TickAll", _stepSweepMotorTickAll);
            // Both run regardless of purchase state: the buttons module owns the
            // power-toggle removal and the pre-purchase hover tips, and the feed
            // sync resolves claim timeouts.
            RunLateUpdateStep("CCTVMonitorFeedSync.Tick", CCTVMonitorFeedSync.Tick);
            RunLateUpdateStep("CCTVVanillaMonitorButtons.Tick", CCTVVanillaMonitorButtons.Tick);
            RunLateUpdateStep("CCTVAccessButton.Tick", CCTVAccessButton.Tick);

            if (!CCTVTerminalUnlockable.IsPurchased())
            {
                RunLateUpdateStep("OpenBodyCamsCompat.Tick", OpenBodyCamsCompat.Tick);
                RunLateUpdateStep("TerminalNotPurchased.Teardown", _stepTerminalNotPurchasedTeardown);
                return;
            }

            // Purchased: re-arm the unpurchased teardown for the next transition.
            _unpurchasedTeardownDone = false;

            // #579 — behind the purchase gate deliberately. The note is the CCTV
            // system's controls reference, so a profile that has not bought the
            // system must not find one lying in the ship.
            RunLateUpdateStep("CCTVStickyNoteItem.EnsureSpawnedInShip", CCTVStickyNoteItem.EnsureSpawnedInShip);
            RunLateUpdateStep("EnsurePersistentMonitor", _stepEnsurePersistentMonitor);
            RunLateUpdateStep("CCTVVanillaMonitorDisplay.Tick", CCTVVanillaMonitorDisplay.Tick);
            RunLateUpdateStep("ClearFeedsWhenShipPhaseBegins", _stepClearFeedsWhenShipPhaseBegins);
            RunLateUpdateStep("MonitorFocus.IsFocused", _stepReadFocusState);
            bool focused = _focusedThisFrame;

            // Phase 1.5a Round 2 â€” clears MonitorFocus.WasActiveThisFrame once we've moved
            // past the frame ESC was pressed on. Plugin is a BaseUnityPlugin (MonoBehaviour)
            // and is always active, so LateUpdate ticks every frame regardless of overlay
            // state. Cheap: one int comparison per frame.
            if (focused || Time.unscaledTime >= _nextIdleCompatTickAt)
            {
                _nextIdleCompatTickAt = focused
                    ? Time.unscaledTime
                    : Time.unscaledTime + IdleCompatTickIntervalSeconds;
                RunLateUpdateStep("OpenBodyCamsCompat.Tick", OpenBodyCamsCompat.Tick);
            }
            RunLateUpdateStep("ShipTurretController.Tick", ShipTurretController.Tick);
            RunLateUpdateStep("MonitorFocus.TickFrame", MonitorFocus.TickFrame);
            RunLateUpdateStep(
                "Y4NGZPlayerAnimationBridge.TickLocalFirstPersonHandsAfterCamera",
                Y4NGZPlayerAnimationBridge.TickLocalFirstPersonHandsAfterCamera);
            RunLateUpdateStep("CCTVFocusControlsOverlay.Tick", CCTVFocusControlsOverlay.Tick);

            // T7 â€” per-frame mouselook on the active focus pane. Also owns the
            // once-per-LateUpdate isPlayerDead check that closes the
            // death-while-focused stuck-look bug. Cheap: early-return when
            // not focused.
            RunLateUpdateStep("FocusMouselook.Tick", FocusMouselook.Tick);
            if (focused || CCTVMarkerManager.HasActiveMarkers)
                RunLateUpdateStep("CCTVMarkerManager.Tick", CCTVMarkerManager.Tick);
            if (focused || CCTVScanGlowManager.HasActiveGlows)
                RunLateUpdateStep("CCTVScanGlowManager.Tick", CCTVScanGlowManager.Tick);

            RunLateUpdateStep("FocusPerfProbe.Tick", _stepFocusPerfProbe);
        }

        private void RunLateUpdateStep(string stepName, System.Action action)
        {
            if (action == null) return;

            // Always timed since 2026-07-31 (GetTimestamp is a raw QPC read; negligible next to
            // the work being measured): the per-frame breakdown feeds the slow-pass report, and
            // the focus probe keeps its original focused-only recording.
            bool focusTimed = MonitorFocus.IsFocused;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                action();
            }
            catch (System.Exception ex)
            {
                float now = Time.unscaledTime;
                if (!_lateUpdateErrorLogTimes.TryGetValue(stepName, out float lastLogAt) || now - lastLogAt >= 2f)
                {
                    _lateUpdateErrorLogTimes[stepName] = now;
                    Log.LogError($"[LethalCCTV] LateUpdate step '{stepName}' failed: {ex}");
                }
            }
            finally
            {
                long endedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                long elapsed = endedAt - startedAt;
                _frameStepMilliseconds.TryGetValue(stepName, out double accumulated);
                _frameStepMilliseconds[stepName] = accumulated + TicksToMilliseconds(elapsed);
                if (focusTimed)
                    FocusPerfProbe.RecordStep(stepName, elapsed);

                // The bookkeeping above runs inside the timed pass but outside every step's own
                // measurement, so it used to land in "unattributed" — and FocusPerfProbe.RecordStep
                // only runs while focused, which is exactly the focused-only shape of the gap.
                _harnessMilliseconds += TicksToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - endedAt);
            }
        }

        private void EnsurePersistentMonitor()
        {
            if (QuadMonitor.MonitorRoot != null) return;
            if (Time.unscaledTime < _nextMonitorSpawnProbeTime) return;
            _nextMonitorSpawnProbeTime = Time.unscaledTime + 0.5f;

            if (!CCTVTerminalUnlockable.IsPurchased()) return;
            if (GameObject.Find("Environment/HangarShip") == null) return;

            QuadMonitor.Spawn();
            if (QuadMonitor.MonitorRoot != null)
            {
                QuadCameraAssignment.Unassign();
                Log.LogInfo("[LethalCCTV] Ship-persistent monitor spawned and initialized to display-off state.");
            }
        }

        private void ClearFeedsWhenShipPhaseBegins()
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null) return;

            bool inShipPhase = sor.inShipPhase;
            if (inShipPhase && !_lastKnownShipPhase)
            {
                MonitorFocus.ForceExit("ship-phase");
                MonitorFocus.ClearZoomState();
                QuadCameraAssignment.Unassign();
                CCTVMarkerManager.ClearAll();
                CCTVScanGlowManager.ClearAll();
                Log.LogInfo("[LethalCCTV] Ship phase detected; CCTV feeds cleared to display-off state.");
            }
            _lastKnownShipPhase = inShipPhase;
        }

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            // #393: this teardown unsubscribes the dungeon-generation hook and calls
            // Harmony.UnpatchSelf, so an unexpected destroy silently disables every CCTV
            // system for the rest of the run. That is exactly what happened in a profile
            // where BepInEx's manager object was not hide-flagged, and the round-time log
            // was blank because nothing here ever announced it. Never again.
            if (!_applicationQuitting)
            {
                Log?.LogError(
                    "[LethalCCTV] SurveillanceBootstrap host destroyed outside application shutdown " +
                    $"(frame={Time.frameCount}, host='{(gameObject != null ? gameObject.name : "<null>")}', " +
                    $"scene='{(gameObject != null ? gameObject.scene.name : "<null>")}'). " +
                    "Cameras, interior support and all Harmony patches are going down with it.");
            }

            // Phase 1.7b â€” tear the bake down FIRST so a late-firing
            // endCameraRendering callback cannot deref anything we're about to
            // destroy. QuadMonitor.Despawn will also call Shutdown â€” idempotent.
            // Defensive: runs even if a downstream teardown throws and skips
            // Despawn entirely.
            NightVisionBaker.Shutdown();
            CctvTileCullingBypass.Shutdown();

            if (_subscribeCoroutine != null)
            {
                StopCoroutine(_subscribeCoroutine);
                _subscribeCoroutine = null;
            }
            if (Config != null)
            {
                Config.NightVisionParamsChanged -= OnNightVisionParamsChanged;
            }
            if (CameraSpawner != null)
            {
                CameraSpawner.CamerasReady -= OnCamerasReady;
                CameraSpawner.Unsubscribe();
            }
            OpenBodyCamsCompat.Shutdown();
            ShipTurretController.Shutdown();
            _harmony?.UnpatchSelf();
            RadarOverlay.Shutdown();
            CCTVWalkieTalkieBridge.Shutdown();
            CCTVMarkerManager.Shutdown();
            CCTVScanGlowManager.Shutdown();
            Y4NGZPlayerAnimationBridge.Shutdown();
            CCTVVanillaMonitorButtons.Shutdown();
            CCTVMonitorFeedSync.Shutdown();
            CCTVOperatorStation.Shutdown();
            CCTVVanillaMonitorDisplay.Shutdown();
            CCTVFocusControlsOverlay.Shutdown();
            QuadCameraAssignment.Unassign();
            MonitorFocus.Shutdown();
            // Belt-and-braces: MonitorFocus.Shutdown already routes through
            // ForceExit -> RestoreLocalPlayerObstructors, which restores the
            // phone. This also clears the compat's once-per-reason log gate so a
            // plugin reload starts from a clean slate.
            LethalPhonesCompat.Shutdown();
            QuadMonitor.Despawn();
        }
    }
}



