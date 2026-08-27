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
        private static void CreateFocusChromeOverlay(Transform parent, Mesh quadMesh)
        {
            _focusChromeRoot = new GameObject("PhysicalFocusChrome");
            _focusChromeRoot.transform.SetParent(parent, worldPositionStays: false);
            _focusChromeRoot.transform.localPosition = Vector3.zero;
            _focusChromeRoot.transform.localRotation = Quaternion.identity;
            _focusChromeRoot.transform.localScale = Vector3.one;

            Material rule = CreateFocusChromeMaterial("LethalCCTV_FocusChromeRule", new Color(0.025f, 0.58f, 0.22f, 1f));
            Material ruleDim = CreateFocusChromeMaterial("LethalCCTV_FocusChromeRuleDim", new Color(0.014f, 0.22f, 0.12f, 1f));

            const float outerW = 944f;
            const float outerH = 690f;
            const float line = 2.0f;
            const float thick = 3.0f;
            const float topSectionH = 480f;
            const float bottomDividerX = 56f;
            const float rightColumnCenterX = 267f;
            const float rightTopPanelH = 92f;

            CreateFocusChromeRule(_focusChromeRoot.transform, "ChromeTopRule", new Vector2(0f, outerH * 0.5f), new Vector2(outerW, line), ruleDim, quadMesh);
            CreateFocusChromeRule(_focusChromeRoot.transform, "ChromeBottomRule", new Vector2(0f, -outerH * 0.5f), new Vector2(outerW, line), ruleDim, quadMesh);
            CreateFocusChromeRule(_focusChromeRoot.transform, "ChromeLeftRule", new Vector2(-outerW * 0.5f, 0f), new Vector2(line, outerH), ruleDim, quadMesh);
            CreateFocusChromeRule(_focusChromeRoot.transform, "ChromeRightRule", new Vector2(outerW * 0.5f, 0f), new Vector2(line, outerH), ruleDim, quadMesh);

            float mainDividerY = FOCUS_CHROME_LAYOUT_HEIGHT_PX * 0.5f - topSectionH;
            CreateFocusChromeRule(_focusChromeRoot.transform, "ChromeMainDivider", new Vector2(0f, mainDividerY), new Vector2(outerW, thick), rule, quadMesh);
            CreateFocusChromeRule(_focusChromeRoot.transform, "ChromeBottomColumnDivider", new Vector2(bottomDividerX, -248f), new Vector2(thick, 210f), rule, quadMesh);
            CreateFocusChromeRule(_focusChromeRoot.transform, "ChromeRightSectionDivider", new Vector2(rightColumnCenterX, -226f), new Vector2(392f, line), ruleDim, quadMesh);
            CreateFocusChromeRule(_focusChromeRoot.transform, "ChromeControlsDivider", new Vector2(rightColumnCenterX, -166f), new Vector2(thick, rightTopPanelH), ruleDim, quadMesh);

            CreateFocusChromeCorner(_focusChromeRoot.transform, "ChromeCameraCornerTL", new Vector2(-456f, 254f), true, true, rule, quadMesh);
            CreateFocusChromeCorner(_focusChromeRoot.transform, "ChromeCameraCornerTR", new Vector2(456f, 254f), false, true, rule, quadMesh);
            CreateFocusChromeCorner(_focusChromeRoot.transform, "ChromeCameraCornerBL", new Vector2(-456f, -104f), true, false, rule, quadMesh);
            CreateFocusChromeCorner(_focusChromeRoot.transform, "ChromeCameraCornerBR", new Vector2(456f, -104f), false, false, rule, quadMesh);

            TMP_FontAsset fontAsset = ResolveTMPFontAsset();
            if (fontAsset != null)
            {
                CreateFocusChromeText(_focusChromeRoot.transform, "ChromeTitle", "LETHALCCTV SECURITY FEED", new Vector2(-438f, 326f), new Vector2(360f, 26f), 0.0095f, new Color(0.16f, 1f, 0.32f, 1f), TextAlignmentOptions.TopLeft, fontAsset);
                CreateFocusChromeText(_focusChromeRoot.transform, "ChromePage", "PAGE", new Vector2(382f, 326f), new Vector2(70f, 22f), 0.0066f, new Color(0.72f, 0.75f, 0.72f, 1f), TextAlignmentOptions.TopRight, fontAsset);
                CreateFocusChromeText(_focusChromeRoot.transform, "ChromeRadar", "RADAR MAP", new Vector2(-438f, -112f), new Vector2(130f, 22f), 0.0069f, new Color(1f, 0.60f, 0.10f, 1f), TextAlignmentOptions.TopLeft, fontAsset);
                CreateFocusChromeText(_focusChromeRoot.transform, "ChromeInterior", "INTERIOR", new Vector2(92f, -112f), new Vector2(100f, 22f), 0.0062f, Color.white, TextAlignmentOptions.TopLeft, fontAsset);
                CreateFocusChromeText(_focusChromeRoot.transform, "ChromeControls", "CONTROLS", new Vector2(274f, -112f), new Vector2(110f, 22f), 0.0062f, new Color(0.57f, 0.82f, 0.64f, 1f), TextAlignmentOptions.TopLeft, fontAsset);
                CreateFocusChromeText(_focusChromeRoot.transform, "ChromeSignal", "SIGNAL LOG", new Vector2(92f, -226f), new Vector2(140f, 22f), 0.0064f, new Color(1f, 0.60f, 0.10f, 1f), TextAlignmentOptions.TopLeft, fontAsset);
            }
            else
            {
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] Focus chrome labels skipped: TMP font unavailable.");
            }

            _focusChromeRoot.SetActive(false);
            SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] Physical focus chrome overlay created.");
        }

        private static void CreateFocusChromeCorner(Transform parent, string name, Vector2 cornerPx, bool left, bool top, Material material, Mesh quadMesh)
        {
            float sx = left ? 1f : -1f;
            float sy = top ? -1f : 1f;
            CreateFocusChromeRule(parent, name + "_H", new Vector2(cornerPx.x + sx * 22f, cornerPx.y), new Vector2(44f, 2.4f), material, quadMesh);
            CreateFocusChromeRule(parent, name + "_V", new Vector2(cornerPx.x, cornerPx.y + sy * 22f), new Vector2(2.4f, 44f), material, quadMesh);
        }

        private static void CreateFocusChromeRule(Transform parent, string name, Vector2 centerPx, Vector2 sizePx, Material material, Mesh quadMesh)
        {
            if (material == null || quadMesh == null) return;

            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = FocusChromePixelToLocal(centerPx, FOCUS_CHROME_Z_M);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(
                Mathf.Max(0.0006f, sizePx.x / FOCUS_CHROME_LAYOUT_WIDTH_PX * QUADRANT_WIDTH_M * 2f),
                Mathf.Max(0.0006f, sizePx.y / FOCUS_CHROME_LAYOUT_HEIGHT_PX * QUADRANT_HEIGHT_M * 2f),
                1f);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = quadMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            mr.sharedMaterial = material;
        }

        private static void CreateFocusChromeText(
            Transform parent,
            string name,
            string text,
            Vector2 topLeftPx,
            Vector2 sizePx,
            float fontSize,
            Color color,
            TextAlignmentOptions alignment,
            TMP_FontAsset fontAsset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = FocusChromePixelToLocal(topLeftPx, FOCUS_CHROME_TEXT_Z_M);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = fontAsset;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.text = text;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(
                    sizePx.x / FOCUS_CHROME_LAYOUT_WIDTH_PX * QUADRANT_WIDTH_M * 2f,
                    sizePx.y / FOCUS_CHROME_LAYOUT_HEIGHT_PX * QUADRANT_HEIGHT_M * 2f);
            }
            if (tmp.fontMaterial != null)
            {
                tmp.fontMaterial.SetFloat("_CullMode", 0f);
                tmp.fontMaterial.EnableKeyword("_FACE_CULLING_OFF");
            }
            tmp.ForceMeshUpdate();
        }

        private static Vector3 FocusChromePixelToLocal(Vector2 centeredPx, float z)
        {
            float x = centeredPx.x / FOCUS_CHROME_LAYOUT_WIDTH_PX * QUADRANT_WIDTH_M * 2f;
            float y = centeredPx.y / FOCUS_CHROME_LAYOUT_HEIGHT_PX * QUADRANT_HEIGHT_M * 2f;
            return new Vector3(x, y, z);
        }

        private static Material CreateFocusChromeMaterial(string name, Color color)
        {
            Material mat = VanillaShaderInfo.SourceMaterial != null
                ? UnityEngine.Object.Instantiate(VanillaShaderInfo.SourceMaterial)
                : new Material(Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard"));
            mat.name = name;
            BindDisplayTexture(mat, Texture2D.whiteTexture);
            SetRuntimeMaterialColor(mat, color);
            if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", Color.black);
            if (mat.HasProperty("_EmissiveColorLDR")) mat.SetColor("_EmissiveColorLDR", Color.black);
            if (mat.HasProperty("_DoubleSidedEnable")) mat.SetFloat("_DoubleSidedEnable", 1f);
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Off);
            mat.EnableKeyword("_DOUBLESIDED_ON");
            ValidateMaterial(mat);
            _focusChromeMaterials.Add(mat);
            return mat;
        }

        private static void SetFocusChromeVisible(bool visible)
        {
            if (_focusChromeRoot != null && _focusChromeRoot.activeSelf != visible)
                _focusChromeRoot.SetActive(visible);
            if (_physicalMonitorVisualsHidden)
                SetRendererTreeEnabled(_focusChromeRoot, visible);
        }

        private static void SetRendererTreeEnabled(GameObject root, bool enabled)
        {
            if (root == null)
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = enabled;
            }
        }

    }
}
