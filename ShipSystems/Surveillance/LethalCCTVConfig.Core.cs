using System;
using BepInEx.Configuration;
using CSync.Extensions;
using CSync.Lib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    public partial class LethalCCTVConfig : SyncedConfig2<LethalCCTVConfig>
    {
        // === Phase 1 — Monitor Transform ===
        // Promoted from six config entries to constants for 1.0 (#575). This is
        // the ship-monitor anchor Lawson tuned in-game on the Phase 1.3 deploy;
        // it is a placement fact about the HangarShip, not a player preference,
        // and a stale entry in an old cfg put the wall through the hull.
        public Vector3 MonitorPosition => new Vector3(11.0f, 3.0f, -13.0f);
        public Quaternion MonitorRotation => Quaternion.Euler(0.0f, 90.0f, 0.0f);

        internal const string ShipTerminalSection = "Ship Terminal";
        internal const string CameraPlacementSection = "Camera Placement";
        internal const string CameraCountsSection = "Camera Counts";
        internal const string ShipMonitorSection = "Ship Monitor";
        internal const string PerformanceSection = "Performance";
        internal const string DisplaySection = "Display and Lighting";
        internal const string PhysicalCamerasSection = "Physical Cameras";
        internal const string OperatorControlsSection = "Operator Controls";
        internal const string OperatorPermissionsSection = "Operator Permissions";
        internal const string DiagnosticsSection = "Diagnostics";
        internal const string RadarSection = "Radar";
        internal const string TrackingBoxesSection = "Tracking Boxes";

        // === Phase 0 Diagnostics ===
        public ConfigEntry<bool> ReconLoggingEnabled { get; private set; }
        public ConfigEntry<bool> PlacementDebugLoggingEnabled { get; private set; }

        // === Phase 1 — Camera Exclusion ===
        [SyncedEntryField] public SyncedEntry<bool> ExcludeEntranceTiles;
        [SyncedEntryField] public SyncedEntry<bool> ExcludeFireExitTiles;
        [SyncedEntryField] public SyncedEntry<bool> ExcludeMineshaftTunnels;
        [SyncedEntryField] public SyncedEntry<bool> ExcludeTinyTiles;
        [SyncedEntryField] public SyncedEntry<float> TinyTileMaxFloorAreaM2;
        [SyncedEntryField] public SyncedEntry<string> TileNameExclusionPatterns;

        // === Phase 1 — Camera Placement ===
        // Promoted to constants for 1.0 (#575): where in a tile's bounds the
        // camera sits is placement calibration the whole surface-mount pass is
        // tuned against, not a dial.
        public float PlacementHeightFraction => 0.85f;
        public float PlacementHorizontalFraction => 0.35f;

        // === #393 — Standalone (no Contracted) vanilla store price ===
        [SyncedEntryField] public SyncedEntry<int> CctvTerminalPrice;

        // === Phase 2 — Ship-Side Hijack ===
        [SyncedEntryField] public SyncedEntry<float> CycleSeconds;

        // === #597 — GeneralImprovements better-monitors screen placement ===
        // Per-client on purpose: GI's UseBetterMonitors/AddMoreBetterMonitors are
        // client-side, so the valid index range differs between host and client.
        public ConfigEntry<int> GeneralImprovementsFeedScreenIndex { get; private set; }
        public ConfigEntry<int> GeneralImprovementsRadarScreenIndex { get; private set; }

        // === Phase 2 — Render Performance ===
        public ConfigEntry<int> RenderHzPerMonitor { get; private set; }
        public ConfigEntry<int> BodycamRenderHz { get; private set; }
        public ConfigEntry<bool> DynamicActiveRendererEnabled { get; private set; }
        public ConfigEntry<int> InactivePaneRenderHz { get; private set; }
        // #562 — idle rate for the single feed left on the monitor wall when
        // nobody is operating the station.
        public ConfigEntry<int> UnmannedIdleRenderHz { get; private set; }
        public ConfigEntry<int> RenderRTWidth { get; private set; }
        public ConfigEntry<float> FarClipPlane { get; private set; }
        public ConfigEntry<float> FieldOfView { get; private set; }
        public ConfigEntry<bool> CCTVShadowMapsEnabled { get; private set; }
        public ConfigEntry<bool> UseVanillaRadarFeed { get; private set; }
        // #542 â€” a facility can hold 40 pieces of scrap. Item blips are the one
        // marker class that can swamp the map, so they get their own switch.
        public ConfigEntry<bool> ShowRadarItemBlips { get; private set; }
        // #542 — one dial over every radar marker class. The authored per-class
        // ratios are unaffected, so raising it can never reorder the size
        // hierarchy the map is read by.
        public ConfigEntry<float> RadarBlipScale { get; private set; }
        // The operator-presentation calibration family (live arms, visor,
        // operator root, Api shoulder/camera anchors, cap plugs, elbow
        // poles, hand rest frames and the debug-nudge hand offsets) was
        // promoted to constants in Y4NGZPlayerAnimationBridge for 1.0
        // (#575). The retired A/B levers (reach assist, intro camera
        // follows hand, head hide, LEGACY arms offset) went with them.

        // === Phase 1.8 — Room-Aware Placement ===
        // Threshold knobs that gate which tiles get cameras are synced, same
        // pattern as the existing exclusion config — host's choice propagates so
        // every client spawns cameras on the same tiles. The aim-height and the
        // Gate A diagnostic flag are local; aim affects only per-client camera
        // rotation (RT contents are client-local anyway), and the Gate A flag is
        // a per-client testing safety.
        [SyncedEntryField] public SyncedEntry<bool> ExcludeCorridors;
        [SyncedEntryField] public SyncedEntry<int> CorridorMaxConnectedDoorways;
        [SyncedEntryField] public SyncedEntry<float> MinHorizontalDimensionM;
        [SyncedEntryField] public SyncedEntry<float> MinFloorAreaM2;
        // Phase 1.8 Placement T1 — IMPLEMENT building blocks. These four
        // synced knobs are READ by T2/T3/T4 (RoomComponentBuilder /
        // CameraSelector / CameraBudgetCapper) but those types have no
        // callers until T5 (spawner integration). The bool below is a
        // local per-client STOP flag mirroring Phase18GateAOnly's shape;
        // it gates the T5 instantiation step so first-deploy of placement
        // code lands in dry-run mode.
        [SyncedEntryField] public SyncedEntry<int> JunctionMinDegree;
        [SyncedEntryField] public SyncedEntry<int> MinimumCameraCount;
        [SyncedEntryField] public SyncedEntry<int> MaximumCameraCount;
        public int NormalizedMaximumCameraCount => Mathf.Max(0, MaximumCameraCount.Value);
        public int NormalizedMinimumCameraCount =>
            Mathf.Clamp(MinimumCameraCount.Value, 0, NormalizedMaximumCameraCount);
        [SyncedEntryField] public SyncedEntry<string> CameraBudgetPriority;
        [SyncedEntryField] public SyncedEntry<string> LinearChainCoverage;
        [SyncedEntryField] public SyncedEntry<float> LeafCameraMinAreaM2;
        [SyncedEntryField] public SyncedEntry<bool> OneCameraPerTile;
        // Mount-mode threshold and wall-mount height: promoted to constants for
        // 1.0 (#575). Both are keyed off measured tile geometry (8.0 sits
        // between every real low-ceiling room and every tall-tile shaft; 3.0
        // clears head height), so they are calibration rather than preference,
        // and being synced they were never a per-client choice anyway.
        public float WallMountMinRoomHeightM => 8.0f;
        public float WallMountHeightM => 3.0f;

        // === Phase 2.0 — Legibility Probe ===
        // Diagnostic flag, NOT synced. Gates the post-selection Task-1
        // probe (Cameras/Probe/PlacementProbe.cs): 8N candidate lines
        // per tile + the Room-layer fail-loud assertion + the unified
        // entrance-rule preview. Linecast cost during dungeon load is
        // non-trivial, so this lives behind its own switch (not
        // ReconLoggingEnabled, which is cheap probe-only logging).
        public ConfigEntry<bool> P20ProbeEnabled { get; private set; }

        // === Phase 1.7 — Display ===
        public ConfigEntry<bool> NightVisionEnabled { get; private set; }
        public ConfigEntry<float> NightVisionGain { get; private set; }
        public ConfigEntry<bool> NightVisionAutoGain { get; private set; }
        public ConfigEntry<bool> NightVisionFlipY { get; private set; }

        // Analog-artifact block. Every one of these maps 1:1 onto a float property
        // on the night-vision bake material and is pushed by
        // QuadMonitor.ApplyNightVisionParams behind a Material.HasProperty guard,
        // so a bundle that predates any of them just ignores that knob.
        public ConfigEntry<float> FeedColorRetention { get; private set; }
        public ConfigEntry<float> FeedScanlineStrength { get; private set; }
        public ConfigEntry<float> FeedNoiseStrength { get; private set; }
        public ConfigEntry<float> FeedVignetteStrength { get; private set; }
        public ConfigEntry<float> FeedChromaAberration { get; private set; }
        public ConfigEntry<float> FeedRollBarStrength { get; private set; }
        public ConfigEntry<float> FeedRollBarSpeed { get; private set; }
        public ConfigEntry<float> FeedCurvatureStrength { get; private set; }
        public ConfigEntry<float> FeedInterlaceStrength { get; private set; }
        public ConfigEntry<float> FeedDropoutStrength { get; private set; }

        public ConfigEntry<bool> CCTVFillLightEnabled { get; private set; }
        public ConfigEntry<bool> CCTVRenderFillLightEnabled { get; private set; }
        public ConfigEntry<float> CCTVFillLightIntensity { get; private set; }
        public ConfigEntry<float> CCTVFillLightRange { get; private set; }

        // === Phase 2.2 — Physical Camera Visuals ===
        public ConfigEntry<bool> PhysicalCameraVisualsEnabled { get; private set; }
        public ConfigEntry<float> PhysicalCameraVisualScale { get; private set; }
        // Promoted to a constant for 1.0 (#575): the snap budget is a property
        // of the bundled camera mesh's mount geometry, not a player dial.
        public float PhysicalCameraVisualMaxSnapOffsetM => 0.9f;
        public ConfigEntry<bool> SecuritySweepEnabled { get; private set; }
        public ConfigEntry<float> SecuritySweepYawDegrees { get; private set; }
        public ConfigEntry<float> SecuritySweepSeconds { get; private set; }
        public ConfigEntry<float> SecurityTrackDegreesPerSecond { get; private set; }
        public ConfigEntry<float> SecurityDetectionIndicatorIntensity { get; private set; }
        public ConfigEntry<float> SecurityIdleConeIntensity { get; private set; }
        public ConfigEntry<float> SecurityLensDotGlow { get; private set; }

        /// <summary>
        /// Fires when display or scoped fill-light parameters change. Plugin listens
        /// and pushes shader values into the live bake material without a respawn;
        /// fill-light values are read on each CCTV camera render.
        /// </summary>
        public event Action NightVisionParamsChanged;

        // === #541 — Operator control surface ===
        // Two independent per-client switches. The overlay one is a
        // presentation preference; the debug one decides whether the
        // placement/station/arms developer hotkeys exist at all. Both default
        // true, so nothing changes until a player opts out.
        public ConfigEntry<bool> ShowOperatorControlsOverlay { get; private set; }
        public ConfigEntry<bool> EnableOperatorDebugTools { get; private set; }
        [SyncedEntryField] public SyncedEntry<bool> AllowRadarView;
        [SyncedEntryField] public SyncedEntry<bool> AllowCameraPings;
        [SyncedEntryField] public SyncedEntry<bool> AllowWalkieTalkie;
        [SyncedEntryField] public SyncedEntry<bool> AllowTargetScanning;
        [SyncedEntryField] public SyncedEntry<bool> AllowRemoteHacking;

        // === 1.1.0 - physical security and semantic tracking ===
        [SyncedEntryField] public SyncedEntry<bool> BreakableCameras;
        [SyncedEntryField] public SyncedEntry<float> CameraHealth;
        [SyncedEntryField] public SyncedEntry<bool> AlarmLockdownGates;
        [SyncedEntryField] public SyncedEntry<bool> ShowMainEntranceTrackingBox;
        [SyncedEntryField] public SyncedEntry<bool> ShowFireExitTrackingBoxes;
        [SyncedEntryField] public SyncedEntry<bool> ShowApparatusTrackingBox;

        // === Phase X — Focus Mouselook ===
        // Per-client. Mouselook on the active pane in focus mode; tuned camera
        // calibration keeps a ±90° yaw arc and ±75° pitch arc.
        public float PitchClampDeg => 75.0f;
        public float YawClampDeg => 90.0f;
        public ConfigEntry<float> MouselookSensitivityMul { get; private set; }

        public LethalCCTVConfig(ConfigFile cfg) : base(SurveillancePluginInfo.PLUGIN_GUID)
        {
            BindDiagnostics(cfg);
            BindCameraSelection(cfg);
            BindShipTerminalStore(cfg);
            BindShipMonitorHijack(cfg);
            BindRendering(cfg);
            BindOperatorControls(cfg);
            BindRoomAwarePlacement(cfg);
            BindLegibilityProbe(cfg);
            BindDisplay(cfg);
            BindPhysicalCameras(cfg);
            BindFocusMouselook(cfg);
            BindSecurityAndTracking(cfg);

            ConfigManager.Register(this);
        }
    }
}
