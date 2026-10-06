using System;
using System.Collections.Generic;
using Y4NGZCompany.Facility.Cameras;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class QuadMonitor
    {
        /// <summary>
        /// Reset slot's material texture to its canonical per-slot RT (QuadRTs[slotIndex]).
        /// Called by QuadCameraAssignment when a real camera is bound to the slot, undoing
        /// any prior empty-fallback binding.
        /// </summary>
        internal static void BindRTToSlot(int slotIndex)
        {
            if (QuadMaterials == null || slotIndex < 0 || slotIndex >= QuadMaterials.Length) return;
            if (QuadRTs == null || slotIndex >= QuadRTs.Length) return;
            Material mat = QuadMaterials[slotIndex];
            RenderTexture rt = QuadRTs[slotIndex];
            if (mat == null || rt == null) return;
            BindDisplayTexture(mat, rt);
            _slotCurrentDisplayTex[slotIndex] = rt;
            MonitorFocus.NotifySlotDisplayTextureChanged(slotIndex, rt);
            CCTVVanillaMonitorDisplay.NotifySlotTextureChanged(slotIndex, rt);
        }

        /// <summary>
        /// Bind slot's material to the shared static black RT. Used on last-page underflow
        /// where the page has fewer than 4 cameras. Visually unambiguous (solid black =
        /// "no camera assigned"), survives shader idiosyncrasies a null-texture binding
        /// might surface (per Phase 1.5a SPEC §5 R3 chosen approach (b)).
        /// </summary>
        internal static void BindEmptyToSlot(int slotIndex)
        {
            if (QuadMaterials == null || slotIndex < 0 || slotIndex >= QuadMaterials.Length) return;
            EnsureEmptySlotBlackRT();
            Material mat = QuadMaterials[slotIndex];
            if (mat == null || _emptySlotBlackRT == null) return;
            BindDisplayTexture(mat, _emptySlotBlackRT);
            // Mirror the binding to the focus overlay so an empty slot does NOT show
            // the previously-baked QuadRTs frame in focus mode (latent pre-1.7b bug
            // promoted to a hard fix by the dual-RT split). Wall and overlay flip
            // to the black RT on the same frame.
            _slotCurrentDisplayTex[slotIndex] = _emptySlotBlackRT;
            MonitorFocus.NotifySlotDisplayTextureChanged(slotIndex, _emptySlotBlackRT);
            CCTVVanillaMonitorDisplay.NotifySlotTextureChanged(slotIndex, _emptySlotBlackRT);
            SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] SelectCameras: slot {slotIndex} bound to empty-fallback black RT.");
        }

        internal static void SetPhysicalDisplayBlanked(bool blanked)
        {
            if (QuadMaterials == null) return;
            if (blanked) EnsureEmptySlotBlackRT();

            for (int i = 0; i < QuadMaterials.Length; i++)
            {
                Material mat = QuadMaterials[i];
                if (mat == null) continue;

                Texture texture = blanked
                    ? _emptySlotBlackRT
                    : GetCurrentDisplayTexture(i);
                if (texture == null) continue;

                BindDisplayTexture(mat, texture);
            }
        }

        internal static void SetFocusScreenTexture(Texture texture, bool active)
        {
            if (_focusScreen == null || _focusScreenMaterial == null)
            {
                if (active)
                {
                    SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] Physical focus screen requested but focus screen quad/material is unavailable.");
                }
                return;
            }

            if (active && texture != null)
            {
                BindDisplayTexture(_focusScreenMaterial, texture);
                _focusScreen.SetActive(true);
                if (_physicalMonitorVisualsHidden)
                    SetRendererTreeEnabled(_focusScreen, true);
                SetFocusChromeVisible(true);
                if (!_focusScreenLogged)
                {
                    _focusScreenLogged = true;
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] Physical focus screen active: texture='{texture.name}' size={DescribeTextureSize(texture)} " +
                        $"quadScale={_focusScreen.transform.localScale} localPos={_focusScreen.transform.localPosition} property='{VanillaShaderInfo.MainTexturePropertyName}'.");
                }
            }
            else
            {
                _focusScreen.SetActive(false);
                SetFocusChromeVisible(false);
                if (_physicalMonitorVisualsHidden)
                    SetRendererTreeEnabled(_focusScreen, false);
                BindDisplayTexture(_focusScreenMaterial, Texture2D.blackTexture);
            }
        }

        /// <summary>
        /// Returns the texture currently bound to slot's wall material (display RT
        /// when bound, shared empty-fallback black RT when empty). Used by
        /// MonitorFocus.EnterFocus and CreateOverlayQuadrant to bind the overlay
        /// RawImage to the same texture the wall material shows — keeping the two
        /// consumers in lockstep across paging.
        /// </summary>
        internal static Texture GetCurrentDisplayTexture(int slot)
        {
            if (_slotCurrentDisplayTex == null) return null;
            if (slot < 0 || slot >= _slotCurrentDisplayTex.Length) return null;
            return _slotCurrentDisplayTex[slot];
        }

        internal static Transform FocusViewAnchor => CCTVOperatorStation.FocusViewAnchor != null
            ? CCTVOperatorStation.FocusViewAnchor
            : (_runtimeFocusViewAnchor != null
                ? _runtimeFocusViewAnchor
                : FindChildRecursive(MonitorRoot != null ? MonitorRoot.transform : null, "CCTVFocusViewAnchor"));

        /// <summary>
        /// Phase 1.7 — set the slot's CAM NN label to the bound camera's spawn index
        /// (zero-padded to 2 digits), or hide the label entirely when cameraIndex is null
        /// (empty slot / underflow). Caller is QuadCameraAssignment.SelectCameras; called
        /// once per slot per paging change. Silent no-op when labels were skipped at
        /// construction (no TMP font asset resolved).
        /// </summary>
        internal static void UpdateCameraLabel(int slot, CCTVCamera camera)
        {
            if (slot < 0) return;
            string labelText = QuadCameraAssignment.BuildCameraLabel(camera);
            MonitorFocus.UpdateCameraLabel(slot, labelText);
            if (_cameraLabels == null || slot < 0 || slot >= _cameraLabels.Length) return;
            TextMeshPro label = _cameraLabels[slot];
            if (label == null) return;
            if (!string.IsNullOrEmpty(labelText))
            {
                label.text = labelText;
                if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
            }
            else if (label.gameObject.activeSelf)
            {
                label.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Phase 1.7b — writes the current night-vision and analog-artifact config
        /// values into the shared bake material. Silent no-op when the bake is inactive
        /// (bundle missing → BakeMaterial is null, raw passthrough is the display path).
        /// Called at Spawn time and from the config SettingChanged path; idempotent.
        ///
        /// 0.0.11 — every write goes through Material.HasProperty first. That guard is
        /// what lets this method ship ahead of, or behind, any given shader bundle: the
        /// ten analog knobs below were added to the shader in 0.0.11, and against an
        /// older bundle they are simply skipped rather than throwing or logging.
        /// </summary>
        internal static void ApplyNightVisionParams()
        {
            Material m = NightVisionBaker.BakeMaterial;
            if (m == null) return;
            LethalCCTVConfig cfg = SurveillanceBootstrap.Config;
            float gain = cfg != null ? cfg.NightVisionGain.Value : 2.0f;
            float grayscaleOn = (cfg != null && cfg.NightVisionEnabled.Value) ? 1.0f : 0.0f;
            float flipY = (cfg == null || cfg.NightVisionFlipY.Value) ? 1.0f : 0.0f;

            Push(m, NightVisionShader.GainPropertyId, gain);
            Push(m, NightVisionShader.GrayscaleEnabledPropertyId, grayscaleOn);
            Push(m, NightVisionShader.FlipYPropertyId, flipY);

            Push(m, NightVisionShader.ColorRetentionPropertyId, cfg != null ? cfg.FeedColorRetention.Value : 0.35f);
            Push(m, NightVisionShader.ScanlineStrengthPropertyId, cfg != null ? cfg.FeedScanlineStrength.Value : 0.018f);
            Push(m, NightVisionShader.NoiseStrengthPropertyId, cfg != null ? cfg.FeedNoiseStrength.Value : 0.006f);
            Push(m, NightVisionShader.VignetteStrengthPropertyId, cfg != null ? cfg.FeedVignetteStrength.Value : 0f);
            Push(m, NightVisionShader.ChromaAberrationPropertyId, cfg != null ? cfg.FeedChromaAberration.Value : 0f);
            Push(m, NightVisionShader.RollBarStrengthPropertyId, cfg != null ? cfg.FeedRollBarStrength.Value : 0f);
            Push(m, NightVisionShader.RollBarSpeedPropertyId, cfg != null ? cfg.FeedRollBarSpeed.Value : 0.12f);
            Push(m, NightVisionShader.CurvatureStrengthPropertyId, cfg != null ? cfg.FeedCurvatureStrength.Value : 0f);
            Push(m, NightVisionShader.InterlaceStrengthPropertyId, cfg != null ? cfg.FeedInterlaceStrength.Value : 0f);
            Push(m, NightVisionShader.DropoutStrengthPropertyId, cfg != null ? cfg.FeedDropoutStrength.Value : 0f);

            static void Push(Material mat, int propertyId, float value)
            {
                if (mat.HasProperty(propertyId))
                {
                    mat.SetFloat(propertyId, value);
                }
            }
        }

        /// <summary>
        /// Page indicator update. Called by QuadCameraAssignment.GoToPage and on initial
        /// Assign. Zero-total renders "No Cameras" per Phase 1.5a SPEC Q4.
        /// </summary>
        internal static void UpdatePageIndicator(int currentPageZeroIndexed, int totalPages)
        {
            string text = totalPages > 0
                ? $"Page {currentPageZeroIndexed + 1} / {totalPages}"
                : "No Cameras";
            if (_pageIndicator != null)
            {
                _pageIndicator.text = text;
            }
            else if (_pageIndicatorLegacy != null)
            {
                _pageIndicatorLegacy.text = text;
            }
            MonitorFocus.UpdatePageIndicator(currentPageZeroIndexed, totalPages);
        }

    }
}
