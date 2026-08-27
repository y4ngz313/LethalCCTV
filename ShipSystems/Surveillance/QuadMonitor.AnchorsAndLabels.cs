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
        private static void CreateRuntimeInteractionAnchors(Transform parent)
        {
            if (parent == null) return;

            _runtimeFocusViewAnchor = CreateRuntimeAnchor(
                parent,
                "CCTVRuntimeFocusViewAnchor",
                new Vector3(FOCUS_VIEW_CAMERA_X_M, FOCUS_VIEW_CAMERA_Y_M, FOCUS_VIEW_CAMERA_Z_M),
                Quaternion.identity);

            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] Runtime CCTV focus anchor created: focusLocal={_runtimeFocusViewAnchor.localPosition} " +
                $"focusEuler={_runtimeFocusViewAnchor.localEulerAngles}.");
        }

        private static Transform CreateRuntimeAnchor(Transform parent, string name, Vector3 localPosition, Quaternion localRotation)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = localRotation;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        // T8 — per-slot outline mesh. Separate GameObject, separate material,
        // sized slightly larger than the main quad and positioned slightly
        // behind in monitor-local Z. PaneHighlight toggles the material's
        // _UnlitColor (HDRP/Unlit standard property; matches the cloned
        // SourceMaterial's shader) between transparent (inactive) and a
        // CRT-green tint (active). Requirement B: this material is NOT in
        // QuadMaterials and is NOT bound to any RT — it's an independent
        // visual element that cannot bleed into the shared RT path.
        private static Material CreateQuadrantOutline(Transform parent, int slotIndex, Mesh quadMesh)
        {
            float xOffset = (slotIndex % 2 == 0) ? -QUADRANT_WIDTH_M / 2f : QUADRANT_WIDTH_M / 2f;
            float yOffset = (slotIndex < 2) ? QUADRANT_HEIGHT_M / 2f : -QUADRANT_HEIGHT_M / 2f;
            // Sit behind the main quad (smaller Z → farther from viewer given
            // +Z normal). Distance is large enough to beat z-fight tolerance,
            // small enough to be invisible.
            float zOffset = LIVE_SCREEN_Z_M + slotIndex * QUADRANT_Z_OFFSET_STEP - OUTLINE_Z_OFFSET_M;

            var go = new GameObject($"Quadrant_{slotIndex}_Outline");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(xOffset, yOffset, zOffset);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(
                QUADRANT_WIDTH_M + OUTLINE_BORDER_M * 2f,
                QUADRANT_HEIGHT_M + OUTLINE_BORDER_M * 2f,
                1f);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = quadMesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            // Clone SourceMaterial so the outline mesh renders through the same
            // HDRP/Unlit pipeline the main quads use — avoids the "wrong-pipeline
            // whiteout" class of bugs that Phase 1.5 closed. Then strip the
            // texture binding to a 1×1 white so the _UnlitColor tint is the
            // dominant signal, not a sampled scene texture.
            Material clone = UnityEngine.Object.Instantiate(VanillaShaderInfo.SourceMaterial);
            clone.name = $"LethalCCTV_QuadOutline_{slotIndex}";
            BindDisplayTexture(clone, Texture2D.whiteTexture);
            // Inactive on spawn — PaneHighlight.SetActive paints the active slot
            // on EnterFocus and on cycle.
            clone.SetColor("_UnlitColor", Color.clear);
            clone.SetFloat("_DoubleSidedEnable", 0f);
            clone.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Back);
            clone.DisableKeyword("_DOUBLESIDED_ON");
            HDMaterial.ValidateMaterial(clone);

            mr.sharedMaterial = clone;
            return clone;
        }

        private static TextMeshPro CreateCameraLabel(Transform parent, int slotIndex, TMP_FontAsset fontAsset)
        {
            // Match CreateQuadrant's local-offset math so the label tracks each quad's
            // top-left interior corner, with a tiny +Z forward offset so it sits in
            // front of the quad's display mesh (not co-planar, not embedded behind it).
            // Parent is MonitorRoot directly (not the Quadrant) — keeps identity localScale
            // so the Quadrant's non-uniform (1, 1.4, 1) scale doesn't stretch the text.
            float quadX = (slotIndex % 2 == 0) ? -QUADRANT_WIDTH_M / 2f : QUADRANT_WIDTH_M / 2f;
            float quadY = (slotIndex < 2) ? QUADRANT_HEIGHT_M / 2f : -QUADRANT_HEIGHT_M / 2f;
            float quadZ = LIVE_SCREEN_Z_M + slotIndex * QUADRANT_Z_OFFSET_STEP;
            // Quad mesh extends ±0.5 in local mesh coords; after Quadrant.localScale
            // (1, 1.4, 1) it spans ±0.5 horizontally and ±0.7 vertically in MonitorRoot
            // space. Top-left interior corner with a small inset:
            float labelLocalX = quadX - (QUADRANT_WIDTH_M / 2f) + LABEL_INSET_X_M;
            float labelLocalY = quadY + (QUADRANT_HEIGHT_M / 2f) - LABEL_INSET_Y_M;
            float labelLocalZ = quadZ + LABEL_FORWARD_Z_OFFSET_M;

            var go = new GameObject($"CameraLabel_{slotIndex}");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(labelLocalX, labelLocalY, labelLocalZ);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = fontAsset;
            tmp.fontSize = LABEL_FONT_SIZE;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.color = Color.white;
            tmp.text = "CAM_--";
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.sizeDelta = new Vector2(LABEL_BOX_WIDTH_M, LABEL_BOX_HEIGHT_M);
                // Pivot at top-left so the textbox's anchor sits at the label's
                // localPosition — keeps the text glued to the corner regardless of
                // string length.
                rt.pivot = new Vector2(0f, 1f);
            }

            // Cull-off on the per-instance fontMaterial. Setting _CullMode alone left the
            // page indicator invisible under suspicion-2 (cull keyword not honoured); set
            // the matching keyword too. fontMaterial is the auto-clone — never mutate
            // fontSharedMaterial here, that would corrupt the TMP font asset across all
            // labels (and across the page indicator, which holds a separate clone via the
            // same getter).
            if (tmp.fontMaterial != null)
            {
                tmp.fontMaterial.SetFloat("_CullMode", 0f);
                tmp.fontMaterial.EnableKeyword("_FACE_CULLING_OFF");
            }

            // Force the text mesh to build immediately so layout is correct before
            // the first render.
            tmp.ForceMeshUpdate();

            return tmp;
        }

        private static void CreatePageIndicator(Transform parent)
        {
            // Position: centered horizontally, below the 2×2 grid by margin. Grid bottom
            // is at local y = -QUADRANT_HEIGHT_M (each bottom quadrant centered at -0.7m
            // with height 1.4m extending to -1.4m).
            Vector3 localPos = new Vector3(0f, -(QUADRANT_HEIGHT_M + PAGE_INDICATOR_MARGIN_M), 0f);

            var go = new GameObject("PageIndicator");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            TMP_FontAsset fontAsset = ResolveTMPFontAsset();
            if (fontAsset != null)
            {
                var tmp = go.AddComponent<TextMeshPro>();
                tmp.font = fontAsset;
                tmp.fontSize = PAGE_INDICATOR_FONT_SIZE;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white;
                tmp.text = "Page 1 / 1";
                // TMP 3D's RectTransform sizing is in world units once outside a Canvas; a
                // 2m × 0.3m rect comfortably fits "Page NN / NN" at 0.15m font size.
                var rt = go.GetComponent<RectTransform>();
                if (rt != null) rt.sizeDelta = new Vector2(QUADRANT_WIDTH_M * 2f, 0.3f);
                // Phase 1.5a Round 2 — Item 2 fix. TMP's mesh has front/back faces and the
                // shader culls one by default; depending on which direction the player views
                // the monitor from, the indicator could face away. Use fontMaterial (the
                // per-instance auto-cloned material) rather than fontSharedMaterial so we
                // don't mutate the shared TMP font asset.
                if (tmp.fontMaterial != null)
                {
                    tmp.fontMaterial.SetFloat("_CullMode", 0f);
                }
                _pageIndicator = tmp;
                SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] Page indicator created (TextMeshPro 3D, font='{fontAsset.name}', fontSize={PAGE_INDICATOR_FONT_SIZE}).");
            }
            else
            {
                // Legacy TextMesh fallback (per Phase 1.5a SPEC Q5 chain). Not pretty, but
                // visible — the alternative is suppressing the indicator entirely, which
                // Lawson explicitly rejected.
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] TMP_FontAsset chain exhausted — falling back to legacy TextMesh.");
                var tm = go.AddComponent<TextMesh>();
                tm.text = "Page 1 / 1";
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                tm.characterSize = PAGE_INDICATOR_FONT_SIZE;
                tm.fontSize = 32;
                tm.color = Color.white;
                Font fallbackFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (fallbackFont != null)
                {
                    tm.font = fallbackFont;
                    var mr = go.GetComponent<MeshRenderer>();
                    // sharedMaterial setter (not .material) — we're storing a reference to
                    // the Font's atlas material, not cloning. Keeps R7 grep clean.
                    if (mr != null) mr.sharedMaterial = fallbackFont.material;
                }
                _pageIndicatorLegacy = tm;
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] Page indicator created (legacy TextMesh fallback).");
            }
        }

    }
}
