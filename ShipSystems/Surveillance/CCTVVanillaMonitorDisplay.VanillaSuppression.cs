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
        private static void SuppressVanillaLowerMonitorFeeds()
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null)
                return;

            // #597 — with GeneralImprovements' better monitors the vanilla lower monitors are
            // already hidden (MeshRenderer/Collider disabled, objects kept), and
            // StartOfRound.mapScreen now drives GI's VISIBLE map surface,
            // MonitorGroup/Monitors/BigMiddle/MScreen. Disabling that MCR and its cameras —
            // or the level description and video reel feeding GI's screens — would blank
            // surfaces the player can see and that CCTV does not own. CCTV takes its own GI
            // screens instead, so there is nothing on the vanilla wall left to suppress.
            if (IsGeneralImprovementsMonitorWallActive())
            {
                if (_suppressionCaptured)
                    RestoreVanillaLowerMonitorFeeds();
                return;
            }

            if (!_suppressionCaptured)
            {
                _suppressionCaptured = true;
                _levelDescriptionWasEnabled = sor.screenLevelDescription != null && sor.screenLevelDescription.enabled;

                VideoPlayer reel = sor.screenLevelVideoReel;
                _videoReelWasEnabled = reel != null && reel.enabled;
                _videoReelWasPlaying = reel != null && reel.isPlaying;

                _mapScreenWasEnabled = sor.mapScreen != null && sor.mapScreen.enabled;
                TryCaptureMapScreenRawImage();
                CaptureLowerLeftGraphics();
            }

            if (sor.screenLevelDescription != null)
                sor.screenLevelDescription.enabled = false;
            SetSuppressedLowerLeftGraphics(enabled: false);

            if (sor.screenLevelVideoReel != null)
            {
                if (sor.screenLevelVideoReel.isPlaying)
                    sor.screenLevelVideoReel.Stop();
                sor.screenLevelVideoReel.enabled = false;
            }

            if (sor.mapScreen != null)
            {
                sor.mapScreen.enabled = false;
                // The disabled MCR can no longer drive its camera, so whatever
                // enabled-state the camera had freezes. Frozen-TRUE = the vanilla
                // MapCamera does a full HDRP render every frame for the entire CCTV
                // session (confirmed by the 2026-06-12 focus diagnostics: MapCamera
                // enabled while focused). Vanilla re-manages cam.enabled on the
                // first MCR Update after restore.
                try
                {
                    if (sor.mapScreen.cam != null && sor.mapScreen.cam.enabled)
                    {
                        sor.mapScreen.cam.enabled = false;
                        if (!_loggedMapCameraFrozenOff)
                        {
                            _loggedMapCameraFrozenOff = true;
                            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Disabled frozen-on vanilla MapCamera while CCTV owns the lower monitors.");
                        }
                    }
                }
                catch { }
            }

            if (_mapScreenRawImage != null && _leftRenderTexture != null)
                _mapScreenRawImage.texture = _leftRenderTexture;
        }

        private static void ReassertSuppressedLowerMonitorFeedsFast()
        {
            if (!_suppressionCaptured)
                return;

            StartOfRound sor = StartOfRound.Instance;
            if (sor == null)
                return;

            try
            {
                if (sor.screenLevelDescription != null && sor.screenLevelDescription.enabled)
                    sor.screenLevelDescription.enabled = false;

                SetSuppressedLowerLeftGraphics(enabled: false);

                VideoPlayer reel = sor.screenLevelVideoReel;
                if (reel != null)
                {
                    if (reel.isPlaying)
                        reel.Stop();
                    if (reel.enabled)
                        reel.enabled = false;
                }

                ManualCameraRenderer mapScreen = sor.mapScreen;
                if (mapScreen != null)
                {
                    if (mapScreen.enabled)
                        mapScreen.enabled = false;
                    if (mapScreen.cam != null && mapScreen.cam.enabled)
                        mapScreen.cam.enabled = false;
                    if (mapScreen.mapCamera != null && mapScreen.mapCamera.enabled)
                        mapScreen.mapCamera.enabled = false;
                }

                if (_mapScreenRawImage != null && _leftRenderTexture != null && _mapScreenRawImage.texture != _leftRenderTexture)
                    _mapScreenRawImage.texture = _leftRenderTexture;
            }
            catch { }
        }

        private static void ReassertSuppressedLowerMonitorFeedsFrame()
        {
            if (!_suppressionCaptured)
                return;

            StartOfRound sor = StartOfRound.Instance;
            if (sor == null)
                return;

            try
            {
                if (sor.screenLevelDescription != null && sor.screenLevelDescription.enabled)
                    sor.screenLevelDescription.enabled = false;

                SetSuppressedLowerLeftGraphics(enabled: false);

                VideoPlayer reel = sor.screenLevelVideoReel;
                if (reel != null)
                {
                    if (reel.enabled)
                        reel.enabled = false;
                    if (reel.isPlaying)
                        reel.Stop();
                }

                ManualCameraRenderer mapScreen = sor.mapScreen;
                if (mapScreen != null)
                {
                    if (mapScreen.enabled)
                        mapScreen.enabled = false;
                    if (mapScreen.cam != null && mapScreen.cam.enabled)
                        mapScreen.cam.enabled = false;
                    if (mapScreen.mapCamera != null && mapScreen.mapCamera.enabled)
                        mapScreen.mapCamera.enabled = false;
                }

                if (_mapScreenRawImage != null && _leftRenderTexture != null && _mapScreenRawImage.texture != _leftRenderTexture)
                    _mapScreenRawImage.texture = _leftRenderTexture;
            }
            catch { }
        }

        private static void RestoreVanillaLowerMonitorFeeds()
        {
            if (!_suppressionCaptured)
                return;

            StartOfRound sor = StartOfRound.Instance;
            if (sor != null)
            {
                try
                {
                    if (sor.screenLevelDescription != null)
                        sor.screenLevelDescription.enabled = _levelDescriptionWasEnabled;

                    if (sor.screenLevelVideoReel != null)
                    {
                        sor.screenLevelVideoReel.enabled = _videoReelWasEnabled;
                        if (_videoReelWasPlaying)
                            sor.screenLevelVideoReel.Play();
                    }

                    if (sor.mapScreen != null)
                        sor.mapScreen.enabled = _mapScreenWasEnabled;
                }
                catch { }
            }

            if (_mapScreenRawImage != null)
            {
                try { _mapScreenRawImage.texture = _mapScreenRawImageTexture; } catch { }
            }
            RestoreSuppressedLowerLeftGraphics();

            _mapScreenRawImage = null;
            _mapScreenRawImageTexture = null;
            _lowerLeftSuppressedGraphics = null;
            _lowerLeftSuppressedGraphicStates = null;
            _suppressionCaptured = false;
            _loggedMapCameraFrozenOff = false;
        }

        private static void CaptureLowerLeftGraphics()
        {
            RectTransform target = ResolveLowerLeftMonitorTarget();
            if (target == null)
                return;

            Graphic[] graphics = target.GetComponentsInChildren<Graphic>(includeInactive: true);
            if (graphics == null || graphics.Length == 0)
                return;

            _lowerLeftSuppressedGraphics = graphics;
            _lowerLeftSuppressedGraphicStates = new bool[graphics.Length];
            for (int i = 0; i < graphics.Length; i++)
                _lowerLeftSuppressedGraphicStates[i] = graphics[i] != null && graphics[i].enabled;
        }

        private static void SetSuppressedLowerLeftGraphics(bool enabled)
        {
            if (_lowerLeftSuppressedGraphics == null)
                return;

            for (int i = 0; i < _lowerLeftSuppressedGraphics.Length; i++)
            {
                Graphic graphic = _lowerLeftSuppressedGraphics[i];
                if (graphic == null)
                    continue;

                if (graphic.transform != null &&
                    graphic.transform.name.StartsWith("LethalCCTV_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (graphic.enabled != enabled)
                    graphic.enabled = enabled;
            }
        }

        private static void RestoreSuppressedLowerLeftGraphics()
        {
            if (_lowerLeftSuppressedGraphics == null || _lowerLeftSuppressedGraphicStates == null)
                return;

            int count = Mathf.Min(_lowerLeftSuppressedGraphics.Length, _lowerLeftSuppressedGraphicStates.Length);
            for (int i = 0; i < count; i++)
            {
                Graphic graphic = _lowerLeftSuppressedGraphics[i];
                if (graphic != null)
                    graphic.enabled = _lowerLeftSuppressedGraphicStates[i];
            }
        }

        private static void TryCaptureMapScreenRawImage()
        {
            _mapScreenRawImage = null;
            _mapScreenRawImageTexture = null;

            GameObject mapScreenUi = GameObject.Find("Systems/GameSystems/ItemSystems/MapScreenUI");
            if (mapScreenUi == null)
                return;

            RawImage[] images = mapScreenUi.GetComponentsInChildren<RawImage>(includeInactive: true);
            for (int i = 0; i < images.Length; i++)
            {
                RawImage image = images[i];
                if (image == null || image.texture == null)
                    continue;

                if (string.Equals(image.texture.name, "MapScreenVideo", StringComparison.OrdinalIgnoreCase))
                {
                    _mapScreenRawImage = image;
                    _mapScreenRawImageTexture = image.texture;
                    return;
                }
            }
        }

        /// <summary>Blank both owned monitors while the ship is unpowered. The
        /// monitor cameras are manually rendered (enabled=false), so one clear
        /// holds until power returns — no per-frame work beyond the gate.</summary>
        private static void ApplyMonitorPowerBlank()
        {
            VanillaRadarFeed.SetActive(false);
            if (_powerBlanked)
                return;

            _powerBlanked = true;
            ClearRenderTextureToBlack(_leftRenderTexture);
            ClearRenderTextureToBlack(_rightRenderTexture);
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Ship power offline; CCTV feed and radar monitors blanked.");
        }

        private static void ClearMonitorPowerBlank()
        {
            if (!_powerBlanked)
                return;

            _powerBlanked = false;
            // Force a full repaint on the first powered frame: the cached
            // schedule would otherwise leave the cleared textures on screen
            // until the next ambient compositor interval.
            _compositorDirty = true;
            _rightCompositorDirty = true;
            _forceFastMaterialVerify = true;
            _lastCompositorRenderAt = 0f;
            _nextCompositorRenderAt = 0f;
            _nextRightCompositorRenderAt = 0f;
            _nextBindMaintenanceAt = 0f;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Ship power restored; CCTV feed and radar monitors resumed.");
        }

        private static void ClearRenderTextureToBlack(RenderTexture texture)
        {
            if (texture == null)
                return;

            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                GL.Clear(clearDepth: true, clearColor: true, backgroundColor: Color.black);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] CCTV monitor power blank failed: {ex.Message}");
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static void SetActive(bool active)
        {
            if (_lowerLeftRoot != null && _lowerLeftRoot.activeSelf != active)
                _lowerLeftRoot.SetActive(active);

            if (!active)
            {
                VanillaRadarFeed.SetActive(false);
                // Cheap no-op when nothing is held — this runs every frame while
                // CCTV mode is off.
                if (!_suppressionCaptured &&
                    !_lowerLeftBinding.IsBound &&
                    !_lowerRightBinding.IsBound &&
                    !_lowerRightAuxBinding.IsBound)
                {
                    return;
                }

                RestoreVanillaLowerMonitorFeeds();
                // Reverse bind order: aux saved its slot AFTER left/right bound, so
                // it must release first for the originals to land back intact.
                _lowerRightAuxBinding.Restore();
                _lowerRightBinding.Restore();
                _lowerLeftBinding.Restore();
                _nextBindMaintenanceAt = 0f;
                _nextCompositorRenderAt = 0f;
                _nextReassertAt = 0f;
                _nextFullSuppressionAt = 0f;
                _nextFastMaterialVerifyAt = 0f;
                _forceFastMaterialVerify = false;
                _lastCompositorRenderAt = 0f;
                _compositorDirty = true;
            }
        }

    }
}
