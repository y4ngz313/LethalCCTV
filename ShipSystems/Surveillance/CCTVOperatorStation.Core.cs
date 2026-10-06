using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVOperatorStation
    {
        private const string RootName = "LethalCCTV_VanillaOperatorStation";
        private const string MonitorWallPath = "Environment/HangarShip/ShipModels2b/MonitorWall";
        private const string LowerRightMonitorPath = "Environment/HangarShip/ShipModels2b/MonitorWall/SingleScreen";
        private const float StationDistanceFromMonitor = 1.28f;
        private const float StationDropFromTopMonitor = 3.18f;
        private const float StationSlotOffsetFromDeadline = 1.45f;
        // NOT ".lethalbundle": LethalLevelLoader auto-loads every *.lethalbundle in the
        // plugins folder, and a second AssetBundle.LoadFromFile on an already-loaded
        // bundle returns null — which silently forced the generated-prop fallback.
        private const string StationPropsBundleFile = "y4ngz-cctv-stationprops.stationbundle";
        private const string StationPropsBundleName = "y4ngz-cctv-stationprops";
        // The press clip outlived the external access button prefab (removed
        // 2026-07-02): it now voices the vanilla monitor bezel buttons.
        private const string AccessButtonPressClipAssetName = "Y4NGZ_CCTV_ButtonPress";


        // Access button default: desk-flat beside the joystick. Values are the
        // F10-authored placement from the tuned dev profile (2026-08-20); the old
        // pre-authoring defaults left the button floating in open air on any
        // profile without a local placement JSON (#569). Operator-authored
        // placement (target "button") still wins.
        private static readonly Vector3 AccessButtonLocalPosition = new Vector3(0.489f, 1.089f, 1.025f);
        private static readonly Vector3 AccessButtonLocalEuler = new Vector3(270f, 0.3f, 0f);
        private static readonly Vector3 AccessButtonLocalScale = new Vector3(0.487f, 0.487f, 0.487f);

        // #582 — CCTV sticky note default. The note used to be probed out of the
        // monitor's world AABB, which put it inside the wall and dropped it under
        // the floor; it is now placed from an authored anchor under this root.
        //
        // The derived desk-plane guess that replaced the probe was still only a
        // guess. These numbers are the pose Lawson authored through the F9
        // ship-scoped placement editor and verified in game, so the code default
        // and the authored default are now the same pose: a profile with no local
        // placement JSON spawns the note exactly where the editor put it.
        // Operator-authored placement (target "note") still wins.
        private static readonly Vector3 NoteLocalPosition = new Vector3(1.33f, 2.53f, 1.37f);
        private static readonly Vector3 NoteLocalEuler = new Vector3(351.47f, 263.85f, 0f);
        private static readonly Vector3 NoteLocalScale = Vector3.one * CCTVStickyNoteItem.VanillaNoteScale;
        private const float BrakeLeverCardinalTiltDeg = 7f;
        private const float JoystickIdleAfterSeconds = 0.16f;
        // Below this the stick is considered fully settled and the pivot is
        // released so StartMatchLever's Animator can drive the throttle mesh.
        private const float JoystickRestEpsilonSqr = 1e-5f;
        // Focus/radar seat defaults are the F10-authored placements from the tuned
        // dev profile (2026-08-20); the previous defaults seated the camera ~0.5m
        // and ~16 deg off on profiles without a local placement JSON (#569).
        private static readonly Vector3 FocusLocalPosition = new Vector3(-0.55f, 1.92f, 0.547f);
        // Face the monitor wall with a level horizon and leave room for both hands.
        private static readonly Vector3 FocusLocalEuler = new Vector3(7f, 87f, 0f);
        private static readonly Vector3 RadarFocusLocalPosition = FocusLocalPosition;
        private static readonly Vector3 RadarFocusLocalEuler = new Vector3(358.49f, 133.75f, 342.72f);
        private static readonly Vector3 OperatorPoseLocalPosition = new Vector3(0.04f, 1.12f, 0.56f);
        private static readonly Vector3 OperatorPoseLocalEuler = new Vector3(0f, 87f, 0f);
        private static readonly Vector3 PlayerRootLocalPosition = new Vector3(
            OperatorPoseLocalPosition.x,
            0f,
            OperatorPoseLocalPosition.z);
        private static readonly Vector3 PlayerRootLocalEuler = OperatorPoseLocalEuler;
        private const float DefaultThrottleLeftOffsetMeters = 0.72f;
        private const float ThrottleNearbyTransformRadius = 1.50f;
        // Loosened after the 2026-06-12 run: the lever's black casing mesh was never
        // captured by the renderer sweep (log showed zero renderer targets), leaving
        // the casing behind when the assembly moved. The candidate dump below logs
        // every nearby renderer with its exclusion reason so future misses are
        // diagnosable from a single run.
        private const float ThrottleNearbyRendererCenterRadius = 1.10f;
        private const float ThrottleNearbyRendererClosestRadius = 0.65f;
        private const float ThrottleNearbyRendererMaxAxis = 1.30f;
        private const float ThrottleNearbyRendererMaxVerticalDelta = 0.95f;
        private const float ThrottleRendererCandidateDumpRadius = 1.80f;
        private const float ControlGeometryCandidateRadius = 2.25f;
        private const int ControlGeometryRendererDumpLimit = 60;
        private const int ControlGeometryColliderDumpLimit = 40;

        private static GameObject _root;
        private static Transform _focusViewAnchor;
        private static Transform _radarViewAnchor;
        private static Transform _playerRootAnchor;
        private static Transform _operatorPoseAnchor;
        private static Transform _noteAnchor;
        private static CCTVStationPlacementEditor _placementEditor;
        private static float _nextProbeAt;
        private static float _debugStationSpawnUntil;
        private static AssetBundle _stationPropsBundle;
        private static bool _stationPropsBundleLoadAttempted;
        private static bool _warnedStationPropsBundleMissing;
        private static bool _defaultThrottlePlacementCaptured;
        private static Vector3 _defaultThrottleLocalPosition;
        private static Vector3 _defaultThrottleLocalEuler;
        private static Vector3 _defaultThrottleLocalScale;
        private static Vector3 _defaultThrottleWorldPosition;
        private static Quaternion _defaultThrottleWorldRotation;
        private static Vector3 _defaultThrottleWorldScale;
        private static Transform _throttleTransform;
        // Lever pieces that do NOT live under the resolved primary transform (e.g. the
        // visible handle mesh vs. the interact trigger in vanilla's split hierarchy).
        // They are translated by the same world delta as the primary so the visual
        // prop, collider, and grab point always move together.
        private static readonly List<Transform> _throttleSecondaryTargets = new List<Transform>();
        private static readonly List<Vector3> _throttleSecondaryDefaultWorld = new List<Vector3>();
        private static readonly List<Quaternion> _throttleSecondaryDefaultWorldRotations = new List<Quaternion>();
        private static readonly List<Vector3> _throttleSecondaryDefaultLocalScales = new List<Vector3>();
        private static bool _throttleHierarchyDumped;
        private static bool _throttleRendererCandidatesDumped;
        private static bool _stationEventsSubscribed;
        private static Transform _joystickTiltPivot;
        private static Transform _rightHandTarget;
        private static Transform _leftHandIdleTarget;
        private static Transform _leftHandPressTarget;
        private static Transform _lookTarget;
        private static Quaternion _joystickTiltBaseLocalRotation = Quaternion.identity;
        private static Vector3 _joystickTiltNeutralLocalAxis = Vector3.up;
        private static readonly CCTVJoystickPhaseDriver _joystickPhase = new CCTVJoystickPhaseDriver();
        private static float _lastJoystickMoveAt;
        private static bool _joystickCameraControlActive;
        private static float _nextBrakeLeverControlResolveAt;
        // #502 — backoff for the brake-lever resolve, which used to re-run a
        // scene-wide FindObjectOfType<StartMatchLever> at 1 Hz forever.
        private static readonly float[] BrakeLeverResolveBackoffSeconds = { 1.0f, 5.0f, 15.0f };
        private static int _brakeLeverResolveFailures;
        // #502 — 0.5s over static ship geometry cost 8-18 ms per pass for a
        // result that only changes on station spawn, teardown and focus
        // takeover. Those three all force an immediate re-aim (Ensure's spawn
        // path, Shutdown resetting _nextFocusAnchorAimAt to 0, and the
        // force: true calls from MonitorFocus), so the timer is now only a
        // slow safety net for anything that moves an anchor without an event.
        private const float FocusAnchorAimIntervalSeconds = 5.0f;
        private static float _nextFocusAnchorAimAt;
        private static bool _warnedBrakeLeverControlMissing;
        private static bool _loggedBrakeLeverControlRig;
        private static string _rightHandGripSource = "unresolved";
        private static bool _controlGeometryDumpedOnEnter;
        private static AudioClip _accessButtonPressClip;
        private static bool _accessButtonPressClipLoadAttempted;

        // #502 — cache for the MonitorWall probe. GameObject.Find walks the
        // whole scene by path; it used to run at 1 Hz from the spawn path and
        // again from the station-pose fallback. The wall is ship geometry, so
        // it is resolved from StartOfRound.elevatorTransform when available
        // (no scene walk at all) and only cached-through afterwards.
        private static Transform _monitorWallCache;

        private static Transform ResolveMonitorWall()
        {
            if (_monitorWallCache != null)
                return _monitorWallCache;

            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            Transform wall = ship != null ? ship.Find("ShipModels2b/MonitorWall") : null;
            if (wall == null)
                wall = GameObject.Find(MonitorWallPath)?.transform;

            _monitorWallCache = wall;
            return wall;
        }

        private sealed class ControlGeometryCandidate
        {
            internal Transform Transform;
            internal string Path;
            internal Bounds Bounds;
            internal float CenterDistance;
            internal float ClosestDistance;
            internal float MaxAxis;
            internal string Source;
        }

        internal static bool IsReady => _root != null;
        internal static Transform FocusViewAnchor => _focusViewAnchor;
        internal static Transform RadarViewAnchor => _radarViewAnchor;
        internal static Transform PlayerRootAnchor => _playerRootAnchor != null ? _playerRootAnchor : _operatorPoseAnchor;
        internal static Transform OperatorPoseAnchor => _operatorPoseAnchor;
        /// <summary>
        /// #582 — spawn pose for the CCTV controls sticky note. Null until the
        /// station root exists; CCTVStickyNoteItem re-asks every frame.
        /// </summary>
        internal static Transform NoteAnchor => _noteAnchor;
        internal static Transform JoystickTiltPivot => _joystickTiltPivot;
        internal static Transform RightHandTarget => _rightHandTarget;
        internal static Transform LeftHandIdleTarget => _leftHandIdleTarget;
        internal static Transform LeftHandPressTarget => _leftHandPressTarget;
        internal static Transform LookTarget => _lookTarget;

        // World-space point the player must actually aim at to use the station.
        // Keep the interaction anchored to the monitor, not the desk controls.
        internal static void Ensure()
        {
            Ensure(allowUnpurchasedForDebug: false);
        }

        private static void Ensure(bool allowUnpurchasedForDebug)
        {
            EnsureStationEventSubscriptions();

            if (_root != null)
            {
                if (_root.scene.IsValid() && _root.activeInHierarchy)
                {
                    if (!allowUnpurchasedForDebug && !CCTVTerminalUnlockable.IsPurchased() && !IsDebugStationSpawnActive())
                    {
                        Shutdown();
                        return;
                    }
                    // Self-heals per frame: covers mid-day purchase and a destroyed clone.
                    RunPendingWarmupStep();
                    CCTVAccessButton.Ensure(_root.transform);
                    return;
                }

                Shutdown();
            }

            if (!allowUnpurchasedForDebug && !CCTVTerminalUnlockable.IsPurchased())
                return;

            if (Time.unscaledTime < _nextProbeAt)
                return;

            _nextProbeAt = Time.unscaledTime + 1.0f;

            long stepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!TryResolveStationPose(out Transform ship, out Vector3 worldPosition, out Quaternion worldRotation, out string poseSource) ||
                ResolveMonitorWall() == null)
                return;
            double poseMs = TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - stepStartedAt);

            _root = new GameObject(RootName);
            _root.transform.SetParent(ship, worldPositionStays: true);
            _root.transform.SetPositionAndRotation(worldPosition, worldRotation);
            _root.transform.localScale = Vector3.one;

            // Re-assert after the stale-root Shutdown above dropped them, so the
            // freshly spawned station is subscribed within this same pass.
            EnsureStationEventSubscriptions();

            stepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            CreateAnchors(_root.transform);
            ApplySavedStationPlacements();
            EnsureFocusAnchorAimed();
            double anchorsMs = TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - stepStartedAt);

            stepStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            ConfigureBrakeLeverControlRig();
            CCTVAccessButton.Ensure(_root.transform);
            double rigMs = TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - stepStartedAt);

            // Warm-ups (authored-controller bundle recovery, HUD-dim scene scan) run
            // one per frame on the following self-heal ticks instead of here: the
            // spawn pass alone measured ~955ms two sessions in a row (#280).
            _pendingWarmupStep = 1;

            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV.Timing] station spawn pass split: pose={poseMs:F1}ms anchors={anchorsMs:F1}ms " +
                $"controlRig={rigMs:F1}ms; warmups deferred to the next frames (#280).");
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Vanilla CCTV standing operator station spawned from {poseSource}. " +
                $"rootWorld={FormatVector(_root.transform.position)} rootLocal={FormatVector(_root.transform.localPosition)} " +
                $"rootEuler={FormatVector(_root.transform.eulerAngles)} control={GetPath(_joystickTiltPivot)} interactTarget=vanilla-red-monitor-button.");
        }

        private static int _pendingWarmupStep;

        private static void RunPendingWarmupStep()
        {
            if (_pendingWarmupStep == 0)
                return;

            int step = _pendingWarmupStep;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (step == 1)
            {
                Y4NGZPlayerAnimationBridge.WarmUpAuthoredController();
                _pendingWarmupStep = 2;
            }
            else
            {
                MonitorFocus.PrewarmFocusHudScan();
                _pendingWarmupStep = 0;
            }

            // Warm-up timing is profiling detail on a one-shot path, not a fault. It stays at
            // Warning only while the diagnostics toggle asks for it.
            string warmup =
                $"[LethalCCTV.Timing] station warmup step {step} " +
                $"({(step == 1 ? "authored controller" : "focus HUD scan")}) took " +
                $"{TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - startedAt):F1}ms (#280).";
            if (SurveillanceBootstrap.Config?.PerformanceTimingLogging?.Value == true)
                SurveillanceBootstrap.Log?.LogWarning(warmup);
            else
                SurveillanceBootstrap.Log?.LogDebug(warmup);
        }

        private static double TicksToMs(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        internal static void Shutdown()
        {
            ReleaseJoystickSession();
            ClearStationEventSubscriptions();
            _placementEditor?.Cancel();
            CCTVAccessButton.Shutdown();

            if (_root != null)
                UnityEngine.Object.Destroy(_root);

            _root = null;
            _pendingWarmupStep = 0;
            _focusViewAnchor = null;
            _radarViewAnchor = null;
            _playerRootAnchor = null;
            _operatorPoseAnchor = null;
            _noteAnchor = null;
            _focusAnchorAimApplied = false;
            _radarAimRejectionLogged = false;
            _radarScreenRenderer = null;
            _radarScreenCenterLocal = Vector3.zero;
            _radarScreenCenterResolved = false;
            _nextRadarScreenCenterResolveAt = 0f;
            _defaultThrottlePlacementCaptured = false;
            _defaultThrottleWorldPosition = Vector3.zero;
            _throttleTransform = null;
            _throttleSecondaryTargets.Clear();
            _throttleSecondaryDefaultWorld.Clear();
            _throttleSecondaryDefaultWorldRotations.Clear();
            _throttleSecondaryDefaultLocalScales.Clear();
            _throttleHierarchyDumped = false;
            _throttleRendererCandidatesDumped = false;
            _joystickTiltPivot = null;
            _rightHandTarget = null;
            _leftHandIdleTarget = null;
            _leftHandPressTarget = null;
            _lookTarget = null;
            _joystickTiltBaseLocalRotation = Quaternion.identity;
            _joystickPhase.Reset();
            _lastJoystickMoveAt = 0f;
            _joystickCameraControlActive = false;
            _nextBrakeLeverControlResolveAt = 0f;
            _nextFocusAnchorAimAt = 0f;
            _monitorWallCache = null;
            _brakeLeverResolveFailures = 0;
            _warnedBrakeLeverControlMissing = false;
            _loggedBrakeLeverControlRig = false;
            _controlGeometryDumpedOnEnter = false;
        }

        private static void EnsureStationEventSubscriptions()
        {
            if (_stationEventsSubscribed)
                return;

            CCTVStationEvents.ChairEntered += OnStationChairEntered;
            CCTVStationEvents.ChairExited += OnStationChairExited;
            CCTVStationEvents.CameraControlChanged += OnStationCameraControlChanged;
            CCTVStationEvents.JoystickMoved += OnStationJoystickMoved;
            _stationEventsSubscribed = true;
        }

        // Mirror of EnsureStationEventSubscriptions. Without it the four handlers
        // stayed on CCTVStationEvents after every Shutdown (ship phase change,
        // un-purchase, plugin teardown) and accumulated across sessions.
        private static void ClearStationEventSubscriptions()
        {
            if (!_stationEventsSubscribed)
                return;

            CCTVStationEvents.ChairEntered -= OnStationChairEntered;
            CCTVStationEvents.ChairExited -= OnStationChairExited;
            CCTVStationEvents.CameraControlChanged -= OnStationCameraControlChanged;
            CCTVStationEvents.JoystickMoved -= OnStationJoystickMoved;
            _stationEventsSubscribed = false;
        }

        private static void OnStationChairEntered(PlayerControllerB _)
        {
            BeginJoystickSession(_);
            _joystickCameraControlActive = false;
            ResetJoystickMotionTarget(immediate: true);
            UpdateJoystickMotion(force: true);

            // Diagnostic-only: the renderer/collider candidate scans inside this
            // dump cost ~740ms and were the bulk of the CCTV enter lag spike
            // (EnterPerf, 2026-07-11). Opt in via the recon/placement-debug
            // config flags when the geometry tables are actually needed.
            bool wantGeometryDump = SurveillanceBootstrap.Config != null &&
                (SurveillanceBootstrap.Config.ReconLoggingEnabled.Value || SurveillanceBootstrap.Config.PlacementDebugLoggingEnabled.Value);
            if (wantGeometryDump && !_controlGeometryDumpedOnEnter)
            {
                _controlGeometryDumpedOnEnter = true;
                DumpControlGeometry("station-enter");
            }
        }

        private static void OnStationChairExited(PlayerControllerB _)
        {
            // The animation session retains the lever through its exit hold.
            // Entry failures without a session still release on chair exit.
            if (!Y4NGZPlayerAnimationBridge.IsLocalSessionActive(_)) ReleaseJoystickSession(_);
            _joystickCameraControlActive = false;
            ResetJoystickMotionTarget();
        }

        private static void OnStationCameraControlChanged(bool active)
        {
            _joystickCameraControlActive = active;
            if (!active)
                ResetJoystickMotionTarget();
        }

        private static void OnStationJoystickMoved(Vector2 appliedDeltaDeg)
        {
            _lastJoystickMoveAt = Time.unscaledTime;
            _joystickPhase.SetRequested(appliedDeltaDeg);
        }

        private static AssetBundle TryGetStationPropsBundle()
        {
            if (_stationPropsBundle != null)
                return _stationPropsBundle;
            if (_stationPropsBundleLoadAttempted)
                return null;

            _stationPropsBundleLoadAttempted = true;

            // Reuse an instance another loader already holds (LethalLevelLoader grabs
            // *.lethalbundle files; older deploys shipped this bundle under that
            // extension). LoadFromFile on an already-loaded bundle returns null.
            _stationPropsBundle = FindAlreadyLoadedStationPropsBundle();
            if (_stationPropsBundle != null)
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] CCTV station props bundle reused from an already-loaded AssetBundle instance (likely LethalLevelLoader).");
                return _stationPropsBundle;
            }

            string pluginDirectory = GetPluginDirectory();
            string[] bundleCandidates =
            {
                Path.Combine(pluginDirectory ?? string.Empty, StationPropsBundleFile),
                Path.Combine(pluginDirectory ?? string.Empty, "Assets", StationPropsBundleFile),
            };

            for (int i = 0; i < bundleCandidates.Length; i++)
            {
                string path = bundleCandidates[i];
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;

                try
                {
                    _stationPropsBundle = AssetBundle.LoadFromFile(path);
                    if (_stationPropsBundle != null)
                    {
                        SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] CCTV station props bundle loaded from '{path}'.");
                        return _stationPropsBundle;
                    }

                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] AssetBundle.LoadFromFile returned null for station props bundle '{path}' (duplicate load by another mod?).");
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Station props bundle load failed for '{path}': {ex.Message}");
                }
            }

            if (!_warnedStationPropsBundleMissing)
            {
                _warnedStationPropsBundleMissing = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Station props bundle '{StationPropsBundleFile}' could not be loaded from '{pluginDirectory}'; " +
                    "the provided rolling chair / joystick prefabs are unavailable and generated fallback props will be used.");
            }

            return null;
        }

        private static AssetBundle FindAlreadyLoadedStationPropsBundle()
        {
            try
            {
                foreach (AssetBundle bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle != null &&
                        string.Equals(bundle.name, StationPropsBundleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return bundle;
                    }
                }
            }
            catch { }

            return null;
        }

        private static AudioClip LoadAudioClipAsset(AssetBundle bundle, string assetName)
        {
            if (bundle == null || string.IsNullOrWhiteSpace(assetName))
                return null;

            AudioClip clip = bundle.LoadAsset<AudioClip>(assetName);
            if (clip != null)
                return clip;

            clip = bundle.LoadAsset<AudioClip>(assetName + ".mp3");
            if (clip != null)
                return clip;

            string[] assetNames = bundle.GetAllAssetNames();
            if (assetNames == null)
                return null;

            for (int i = 0; i < assetNames.Length; i++)
            {
                string candidate = assetNames[i];
                string fileName = Path.GetFileNameWithoutExtension(candidate);
                if (!string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                clip = bundle.LoadAsset<AudioClip>(candidate);
                if (clip != null)
                    return clip;
            }

            return null;
        }

        /// <summary>
        /// Button press clip from the station props bundle, shared by the taken-over
        /// vanilla monitor bezel buttons (CCTVVanillaMonitorButtons).
        /// </summary>
        internal static AudioClip GetButtonPressClip()
        {
            if (_accessButtonPressClipLoadAttempted)
                return _accessButtonPressClip;

            _accessButtonPressClipLoadAttempted = true;
            AssetBundle bundle = TryGetStationPropsBundle();
            if (bundle == null)
                return null;

            _accessButtonPressClip =
                LoadAudioClipAsset(bundle, AccessButtonPressClipAssetName) ??
                LoadAudioClipAsset(bundle, "press");

            if (_accessButtonPressClip != null)
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] CCTV button press audio loaded from '{StationPropsBundleFile}' asset='{_accessButtonPressClip.name}'.");
            else
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Station props bundle did not contain button press audio; monitor buttons will interact silently.");

            return _accessButtonPressClip;
        }
    }
}
