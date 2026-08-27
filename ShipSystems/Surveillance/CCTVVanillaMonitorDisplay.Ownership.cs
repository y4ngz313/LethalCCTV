using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Rendering;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVVanillaMonitorDisplay
    {
        /// <summary>2026-08-06 steady-cost gate: the fast reassert touches a dozen native
        /// Unity properties every frame (VideoPlayer enabled/isPlaying, MCR + two camera
        /// enabled flags, the suppressed-graphics loop with a transform.name marshal per
        /// entry, three binding ReassertFast driver sweeps) and profiled as part of the
        /// ~0.8ms/frame steady SurveillanceBootstrap.LateUpdate cost. Every vanilla code
        /// path that can steal the wall is Harmony-patched (CCTVMonitorModePatches) and
        /// calls back with force:true immediately after it runs, so the polled pass only
        /// needs a 0.1s safety cadence — same shape as the existing 0.2s
        /// FullSuppression/FastMaterialVerify intervals.</summary>
        private const float FastReassertIntervalSeconds = 0.1f;
        private static float _nextFastReassertAt;
        private static bool _stoodDownForQuotaPresentation;
        private static bool _stoodDownForExternalMonitorTakeover;

        /// <summary>
        /// Any material carrying this marker in an owned slot was put there by
        /// ShipSystems' quota-ceremony camera binding
        /// (CeremonyCameraMonitorBinding.CloneNamePrefix — the two assemblies
        /// only talk through reflection, so the string is duplicated here).
        /// </summary>
        private const string CeremonyCloneMarker = "_LGUCeremonyCamera";

        /// <summary>
        /// True while ShipSystems' quota ceremony is allowed to hold this exact
        /// slot: the material in it is the ceremony's clone AND the presentation
        /// scope is open. The ceremony paints the interior-camera monitor's slot
        /// for the scope's length and suspends its producer itself
        /// (y4ngz313/Y4NGZCompany#564); every CCTV reassert path — the 0.1s fast
        /// pass, the 1s Reassert, the 1s bind maintenance, and the event-driven
        /// post-vanilla-writer hook — funnels its material write through the
        /// ScreenBinding, so this per-slot check stands down exactly the slot the
        /// ceremony borrowed while every other slot, the suppressed lower feeds,
        /// and the map-screen texture pin keep their normal ownership. Outside
        /// the scope the marker grants nothing, so a stale leftover ceremony
        /// clone is evicted by the next ordinary reassert.
        /// </summary>
        private static bool IsCeremonyHeldMaterial(Material material)
        {
            if (material == null)
                return false;

            string name;
            try { name = material.name; }
            catch { return false; }
            if (name == null || name.IndexOf(CeremonyCloneMarker, StringComparison.Ordinal) < 0)
                return false;

            return CCTVShipSystemsBridge.IsQuotaPresentationActive();
        }

        /// <summary>
        /// Latches the presentation scope so its close triggers one immediate
        /// forced re-take: the per-slot stand-down above leaves the ceremony's
        /// clone alone while the scope is open, and this makes the recovery
        /// prompt instead of waiting out the polled cadences.
        /// </summary>
        private static void TrackQuotaPresentationScope()
        {
            if (CCTVShipSystemsBridge.IsQuotaPresentationActive())
            {
                _stoodDownForQuotaPresentation = true;
                return;
            }

            if (_stoodDownForQuotaPresentation)
            {
                _stoodDownForQuotaPresentation = false;
                _forceFastMaterialVerify = true;
                _nextFullSuppressionAt = 0f;
                _nextReassertAt = 0f;
            }
        }

        /// <summary>
        /// Full-wall takeovers outrank CCTV. Latch the scope so its close forces
        /// an immediate material verification instead of waiting for a polling
        /// cadence after Contracted releases the wall.
        /// </summary>
        private static void TrackExternalMonitorTakeoverScope()
        {
            if (CCTVShipSystemsBridge.IsExternalMonitorTakeoverActive())
            {
                _stoodDownForExternalMonitorTakeover = true;
                return;
            }

            if (!_stoodDownForExternalMonitorTakeover)
                return;

            _stoodDownForExternalMonitorTakeover = false;
            _forceFastMaterialVerify = true;
            _nextFullSuppressionAt = 0f;
            _nextReassertAt = 0f;
        }

        private static void ReassertCctvScreenOwnershipFast(float now, bool force = false)
        {
            TrackQuotaPresentationScope();
            TrackExternalMonitorTakeoverScope();

            if (CCTVShipSystemsBridge.IsExternalMonitorTakeoverActive())
            {
                _lowerLeftBinding.ReassertFast(verifyMaterial: false);
                _lowerRightBinding.ReassertFast(verifyMaterial: false);
                _lowerRightAuxBinding.ReassertFast(verifyMaterial: false);
                return;
            }


            if (!force && !_forceFastMaterialVerify && now < _nextFastReassertAt)
                return;
            _nextFastReassertAt = now + FastReassertIntervalSeconds;
            ReassertSuppressedLowerMonitorFeedsFrame();
            if (now >= _nextFullSuppressionAt)
            {
                _nextFullSuppressionAt = now + FullSuppressionIntervalSeconds;
                ReassertSuppressedLowerMonitorFeedsFast();
            }

            bool verifyMaterial = _forceFastMaterialVerify || now >= _nextFastMaterialVerifyAt;
            if (verifyMaterial)
            {
                _forceFastMaterialVerify = false;
                _nextFastMaterialVerifyAt = now + FastMaterialVerifyIntervalSeconds;
            }

            _lowerLeftBinding.ReassertFast(verifyMaterial);
            _lowerRightBinding.ReassertFast(verifyMaterial);
            _lowerRightAuxBinding.ReassertFast(verifyMaterial);

            if (_mapScreenRawImage != null && _leftRenderTexture != null &&
                _mapScreenRawImage.texture != _leftRenderTexture)
            {
                _mapScreenRawImage.texture = _leftRenderTexture;
            }
        }

        internal static void ReassertCctvScreenOwnershipAfterVanilla()
        {
            if (!_cctvModeActive)
                return;
            if (_leftRenderTexture == null && _rightRenderTexture == null)
                return;

            // force: this is the event-driven hook fired right after a patched vanilla
            // writer ran; it must never be absorbed by the polled-cadence gate.
            ReassertCctvScreenOwnershipFast(Time.unscaledTime, force: true);
        }

        /// <summary>
        /// #597 — one probe per frame for "GeneralImprovements' replacement monitor wall is
        /// live". Every ownership path below asks this, several of them on the 0.1s fast
        /// reassert cadence, and the answer cannot change within a frame.
        /// </summary>
        private static int _giWallProbeFrame = -1;
        private static bool _giWallProbeResult;

        internal static bool IsGeneralImprovementsMonitorWallActive()
        {
            if (!GeneralImprovementsMonitorCompat.IsLoaded)
                return false;

            int frame = Time.frameCount;
            if (_giWallProbeFrame == frame)
                return _giWallProbeResult;

            _giWallProbeFrame = frame;
            _giWallProbeResult = GeneralImprovementsMonitorCompat.AreBetterMonitorsActive();
            return _giWallProbeResult;
        }

        internal static bool ShouldSuppressVanillaMonitorRendererUpdate(ManualCameraRenderer renderer)
        {
            if (!_cctvModeActive || renderer == null)
                return false;

            StartOfRound sor = StartOfRound.Instance;
            // The blanket mapScreen rule exists because on the vanilla wall the map screen
            // renderer IS the lower-left monitor CCTV paints.
            //
            // #597 scoped it off under GeneralImprovements, because GI repoints
            // mapScreen.mesh at its own BigMiddle frame and drives the visible MScreen
            // surface from it — and back then CCTV lived on separate GI screens, so skipping
            // vanilla's Update would have frozen a screen we did not own.
            //
            // #600 makes the map screen the DEFAULT CCTV feed target under GI, so the
            // original reasoning applies again whenever we actually own it: we are painting
            // that visible surface, and vanilla's map Update has nothing left to contribute.
            // When we do not own it, keep falling through to the driver-identity check, which
            // matches only the drivers this binding actually displaced.
            if (sor != null && renderer == sor.mapScreen &&
                (!IsGeneralImprovementsMonitorWallActive() || OwnsGeneralImprovementsMapScreen()))
            {
                return true;
            }

            return _lowerLeftBinding.OwnsDriver(renderer) ||
                   _lowerRightBinding.OwnsDriver(renderer) ||
                   _lowerRightAuxBinding.OwnsDriver(renderer);
        }

        private static void ReassertCctvScreenOwnership()
        {
            TrackQuotaPresentationScope();
            TrackExternalMonitorTakeoverScope();
            if (CCTVShipSystemsBridge.IsExternalMonitorTakeoverActive())
                return;


            if (_suppressionCaptured)
                SuppressVanillaLowerMonitorFeeds();

            _lowerLeftBinding.Reassert();
            _lowerRightBinding.Reassert();
            _lowerRightAuxBinding.Reassert();

            if (_mapScreenRawImage != null && _leftRenderTexture != null)
                _mapScreenRawImage.texture = _leftRenderTexture;
        }

        internal static bool IsCctvModeActive => _cctvModeActive;

        /// <summary>
        /// Cached "is the monitor wall on-screen for the local player" flag from
        /// the last Tick (CCTVVanillaMonitorDisplay.Tick.cs), reused by observer
        /// CCTVCameraThrottle instances so per-camera Update calls don't redo the
        /// renderer-visibility work every frame.
        /// </summary>
        internal static bool IsMonitorLocallyVisible => _wasMonitorVisible;

        internal static string GetRenderDiagnostics()
        {
            string left = _leftRenderTexture != null ? $"{_leftRenderTexture.width}x{_leftRenderTexture.height}" : "<null>";
            string right = _rightRenderTexture != null ? $"{_rightRenderTexture.width}x{_rightRenderTexture.height}" : "<null>";
            return $"monitor compositor: active={_cctvModeActive} focusedPassiveInterval={FocusedCompositorPassiveIntervalSeconds:F3}s " +
                   $"focusedBurstInterval={FocusedCompositorInteractiveIntervalSeconds:F3}s " +
                   $"visibleInterval={VisibleAmbientCompositorIntervalSeconds:F3}s hiddenInterval={HiddenAmbientCompositorIntervalSeconds:F3}s " +
                   $"rightFocusInterval={RightFocusedCompositorIntervalSeconds:F3}s rightIdleFocusInterval={RightFocusedIdleCompositorIntervalSeconds:F3}s " +
                   $"scale={MonitorRenderScale}x " +
                   $"leftRT={left} rightRT={right} " +
                   $"leftCamEnabled={(_leftCamera != null && _leftCamera.enabled)} rightCamEnabled={(_rightCamera != null && _rightCamera.enabled)} " +
                   $"fastReassert={FastReassertIntervalSeconds:F2}s-polled+event-forced materialVerify={FastMaterialVerifyIntervalSeconds:F2}s " +
                   $"reassertInterval={ReassertIntervalSeconds:F2}s";
        }

        internal static void SetCctvModeActive(bool active, bool announce = true)
        {
            if (_cctvModeActive == active)
                return;

            _cctvModeActive = active;
            if (active)
            {
                _compositorDirty = true;
                _nextCompositorRenderAt = 0f;
                _nextReassertAt = 0f;
                _nextFullSuppressionAt = 0f;
                _nextFastMaterialVerifyAt = 0f;
                _forceFastMaterialVerify = true;
                _lastCompositorRenderAt = 0f;
                CCTVShipSystemsBridge.BeginExternalMonitorTakeover();
            }
            else
            {
                TickCctvTargetOutlines(active: false);
                SetActive(false);
                CCTVShipSystemsBridge.EndExternalMonitorTakeover();
            }

            if (announce)
            {
                HUDManager.Instance?.DisplayTip(
                    "CCTV MONITORS",
                    active ? "CCTV view enabled." : "Vanilla monitor view restored.",
                    isWarning: false);
            }
        }

        internal static void PrimeMonitorOwnershipNow(bool renderNow = true)
        {
            if (!CCTVTerminalUnlockable.IsPurchased() || !_cctvModeActive || QuadMonitor.QuadRTs == null)
                return;

            EnsureRenderRig();
            if (_leftRenderTexture == null || _rightRenderTexture == null)
                return;

            float now = Time.unscaledTime;
            VanillaRadarFeed.SetActive(true);
            DumpMonitorDiagnosticsOnce();
            EnsureWorldUiOverlays();
            SuppressVanillaLowerMonitorFeeds();
            BindLeftMonitorTexture();
            BindRightMonitorTexture();
            SyncSlotTextures();
            RefreshLeftCameraLabel(now, monitorVisible: true);
            SyncRightMonitor(allowRadarWork: true);

            if (_lowerLeftRoot != null && !_lowerLeftRoot.activeSelf)
            {
                _lowerLeftRoot.SetActive(true);
                _lowerLeftRoot.transform.SetAsLastSibling();
            }

            _compositorDirty = true;
            _forceFastMaterialVerify = true;
            ReassertCctvScreenOwnershipFast(now, force: true);
            ReassertCctvScreenOwnership();

            if (renderNow)
            {
                RenderMonitorTextures(now, monitorVisible: true);
                PinLowerLeftVideoTexture();
                _lastCompositorRenderAt = now;
                _nextCompositorRenderAt = now + ResolveCompositorInterval(monitorVisible: true);
                _nextRightCompositorRenderAt = now + ResolveRightCompositorInterval(monitorVisible: true);
                _compositorDirty = false;
                _rightCompositorDirty = false;
            }
        }

        internal static void NotifyActiveFeedChanged()
        {
            // The visible station is the physical lower-left compositor. Feed
            // selection must invalidate that live path immediately; the legacy
            // MonitorFocus overlay is deliberately inactive in physical focus.
            SyncSlotTextures();
            RefreshLeftCameraLabel(Time.unscaledTime, monitorVisible: true);
            _compositorDirty = true;
            _nextCompositorRenderAt = 0f;
            _forceFastMaterialVerify = true;
        }

        internal static void NotifySlotTextureChanged(int slot, Texture texture)
        {
            if (_slotImages == null || slot < 0 || slot >= _slotImages.Length || _slotImages[slot] == null)
                return;

            // Facility slot 0 may continue baking while a bodycam/turret owns the
            // visible feed. Do not let that background notification replace the
            // selected source in the physical compositor.
            Texture visibleTexture = slot == 0 && !MonitorFocus.IsFacilityFeedActive
                ? ResolveLeftFeedTexture()
                : texture;
            _slotImages[slot].texture = visibleTexture != null
                ? visibleTexture
                : Texture2D.blackTexture;
            _compositorDirty = true;
            _forceFastMaterialVerify = true;
        }

        internal static void Shutdown()
        {
            TickCctvTargetOutlines(active: false);
            VanillaRadarFeed.SetActive(false);
            RestoreVanillaLowerMonitorFeeds();
            _lowerRightAuxBinding.Restore();
            _lowerRightBinding.Restore();
            _lowerLeftBinding.Restore();
            CCTVShipSystemsBridge.EndExternalMonitorTakeover();

            if (_lowerLeftRoot != null)
                UnityEngine.Object.Destroy(_lowerLeftRoot);
            if (_rigRoot != null)
                UnityEngine.Object.Destroy(_rigRoot);

            ReleaseRenderTexture(ref _leftRenderTexture);
            ReleaseRenderTexture(ref _rightRenderTexture);

            _rigRoot = null;
            _leftCamera = null;
            _rightCamera = null;
            _slotImages = null;
            _rightRadarImage = null;
            _rightRadarLabel = null;
            _rightRadarLegend = null;
            _lowerLeftRoot = null;
            _lowerLeftImage = null;
            _leftCameraLabel = null;
            _leftPageLabel = null;
            _leftSwitchFlashImage = null;
            _leftReticleRoot = null;
            _leftTerminalOverlayRoot = null;
            ClearLeftScreenFurnitureRefs();
            if (_signalLostNoiseTexture != null)
            {
                UnityEngine.Object.Destroy(_signalLostNoiseTexture);
                _signalLostNoiseTexture = null;
            }
            CctvScreenClock.ResetDiagnostics();
            _leftSwitchFlashStartedAt = 0f;
            _leftSwitchFlashUntil = 0f;
            _lastLeftCameraLabel = null;
            _lastLeftPageLabel = null;
            _cctvModeActive = false;
            _loggedWaitingForTarget = false;
            _loggedCreated = false;
            _loggedLeftMissing = false;
            _loggedRightMissing = false;
            _loggedPinningMapVideo = false;
            _nextProbeAt = 0f;
            _nextBindMaintenanceAt = 0f;
            _nextCompositorRenderAt = 0f;
            _nextRightCompositorRenderAt = 0f;
            _nextReassertAt = 0f;
            _nextFullSuppressionAt = 0f;
            _nextFastMaterialVerifyAt = 0f;
            _nextFastReassertAt = 0f;
            _stoodDownForQuotaPresentation = false;
            _stoodDownForExternalMonitorTakeover = false;
            _forceFastMaterialVerify = false;
            _compositorDirty = true;
            _rightCompositorDirty = true;
            _lastCompositorRenderAt = 0f;
            _wasMonitorVisible = false;
            _bindMaintenancePending = false;
            _suppressedUnobservedBindMaintenancePasses = 0L;
            _nextBindMaintenanceSuppressionReportAt = 0f;
            _bindMaintenanceGateLogged = false;
            _bindMaintenanceResumeLogged = false;
            _powerBlanked = false;
            _diagnosticsDumped = false;
            _generalImprovementsWallSeen = false;
            _giWallProbeFrame = -1;
            _giWallProbeResult = false;
            GeneralImprovementsMonitorCompat.ResetDiagnostics();
        }

        private static void EnsureRenderRig()
        {
            if (_rigRoot != null && _leftRenderTexture != null && _rightRenderTexture != null)
                return;

            ShutdownRenderRigOnly();

            _rigRoot = new GameObject(RigRootName);
            UnityEngine.Object.DontDestroyOnLoad(_rigRoot);
            _rigRoot.transform.position = new Vector3(
                OffscreenRigParking.SlotX(OffscreenRigSlot.CctvVanillaMonitor),
                OffscreenRigParking.ParkingHeight,
                0f);

            RectTransform leftCanvas = CreateMonitorCanvas("LowerLeft", 0f, out _leftCamera, out _leftRenderTexture);
            CreateLowerLeftRigUi(leftCanvas);

            RectTransform rightCanvas = CreateMonitorCanvas("Right", MonitorWidth + 256f, out _rightCamera, out _rightRenderTexture);
            CreateRightRigUi(rightCanvas);
        }

        private static void ShutdownRenderRigOnly()
        {
            if (_rigRoot != null)
                UnityEngine.Object.Destroy(_rigRoot);
            ReleaseRenderTexture(ref _leftRenderTexture);
            ReleaseRenderTexture(ref _rightRenderTexture);
            _rigRoot = null;
            _leftCamera = null;
            _rightCamera = null;
            _slotImages = null;
            _rightRadarImage = null;
            _rightRadarLabel = null;
            _rightRadarLegend = null;
            _leftReticleRoot = null;
            _leftTerminalOverlayRoot = null;
            ClearLeftScreenFurnitureRefs();
            // The cached label/page strings describe the widgets that were just
            // destroyed. Leaving them set means a rebuilt rig whose camera happens to
            // match keeps showing the freshly constructed placeholder text forever,
            // because RefreshLeftCameraAndPageLabels sees no change to apply.
            _lastLeftCameraLabel = null;
            _lastLeftPageLabel = null;
            // The blank is a property of the CURRENT textures; a rig rebuild
            // hands out fresh ones, so drop the latch and let the power gate
            // re-clear them on the next unpowered tick.
            _powerBlanked = false;
        }

        /// <summary>
        /// Drops the screen-furniture widget references and the change-detection cache
        /// that mirrors them. The GameObjects themselves die with the rig root; the
        /// cache has to be reset alongside or the rebuilt widgets never get their first
        /// update.
        /// </summary>
        private static void ClearLeftScreenFurnitureRefs()
        {
            _leftClockLabel = null;
            _leftRecDot = null;
            _leftRecLabel = null;
            _leftStatusLabel = null;
            _leftSignalBars = null;
            _leftSignalLostRoot = null;
            _leftSignalLostStatic = null;
            _leftSignalLostLabel = null;
            _lastLeftClockText = null;
            _lastLeftStatusText = null;
            _lastLeftSignalLostText = null;
            _lastLeftSignalBars = -1;
            _lastLeftRecOn = false;
            _lastLeftSignalLost = false;
            _signalLostStaticStep = 0;
        }

        private static RectTransform CreateMonitorCanvas(string name, float xOffset, out Camera camera, out RenderTexture renderTexture)
        {
            renderTexture = new RenderTexture(MonitorRenderWidth, MonitorRenderHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = $"LethalCCTV_{name}_VanillaMonitorRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            renderTexture.Create();

            GameObject cameraGo = new GameObject($"{name}MonitorCamera");
            cameraGo.transform.SetParent(_rigRoot.transform, worldPositionStays: false);
            cameraGo.transform.localPosition = new Vector3(xOffset, 0f, -10f);
            camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.orthographic = true;
            camera.orthographicSize = MonitorHeight * 0.5f;
            camera.aspect = MonitorWidth / (float)MonitorHeight;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 30f;
            camera.cullingMask = 1 << UiRenderLayer;
            camera.targetTexture = renderTexture;
            camera.enabled = false;

            // Manually driven (enabled = false, rendered via explicit Camera.Render()
            // calls). Under HDRP a camera without HDAdditionalCameraData cannot resolve
            // into its assigned RenderTexture and instead stomps the backbuffer with its
            // clear color. Mirrors ShipTurretController.Core.cs's manual-camera pattern.
            HDAdditionalCameraData hdrp = cameraGo.AddComponent<HDAdditionalCameraData>();
            hdrp.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hdrp.backgroundColorHDR = camera.backgroundColor;

            GameObject canvasGo = new GameObject($"{name}MonitorCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(Image));
            canvasGo.transform.SetParent(_rigRoot.transform, worldPositionStays: false);
            canvasGo.transform.localPosition = new Vector3(xOffset, 0f, 0f);
            SetLayerRecursive(canvasGo, UiRenderLayer);

            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;

            RectTransform canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(MonitorWidth, MonitorHeight);
            canvasRect.localScale = Vector3.one;

            Image background = canvasGo.GetComponent<Image>();
            background.color = Color.black;
            background.raycastTarget = false;

            return canvasRect;
        }

        private static void CreateLowerLeftRigUi(RectTransform canvas)
        {
            _slotImages = new RawImage[4];
            _slotImages[0] = CreateSlotImage(canvas, 0);
            CctvScreenOutlineOverlay.Initialize(canvas);
            // Sibling order is draw order. The signal-lost plate goes on top of the feed
            // AND the target outlines — a dead channel should not still be highlighting
            // targets — but under every other piece of chrome, so the CAM/PAGE/clock/REC
            // readouts stay legible over it, the way a real DVR looks when it loses one
            // channel.
            CreateLeftSignalLostPlate(canvas);
            CreateLeftCornerBrackets(canvas);
            CreateLeftCameraLabel(canvas);
            CreateLeftRecIndicator(canvas);
            CreateLeftStatusLine(canvas);
            CreateLeftSwitchFlash(canvas);
            CreateLeftReticle(canvas);
            CreateBorder(canvas, new Color(0f, 0.8f, 0.2f, 0.65f), 5f);
            CreateLeftTerminalOverlayRoot(canvas);
        }

        internal static bool TryAttachToLowerLeftOverlay(GameObject root)
        {
            if (root == null)
                return false;

            EnsureRenderRig();
            if (_leftTerminalOverlayRoot == null)
                return false;

            root.transform.SetParent(_leftTerminalOverlayRoot, worldPositionStays: false);
            root.transform.SetAsLastSibling();
            SetLayerRecursive(root, UiRenderLayer);
            return true;
        }

        internal static Vector2 GetLowerLeftOverlayDockPosition()
        {
            return Vector2.zero;
        }

        internal static Vector2 GetLowerLeftOverlayDockSize()
        {
            return new Vector2(960f, 520f);
        }

        internal static bool TryGetCctvPaneGeometry(out Vector2 center, out Vector2 size)
        {
            center = Vector2.zero;
            size = Vector2.zero;

            if (_slotImages == null || _slotImages.Length == 0 || _slotImages[0] == null)
                return false;

            RectTransform pane = _slotImages[0].transform.parent as RectTransform;
            if (pane == null)
                pane = _slotImages[0].rectTransform;

            center = pane.anchoredPosition;
            size = pane.rect.size;
            if (size.x <= 1f || size.y <= 1f)
                size = new Vector2(MonitorWidth - 28f, MonitorHeight - 28f);
            return size.x > 1f && size.y > 1f;
        }

        private static void TickCctvTargetOutlines(bool active)
        {
            if (!active)
            {
                ClearCctvTargetOutlinesIfActive();
                return;
            }

            CCTVCamera activeCamera = QuadCameraAssignment.GetBoundCamera(0);
            if (activeCamera == null)
            {
                ClearCctvTargetOutlinesIfActive();
                return;
            }

            _targetOutlinesActive = true;
            CctvOutlineManager.Tick(activeCamera, true);
            CctvScreenOutlineOverlay.Tick(true);
        }

        private static bool ShouldRunTargetOutlines()
        {
            return MonitorFocus.IsFocused
                && MonitorFocus.IsFacilityFeedActive
                && _cctvModeActive;
        }

        private static void ClearCctvTargetOutlinesIfActive()
        {
            if (!_targetOutlinesActive)
                return;

            CctvOutlineManager.Tick(null, false);
            CctvScreenOutlineOverlay.Tick(false);
            _targetOutlinesActive = false;
        }

    }
}
