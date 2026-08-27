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
        private static void HidePhysicalMonitorVisuals(GameObject root)
        {
            if (root == null)
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = false;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    colliders[i].enabled = false;
            }

            root.SetActive(false);

            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] QuadMonitor physical visuals hidden and root deactivated; render textures remain active for the vanilla monitor station.");
            _physicalMonitorVisualsHidden = true;
        }

        private static void CleanupPartialConstruction(
            GameObject localRoot,
            RenderTexture[] localRTs,
            RenderTexture[] localRawRTs,
            Material[] localMaterials)
        {
            // Phase 1.7b — tear the bake down first so a callback firing mid-cleanup
            // cannot dereference half-released RTs. Shutdown is idempotent and safe
            // to call when TryStart was never invoked (e.g. throw before TryStart).
            NightVisionBaker.Shutdown();

            if (localMaterials != null)
            {
                for (int i = 0; i < localMaterials.Length; i++)
                {
                    if (localMaterials[i] != null)
                    {
                        UnityEngine.Object.Destroy(localMaterials[i]);
                    }
                }
            }
            if (localRTs != null)
            {
                for (int i = 0; i < localRTs.Length; i++)
                {
                    if (localRTs[i] != null)
                    {
                        localRTs[i].Release();
                        UnityEngine.Object.Destroy(localRTs[i]);
                    }
                }
            }
            if (localRawRTs != null)
            {
                for (int i = 0; i < localRawRTs.Length; i++)
                {
                    if (localRawRTs[i] != null)
                    {
                        localRawRTs[i].Release();
                        UnityEngine.Object.Destroy(localRawRTs[i]);
                    }
                }
            }
            if (localRoot != null)
            {
                UnityEngine.Object.Destroy(localRoot);
            }
            _focusChromeRoot = null;
            DestroyFocusChromeMaterials();
        }

        private static Material CreateQuadrant(Transform parent, int slotIndex, Mesh quadMesh, RenderTexture rt)
        {
            // Slot layout: 0=TL, 1=TR, 2=BL, 3=BR. Local offsets in metres around parent
            // origin; small per-slot Z step breaks coplanarity with anything else that
            // ends up parented at the same position (R4 mitigation).
            float xOffset = (slotIndex % 2 == 0) ? -QUADRANT_WIDTH_M / 2f : QUADRANT_WIDTH_M / 2f;
            float yOffset = (slotIndex < 2) ? QUADRANT_HEIGHT_M / 2f : -QUADRANT_HEIGHT_M / 2f;
            float zOffset = LIVE_SCREEN_Z_M + slotIndex * QUADRANT_Z_OFFSET_STEP;

            var go = new GameObject($"Quadrant_{slotIndex}");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(xOffset, yOffset, zOffset);
            go.transform.localRotation = Quaternion.identity;
            // Unity built-in Quad mesh is 1×1 in local XY with +Z normal; per-axis scale
            // gives us the desired world dimensions directly.
            go.transform.localScale = new Vector3(QUADRANT_WIDTH_M, QUADRANT_HEIGHT_M, 1f);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = quadMesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            // R7-critical: clone via Object.Instantiate from sharedMaterial. NEVER read
            // VanillaShaderInfo.SourceMaterial via a .material getter on a renderer (that
            // is the path that would mutate vanilla); we hold a sharedMaterial reference
            // directly and Object.Instantiate produces a standalone clone.
            Material clone = UnityEngine.Object.Instantiate(VanillaShaderInfo.SourceMaterial);
            clone.name = $"LethalCCTV_QuadMat_{slotIndex}";

            // Bind the RT to the probed property name. Texture-property name was selected
            // in VanillaShaderInfo.EnsureProbed based on what the live vanilla shader
            // actually exposes — not hardcoded.
            BindDisplayTexture(clone, rt);

            // 0.0.6 H4 fix — neutralize the inherited vanilla _UnlitColor tint. Vanilla
            // LC's ship-monitor material carries a green CRT tint baked into _UnlitColor;
            // in 0.0.4 the night-vision shader swap bypassed this (custom shader did not
            // sample _UnlitColor). With the bake moved to the RT-writer side in 0.0.5,
            // the wall reverted to HDRP/Unlit and the inherited green now multiplies
            // over our already-filtered baked output. The baked RT IS the desired
            // display; the wall shows it 1:1. Locked regardless of the bake-alpha
            // outcome — HDRP/Unlit is opaque-queue, ignores RT alpha, would still
            // tint a fully-opaque baked feed green.
            if (clone.HasProperty("_UnlitColor"))
            {
                clone.SetColor("_UnlitColor", Color.white);
            }

            // NOTE: HDMaterial.ValidateMaterial does NOT flip _DOUBLESIDED_ON keyword on
            // cloned HDRP/Unlit materials in LC's HDRP version (verified Phase 1.5a Round 1
            // deploy: IsKeywordEnabled stays False despite EnableKeyword + ValidateMaterial).
            // Quads happen to face the viewer correctly via default mesh orientation, so
            // this is technical debt, not a visual bug. Phase 1.6+ may revisit if a future
            // Quad reuse case requires actual double-sided rendering. The diagnostic log
            // line in Spawn remains as the canary for any future change in HDRP keyword
            // behaviour.
            clone.SetFloat("_DoubleSidedEnable", 1f);
            clone.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Off);
            clone.EnableKeyword("_DOUBLESIDED_ON");
            HDMaterial.ValidateMaterial(clone);

            mr.sharedMaterial = clone;
            return clone;
        }

        private static void CreateScreenBacking(Transform parent, Mesh quadMesh)
        {
            _screenBackingMaterial = CreateScreenSurfaceMaterial("LethalCCTV_BlackScreenBacking", Texture2D.blackTexture, Color.black);

            _screenBacking = new GameObject("ScreenBlackBacking");
            _screenBacking.transform.SetParent(parent, worldPositionStays: false);
            _screenBacking.transform.localPosition = new Vector3(0f, 0f, SCREEN_BACKING_Z_M);
            _screenBacking.transform.localRotation = Quaternion.identity;
            _screenBacking.transform.localScale = new Vector3(QUADRANT_WIDTH_M * 2f, QUADRANT_HEIGHT_M * 2f, 1f);

            var mf = _screenBacking.AddComponent<MeshFilter>();
            mf.sharedMesh = quadMesh;

            var mr = _screenBacking.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            mr.sharedMaterial = _screenBackingMaterial;
        }

        private static void CreateFocusScreen(Transform parent, Mesh quadMesh)
        {
            _focusScreenMaterial = CreateScreenSurfaceMaterial("LethalCCTV_PhysicalFocusScreen", Texture2D.blackTexture, Color.white);

            _focusScreen = new GameObject("PhysicalFocusScreen");
            _focusScreen.transform.SetParent(parent, worldPositionStays: false);
            _focusScreen.transform.localPosition = new Vector3(0f, 0f, FOCUS_SCREEN_Z_M);
            _focusScreen.transform.localRotation = Quaternion.identity;
            _focusScreen.transform.localScale = new Vector3(QUADRANT_WIDTH_M * 2f, QUADRANT_HEIGHT_M * 2f, 1f);

            var mf = _focusScreen.AddComponent<MeshFilter>();
            mf.sharedMesh = quadMesh;

            var mr = _focusScreen.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            mr.sharedMaterial = _focusScreenMaterial;
            _focusScreen.SetActive(false);

            CreateFocusChromeOverlay(parent, quadMesh);
        }

    }
}
