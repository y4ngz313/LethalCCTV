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
        /// <summary>
        /// #577 — hold state of the rebindable push-to-talk action, for overlay hosts that live
        /// outside station focus and therefore never see the performed/canceled callbacks below.
        /// The action is authoritative whenever it exists, disabled included: a disabled action
        /// reports not-pressed, which is input being suppressed on purpose and must not be
        /// second-guessed by reading the hardware. The raw V fallback covers only the window where
        /// there is no action at all — before MonitorFocus initializes, or after Shutdown.
        /// </summary>
        internal static bool IsWalkiePushToTalkHeld()
        {
            // #600: the action lookup is isolated behind InputBindingsUnavailable so an
            // InputUtils too old to load LethalCCTVInputActions falls through to the raw-key
            // path instead of throwing a TypeLoadException into this caller's frame.
            if (!InputBindingsUnavailable)
            {
                InputAction action = TryGetWalkiePushToTalkAction();
                if (action != null)
                    return action.IsPressed();
            }

            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard.vKey.isPressed;
        }

        private static void OnCycleActivePrevPerformed(InputAction.CallbackContext _)
        {
            if (!IsFocused) return;
            if (IsStationEditInputActive()) return;
            if (IsHackingOverlayOpen)
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][CCTVHackTerminal] Focus action LEFT received.");
                _hackingOverlay.NavigateLeft("action-left");
                return;
            }
            if (IsMainframeOverlayOpen) { _mainframeOverlay.NavigateLeft("action-left"); return; }
            if (_placementEditorOpen) return;
            if (_reviewMenuOpen) return;
            NavigateHorizontal(-1);
        }

        private static void OnCameraPreCull(Camera camera)
        {
            if (!IsFocused || _isExitingFocus || _focusViewExitPending || _focusViewAnimating)
                return;
            if (_stationThirdPersonPreviewActive)
                return;
            if (_physicalFocusCamera == null || _physicalFocusCameraTransform == null)
                return;
            if (camera != _physicalFocusCamera)
                return;

            ApplyStationPhysicalFocusView();
        }

        private static void OnCycleActiveNextPerformed(InputAction.CallbackContext _)
        {
            if (!IsFocused) return;
            if (IsStationEditInputActive()) return;
            if (IsHackingOverlayOpen)
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][CCTVHackTerminal] Focus action RIGHT received.");
                _hackingOverlay.NavigateRight("action-right");
                return;
            }
            if (IsMainframeOverlayOpen) { _mainframeOverlay.NavigateRight("action-right"); return; }
            if (_placementEditorOpen) return;
            if (_reviewMenuOpen) return;
            NavigateHorizontal(+1);
        }

        private static void OnPagePrevPerformed(InputAction.CallbackContext _)
        {
            if (!IsFocused) return;
            if (IsStationEditInputActive()) return;
            if (IsHackingOverlayOpen)
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][CCTVHackTerminal] Focus action UP received.");
                _hackingOverlay.NavigateUp("action-up");
                return;
            }
            if (IsMainframeOverlayOpen) { _mainframeOverlay.NavigateUp("action-up"); return; }
            if (_placementEditorOpen) return;
            if (_reviewMenuOpen) return;
            NavigateVertical(-1);
            // Both axes step the same unified bodycam-first feed roster.
        }

        private static void OnPageNextPerformed(InputAction.CallbackContext _)
        {
            if (!IsFocused) return;
            if (IsStationEditInputActive()) return;
            if (IsHackingOverlayOpen)
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][CCTVHackTerminal] Focus action DOWN received.");
                _hackingOverlay.NavigateDown("action-down");
                return;
            }
            if (IsMainframeOverlayOpen) { _mainframeOverlay.NavigateDown("action-down"); return; }
            if (_placementEditorOpen) return;
            if (_reviewMenuOpen) return;
            NavigateVertical(+1);
        }

        private static void OnHackSubmitPerformed(InputAction.CallbackContext _)
        {
            if (!IsFocused) return;
            if (IsStationEditInputActive()) return;
            if (IsMainframeOverlayOpen)
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][MainframeControl] Focus action ENTER received.");
                PlayInteractSfx();
                _mainframeOverlay.Submit("action-enter");
                return;
            }
            if (!IsHackingOverlayOpen) return;
            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][CCTVHackTerminal] Focus action ENTER received.");
            PlayInteractSfx();
            _hackingOverlay.Submit("action-enter");
        }

        private static void OnPingMarkerPerformed(InputAction.CallbackContext _)
        {
            if (!IsFocused) return;
            if (IsStationEditInputActive()) return;
            if (_placementEditorOpen) return;
            if (_reviewMenuOpen) return;
            if (IsHackingOverlayOpen) return;
            if (IsTurretPageActive)
            {
                ResetStationRadarLookView();
                CCTVStationEvents.RaiseActionButtonPressed("turret-fire");
                ShipTurretController.TryFire();
                return;
            }
            ResetStationRadarLookView();
            CCTVStationEvents.RaiseActionButtonPressed("station-action");
            PlayInteractSfx();
            TriggerCameraPing();
        }

        private static void OnWalkiePushToTalkPerformed(InputAction.CallbackContext _)
        {
            if (!IsFocused) return;
            if (IsStationEditInputActive()) return;
            // Inside the mainframe INTERCOM submenu, push-to-talk drives the intercom broadcast.
            if (IsMainframeOverlayOpen)
            {
                _mainframeOverlay.SetPushToTalk(true);
                RefreshOperatorStats(force: true);
                return;
            }
            if (SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowWalkieTalkie.Value)
            {
                ShowTimedScanStatus("WALKIE TALKIE DISABLED BY HOST", OVERLAY_SCAN_RED, 1.4f);
                return;
            }
            if (_placementEditorOpen) return;
            if (_reviewMenuOpen) return;
            if (IsHackingOverlayOpen) return;
            CCTVWalkieTalkieBridge.SetLocalSpeaking(true);
            RefreshOperatorStats(force: true);
        }

        private static void OnWalkiePushToTalkCanceled(InputAction.CallbackContext _)
        {
            // A release is never gated. The guards that used to sit here suppress *starting* a
            // transmission, but suppressing the matching release is how a hold gets stuck: opening
            // the station edit menu (or the placement editor, or the hacking overlay) mid-hold
            // swallowed the key-up and left the operator keyed open with no way to release.
            if (IsMainframeOverlayOpen)
            {
                // The overlay owns the bridge while it is open, and only the key source is released
                // here — a pointer hold on its transmit button keeps the intercom live.
                _mainframeOverlay.SetPushToTalk(false);
                RefreshOperatorStats(force: true);
                return;
            }

            CCTVWalkieTalkieBridge.SetLocalSpeaking(false);
            RefreshOperatorStats(force: true);
        }

        /// <summary>
        /// #541 — flip the controls tooltip on/off without leaving the station.
        /// Writing the ConfigEntry persists the choice and reuses the existing
        /// teardown/rebuild transition in CCTVFocusControlsOverlay.Tick, which
        /// re-reads the value every frame.
        /// </summary>
        private static void OnToggleControlsOverlayPerformed(InputAction.CallbackContext _)
        {
            if (!IsFocused) return;
            // #579 — the panel this toggled is retired for normal play; the sticky
            // note carries the controls instead. H stays wired only as a debug
            // affordance so the edit-mode panel can be flipped without leaving.
            if (!IsOperatorDebugToolsEnabled) return;
            if (IsStationEditInputActive()) return;
            if (IsHackingOverlayOpen) return;
            if (IsMainframeOverlayOpen) return;
            if (_placementEditorOpen) return;
            if (_reviewMenuOpen) return;

            var entry = SurveillanceBootstrap.Config?.ShowOperatorControlsOverlay;
            if (entry == null) return;

            entry.Value = !entry.Value;
            PlayInteractSfx();
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Operator controls overlay toggled {(entry.Value ? "on" : "off")} by keybind.");
        }

        private static void NavigateHorizontal(int delta)
        {
            NavigateSingleCamera(delta);
        }

        private static void NavigateVertical(int delta)
        {
            NavigateSingleCamera(delta);
        }

        private static int GetNormalFeedCount(out int bodycamCount, out int facilityCount)
        {
            bodycamCount = OpenBodyCamsCompat.PlayerFeedCount;
            facilityCount = QuadCameraAssignment.TotalPages;
            return SurveillanceFeedOrder.TotalCount(bodycamCount, facilityCount);
        }

        private static int GetCurrentNormalFeedIndex(int bodycamCount, int facilityCount)
        {
            if (IsBodycamFeedActive)
            {
                int bodycamIndex = ActiveBodycamFeedIndex;
                return SurveillanceFeedOrder.ToGlobalIndex(
                    SurveillanceFeedKind.Bodycam,
                    bodycamIndex,
                    bodycamCount,
                    facilityCount);
            }

            if (IsFacilityFeedActive)
            {
                return SurveillanceFeedOrder.ToGlobalIndex(
                    SurveillanceFeedKind.Facility,
                    QuadCameraAssignment.CurrentPage,
                    bodycamCount,
                    facilityCount);
            }

            return -1;
        }

        private static void InitializeFeedSelection()
        {
            ActiveSlot = 0;
            _activeFeedKind = SurveillanceFeedKind.Facility;
            _activeBodycamPlayerSlot = -1;
            _turretPageActive = false;

            OpenBodyCamsCompat.Tick();
            int bodycamCount = OpenBodyCamsCompat.PlayerFeedCount;
            if (bodycamCount > 0
                && OpenBodyCamsCompat.TryGetPlayerSlotAtFeedIndex(0, out int playerSlot))
            {
                _activeFeedKind = SurveillanceFeedKind.Bodycam;
                _activeBodycamPlayerSlot = playerSlot;
                CCTVVanillaMonitorDisplay.NotifyActiveFeedChanged();
                return;
            }

            if (QuadCameraAssignment.TotalPages <= 0 && ShipTurretController.IsUnlocked)
                _turretPageActive = true;

            CCTVVanillaMonitorDisplay.NotifyActiveFeedChanged();
        }

        private static void EnsureActiveFeedAvailable()
        {
            if (IsTurretPageActive && ShipTurretController.IsUnlocked)
                return;

            int normalCount = GetNormalFeedCount(out int bodycamCount, out int facilityCount);
            if (IsBodycamFeedActive && ActiveBodycamFeedIndex >= 0)
                return;
            if (IsFacilityFeedActive && facilityCount > 0)
                return;

            if (normalCount > 0)
            {
                SelectNormalFeed(0, playFeedback: false);
                return;
            }

            SetTurretPageActive(ShipTurretController.IsUnlocked);
        }

        private static void LeaveTurretPage(bool forward)
        {
            int normalCount = GetNormalFeedCount(out _, out _);
            if (normalCount <= 0)
                return;

            SelectNormalFeed(forward ? 0 : normalCount - 1, playFeedback: true);
        }

        private static void SetTurretPageActive(bool active)
        {
            bool shouldActivate = active && ShipTurretController.IsUnlocked;
            if (_turretPageActive == shouldActivate)
            {
                RefreshTurretFocusPage();
                RefreshBodycamFocusSlot();
                RefreshFocusDockLayout();
                UpdatePageIndicator(QuadCameraAssignment.CurrentPage, QuadCameraAssignment.TotalPages);
                CCTVVanillaMonitorDisplay.NotifyActiveFeedChanged();
                return;
            }

            _turretPageActive = shouldActivate;
            if (_turretPageActive)
            {
                ActiveSlot = 0;
                OpenBodyCamsCompat.SetFocusRenderingActive(false);
                CCTVMonitorFeedSync.NotifyLocalActiveSlotChanged(ActiveSlot);
            }

            if (IsFocused)
            {
                PlayPageSfx();
                CCTVStationEvents.RaiseActionButtonPressed("camera-page");
            }

            HideCameraScanOverlay();
            ApplyActivePaneLayout();
            RefreshTurretFocusPage();
            RefreshBodycamFocusSlot();
            RefreshFocusDockLayout();
            UpdatePageIndicator(QuadCameraAssignment.CurrentPage, QuadCameraAssignment.TotalPages);
            PaneHighlight.SetActive(ActiveSlot);
            SyncCameraAudioProxy();
            UpdateActivePaneReticle();
            CCTVVanillaMonitorDisplay.NotifyActiveFeedChanged();
            RefreshOperatorStats(force: true);

            if (!_turretPageActive)
                QuadCameraAssignment.WakeRender();
        }

        private static void SetActiveSlot(int slot)
        {
            bool feedChanged = _turretPageActive || _activeFeedKind != SurveillanceFeedKind.Facility;
            _turretPageActive = false;
            _activeFeedKind = SurveillanceFeedKind.Facility;
            _activeBodycamPlayerSlot = -1;
            ActiveSlot = 0;
            OpenBodyCamsCompat.SetFocusRenderingActive(false);
            PaneHighlight.SetActive(ActiveSlot);
            ApplyActivePaneLayout();
            HideCameraScanOverlay();
            RefreshTurretFocusPage();
            RefreshBodycamFocusSlot();
            UpdatePageIndicator(QuadCameraAssignment.CurrentPage, QuadCameraAssignment.TotalPages);
            UpdateActivePaneReticle();
            SyncCameraAudioProxy();
            CCTVVanillaMonitorDisplay.NotifyActiveFeedChanged();
            ApplyZoomToActiveCamera();
            if (IsFocused && feedChanged)
            {
                PlaySwitchCameraSfx();
                QuadCameraAssignment.RequestWakeAllSlots();
                CCTVStationEvents.RaiseCameraSelected(ActiveSlot);
            }

            // Replicate to observer clients so CCTVCameraThrottle can keep the
            // right facility camera awake on their machines too (no-ops when we
            // are not the operator, see NotifyLocalActiveSlotChanged).
            CCTVMonitorFeedSync.NotifyLocalActiveSlotChanged(ActiveSlot);
        }

        private static bool SelectNormalFeed(int globalIndex, bool playFeedback)
        {
            GetNormalFeedCount(out int bodycamCount, out int facilityCount);
            if (!SurveillanceFeedOrder.TryResolve(
                    globalIndex,
                    bodycamCount,
                    facilityCount,
                    out SurveillanceFeedAddress address))
            {
                return false;
            }

            int previousGlobal = GetCurrentNormalFeedIndex(bodycamCount, facilityCount);
            bool wasTurret = IsTurretPageActive;
            _turretPageActive = false;
            ActiveSlot = 0;

            if (address.Kind == SurveillanceFeedKind.Bodycam)
            {
                if (!OpenBodyCamsCompat.TryGetPlayerSlotAtFeedIndex(address.LocalIndex, out int playerSlot))
                    return false;
                _activeFeedKind = SurveillanceFeedKind.Bodycam;
                _activeBodycamPlayerSlot = playerSlot;
            }
            else
            {
                _activeFeedKind = SurveillanceFeedKind.Facility;
                _activeBodycamPlayerSlot = -1;
                QuadCameraAssignment.GoToPage(address.LocalIndex);
            }

            OpenBodyCamsCompat.SetFocusRenderingActive(IsBodycamFeedActive);
            CCTVMonitorFeedSync.NotifyLocalActiveSlotChanged(ActiveSlot);
            CCTVVanillaMonitorDisplay.NotifyActiveFeedChanged();
            HideCameraScanOverlay();
            ApplyActivePaneLayout();
            RefreshTurretFocusPage();
            RefreshBodycamFocusSlot();
            RefreshFocusDockLayout();
            UpdatePageIndicator(QuadCameraAssignment.CurrentPage, QuadCameraAssignment.TotalPages);
            PaneHighlight.SetActive(ActiveSlot);
            SyncCameraAudioProxy();
            ApplyZoomToActiveCamera();
            UpdateActivePaneReticle();
            RefreshOperatorStats(force: true);
            BoostStationCameraRenderCadence(0.25f);

            bool changed = wasTurret || previousGlobal != globalIndex;
            if (IsFocused && playFeedback && changed)
            {
                PlayPageSfx();
                CCTVStationEvents.RaiseActionButtonPressed("camera-page");
                CCTVStationEvents.RaiseCameraSelected(ActiveSlot);
            }
            if (IsFacilityFeedActive)
                QuadCameraAssignment.RequestWakeAllSlots();
            return true;
        }

        private static void NavigateSingleCamera(int delta)
        {
            // Feed navigation owns only the selected feed. SPACE independently
            // owns the physical radar glance and must survive every arrow press.
            if (IsTurretPageActive)
            {
                LeaveTurretPage(delta >= 0);
                return;
            }

            int normalCount = GetNormalFeedCount(out int bodycamCount, out int facilityCount);
            if (normalCount <= 0)
            {
                if (ShipTurretController.IsUnlocked)
                    SetTurretPageActive(true);
                return;
            }

            int current = GetCurrentNormalFeedIndex(bodycamCount, facilityCount);
            if (current < 0)
                current = 0;
            int requested = current + (delta < 0 ? -1 : 1);
            if (requested < 0 || requested >= normalCount)
            {
                if (ShipTurretController.IsUnlocked)
                {
                    SetTurretPageActive(true);
                    return;
                }
                requested = SurveillanceFeedOrder.WrapIndex(requested, normalCount);
            }

            SelectNormalFeed(requested, playFeedback: true);
        }

        private static string GetActiveFeedLabel()
        {
            if (IsTurretPageActive)
                return "TURRET";
            if (IsBodycamFeedActive
                && OpenBodyCamsCompat.TryGetPlayerName(ActiveBodycamFeedIndex, out string playerName))
                return playerName;
            CCTVCamera active = GetActiveCamera();
            return active != null ? active.ResolvedLabel : "NO CAMERA";
        }

        internal static string GetActiveFeedDisplayLabel()
        {
            return GetActiveFeedLabel();
        }

        internal static string GetActiveFeedPageLabel()
        {
            int normalCount = GetNormalFeedCount(out int bodycamCount, out int facilityCount);
            int total = normalCount + (ShipTurretController.IsUnlocked ? 1 : 0);
            int current = IsTurretPageActive
                ? total - 1
                : GetCurrentNormalFeedIndex(bodycamCount, facilityCount);
            return total > 0 && current >= 0
                ? $"{current + 1:00}/{total:00}"
                : "00/00";
        }

        internal static void UpdateCameraLabel(int slot, string labelText, bool alignRight = false)
        {
            if (_overlayCameraLabels == null || slot < 0 || slot >= _overlayCameraLabels.Length) return;
            TextMeshProUGUI label = _overlayCameraLabels[slot];
            Image background = _overlayCameraLabelBackgrounds != null && slot < _overlayCameraLabelBackgrounds.Length
                ? _overlayCameraLabelBackgrounds[slot]
                : null;
            if (label == null) return;

            if (!string.IsNullOrEmpty(labelText))
            {
                label.text = labelText;
                label.enabled = true;
                if (background != null) background.enabled = true;
            }
            else
            {
                label.text = "CAM_--";
                label.enabled = false;
                if (background != null) background.enabled = false;
            }
            ApplyCameraLabelLayout(slot, alignRight);
            UpdateActiveSlotLabel(ActiveSlot);
        }

        internal static void UpdatePageIndicator(int currentPageZeroIndexed, int totalPages)
        {
            if (_overlayPageText == null) return;
            int normalCount = GetNormalFeedCount(out int bodycamCount, out int facilityCount);
            int focusTotal = normalCount + (ShipTurretController.IsUnlocked ? 1 : 0);
            if (focusTotal <= 0)
            {
                _overlayPageText.text = "NO CAMERAS";
                return;
            }
            if (IsTurretPageActive)
            {
                _overlayPageText.text = $"PAGE {focusTotal}/{focusTotal} // TURRET";
                return;
            }

            int current = GetCurrentNormalFeedIndex(bodycamCount, facilityCount);
            _overlayPageText.text = current >= 0
                ? $"PAGE {current + 1}/{focusTotal}"
                : $"PAGE --/{focusTotal}";
        }

        internal static void UpdateActiveSlotLabel(int activeSlot)
        {
            if (_overlayCameraLabels != null)
            {
                for (int i = 0; i < _overlayCameraLabels.Length; i++)
                {
                    TextMeshProUGUI label = _overlayCameraLabels[i];
                    if (label == null) continue;
                    label.color = i == activeSlot ? OVERLAY_ACTIVE_GREEN : Color.white;
                }
            }

            if (_overlayActiveText != null)
            {
                if (IsTurretPageActive)
                {
                    _overlayActiveText.text = "ACTIVE TURRET";
                }
                else if (IsBodycamFeedActive)
                {
                    _overlayActiveText.text = OpenBodyCamsCompat.TryGetPlayerName(
                        ActiveBodycamFeedIndex,
                        out string playerName)
                        ? "ACTIVE BODYCAM // " + playerName
                        : "ACTIVE BODYCAM";
                }
                else
                {
                    CCTVCamera active = QuadCameraAssignment.GetBoundCamera(activeSlot);
                    _overlayActiveText.text = active != null
                        ? "ACTIVE " + active.ResolvedLabel
                        : "ACTIVE EMPTY";
                }
            }
            RadarOverlay.UpdateHighlights();
            UpdateActivePaneReticle();
            SyncCameraAudioProxy();
        }

        private static void RefreshBodycamFocusSlot()
        {
            if (_overlayRoot == null || !_overlayRoot.activeInHierarchy)
                return;
            if (!OpenBodyCamsCompat.IsLoaded || _overlayRawImages == null || !IsBodycamFeedActive)
                return;

            const int slot = 0;
            RawImage image = _overlayRawImages[slot];
            if (image == null) return;

            if (OpenBodyCamsCompat.TryGetFocusTexture(out Texture bodycamTexture))
                image.texture = bodycamTexture;
            else
                image.texture = Texture2D.blackTexture;

            string label = OpenBodyCamsCompat.TryGetPlayerName(
                ActiveBodycamFeedIndex,
                out string playerName)
                ? playerName
                : "BODYCAM";
            UpdateCameraLabel(slot, label, alignRight: true);
        }

        private static void RefreshTurretFocusPage()
        {
            if (_overlayRoot == null || !_overlayRoot.activeInHierarchy)
                return;
            if (_overlayRawImages == null) return;

            if (!IsTurretPageActive)
            {
                for (int slot = 0; slot < _overlayRawImages.Length; slot++)
                {
                    if (_overlayRawImages[slot] != null && !_overlayRawImages[slot].gameObject.activeSelf)
                        _overlayRawImages[slot].gameObject.SetActive(true);
                    if (OverlayOutlines != null && slot < OverlayOutlines.Length && OverlayOutlines[slot] != null && !OverlayOutlines[slot].gameObject.activeSelf)
                        OverlayOutlines[slot].gameObject.SetActive(true);
                    if (_overlayCameraLabelBackgrounds != null && slot < _overlayCameraLabelBackgrounds.Length && _overlayCameraLabelBackgrounds[slot] != null && !_overlayCameraLabelBackgrounds[slot].gameObject.activeSelf)
                        _overlayCameraLabelBackgrounds[slot].gameObject.SetActive(true);

                    if (IsBodycamFeedActive && slot == 0)
                        continue;

                    Texture texture = QuadMonitor.GetCurrentDisplayTexture(slot);
                    if (_overlayRawImages[slot] != null && texture != null)
                        _overlayRawImages[slot].texture = texture;

                    CCTVCamera camera = QuadCameraAssignment.GetBoundCamera(slot);
                    UpdateCameraLabel(slot, camera != null ? camera.ResolvedLabel : null);
                }
                return;
            }

            for (int slot = 0; slot < _overlayRawImages.Length; slot++)
            {
                bool visible = slot == 0;
                if (_overlayRawImages[slot] != null)
                    _overlayRawImages[slot].gameObject.SetActive(visible);
                if (OverlayOutlines != null && slot < OverlayOutlines.Length && OverlayOutlines[slot] != null)
                    OverlayOutlines[slot].gameObject.SetActive(visible);
                if (_overlayCameraLabelBackgrounds != null && slot < _overlayCameraLabelBackgrounds.Length && _overlayCameraLabelBackgrounds[slot] != null)
                    _overlayCameraLabelBackgrounds[slot].gameObject.SetActive(visible);
            }

            if (_overlayRawImages[0] != null)
                _overlayRawImages[0].texture = ShipTurretController.ViewTexture ?? Texture2D.blackTexture;
            UpdateCameraLabel(0, "TURRET");
        }

        private static void TickStationRadarZoomInput()
        {
            if (IsStationEditInputActive())
                return;
            if (!IsStationRadarLookHeld() || CCTVOperatorStation.IsDebugPlacementActive)
                return;

            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f)
                return;

            VanillaRadarFeed.AdjustZoom(scroll / 120f);
        }


    }
}
