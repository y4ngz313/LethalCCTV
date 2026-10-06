using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVVanillaMonitorDisplay
    {
        private const string LowerLeftRootName = "LethalCCTV_LowerLeftCCTVOverlay";
        private const string RigRootName = "LethalCCTV_VanillaMonitorRenderRig";
        // Lower-left large monitor: Cube.001 slot 1, driven by StartOfRound.mapScreen.
        // Lower-right large monitor: Cube.001 slot 2. The separate SingleScreen mesh
        // is the small hydraulics/power monitor above the door controls and must stay vanilla.
        private const string LowerLeftMonitorPath = "Environment/HangarShip/ShipModels2b/MonitorWall/Cube.001";
        private const int FallbackLowerLeftMaterialIndex = 1;
        private const int UiRenderLayer = 31;
        private const int MonitorWidth = 768;
        // Match the 4:3 feed and vanilla monitor face; the old 16:9 canvas
        // compressed the typography horizontally when mapped onto this screen.
        private const int MonitorHeight = 576;
        private const int MonitorRenderScale = 2;
        private const int MonitorRenderWidth = MonitorWidth * MonitorRenderScale;
        private const int MonitorRenderHeight = MonitorHeight * MonitorRenderScale;
        private const float RadarImageRotationDegrees = 0f;
        private const float RadarImageScale = 1.0f;
        private const float ProbeIntervalSeconds = 1.0f;

        private static GameObject _rigRoot;
        private static RenderTexture _leftRenderTexture;
        private static RenderTexture _rightRenderTexture;
        private static Camera _leftCamera;
        private static Camera _rightCamera;
        private static RawImage[] _slotImages;
        private static RawImage _rightRadarImage;
        private static TextMeshProUGUI _rightRadarLabel;
        // #542 — the shape/colour key along the bottom edge of the radar screen.
        // Only shown while the marker-drawing radar feed is the thing on screen;
        // the legacy schematic fallback draws none of the classes it names.
        private static TextMeshProUGUI _rightRadarLegend;

        private static GameObject _lowerLeftRoot;
        private static RawImage _lowerLeftImage;
        private static TextMeshProUGUI _leftCameraLabel;
        private static TextMeshProUGUI _leftPageLabel;
        private static Image _leftSwitchFlashImage;
        private static GameObject _leftReticleRoot;
        private static RectTransform _leftTerminalOverlayRoot;
        private static float _leftSwitchFlashStartedAt;
        private static float _leftSwitchFlashUntil;
        private static string _lastLeftCameraLabel;
        private static string _lastLeftPageLabel;

        // === Screen furniture ===
        // Clock, REC light, framing brackets, status readout and the signal-lost
        // plate. Everything here is composited OUTSIDE the baked feed RT, so none of
        // it picks up the night-vision shader's scanlines or barrel warp — it reads
        // as the DVR's own on-screen display sitting over the picture, which is what
        // a real security monitor looks like.
        private static TextMeshProUGUI _leftClockLabel;
        private static TextMeshProUGUI _leftRecLabel;
        private static TextMeshProUGUI _leftStatusLabel;
        private static GameObject _leftSignalLostRoot;
        private static RawImage _leftSignalLostStatic;
        private static TextMeshProUGUI _leftSignalLostLabel;
        private static Texture2D _signalLostNoiseTexture;
        private static int _signalLostStaticStep;
        private static string _lastLeftClockText;
        private static string _lastLeftStatusText;
        private static string _lastLeftSignalLostText;
        private static bool _lastLeftRecOn;
        private static bool _lastLeftSignalLost;
        // A 1 Hz cycle, lit for the first 55% of it. Sampled from Time.unscaledTime,
        // so it keeps the same phase across a pause and never accumulates drift.
        private const float RecBlinkPeriodSeconds = 1.0f;
        private const float RecBlinkOnFraction = 0.55f;

        // Bind-once screen takeovers (see ScreenBinding). Both visible lower ship
        // monitors are material slots on Cube.001 in the current vanilla wall:
        // lower-left is the map slot, lower-right is the non-map ShipScreen slot.
        private static readonly ScreenBinding _lowerLeftBinding = new ScreenBinding("lower-left");
        private static readonly ScreenBinding _lowerRightBinding = new ScreenBinding("lower-right");
        private static readonly ScreenBinding _lowerRightAuxBinding = new ScreenBinding("lower-right-aux");

        private static bool _suppressionCaptured;
        private static bool _levelDescriptionWasEnabled;
        private static bool _videoReelWasEnabled;
        private static bool _videoReelWasPlaying;
        private static bool _mapScreenWasEnabled;
        private static RawImage _mapScreenRawImage;
        private static Texture _mapScreenRawImageTexture;
        private static Graphic[] _lowerLeftSuppressedGraphics;
        private static bool[] _lowerLeftSuppressedGraphicStates;

        private static bool _cctvModeActive;
        private static float _nextProbeAt;
        // Per-frame Tick reduces to cheap comparisons; everything heavy runs on
        // these cadences. Maintenance = scene lookups / binding / suppression;
        // compositor = the two rig camera renders + blit (a CCTV-monitor refresh
        // rate, intentionally well below the game framerate), paced and bounded by
        // CctvRenderScheduler (#1219 G4).
        private const float BindMaintenanceIntervalSeconds = 1.0f;
        private const float DriverRediscoveryIntervalSeconds = 30f;
        private const float FocusedCompositorPassiveIntervalSeconds = 1f / 24f;
        private const float FocusedCompositorInteractiveIntervalSeconds = 1f / 24f;
        private const float RightFocusedCompositorIntervalSeconds = 0.25f;
        private const float RightFocusedIdleCompositorIntervalSeconds = 0.5f;
        private const float VisibleAmbientCompositorIntervalSeconds = 0.5f;
        private const float HiddenAmbientCompositorIntervalSeconds = 1.0f;
        // Cheap ownership reassertion must run every frame. The vanilla lower-left
        // monitor UI can repaint in Update, and leaving even a short gap lets the
        // map/video feed flash over CCTV. Expensive compositor renders stay on the
        // throttled cadence below.
        private const float ReassertIntervalSeconds = 1.0f;
        private const float FullSuppressionIntervalSeconds = 0.20f;
        private const float FastMaterialVerifyIntervalSeconds = 0.20f;
        private static float _nextBindMaintenanceAt;
        // Tick's view of the frame that last requested a compositor render; the render
        // callbacks run later in the frame (CctvRenderScheduler.Pump) and read these.
        private static bool _scheduledMonitorVisible;
        private static bool _scheduledAllowRadarWork;
        private static float _nextReassertAt;
        private static float _nextFullSuppressionAt;
        private static float _nextFastMaterialVerifyAt;
        private static bool _forceFastMaterialVerify;
        private static bool _compositorDirty = true;
        private static bool _rightCompositorDirty = true;
        private static float _lastCompositorRenderAt;
        private static bool _wasMonitorVisible;
        private static bool _bindMaintenancePending;
        private static long _suppressedUnobservedBindMaintenancePasses;
        private static float _nextBindMaintenanceSuppressionReportAt;
        private static bool _bindMaintenanceGateLogged;
        private static bool _bindMaintenanceResumeLogged;
        // Proof the observability gate in ShouldRequestCompositor actually fires. A fix in
        // this area already shipped once as a silent no-op that looked correct in review,
        // so this one reports itself: a nonzero count is the repaints that used to run
        // where nobody could see them.
        private static long _suppressedUnobservedRepaints;
        private static float _nextSuppressionReportAt;
        private const float SuppressionReportIntervalSeconds = 30f;
        private static bool _powerBlanked;
        private static bool _targetOutlinesActive;
        private static bool _diagnosticsDumped;
        private static bool _loggedWaitingForTarget;
        private static bool _loggedCreated;
        private static bool _loggedLeftMissing;
        private static bool _loggedRightMissing;
        private static bool _loggedPinningMapVideo;
        private static bool _loggedMapCameraFrozenOff;
        private const float LeftSwitchFlashDurationSeconds = 0.075f;
        private const float LeftSwitchTotalDurationSeconds = 0.18f;

        // One owned screen slot: bind-once material takeover with cheap re-assertion,
        // following LGUMonitorTakeover's save/override/restore pattern scoped to a
        // single renderer+slot. sharedMaterials is used throughout — the .materials
        // getter instantiates every slot and allocates a fresh array per call, which
        // is exactly the per-frame churn Phase 0 removes. Saves/restores only its own
        // slot so multiple bindings can coexist on one renderer (left + aux share
        // Cube.001).
        private sealed class ScreenBinding
        {
            private readonly string _label;
            private readonly System.Collections.Generic.List<(ManualCameraRenderer driver, bool wasEnabled)> _disabledDrivers =
                new System.Collections.Generic.List<(ManualCameraRenderer, bool)>();
            private MeshRenderer _renderer;
            private int _materialIndex = -1;
            private Material _savedSlotMaterial;
            private Material _runtimeMaterial;
            private Material[] _ownedSharedMaterials;
            private readonly List<Material> _scratchSharedMaterials = new List<Material>(8);
            private float _nextDriverRediscoveryAt;
            // #597 — set when this binding owns a screen on GeneralImprovements'
            // replacement monitor wall instead of a slot on the vanilla Cube.001.
            // Changes three things: the runtime material is synthesized rather than
            // cloned from GI's blank-screen material, driver suppression also matches
            // the specific ManualCameraRenderer GI pointed at the screen, and material
            // reassertion stands down whenever GI reports its wall unpowered.
            private bool _isGeneralImprovementsScreen;
            // #600 — set when the GI screen this binding owns is GI's MAP screen
            // (Monitors/BigMiddle/MScreen), which is not one of MonitorsAPI.AllMonitors and
            // therefore has no GI adoption path. See StandDownForGeneralImprovementsPower.
            private bool _isGeneralImprovementsMapScreen;
            private ManualCameraRenderer _identityDriver;

            internal ScreenBinding(string label)
            {
                _label = label;
            }

            internal bool IsBound => _renderer != null && _materialIndex >= 0;
            internal MeshRenderer Renderer => _renderer;
            internal int MaterialIndex => _materialIndex;
            internal bool IsGeneralImprovementsScreen => _isGeneralImprovementsScreen;
            internal bool IsGeneralImprovementsMapScreen => _isGeneralImprovementsScreen && _isGeneralImprovementsMapScreen;

            /// <summary>
            /// True while GI owns this screen's power state and currently has it off. GI's
            /// <c>Monitors.TogglePower(false)</c> writes its blank-screen material over every
            /// screen on the wall; re-applying ours at the 0.2s verify cadence would fight
            /// that toggle forever and leave one screen lit in a dark wall. Driver
            /// suppression keeps running either way — it is not a material write.
            ///
            /// #600 — the MAP screen is explicitly excluded. <c>Monitors.TogglePower</c> walks
            /// <c>MonitorsAPI.AllMonitors</c> only, and <c>_mapRenderer</c> is not in it, so
            /// <c>MonitorsAPI.PoweredOn</c> says nothing about the map surface. GI repaints it
            /// from a different place entirely — the <c>SwitchScreenOn</c> prefix calling
            /// <c>Monitors.UpdateMapMaterial(on ? onScreenMat : offScreenMat)</c>, a bare
            /// <c>sharedMaterial</c> write with no OverwrittenMaterial adoption behind it. So
            /// standing down there would hand our feed's surface to GI's map material and
            /// never take it back. While we own the map screen we always re-assert; the 0.2s
            /// FastMaterialVerify pass (ReassertFast) sees slot 0 no longer holding our
            /// runtime material and rewrites the owned array, and the 1s Reassert is the
            /// backstop. The button prefix in CCTVMonitorModePatches keeps the common case
            /// from happening at all.
            /// </summary>
            private bool StandDownForGeneralImprovementsPower =>
                _isGeneralImprovementsScreen &&
                !_isGeneralImprovementsMapScreen &&
                !GeneralImprovementsMonitorCompat.MonitorsPoweredOn();

            /// <summary>
            /// Ship-wide Contracted presentations own every visible monitor
            /// surface. CCTV keeps its producer cameras quiescent but performs
            /// no material writes until the coordinated scope closes.
            /// </summary>
            private static bool StandDownForCoordinatedPresentation =>
                CCTVShipSystemsBridge.IsExternalMonitorTakeoverActive() ||
                CCTVShipSystemsBridge.IsQuotaPresentationActive();

            internal bool OwnsDriver(ManualCameraRenderer driver)
            {
                if (driver == null)
                    return false;

                for (int i = 0; i < _disabledDrivers.Count; i++)
                {
                    if (_disabledDrivers[i].driver == driver)
                        return true;
                }

                if (_identityDriver != null && driver == _identityDriver)
                    return true;

                return IsBound && (driver.mesh == _renderer || driver.mesh2 == _renderer);
            }

            internal bool Bind(
                MeshRenderer renderer,
                int materialIndex,
                Texture texture,
                bool generalImprovementsScreen = false,
                ManualCameraRenderer identityDriver = null,
                bool generalImprovementsMapScreen = false)
            {
                if (renderer == null || texture == null || materialIndex < 0)
                    return false;
                if (_renderer == renderer && _materialIndex == materialIndex)
                {
                    _isGeneralImprovementsMapScreen = generalImprovementsScreen && generalImprovementsMapScreen;

                    // GI rebuilds MonitorGroup on every StartOfRound.Start, so a screen's
                    // driver can be repointed under a binding that is otherwise unchanged.
                    if (identityDriver != null && _identityDriver != identityDriver)
                    {
                        _identityDriver = identityDriver;
                        DisableDriver(identityDriver);
                    }
                    return Reassert();
                }

                Restore();

                Material[] materials;
                try { materials = renderer.sharedMaterials; }
                catch { return false; }
                if (materialIndex >= materials.Length || materials[materialIndex] == null)
                    return false;

                // A slot the quota ceremony currently holds must not be captured
                // as an "original" — restoring it later would write a material the
                // ceremony has since destroyed. Bind maintenance retries within a
                // second of the scope closing.
                if (IsCeremonyHeldMaterial(materials[materialIndex]))
                    return false;

                _renderer = renderer;
                _materialIndex = materialIndex;
                _isGeneralImprovementsScreen = generalImprovementsScreen;
                _isGeneralImprovementsMapScreen = generalImprovementsScreen && generalImprovementsMapScreen;
                _identityDriver = identityDriver;
                _savedSlotMaterial = materials[materialIndex];
                _runtimeMaterial = generalImprovementsScreen
                    ? CreateGeneralImprovementsScreenMaterial(_label, _savedSlotMaterial)
                    : new Material(_savedSlotMaterial)
                    {
                        name = _savedSlotMaterial.name + "_LethalCCTV_" + _label
                    };
                ApplyTextureToMaterial(_runtimeMaterial, texture);
                materials[materialIndex] = _runtimeMaterial;
                _ownedSharedMaterials = materials;
                try { renderer.sharedMaterials = materials; } catch { }

                DisableDrivers();
                ScheduleNextDriverRediscovery(Time.unscaledTime);
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] CCTV {_label} monitor bound to {GetPath(renderer.transform)} materialIndex={materialIndex}" +
                    (generalImprovementsScreen
                        ? _isGeneralImprovementsMapScreen
                            ? " (GeneralImprovements MAP screen)"
                            : " (GeneralImprovements screen)"
                        : string.Empty) +
                    "; hydraulic door monitor untouched.");
                return true;
            }

            /// <summary>
            /// Builds the material CCTV paints onto a GeneralImprovements screen.
            ///
            /// The vanilla path clones the slot material because the Cube.001 slots already
            /// carry the ShipScreen unlit setup CCTV wants. GI's screens instead sit on its
            /// own blank-screen / text-render materials, whose shader and keyword state are
            /// an implementation detail of GI's asset bundle — cloning one and hoping
            /// <c>_UnlitColorMap</c> exists is exactly the guess OpenBodyCams avoids. Build a
            /// plain HDRP/Unlit emissive material instead, the same shape OpenBodyCams uses
            /// for its own monitor output, and only fall back to cloning if HDRP/Unlit cannot
            /// be resolved at all.
            /// </summary>
            private static Material CreateGeneralImprovementsScreenMaterial(string label, Material fallbackTemplate)
            {
                Shader unlit = null;
                try { unlit = Shader.Find("HDRP/Unlit"); }
                catch { }

                if (unlit == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV] HDRP/Unlit was not found; falling back to cloning the GeneralImprovements " +
                        $"screen material for the {label} monitor.");
                    return new Material(fallbackTemplate)
                    {
                        name = fallbackTemplate.name + "_LethalCCTV_" + label
                    };
                }

                var material = new Material(unlit) { name = "LethalCCTV_" + label + "_GIScreen" };
                if (material.HasProperty("_AlbedoAffectEmissive"))
                    material.SetFloat("_AlbedoAffectEmissive", 1f);
                if (material.HasProperty("_EmissiveColor"))
                    material.SetColor("_EmissiveColor", Color.white);
                return material;
            }

            // 1 Hz maintenance: put our material back if vanilla stole the slot, and
            // keep the already-known drivers off. No scene scans on this path.
            internal bool Reassert()
            {
                if (!IsBound || _runtimeMaterial == null)
                    return false;

                if (StandDownForCoordinatedPresentation || StandDownForGeneralImprovementsPower)
                {
                    DisableTrackedDriverCameras();
                    return false;
                }

                Material[] materials;
                try { materials = _renderer.sharedMaterials; }
                catch { return false; }
                if (_materialIndex >= materials.Length)
                    return false;

                bool changed = false;
                if (materials[_materialIndex] != _runtimeMaterial &&
                    !IsCeremonyHeldMaterial(materials[_materialIndex]))
                {
                    materials[_materialIndex] = _runtimeMaterial;
                    try { _renderer.sharedMaterials = materials; } catch { }
                    _ownedSharedMaterials = materials;
                    changed = true;
                }

                for (int i = 0; i < _disabledDrivers.Count; i++)
                {
                    ManualCameraRenderer driver = _disabledDrivers[i].driver;
                    if (driver == null)
                        continue;
                    if (driver.enabled)
                    {
                        driver.enabled = false;
                        changed = true;
                    }
                    changed |= DisableDriverCamera(driver);
                }

                return changed;
            }

            internal void ReassertFast(bool verifyMaterial)
            {
                if (!IsBound || _runtimeMaterial == null)
                    return;

                if (StandDownForCoordinatedPresentation || StandDownForGeneralImprovementsPower)
                {
                    DisableTrackedDriverCameras();
                    return;
                }

                if (verifyMaterial && !IsRuntimeMaterialStillAssigned(out Material currentSlotMaterial))
                {
                    // The quota ceremony borrowed this slot; leave its clone in
                    // place and keep only the driver suppression running (the
                    // ceremony suspends the producer itself and wants it off).
                    if (IsCeremonyHeldMaterial(currentSlotMaterial))
                    {
                        DisableTrackedDriverCameras();
                        return;
                    }

                    if (_ownedSharedMaterials != null &&
                        _materialIndex >= 0 &&
                        _materialIndex < _ownedSharedMaterials.Length &&
                        _ownedSharedMaterials[_materialIndex] == _runtimeMaterial)
                    {
                        try { _renderer.sharedMaterials = _ownedSharedMaterials; }
                        catch
                        {
                            Reassert();
                            return;
                        }
                    }
                    else
                    {
                        Reassert();
                        return;
                    }
                }

                DisableTrackedDriverCameras();
            }

            private bool IsRuntimeMaterialStillAssigned(out Material currentSlotMaterial)
            {
                currentSlotMaterial = null;
                if (!IsBound || _runtimeMaterial == null)
                    return false;

                try
                {
                    _scratchSharedMaterials.Clear();
                    _renderer.GetSharedMaterials(_scratchSharedMaterials);
                    if (_materialIndex < 0 || _materialIndex >= _scratchSharedMaterials.Count)
                        return false;

                    currentSlotMaterial = _scratchSharedMaterials[_materialIndex];
                    return currentSlotMaterial == _runtimeMaterial;
                }
                catch
                {
                    return false;
                }
            }

            private void DisableTrackedDriverCameras()
            {
                for (int i = 0; i < _disabledDrivers.Count; i++)
                {
                    ManualCameraRenderer driver = _disabledDrivers[i].driver;
                    if (driver == null)
                        continue;
                    try
                    {
                        if (driver.enabled)
                            driver.enabled = false;
                    }
                    catch { }
                    DisableDriverCamera(driver);
                }
            }

            internal bool RediscoverDriversIfDue(float now)
            {
                if (!IsBound)
                    return false;
                if (_nextDriverRediscoveryAt > 0f && now < _nextDriverRediscoveryAt)
                    return false;

                ScheduleNextDriverRediscovery(now);
                return DisableDrivers();
            }

            private void ScheduleNextDriverRediscovery(float now)
            {
                _nextDriverRediscoveryAt = now + DriverRediscoveryIntervalSeconds;
            }

            // Disable EVERY ManualCameraRenderer that paints this renderer (via mesh
            // or mesh2) — a single survivor reasserts its camera RT on the very next
            // Update and wins (TakeoverManager pass E lesson). Scan runs only at bind
            // time and on the slow rediscovery cadence. The hydraulic door monitor's
            // MCR never matches our renderers.
            private bool DisableDrivers()
            {
                // #597 — on GeneralImprovements' wall the mesh-identity sweep below is not
                // enough on its own. GI repoints the ExternalCam/InternalCam driver's `mesh`
                // at the new screen while building MonitorGroup, so whether the sweep sees it
                // depends on winning a race with GI's rebuild. The compat bridge resolves the
                // same driver from GI's assignment for this screen index instead, by scene
                // identity ("Cameras/FrontDoorSecurityCam/SecurityCamera" /
                // "Cameras/ShipCamera"). Tracking it here puts it through the same
                // disable/restore bookkeeping as a mesh-matched driver.
                bool changed = TrackIdentityDriver();

                ManualCameraRenderer[] cameras;
                try
                {
                    cameras = UnityEngine.Object.FindObjectsByType<ManualCameraRenderer>(
                        FindObjectsInactive.Exclude,
                        FindObjectsSortMode.None);
                }
                catch { return changed; }

                for (int i = 0; i < cameras.Length; i++)
                {
                    ManualCameraRenderer camera = cameras[i];
                    if (camera == null || (camera.mesh != _renderer && camera.mesh2 != _renderer))
                        continue;

                    bool alreadyTracked = false;
                    for (int j = 0; j < _disabledDrivers.Count; j++)
                    {
                        if (_disabledDrivers[j].driver == camera)
                        {
                            alreadyTracked = true;
                            break;
                        }
                    }

                    if (alreadyTracked)
                    {
                        changed |= DisableDriver(camera);
                        continue;
                    }

                    _disabledDrivers.Add((camera, camera.enabled));
                    changed = true;
                    changed |= DisableDriver(camera);
                    SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Disabled ManualCameraRenderer on {GetPath(camera.transform)} while CCTV owns the {_label} monitor.");
                }

                return changed;
            }

            /// <summary>
            /// Adds the GI-resolved driver for this screen to the tracked set, if it is not
            /// already there through a mesh match. Returns true if anything changed.
            /// </summary>
            private bool TrackIdentityDriver()
            {
                ManualCameraRenderer driver = _identityDriver;
                if (driver == null)
                    return false;

                for (int i = 0; i < _disabledDrivers.Count; i++)
                {
                    if (_disabledDrivers[i].driver == driver)
                        return DisableDriver(driver);
                }

                _disabledDrivers.Add((driver, driver.enabled));
                DisableDriver(driver);
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Disabled ManualCameraRenderer on {GetPath(driver.transform)} by driver identity " +
                    $"while CCTV owns the {_label} GeneralImprovements screen.");
                return true;
            }

            private static bool DisableDriver(ManualCameraRenderer driver)
            {
                if (driver == null)
                    return false;

                bool changed = false;
                try
                {
                    if (driver.enabled)
                    {
                        driver.enabled = false;
                        changed = true;
                    }
                }
                catch { }

                return changed | DisableDriverCamera(driver);
            }

            // Disabling an MCR component freezes whatever Camera.enabled state its
            // Update last wrote. Frozen-TRUE means an extra full HDRP render EVERY
            // frame for a screen we now own — measured as the vanilla MapCamera left
            // rendering through the whole CCTV focus session (2026-06-12 log). The
            // re-enabled MCR re-manages cam.enabled itself on the first Update after
            // Restore, so no saved state is needed here.
            private static bool DisableDriverCamera(ManualCameraRenderer driver)
            {
                if (driver == null)
                    return false;

                bool changed = false;
                try
                {
                    if (driver.cam != null && driver.cam.enabled)
                    {
                        driver.cam.enabled = false;
                        changed = true;
                        SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Disabled frozen-on camera '{driver.cam.name}' of suppressed ManualCameraRenderer {GetPath(driver.transform)}.");
                    }
                    if (driver.mapCamera != null && driver.mapCamera != driver.cam && driver.mapCamera.enabled)
                    {
                        driver.mapCamera.enabled = false;
                        changed = true;
                        SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Disabled frozen-on map camera '{driver.mapCamera.name}' of suppressed ManualCameraRenderer {GetPath(driver.transform)}.");
                    }
                }
                catch { }
                return changed;
            }

            internal bool Restore()
            {
                bool changed = false;
                if (_renderer != null && _savedSlotMaterial != null)
                {
                    try
                    {
                        Material[] materials = _renderer.sharedMaterials;
                        if (_materialIndex >= 0 &&
                            _materialIndex < materials.Length &&
                            materials[_materialIndex] == _runtimeMaterial)
                        {
                            materials[_materialIndex] = _savedSlotMaterial;
                            _renderer.sharedMaterials = materials;
                            changed = true;
                        }
                    }
                    catch { }
                }

                for (int i = 0; i < _disabledDrivers.Count; i++)
                {
                    var (driver, wasEnabled) = _disabledDrivers[i];
                    if (driver != null)
                    {
                        try
                        {
                            if (driver.enabled != wasEnabled)
                                changed = true;
                            driver.enabled = wasEnabled;
                        }
                        catch { }
                    }
                }
                _disabledDrivers.Clear();

                if (_runtimeMaterial != null)
                {
                    UnityEngine.Object.Destroy(_runtimeMaterial);
                    changed = true;
                }
                _runtimeMaterial = null;
                _renderer = null;
                _materialIndex = -1;
                _savedSlotMaterial = null;
                _ownedSharedMaterials = null;
                _nextDriverRediscoveryAt = 0f;
                _isGeneralImprovementsScreen = false;
                _isGeneralImprovementsMapScreen = false;
                _identityDriver = null;
                return changed;
            }
        }

    }
}
