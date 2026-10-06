using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVVanillaMonitorDisplay
    {
        private static readonly Color MachineVisionInk = new Color(0.66f, 0.88f, 0.94f, 1f);
        private static TMP_FontAsset _machineVisionFont;
        private static Material _machineVisionFontMaterial;
        private static TextMeshProUGUI _leftZoomLabel;

        internal static void ApplyMachineVisionText(TMP_Text text)
        {
            if (text == null) return;
            if (_machineVisionFont == null)
            {
                Terminal terminal = Object.FindObjectOfType<Terminal>();
                _machineVisionFont = terminal != null && terminal.screenText != null && terminal.screenText.textComponent != null
                    ? terminal.screenText.textComponent.font : TMP_Settings.defaultFontAsset;
            }
            if (_machineVisionFont != null)
            {
                text.font = _machineVisionFont;
                if (_machineVisionFontMaterial == null)
                {
                    _machineVisionFontMaterial = new Material(_machineVisionFont.material)
                    {
                        name = "CCTV_MachineVision_Type",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    // The HUD font's outlined material made small letters muddy.
                    // Own a clean material; never change the terminal/HUD assets.
                    foreach (string property in new[] { "_OutlineWidth", "_FaceDilate", "_UnderlayDilate" })
                        if (_machineVisionFontMaterial.HasProperty(property)) _machineVisionFontMaterial.SetFloat(property, 0f);
                    if (_machineVisionFontMaterial.HasProperty("_FaceColor")) _machineVisionFontMaterial.SetColor("_FaceColor", Color.white);
                    _machineVisionFontMaterial.DisableKeyword("OUTLINE_ON");
                    _machineVisionFontMaterial.DisableKeyword("UNDERLAY_ON");
                    _machineVisionFontMaterial.DisableKeyword("UNDERLAY_INNER");
                    _machineVisionFontMaterial.DisableKeyword("GLOW_ON");
                }
                text.fontSharedMaterial = _machineVisionFontMaterial;
            }
            text.fontStyle = FontStyles.Normal;
            text.characterSpacing = 1.2f;
        }

        private static TextMeshProUGUI CreateMachineVisionLabel(RectTransform parent, string name,
            Vector2 anchor, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions alignment)
        {
            var text = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            text.transform.SetParent(parent, false);
            SetLayerRecursive(text.gameObject, UiRenderLayer);
            text.rectTransform.anchorMin = text.rectTransform.anchorMax = text.rectTransform.pivot = anchor;
            text.rectTransform.anchoredPosition = position;
            text.rectTransform.sizeDelta = size;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = MachineVisionInk;
            text.raycastTarget = false;
            text.richText = false;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            ApplyMachineVisionText(text);
            return text;
        }

        private static void CreateMachineVisionFurniture(RectTransform canvas, float headerInset = 24f)
        {
            // Translucent strips overlay the full camera image; no lens mask or
            // dead corners. Shared rules and spacing carry across both monitors.
            CreateMachineVisionStrip(canvas, "HeaderBacking", 1f, 0f, 66f, new Color(0.015f, 0.03f, 0.04f, 0.67f));
            CreateMachineVisionStrip(canvas, "FooterBacking", 0f, 0f, 36f, new Color(0.015f, 0.03f, 0.04f, 0.72f));
            CreateMachineVisionStrip(canvas, "HeaderRule", 1f, -42f, 1f, new Color(0.55f, 0.79f, 0.85f, 0.65f), headerInset);
            CreateMachineVisionStrip(canvas, "FooterRule", 0f, 36f, 1f, new Color(0.55f, 0.79f, 0.85f, 0.65f), 24f);
        }

        private static void CreateMachineVisionStrip(RectTransform parent, string name, float edge,
            float offset, float height, Color color, float inset = 0f)
        {
            var image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            image.transform.SetParent(parent, false);
            SetLayerRecursive(image.gameObject, UiRenderLayer);
            RectTransform rect = image.rectTransform;
            rect.anchorMin = new Vector2(0f, edge);
            rect.anchorMax = new Vector2(1f, edge);
            rect.pivot = new Vector2(0.5f, edge);
            rect.sizeDelta = new Vector2(-inset * 2f, height);
            rect.anchoredPosition = new Vector2(0f, offset);
            image.color = color;
            image.raycastTarget = false;
        }
    }
}
