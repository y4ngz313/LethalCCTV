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
        internal static void Spawn()
        {
            // Idempotent across round-to-round transitions — QuadCameraAssignment.Assign
            // rebinds new cameras to the same RTs.
            if (MonitorRoot != null)
            {
                return;
            }

            if (!CCTVTerminalUnlockable.IsPurchased())
            {
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] QuadMonitor.Spawn skipped; CCTV terminal is not purchased.");
                return;
            }

            GameObject hangar = GameObject.Find("Environment/HangarShip");
            if (hangar == null)
            {
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] QuadMonitor.Spawn: HangarShip not found — skipping spawn.");
                return;
            }

            VanillaShaderInfo.EnsureProbed();
            if (!VanillaShaderInfo.ProbeSucceeded)
            {
                // Fatal-skip per Phase 1.5 PLAN amendment 1. Cameras keep spawning, focus
                // mode just has nothing to focus on. The ERROR log emitted by EnsureProbed
                // is the diagnostic signal.
                return;
            }

            // Build into locals so a partial-construction throw can't leave the static
            // fields half-set (carried forward from Phase 1).
            GameObject localRoot = null;
            RenderTexture[] localRTs = null;
            RenderTexture[] localRawRTs = null;
            Material[] localMaterials = null;
            try
            {
                Quaternion worldRot = SurveillanceBootstrap.Config.MonitorRotation;
                Vector3 worldPos = SurveillanceBootstrap.Config.MonitorPosition + (worldRot * Vector3.forward * MONITOR_FORWARD_OFFSET_M);

                localRoot = new GameObject("LethalCCTV_QuadMonitor");
                localRoot.transform.SetParent(hangar.transform, worldPositionStays: false);
                localRoot.transform.localPosition = hangar.transform.InverseTransformPoint(worldPos);
                localRoot.transform.localRotation = worldRot;
                localRoot.transform.localScale = Vector3.one * MONITOR_ROOT_SCALE;

                Mesh quadMesh = GetQuadMesh();
                if (quadMesh == null)
                {
                    SurveillanceBootstrap.Log.LogError("[LethalCCTV] QuadMonitor.Spawn: failed to obtain Quad mesh from Resources or primitive fallback. Aborting.");
                    CleanupPartialConstruction(localRoot, localRTs, localRawRTs, localMaterials);
                    return;
                }

                EnsureEmptySlotBlackRT();

                // Subscribe before publishing targets or binding cameras. The empty
                // registration map ignores unrelated cameras during construction.
                NightVisionBaker.TryStart();

                int rtWidth = ResolveRenderTextureWidth();
                int rtHeight = ResolveRenderTextureHeight(rtWidth);

                localRTs = new RenderTexture[4];
                localRawRTs = new RenderTexture[4];
                localMaterials = new Material[4];
                Material[] localOutlineMaterials = new Material[4];
                var rawFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                    ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                for (int i = 0; i < 4; i++)
                {
                    var rt = new RenderTexture(rtWidth, rtHeight, 16, RenderTextureFormat.ARGB32)
                    {
                        name = $"LethalCCTV_Quad{i}_RT",
                        useDynamicScale = false,
                        autoGenerateMips = false,
                        filterMode = FilterMode.Bilinear,
                    };
                    rt.Create();
                    localRTs[i] = rt;

                    var rawRt = new RenderTexture(rtWidth, rtHeight, 16, rawFormat, RenderTextureReadWrite.Linear)
                    {
                        name = $"LethalCCTV_Quad{i}_RawRT",
                        useDynamicScale = false,
                        autoGenerateMips = false,
                        filterMode = FilterMode.Bilinear,
                    };
                    rawRt.Create();
                    localRawRTs[i] = rawRt;

                    localMaterials[i] = CreateQuadrant(localRoot.transform, i, quadMesh, rt);
                    localOutlineMaterials[i] = CreateQuadrantOutline(localRoot.transform, i, quadMesh);
                }
                CreateScreenBacking(localRoot.transform, quadMesh);
                CreateFocusScreen(localRoot.transform, quadMesh);

                CreateRuntimeInteractionAnchors(localRoot.transform);

                _cameraLabels = new TextMeshPro[4];
                TMP_FontAsset labelFont = ResolveTMPFontAsset();
                if (labelFont != null)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        _cameraLabels[i] = CreateCameraLabel(localRoot.transform, i, labelFont);
                    }
                }
                else
                {
                    SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] CAM labels: TMP_FontAsset unavailable — camera labels skipped this load.");
                }

                CreatePageIndicator(localRoot.transform);

                // Phase 1.7b — seed the bake material with current config values
                // before any render fires. Silent no-op when bake is inactive (the
                // wall material is plain HDRP/Unlit with no _Gain / _GrayscaleEnabled
                // properties to write to in the raw-fallback path).
                ApplyNightVisionParams();

                HidePhysicalMonitorVisuals(localRoot);

                // Atomic publish.
                MonitorRoot = localRoot;
                QuadRTs = localRTs;
                RawRTs = localRawRTs;
                QuadMaterials = localMaterials;
                QuadOutlineMaterials = localOutlineMaterials;

                // Seed the per-slot current-display-texture mirror to match the
                // wall material bindings made in CreateQuadrant (each slot bound
                // to its QuadRTs[i]). MonitorFocus.NotifySlotDisplayTextureChanged
                // is intentionally NOT called here — the overlay RawImages re-read
                // GetCurrentDisplayTexture lazily in EnterFocus, and SelectCameras
                // (about to run from Assign) will fire mirror calls per slot anyway.
                for (int i = 0; i < 4; i++) _slotCurrentDisplayTex[i] = localRTs[i];

                SurveillanceBootstrap.Log.LogInfo(
                    $"[LethalCCTV] QuadMonitor spawned at {worldPos} rot={worldRot.eulerAngles} " +
                    $"with 4 Quad meshes ({QUADRANT_WIDTH_M}m × {QUADRANT_HEIGHT_M}m each) " +
                    $"using cloned shader '{VanillaShaderInfo.ShaderName}' and texture property '{VanillaShaderInfo.MainTexturePropertyName}'.");

                // Initial indicator render. QuadCameraAssignment.Assign will overwrite this
                // immediately after Spawn returns; in the brief gap, TotalPages may be 0
                // (no Assign yet), which renders as "No Cameras" — the intentional
                // zero-state per Phase 1.5a Q4.
                UpdatePageIndicator(QuadCameraAssignment.CurrentPage, QuadCameraAssignment.TotalPages);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log.LogError($"[LethalCCTV] QuadMonitor.Spawn failed: {ex}");
                CleanupPartialConstruction(localRoot, localRTs, localRawRTs, localMaterials);
                // Static field _cameraLabels may have been assigned inside the try block
                // before the throw; clear it so it does not hold refs to label
                // GameObjects that were destroyed via the localRoot subtree above.
                _cameraLabels = null;
                _screenBacking = null;
                _focusScreen = null;
                _focusChromeRoot = null;
                DestroyMaterial(ref _screenBackingMaterial);
                DestroyMaterial(ref _focusScreenMaterial);
                DestroyFocusChromeMaterials();
            }
        }

        // The Phase 1.2 hot-reload entry point (RepositionFromConfig) retired
        // with the six ship-monitor transform config entries for 1.0 (#575):
        // the anchor is a constant now, so there is nothing left to reload.

        private static int ResolveRenderTextureWidth()
        {
            int configured = SurveillanceBootstrap.Config?.RenderRTWidth?.Value ?? DEFAULT_RT_WIDTH;
            return Mathf.Clamp(configured, MIN_RT_WIDTH, MAX_RT_WIDTH);
        }

        private static int ResolveRenderTextureHeight(int width)
        {
            return Mathf.Max(16, Mathf.RoundToInt(width * 0.75f));
        }

        internal static void Despawn()
        {
            if (MonitorFocus.IsFocused)
            {
                MonitorFocus.ExitFocus();
            }

            // #502 — FIRST action: drop every camera's targetTexture reference
            // before anything below releases or destroys a render texture.
            // Callers used to be expected to Unassign() beforehand; the shutdown
            // path did not, and 10 "RenderTexture destroyed while in use" errors
            // came out of it. Unassign is idempotent, so a caller that already
            // unassigned pays only a null sweep here. Materials, RTs and the
            // monitor root are all still alive at this point, so the empty-slot
            // rebinds inside Unassign resolve against live objects.
            try
            {
                QuadCameraAssignment.Unassign();
            }
            catch (System.Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] QuadMonitor.Despawn: camera unassign failed: {ex.Message}");
            }

            // Phase 1.7b — unsubscribe the bake handler and destroy the bake
            // material BEFORE releasing RTs, so a late-firing endCameraRendering
            // callback cannot dereference RTs that are about to be destroyed.
            // Shutdown is idempotent: safe to call when bake was never started.
            NightVisionBaker.Shutdown();

            if (QuadMaterials != null)
            {
                for (int i = 0; i < QuadMaterials.Length; i++)
                {
                    if (QuadMaterials[i] != null)
                    {
                        UnityEngine.Object.Destroy(QuadMaterials[i]);
                    }
                }
                QuadMaterials = null;
            }

            if (QuadOutlineMaterials != null)
            {
                for (int i = 0; i < QuadOutlineMaterials.Length; i++)
                {
                    if (QuadOutlineMaterials[i] != null)
                    {
                        UnityEngine.Object.Destroy(QuadOutlineMaterials[i]);
                    }
                }
                QuadOutlineMaterials = null;
            }
            DestroyMaterial(ref _screenBackingMaterial);
            DestroyMaterial(ref _focusScreenMaterial);
            _screenBacking = null;
            _focusScreen = null;
            _focusChromeRoot = null;
            DestroyFocusChromeMaterials();
            _focusScreenLogged = false;

            if (QuadRTs != null)
            {
                for (int i = 0; i < QuadRTs.Length; i++)
                {
                    if (QuadRTs[i] != null)
                    {
                        QuadRTs[i].Release();
                        UnityEngine.Object.Destroy(QuadRTs[i]);
                    }
                }
                QuadRTs = null;
            }

            if (RawRTs != null)
            {
                for (int i = 0; i < RawRTs.Length; i++)
                {
                    if (RawRTs[i] != null)
                    {
                        RawRTs[i].Release();
                        UnityEngine.Object.Destroy(RawRTs[i]);
                    }
                }
                RawRTs = null;
            }

            if (_slotCurrentDisplayTex != null)
            {
                for (int i = 0; i < _slotCurrentDisplayTex.Length; i++) _slotCurrentDisplayTex[i] = null;
            }

            if (_emptySlotBlackRT != null)
            {
                _emptySlotBlackRT.Release();
                UnityEngine.Object.Destroy(_emptySlotBlackRT);
                _emptySlotBlackRT = null;
            }

            if (MonitorRoot != null)
            {
                UnityEngine.Object.Destroy(MonitorRoot);
            }

            // Anchors are children of MonitorRoot above; Destroy(MonitorRoot) tears the
            // whole subtree down. Null the statics so a follow-up Spawn can re-create
            // cleanly.
            _runtimeFocusViewAnchor = null;
            _physicalMonitorVisualsHidden = false;

            MonitorRoot = null;
            _pageIndicator = null;
            _pageIndicatorLegacy = null;
            _cameraLabels = null;
        }

    }
}
