using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DunGen;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using LethalCompanyInputUtils.Api;
using LethalCompanyInputUtils.BindingPathEnums;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class MonitorFocus
    {
        internal static void Initialize()
        {
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] MonitorFocus.Initialize: starting.");

            if (_overlayRoot != null)
            {
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] MonitorFocus.Initialize: already initialized — skipping.");
                return;
            }

            try
            {
                _overlayRoot = new GameObject("LethalCCTV_PhysicalFocusOverlay", typeof(RectTransform));
                // Parent to the BepInEx-managed plugin GameObject so the overlay inherits
                // DDOL state via the parent chain. Phase 1.2 deploy used self-DDOL on a
                // root GO and that silently failed (refSet=True at EnterFocus — the GO
                // got destroyed during main-menu → moon scene transition). Parent-chain
                // inheritance is the canonical "persistent root" pattern from the original
                // Phase 1 SPEC. DontDestroyOnLoad on a parented child is a no-op + warning.
                if (SurveillanceBootstrap.Instance != null)
                {
                    _overlayRoot.transform.SetParent(SurveillanceBootstrap.Instance.transform, worldPositionStays: false);
                }
                else
                {
                    // Fallback: should never hit (Initialize runs from SurveillanceBootstrap.Awake after
                    // Instance is set), but if some future caller invokes Initialize
                    // pre-Awake, fall back to self-DDOL on root.
                    UnityEngine.Object.DontDestroyOnLoad(_overlayRoot);
                    SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] MonitorFocus.Initialize: SurveillanceBootstrap.Instance null — fell back to self-DDOL.");
                }
                SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] MonitorFocus.Initialize: overlay parent='{(_overlayRoot.transform.parent != null ? _overlayRoot.transform.parent.name : "<root>")}' scene='{_overlayRoot.scene.name}'.");
                EnsurePhysicalFocusRenderRig();
                if (_physicalFocusRenderRigRoot != null)
                {
                    _overlayRoot.transform.SetParent(_physicalFocusRenderRigRoot.transform, worldPositionStays: false);
                    SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] MonitorFocus.Initialize: physical overlay parent='{_overlayRoot.transform.parent.name}' renderTexture={PHYSICAL_FOCUS_RT_WIDTH}x{PHYSICAL_FOCUS_RT_HEIGHT}.");
                }

                var canvas = _overlayRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = _physicalFocusUiCamera;
                canvas.sortingOrder = 0;

                RectTransform rootRt = _overlayRoot.GetComponent<RectTransform>();
                rootRt.anchorMin = new Vector2(0.5f, 0.5f);
                rootRt.anchorMax = new Vector2(0.5f, 0.5f);
                rootRt.pivot = new Vector2(0.5f, 0.5f);
                rootRt.sizeDelta = new Vector2(OVERLAY_WIDTH, OVERLAY_HEIGHT);
                rootRt.localPosition = Vector3.zero;
                rootRt.localRotation = Quaternion.identity;
                rootRt.localScale = Vector3.one;

                var scaler = _overlayRoot.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;

                // No GraphicRaycaster — exit is keyboard-only via LcInputActions, no UI clicks.

                CreateOverlayBackground(_overlayRoot.transform);
                CreateOverlayWorkstationBackplate(_overlayRoot.transform);
                _overlayRawImages = new RawImage[4];
                _overlayPaneRects = new RectTransform[4];
                _overlayOutlineRects = new RectTransform[4];
                _overlayPaneFrames = new PaneFrame[4];
                var outlines = new Image[4];
                _overlayCameraLabels = new TextMeshProUGUI[4];
                _overlayCameraLabelBackgrounds = new Image[4];
                _overlayCameraLabelRects = new RectTransform[4];

                // Back to a 2x2 CCTV grid, sized to dominate the terminal CRT
                // while leaving a compact lower instrument strip.
                for (int i = 0; i < 4; i++)
                {
                    _overlayRawImages[i] = CreateOverlayQuadrant(_overlayRoot.transform, i);
                    outlines[i] = CreateOverlayOutline(_overlayRoot.transform, i);
                    _overlayCameraLabelBackgrounds[i] = CreateOverlayCameraLabel(_overlayRoot.transform, i, out _overlayCameraLabels[i]);
                }
                OverlayOutlines = outlines;
                CctvScreenOutlineOverlay.Initialize(_overlayRoot.transform);
                for (int i = 0; i < _overlayCameraLabelBackgrounds.Length; i++)
                {
                    if (_overlayCameraLabelBackgrounds[i] != null)
                        _overlayCameraLabelBackgrounds[i].transform.SetAsLastSibling();
                }

                CreateOverlayChrome(_overlayRoot.transform);
                CreateSignalLogPanel(_overlayRoot.transform);
                CreateRadarInset(_overlayRoot.transform);
                CreateOperatorStatsPanel(_overlayRoot.transform);
                CreateCameraScanOverlay(_overlayRoot.transform);
                CreateCameraReviewMenu(_overlayRoot.transform);
                CreatePlacementEditorMenu(_overlayRoot.transform);
                CreateActivePaneReticle(_overlayRoot.transform);
                EnsureFocusSfx();
                EnsureProvidedFocusAudioLoading();
                EnsureFocusMachineLoopSource();
                EnsureCameraAudioProxy();
                _hackingOverlay = new HackingOverlay(_overlayRoot.transform);
                _hackingOverlay.EnsureBuilt();
                _mainframeOverlay = new MainframeControlOverlay(_overlayRoot.transform);
                _mainframeOverlay.EnsureBuilt();
                CctvOutlineManager.Initialize();
                SetLayerRecursive(_overlayRoot, PHYSICAL_FOCUS_UI_LAYER);

                _overlayRoot.SetActive(false);

                // #600: the binding set lives behind TryBindInputActions so an InputUtils older
                // than the one we compile against costs only the keyboard controls, not the whole
                // overlay. See MonitorFocus.InputBinding.cs for why the try/catch cannot live here.
                TryBindInputActions();

                FocusMouselook.Initialize();
                Camera.onPreCull -= OnCameraPreCull;
                Camera.onPreCull += OnCameraPreCull;

                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] MonitorFocus initialized.");
            }
            catch (Exception ex)
            {
                // Phase 1.1 deploy: 9× "overlay not initialized" warnings at E-press
                // were the symptom of this silent path firing without a log. The
                // catch logs the root cause; partial state is left alone — Shutdown
                // can still clean up _overlayRoot if it was set before the throw.
                SurveillanceBootstrap.Log.LogError($"[LethalCCTV] MonitorFocus.Initialize threw: {ex}");
            }
        }

        private static void EnsurePhysicalFocusRenderRig()
        {
            if (_physicalFocusRenderRigRoot == null)
            {
                _physicalFocusRenderRigRoot = new GameObject("LethalCCTV_PhysicalFocusRenderRig");
                if (SurveillanceBootstrap.Instance != null)
                {
                    _physicalFocusRenderRigRoot.transform.SetParent(SurveillanceBootstrap.Instance.transform, worldPositionStays: false);
                }
                else
                {
                    UnityEngine.Object.DontDestroyOnLoad(_physicalFocusRenderRigRoot);
                }
                _physicalFocusRenderRigRoot.transform.localPosition = new Vector3(12000f, -22000f, 6000f);
                _physicalFocusRenderRigRoot.transform.localRotation = Quaternion.identity;
                _physicalFocusRenderRigRoot.transform.localScale = Vector3.one;
            }

            if (_physicalFocusRenderTexture == null)
            {
                _physicalFocusRenderTexture = new RenderTexture(PHYSICAL_FOCUS_RT_WIDTH, PHYSICAL_FOCUS_RT_HEIGHT, 0, RenderTextureFormat.ARGB32)
                {
                    name = "LethalCCTV_PhysicalFocusRT",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    autoGenerateMips = false,
                    useDynamicScale = false,
                };
                _physicalFocusRenderTexture.Create();
            }

            if (_physicalFocusUiCamera == null)
            {
                GameObject cameraGo = new GameObject("PhysicalFocusUICamera");
                cameraGo.transform.SetParent(_physicalFocusRenderRigRoot.transform, worldPositionStays: false);
                cameraGo.transform.localPosition = new Vector3(0f, 0f, -10f);
                cameraGo.transform.localRotation = Quaternion.identity;
                cameraGo.transform.localScale = Vector3.one;

                _physicalFocusUiCamera = cameraGo.AddComponent<Camera>();
                _physicalFocusUiCamera.clearFlags = CameraClearFlags.Depth;
                _physicalFocusUiCamera.backgroundColor = Color.clear;
                _physicalFocusUiCamera.orthographic = true;
                _physicalFocusUiCamera.orthographicSize = OVERLAY_HEIGHT * 0.5f;
                _physicalFocusUiCamera.aspect = PHYSICAL_FOCUS_RT_WIDTH / (float)PHYSICAL_FOCUS_RT_HEIGHT;
                _physicalFocusUiCamera.nearClipPlane = 0.1f;
                _physicalFocusUiCamera.farClipPlane = 30f;
                _physicalFocusUiCamera.cullingMask = 1 << PHYSICAL_FOCUS_UI_LAYER;
                _physicalFocusUiCamera.targetTexture = _physicalFocusRenderTexture;
                _physicalFocusUiCamera.allowHDR = false;
                _physicalFocusUiCamera.allowMSAA = false;
                _physicalFocusUiCamera.enabled = false;
            }
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            if (root == null) return;
            root.layer = layer;
            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child != null) SetLayerRecursive(child.gameObject, layer);
            }
        }

        internal static void Shutdown()
        {
            // If shutdown happens while focused (plugin reload, scene tear-down
            // during focus), route through ForceExit so disableLookInput is
            // restored. Without this the stash would leak to the next session
            // with no way to recover. Runs BEFORE _overlayRoot is destroyed.
            ForceExit("shutdown");
            CloseStationEditMenu("shutdown");
            SetStationThirdPersonDebugPersistent(false);
            RestoreStationGlobalThirdPersonDebugView();
            // Immediate (not scheduled): TickFrame stops running after shutdown,
            // so a deferred restore would leave the player invisible.
            RestoreLocalPlayerVisualStateAtControllerRestore(_controllerRestorePlayer);
            EndStationPlayerPoseTracking();
            RestoreAudioListenerToPlayer();
            // A pending exit-invariant reassert must not survive shutdown:
            // TickFrame stops, and PlayerControllerB instances persist across
            // rounds, so a stale slot would fold a stale yaw into a walking
            // player on the next init's first tick.
            _exitCameraInvariantPlayer = null;
            _exitCameraInvariantScheduledFrame = -1;
            Camera.onPreCull -= OnCameraPreCull;

            // #600: the unsubscribe half lives beside the subscribe half, for the same
            // JIT-boundary reason. See MonitorFocus.InputBinding.cs.
            UnbindInputActions();
            if (_overlayRoot != null)
            {
                UnityEngine.Object.Destroy(_overlayRoot);
                _overlayRoot = null;
            }
            if (_physicalFocusRenderTexture != null)
            {
                // #502 — unbind first: the UI camera lives under
                // _physicalFocusRenderRigRoot, which is only Destroy()ed below
                // (and deferred to end of frame), so it would otherwise still
                // point at the render texture being released here.
                if (_physicalFocusUiCamera != null)
                    _physicalFocusUiCamera.targetTexture = null;
                _physicalFocusRenderTexture.Release();
                UnityEngine.Object.Destroy(_physicalFocusRenderTexture);
                _physicalFocusRenderTexture = null;
            }
            if (_physicalFocusRenderRigRoot != null)
            {
                UnityEngine.Object.Destroy(_physicalFocusRenderRigRoot);
                _physicalFocusRenderRigRoot = null;
            }
            if (_crtScanlineSprite != null)
            {
                UnityEngine.Object.Destroy(_crtScanlineSprite);
                _crtScanlineSprite = null;
            }
            if (_crtScanlineTexture != null)
            {
                UnityEngine.Object.Destroy(_crtScanlineTexture);
                _crtScanlineTexture = null;
            }
            if (_terminalBackdropSprite != null)
            {
                UnityEngine.Object.Destroy(_terminalBackdropSprite);
                _terminalBackdropSprite = null;
            }
            if (_terminalBackdropTexture != null)
            {
                UnityEngine.Object.Destroy(_terminalBackdropTexture);
                _terminalBackdropTexture = null;
            }
            _physicalFocusUiCamera = null;
            RestoreStationFocusRenderBudget();
            if (_cameraAudioProxyRoot != null)
            {
                UnityEngine.Object.Destroy(_cameraAudioProxyRoot);
                _cameraAudioProxyRoot = null;
                _cameraAudioProxyListener = null;
            }
            StopFocusMachineLoop();
            if (_focusMachineLoopRoot != null)
            {
                UnityEngine.Object.Destroy(_focusMachineLoopRoot);
                _focusMachineLoopRoot = null;
                _focusMachineLoopSource = null;
            }
            FocusMouselook.Shutdown();
            RestoreFocusHudDimming();
            _overlayRawImages = null;
            _overlayPaneRects = null;
            _overlayOutlineRects = null;
            _overlayPaneFrames = null;
            OverlayOutlines = null;
            _overlayCameraLabels = null;
            _overlayCameraLabelBackgrounds = null;
            _overlayCameraLabelRects = null;
            _overlayHeaderText = null;
            _overlayPageText = null;
            _overlayClockText = null;
            _overlayRecText = null;
            _overlayRecDot = null;
            _overlayActiveText = null;
            _radarInsetImage = null;
            _radarInsetRect = null;
            _radarImageRect = null;
            _radarInsetStatus = null;
            _operatorStatsRect = null;
            _operatorStatsText = null;
            _signalLogRect = null;
            _signalLogTitleText = null;
            _signalLogText = null;
            _scanStatusText = null;
            _reviewMenuRoot = null;
            _reviewMenuText = null;
            _placementEditorRoot = null;
            _placementEditorText = null;
            _centerReticle = null;
            _scanLabels = null;
            _scanVisibleUntil = 0f;
            CctvOutlineManager.Shutdown();
            CctvScreenOutlineOverlay.Shutdown();
            _audioListenerPlayer = null;
            _focusedPlayer = null;
            _exitPostureLogPending = false;
            _exitPosturePlayer = null;
            _exitPostureRestoredAt = 0f;
            _exitPostureLogAt = 0f;
            _priorDisableLook = false;
            _priorDisableMove = false;
            _priorDisableInteract = false;
            _focusViewAnimating = false;
            _focusViewExitPending = false;
            _focusViewAnimationDelayUntil = 0f;
            _physicalFocusViewActive = false;
            _stationPoseTrackingLogged = false;
            _stationPlayerPoseLockActive = false;
            _stationPlayerPosePlayer = null;
            _stationPlayerPoseSavedPosition = Vector3.zero;
            _stationPlayerPoseSavedRotation = Quaternion.identity;
            _stationPlayerPoseHandbackRotation = Quaternion.identity;
            _stationPlayerPoseHandbackCameraPitch = 0f;
            _stationPlayerPoseSavedServerPosition = Vector3.zero;
            _stationPlayerPoseSavedSnapToServerPosition = false;
            _stationPlayerPoseSavedDisableSyncInAnimation = false;
            _stationPlayerPoseSavedFreeRotationInInteractAnimation = false;
            _stationPlayerPoseSavedInSpecialInteractAnimation = false;
            _stationPlayerPoseSavedEnteringSpecialAnimation = false;
            _stationPlayerPoseHadController = false;
            _stationPlayerPoseSavedControllerDetectCollisions = false;
            _stationPlayerCameraBaselineCaptured = false;
            _stationPlayerCameraPlayerLocalPosition = Vector3.zero;
            if (_stationPlayerCameraPositionStabilizer != null)
                UnityEngine.Object.Destroy(_stationPlayerCameraPositionStabilizer);
            _stationPlayerCameraPositionStabilizer = null;
            _stationRadarLookBlend = 0f;
            _stationRadarLookSuppressedUntil = 0f;
            _stationFocusAnchorForcedUntil = 0f;
            _nextStationFocusOverlayRefreshAt = 0f;
            _radarLookEventState = false;
            _holdLocalObstructorsForInteractionsApiRestore = false;
            _localObstructorCallLogs.Clear();
            _boundaryInputRecoveryLogs.Clear();
            _stationCameraControlActive = false;
            _stationCameraControlEnablePending = false;
            _stationCameraControlEnableAt = 0f;
            _cameraPlacementEditModeOpen = false;
            _reviewMenuOpen = false;
            _reviewMenuSelectedIndex = 0;
            _placementEditorOpen = false;
            _placementEditSession = null;
            _nextStatsRefreshTime = 0f;
            _nextRadarInsetRefreshTime = 0f;
            _nextSignalLogRefreshTime = 0f;
            _nextOverlayHeaderRefreshTime = 0f;
            _overlayRecOn = false;
            _lastHackDockState = false;
            _lastDockTurretState = false;
            _focusDockLayoutInitialized = false;
            _hackingOverlay = null;
            _mainframeOverlay = null;
            _turretPageActive = false;
            _turretHitmarkerUntil = 0f;
            _turretHitmarkerEnemy = false;
            IsFocused = false;
            _isExitingFocus = false;
            ActiveSlot = 0;
            _activeFeedKind = SurveillanceFeedKind.Facility;
            _activeBodycamPlayerSlot = -1;
        }

        internal static CCTVCamera GetActiveCamera()
        {
            if (!IsFacilityFeedActive)
                return null;
            return QuadCameraAssignment.GetBoundCamera(ActiveSlot);
        }

        internal static bool ShouldSuppressPlayerInput(PlayerControllerB player)
        {
            if (!IsFocused || _isExitingFocus || player == null) return false;
            if (_focusedPlayer != null && ReferenceEquals(player, _focusedPlayer)) return true;
            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            return local != null && ReferenceEquals(player, local);
        }

        internal static void ApplyFocusInputLock(PlayerControllerB player)
        {
            if (player == null) return;
            player.disableLookInput = true;
            player.disableMoveInput = true;
            player.disableInteract = true;
        }

        private static void EnforceFocusInputLock()
        {
            if (_isExitingFocus) return;
            PlayerControllerB player = _focusedPlayer != null
                ? _focusedPlayer
                : (GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null);
            if (!ShouldSuppressPlayerInput(player)) return;
            ApplyFocusInputLock(player);
        }

        private static bool HasFocusCleanupState()
        {
            return IsFocused
                || _focusedPlayer != null
                || _physicalFocusViewActive
                || _focusViewAnimating
                || _focusViewExitPending
                || _stationPlayerPoseLockActive
                // #452: pose settle owns the player before IsFocused flips, so an
                // exit that lands mid-settle still has state to unwind.
                || _stationPoseSettleActive
                || _focusHudDimActive
                || _stationThirdPersonPreviewActive
                || _stationGlobalThirdPersonCameraActive
                || _focusHiddenLocalRenderers.Count > 0
                || _focusForcedLocalRenderers.Count > 0
                || _previewForcedLocalRenderers.Count > 0
                || _focusHiddenLocalVisor != null
                || (_overlayRoot != null && _overlayRoot.activeSelf)
                || (_physicalFocusUiCamera != null && _physicalFocusUiCamera.enabled);
        }

        private static bool LooksLikeStaleFocusInputLock(PlayerControllerB player)
        {
            if (player == null) return false;
            if (player.inTerminalMenu) return false;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen) return false;
            if (player.isTypingChat) return false;
            return player.disableLookInput && player.disableMoveInput && player.disableInteract;
        }

        private static void ClearResidualFocusAnimation(PlayerControllerB player, string reason)
        {
            if (player == null) return;
            if (player.inTerminalMenu) return;
            if (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen) return;
            if (player.isTypingChat) return;
            if (!player.inSpecialInteractAnimation && !player.enteringSpecialAnimation) return;

            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] MonitorFocus clearing residual special-animation state ({reason}).");
            try
            {
                player.UpdateSpecialAnimationValue(false, 0);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] MonitorFocus special-animation reset failed ({reason}): {ex.Message}");
            }

            try
            {
                player.inSpecialInteractAnimation = false;
                player.enteringSpecialAnimation = false;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] MonitorFocus special-animation flag clear failed ({reason}): {ex.Message}");
            }
        }

        private static void RecoverStaleFocusInputLock(PlayerControllerB player, string reason)
        {
            if (!LooksLikeStaleFocusInputLock(player)) return;
            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] MonitorFocus recovered stale local-player input flags ({reason}).");
            ClearResidualFocusAnimation(player, reason);
            player.disableLookInput = false;
            player.disableMoveInput = false;
            player.disableInteract = false;
        }

        /// <summary>
        /// Lifecycle-boundary self-heal for a stuck local input lock (#452).
        /// ForceExit's early-return branches are reached exactly when MonitorFocus
        /// owns no live session state, so nothing found here belongs to an in-flight
        /// focus session — only to one that already ended badly. RecoverStaleFocusInputLock
        /// is the guard that keeps this off legitimate non-CCTV state: it fires only
        /// when look+move+interact are ALL asserted while no terminal, quick menu, or
        /// chat entry explains them, and ClearResidualFocusAnimation runs only from
        /// inside that guard. A non-CCTV special animation that does not hold all
        /// three flags (ladder climb, teleporter beam) is therefore left untouched —
        /// the animation state is never cleared on its own here. Safe when the local
        /// player never entered focus: the flags are simply not set and nothing runs.
        /// Logged once per (operation:reason) like the ObstructorMask boundary logs.
        /// </summary>
        private static void RecoverStaleFocusInputLockAtBoundary(string reason)
        {
            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            bool stale = LooksLikeStaleFocusInputLock(local);
            bool residualAnimation = stale &&
                (local.inSpecialInteractAnimation || local.enteringSpecialAnimation);
            if (stale)
                RecoverStaleFocusInputLock(local, reason);

            if (!_boundaryInputRecoveryLogs.Add(reason))
                return;

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][FocusInputRecovery] boundary={reason} frame={Time.frameCount} " +
                $"player={local != null} clearedInputLock={stale} clearedAnimation={residualAnimation}.");
        }

        private static void RestoreFocusInputState()
        {
            PlayerControllerB stashedPlayer = _focusedPlayer;
            if (stashedPlayer != null)
            {
                ClearResidualFocusAnimation(stashedPlayer, "exit-focus-stashed");
                stashedPlayer.disableLookInput = _priorDisableLook;
                stashedPlayer.disableMoveInput = _priorDisableMove;
                stashedPlayer.disableInteract = _priorDisableInteract;
            }

            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (local != null && !ReferenceEquals(local, stashedPlayer))
            {
                RecoverStaleFocusInputLock(local, "exit-focus-local");
            }

            _focusedPlayer = null;
            _priorDisableLook = false;
            _priorDisableMove = false;
            _priorDisableInteract = false;
        }

        // Signature kept compatible with the original InteractTrigger.onInteract
        // wiring (InteractEvent : UnityEvent<PlayerControllerB>) — caller still
        // passes the local player, we still ignore it and read GameNetworkManager
        // instead so a future networked-interactor wouldn't accidentally use the
        // remote client's state.
        internal static void EnterFocus(PlayerControllerB requestedPlayer)
        {
            long enterPerfStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long enterPerfPrev = enterPerfStart;
            System.Text.StringBuilder enterPerfPhases = new System.Text.StringBuilder(256);
            void MarkEnterPhase(string phaseName)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double ms = (now - enterPerfPrev) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                enterPerfPrev = now;
                enterPerfPhases.Append(phaseName).Append('=').Append(ms.ToString("F1")).Append("ms ");
                // Same phases also feed the per-entry TickFrame bucket line (#291)
                // so one log line accounts for the whole entry tick.
                EntryPerfMark("Enter." + phaseName);
            }
            if (_overlayRoot == null)
            {
                // Unity's == overload returns true for destroyed-but-referenced objects too.
                // ReferenceEquals isolates literal-null (Initialize never ran / Shutdown
                // nulled) from destroyed-object (DontDestroyOnLoad didn't stick across a
                // scene transition).
                bool refSet = !ReferenceEquals(_overlayRoot, null);
                SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV] MonitorFocus.EnterFocus: overlay not initialized (refSet={refSet}); continuing with physical focus view only.");
            }
            if (!TryEnterFocusPreflight(
                    allowPoseSettleOwner: true,
                    out PlayerControllerB lp,
                    out _))
                return;

            // Defensive re-bind: if Despawn/Spawn cycled the RTs while the overlay was
            // alive, the cached texture references on the RawImages are stale. Reads
            // GetCurrentDisplayTexture so empty slots resolve to the shared black RT
            // rather than the slot's QuadRTs (which may hold a previously-bound
            // camera's last baked frame in the dual-RT design).
            if (_overlayRawImages != null)
            {
                for (int i = 0; i < _overlayRawImages.Length; i++)
                {
                    if (_overlayRawImages[i] != null)
                    {
                        _overlayRawImages[i].texture = QuadMonitor.GetCurrentDisplayTexture(i);
                    }
                }
            }
            InitializeFeedSelection();
            _stationCameraControlActive = false;
            _stationCameraControlEnablePending = false;
            _stationCameraControlEnableAt = 0f;
            _stationCameraRenderBoostUntil = 0f;
            _stationRadarLookBlend = 0f;
            _stationRadarLookSuppressedUntil = 0f;
            _stationFocusAnchorForcedUntil = 0f;
            _radarLookEventState = false;
            RefreshRadarInsetTexture();
            MarkEnterPhase("radar-inset");
            RefreshOverlayFonts();
            MarkEnterPhase("overlay-fonts");
            UpdatePageIndicator(QuadCameraAssignment.CurrentPage, QuadCameraAssignment.TotalPages);
            UpdateActiveSlotLabel(ActiveSlot);
            RefreshTurretFocusPage();
            RefreshBodycamFocusSlot();
            MarkEnterPhase("overlay-prep");
            QuadMonitor.SetPhysicalDisplayBlanked(false);
            if (_overlayRoot != null)
            {
                SetLayerRecursive(_overlayRoot, PHYSICAL_FOCUS_UI_LAYER);
                _overlayRoot.SetActive(false);
            }
            MarkEnterPhase("overlay-layer+activate");
            if (_physicalFocusUiCamera != null) _physicalFocusUiCamera.enabled = false;
            QuadMonitor.SetFocusScreenTexture(null, false);
            MarkEnterPhase("display");

            // Look-suppression stash. Capture BEFORE we set true so the restore
            // path can replay the player's pre-focus state faithfully — vanilla
            // may own a true here via inSpecialAnimation; restoring to that
            // captured value (rather than unconditional false) keeps us out of
            // vanilla's way. SPEC Decision 3.
            _focusedPlayer = lp;
            _priorDisableLook = lp.disableLookInput;
            _priorDisableMove = lp.disableMoveInput;
            _priorDisableInteract = lp.disableInteract;
            lp.disableLookInput = true;
            lp.disableMoveInput = true;
            lp.disableInteract = true;
            // The anchor auto-aim depends on StartOfRound UI that may not have been
            // ready at station-spawn time; force one attempt now so the enter
            // animation targets the monitor-derived pose, not the hand-tuned one.
            CCTVOperatorStation.EnsureFocusAnchorAimed(force: true);
            MarkEnterPhase("focus-anchor-aim");
            CCTVOperatorStation.EnsureRadarAnchorAimed(force: true);
            MarkEnterPhase("radar-anchor-aim");
            // The monitor stays vanilla until the enter animation's hand
            // actually presses the red button; TickFrame completes the flip.
            ScheduleStationFeedFlip();
            MarkEnterPhase("anchors+lock");

            // ORDER REMAINS INTENTIONAL: the pre-focus settle has already held
            // the exact canonical root/camera pose for a full frame, so the view
            // path captures that real pose first. Tracking then consumes the
            // settle-start snapshot before applying the existing seated pose lock.
            BeginPhysicalFocusView(lp);
            MarkEnterPhase("BeginPhysicalFocusView.rest");
            if (_stationPoseSettleTransferToFocus && ReferenceEquals(_stationPoseSettlePlayer, lp))
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][PoseSettle] captured head={FormatDebugVector(_priorFocusCameraWorldPosition)}.");
            }
            BeginStationPlayerPoseTracking(lp);
            MarkEnterPhase("BeginStationPlayerPoseTracking");
            ApplyStationFocusRenderBudget(lp.gameplayCamera);
            MarkEnterPhase("ApplyStationFocusRenderBudget");
            ApplyFocusHudDimming(force: true);
            MarkEnterPhase("hud-dim#1.rest");
            EnsureProvidedFocusAudioLoading();
            MarkEnterPhase("focus-audio-load");

            // Cursor LOCKED per SPEC Decision 3: mouselook drives the active
            // pane, so the cursor must not escape the window. Replaces the
            // Phase 1 cursor-unlock policy.
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            IsFocused = true;
            OpenBodyCamsCompat.SetFocusRenderingActive(IsBodycamFeedActive);
            CctvTargetCache.Invalidate();
            // Replicate the fresh slot (reset to 0 above) so observers pick up
            // the right facility camera as soon as the operator claim lands.
            CCTVMonitorFeedSync.NotifyLocalActiveSlotChanged(ActiveSlot);
            // The vanilla station defers the CCTV-mode flip to the scheduled
            // press-contact event (ScheduleStationFeedFlip above).
            MarkEnterPhase("cctv-mode-activate");
            _chairEventActive = true;
            CCTVStationEvents.RaiseChairEntered(lp);
            MarkEnterPhase("chair-event");
            // Single-feed station: the operator drives the active camera the
            // moment they sit down — there is no separate "press E to take
            // control" mode anymore (E now stands up, same as ESC).
            ScheduleStationCameraControlActivation();
            BoostStationCameraRenderCadence(0.35f);
            MarkEnterPhase("render-cadence+boot-sfx");
            StartFocusMachineLoop();
            MarkEnterPhase("StartFocusMachineLoop");
            LogFocusRenderDiagnostics();
            MarkEnterPhase("LogFocusRenderDiagnostics");
            RefreshTurretFocusPage();
            RefreshBodycamFocusSlot();
            MarkEnterPhase("bodycam+turret-page");
            CCTVWalkieTalkieBridge.EnterFocus(lp);
            MarkEnterPhase("walkie-bridge");
            PaneHighlight.SetActive(ActiveSlot);
            ApplyActivePaneLayout();
            RefreshFocusDockLayout();
            MarkEnterPhase("pane-layout+dock");
            ApplyPhysicalFocusView();
            SyncCameraAudioProxy();
            UpdateActivePaneReticle();
            MarkEnterPhase("focus-view+audio+reticle");
            RefreshOperatorStats(force: true);
            MarkEnterPhase("RefreshOperatorStats");
            ApplyFocusHudDimming(force: true);
            MarkEnterPhase("hud-dim#2.rest");

            // Rising-edge wake render so the overlay opens with fresh content
            // even when the player entered focus while not looking at the wall
            // mesh (which means the throttle's visibility gate was holding the
            // cameras idle). Synchronous one-shot bypasses the throttle entirely.
            QuadCameraAssignment.RequestWakeAllSlots();
            MarkEnterPhase("RequestWakeAllSlots");
            MarkEnterPhase("wake-render");
            double enterTotalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - enterPerfStart)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][EnterPerf] EnterFocus frame {Time.frameCount}: total={enterTotalMs:F1}ms | {enterPerfPhases}");
            _enterPerfLogPending = true;
            _enterPerfFrame = Time.frameCount;
        }

        /// <summary>
        /// True between EnterFocus and the enter animation's press contact.
        /// CCTVMonitorFeedSync consults this so a claim broadcast cannot flip
        /// the local display before the hand reaches the button.
        /// </summary>
        internal static bool IsStationFeedFlipPending => _stationFeedFlipPending;

        private static void ScheduleStationFeedFlip()
        {
            _stationFeedFlipPending = true;
            _stationFeedFlipAt = Time.unscaledTime + STATION_FOCUS_ENTER_FEED_FLIP_DELAY;
        }

        private static void TickPendingStationFeedFlip()
        {
            if (!_stationFeedFlipPending) return;
            if (Time.unscaledTime < _stationFeedFlipAt) return;
            CompleteStationFeedFlip(playFeedback: true);
        }

        /// <summary>
        /// Fired by the enter hand driver at the exact frame the finger meets
        /// the button, so cap depress + SFX + feed flip land on visual contact
        /// instead of a wall-clock guess. No-op if the flip already completed
        /// (the STATION_FOCUS_ENTER_FEED_FLIP_DELAY timer is the fallback).
        /// </summary>
        internal static void CompleteStationFeedFlipAtPressContact()
        {
            if (!_stationFeedFlipPending) return;
            CompleteStationFeedFlip(playFeedback: true);
        }

        private static void CompleteStationFeedFlip(bool playFeedback)
        {
            if (!_stationFeedFlipPending) return;
            _stationFeedFlipPending = false;
            _stationFeedFlipAt = 0f;
            CCTVVanillaMonitorDisplay.SetCctvModeActive(true, announce: false);
            CCTVVanillaMonitorDisplay.PrimeMonitorOwnershipNow(renderNow: true);
            if (playFeedback)
            {
                // Press contact: depress the physical access button cap in sync
                // with the enter animation's hand press (bezel SFX fallback when
                // the external button isn't alive).
                if (CCTVAccessButton.IsAvailable)
                    CCTVAccessButton.PlayPressAnimation();
                else
                    CCTVVanillaMonitorButtons.PlayRedButtonPressSfx();
                PlayBootSfx();
            }
        }

        internal static void ExitFocus()
        {
            if (!HasFocusCleanupState()) return;
            if (_focusViewExitPending) return;
            if (!_stationThirdPersonDebugPersistent)
                SetStationThirdPersonPreview(false);
            DisableStationCameraControl();
            // Build the exit glide while the station pose lock still holds. On
            // the legacy path (Interactions API dead, no authored controller)
            // the stand-up event synchronously ends the anim session, which
            // restores AND CLEARS the saved station pose; raising it first
            // sent the glide to a zeroed target at world origin (round-5
            // Test 3 log: handbackTarget=(0.00,0.00,0.00)). The API path
            // normally ends asynchronously so Contracted profiles rarely saw
            // it, but its EndImmediate fallbacks (double-exit,
            // api-exit-failed) run the same synchronous chain — this ordering
            // fixes those edges too.
            bool exitAnimationStarted = BeginPhysicalFocusExitAnimation();
            CCTVStationEvents.RaiseActionButtonPressed("stand-up");
            if (exitAnimationStarted) return;

            CompleteExitFocusCleanup();
        }

        private static void CompleteExitFocusCleanup()
        {
            if (!HasFocusCleanupState()) return;

            // An exit before press contact still lands on the synced feed state
            // (the claim already flipped it to CCTV); complete silently.
            CompleteStationFeedFlip(playFeedback: false);

            // Capture before RestoreFocusInputState nulls the stash; the exit event
            // fires at the bottom of the cleanup with the same player we seated.
            PlayerControllerB exitingPlayer = _focusedPlayer;
            CloseStationEditMenu("focus-cleanup");
            _cameraPlacementEditModeOpen = false;
            SetStationThirdPersonPreview(false, keepPersistentDebug: _stationThirdPersonDebugPersistent);

            _isExitingFocus = true;
            try
            {

            // Restore look-suppression FIRST, before any other state mutation,
            // and only via the stashed reference (NOT a fresh
            // GameNetworkManager.Instance.localPlayerController lookup — the
            // player object may have been swapped on death/respawn between
            // EnterFocus and ExitFocus, and we want to undo on the EXACT
            // object we modified). Null the reference immediately after
            // restore so a second ExitFocus call cannot write a stale value
            // — protects the multiplayer-respawn case where the local player
            // controller identity can change underneath us.
            RestoreFocusInputState();
            RestoreFocusHudDimming();
            EndPhysicalFocusView();
            ScheduleExitPostureDiagnostic(exitingPlayer);

            if (_hackingOverlay != null && _hackingOverlay.IsOpen)
            {
                _hackingOverlay.Close();
                RefreshFocusDockLayout();
            }
            if (_mainframeOverlay != null && _mainframeOverlay.IsOpen)
            {
                _mainframeOverlay.Close("exit-focus");
                RefreshFocusDockLayout();
            }
            CancelPlacementEditor(restore: true);
            CloseCameraReviewMenu();

            RestoreAudioListenerToPlayer();
            CCTVWalkieTalkieBridge.ExitFocus();
            StopFocusMachineLoop();

            if (_overlayRoot != null)
            {
                _overlayRoot.SetActive(false);
            }
            if (_physicalFocusUiCamera != null)
            {
                _physicalFocusUiCamera.enabled = false;
            }
            QuadMonitor.SetFocusScreenTexture(null, false);
            QuadMonitor.SetPhysicalDisplayBlanked(QuadCameraAssignment.TotalCameraCount == 0);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            PaneHighlight.ClearAll();
            HideCameraScanOverlay();
            if (_centerReticle != null) _centerReticle.gameObject.SetActive(false);
            _turretPageActive = false;
            _stationCameraControlActive = false;
            _stationCameraControlEnablePending = false;
            _stationCameraControlEnableAt = 0f;
            _stationRadarLookBlend = 0f;
            _stationRadarLookSuppressedUntil = 0f;
            _stationFocusAnchorForcedUntil = 0f;
            _nextStationFocusOverlayRefreshAt = 0f;
            if (_radarLookEventState)
            {
                _radarLookEventState = false;
                CCTVStationEvents.RaiseRadarViewChanged(false);
            }
            RefreshTurretFocusPage();
            OpenBodyCamsCompat.SetFocusRenderingActive(false);
            _activeFeedKind = SurveillanceFeedKind.Facility;
            _activeBodycamPlayerSlot = -1;
            CCTVVanillaMonitorDisplay.NotifyActiveFeedChanged();
            }
            finally
            {
                // Camera handback must be unconditional at the exit boundary:
                // if anything above threw before EndPhysicalFocusView ran, the
                // player would otherwise be left with a hijacked camera and no
                // pose restore while IsFocused already reads false.
                if (_physicalFocusViewActive)
                {
                    try { EndPhysicalFocusView(); }
                    catch (Exception ex)
                    {
                        SurveillanceBootstrap.Log?.LogWarning(
                            $"[LethalCCTV] EndPhysicalFocusView failed during exit cleanup fallback: {ex.Message}");
                    }
                }
                IsFocused = false;
                _isExitingFocus = false;
                if (_chairEventActive)
                {
                    _chairEventActive = false;
                    CCTVStationEvents.RaiseChairExited(exitingPlayer);
                }
            }
        }

        /// <summary>
        /// Single chokepoint for "the world changed under us, get out of focus."
        /// Called from SurveillanceBootstrap.OnCamerasReady (dungeon regen — destroys the
        /// CCTVCamera GOs we may be panning) and from FocusMouselook.Tick
        /// (local player death — stops the suppressed-look state from
        /// surviving respawn). Routes through ExitFocus so cursor + the
        /// disableLookInput stash-restore (added in T6c) both run.
        /// </summary>
        internal static void ForceExit(string reason)
        {
            if (_stationPoseSettleActive)
                AbortEntryPoseSettle(reason);

            if (!IsFocused && !_physicalFocusViewActive && _stationGlobalThirdPersonCameraActive)
            {
                SetStationThirdPersonDebugPersistent(false);
                RestoreLocalPlayerObstructors("force-exit-third-person:" + reason);
                EndStationPlayerPoseTracking();
                RecoverStaleFocusInputLockAtBoundary("force-exit-third-person:" + reason);
                return;
            }

            if (!HasFocusCleanupState())
            {
                RestoreLocalPlayerObstructors("force-exit-no-cleanup:" + reason);
                EndStationPlayerPoseTracking();
                RecoverStaleFocusInputLockAtBoundary("force-exit-no-cleanup:" + reason);
                return;
            }
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] MonitorFocus.ForceExit: reason={reason}");
            _focusViewAnimating = false;
            _focusViewExitPending = false;
            _focusViewAnimationDelayUntil = 0f;
            CompleteExitFocusCleanup();
        }

        private static void OnExitFocusPerformed(InputAction.CallbackContext context)
        {
            if (IsFocused)
            {
                bool isEscape = context.action == _inputActions?.ExitFocusKeyEscape;
                // Latch BEFORE ExitFocus mutates IsFocused — order-independent of when
                // vanilla's OpenMenu_performed handler fires on the same frame.
                WasActiveThisFrame = true;
                _wasActiveSetFrame = Time.frameCount;
                if (Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive)
                {
                    if (isEscape)
                        CloseStationEditMenu("focus-escape-action");
                    return;
                }
                if (_stationEditMenuOpen)
                {
                    if (isEscape)
                        CloseStationEditMenu("focus-escape-action");
                    return;
                }
                if (_reviewMenuOpen)
                {
                    CloseCameraReviewMenu();
                    return;
                }
                if (_placementEditorOpen)
                {
                    CancelPlacementEditor(restore: true);
                    ShowTimedScanStatus("PLACEMENT CANCELED", OVERLAY_SCAN_RED, 1.4f);
                    return;
                }
                if (_hackingOverlay != null && _hackingOverlay.IsOpen)
                {
                    if (isEscape)
                    {
                        _hackingOverlay.Close("escape-action");
                        RefreshFocusDockLayout();
                        ShowScanStatus("HACK CANCELED", OVERLAY_SCAN_RED);
                    }
                    else
                    {
                        SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][CCTVHackTerminal] Ignored E exit while hack terminal is open; use ESC to close.");
                    }
                    return;
                }
                if (_mainframeOverlay != null && _mainframeOverlay.IsOpen)
                {
                    if (isEscape)
                    {
                        // ESC backs out a submenu, or closes the mainframe menu (returns to focus).
                        _mainframeOverlay.ConsumeEscapeIfOpen();
                        RefreshFocusDockLayout();
                    }
                    else
                    {
                        SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][MainframeControl] Ignored E exit while mainframe menu is open; use ESC.");
                    }
                    return;
                }
                if (CCTVOperatorStation.IsDebugPlacementActive)
                {
                    if (isEscape)
                        CCTVOperatorStation.CancelDebugPlacement();
                    return;
                }
                ExitFocus();
            }
        }
    }
}
