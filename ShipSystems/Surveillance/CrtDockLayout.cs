using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// The stretch model shared by the mainframe CRT overlays. Both the splice overlay and the
    /// control terminal are authored against a fixed design height and a runtime design width,
    /// so a dock of any aspect fills edge to edge instead of aspect-fitting into a letterbox.
    /// The constants and the dock resolve used to be copied between the two overlays and had
    /// already drifted apart, so they live here once.
    /// </summary>
    internal static class CrtDockLayout
    {
        internal const float DesignHeight = 356f;
        internal const float DefaultDesignWidth = 680f;
        internal const float MinDesignWidth = 400f;
        internal const float MaxDesignWidth = 1100f;
        internal const float ContentInset = 22f;
        internal const float FrameInset = 12f;

        /// <summary>
        /// SDF face dilation applied to the cloned terminal font material. The vanilla terminal
        /// face is thin, and at Lethal Company's native internal resolution a thin stroke lands on
        /// well under one pixel and greys out. Dilating the face thickens every stroke without
        /// changing the font, the colour, or the layout. Kept small so the same screen still reads
        /// as a terminal at the high resolutions the resolution-enhancing mods produce.
        /// </summary>
        private const float LowResFaceDilate = 0.12f;

        private static readonly int FaceDilateId = Shader.PropertyToID("_FaceDilate");

        /// <summary>
        /// Thickens the glyph faces of a cloned TMP font material. Only ever call this on a
        /// material this mod owns: the vanilla terminal's own material is shared with the ship
        /// terminal. A shader without the property is a silent no-op, so a font swap by another
        /// mod cannot turn this into a Unity material warning.
        /// </summary>
        internal static void ApplyLowResFaceThickening(Material clonedFontMaterial)
        {
            if (clonedFontMaterial == null || !clonedFontMaterial.HasProperty(FaceDilateId))
                return;

            float current = clonedFontMaterial.GetFloat(FaceDilateId);
            if (current < LowResFaceDilate)
                clonedFontMaterial.SetFloat(FaceDilateId, LowResFaceDilate);
        }

        /// <summary>
        /// Centres <paramref name="rootRect"/> in its dock and resolves the design width plus the
        /// uniform scale for the given dock size. Returns true when the design width changed and
        /// the caller must re-run its layout pass.
        /// </summary>
        internal static bool TryResolveDock(RectTransform rootRect, Vector2 anchoredPosition, Vector2 dockSize,
            float currentDesignWidth, out float designWidth, out float scale)
        {
            designWidth = currentDesignWidth;
            scale = 1f;
            if (rootRect == null)
                return false;

            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.anchoredPosition = anchoredPosition;

            float dockWidth = Mathf.Max(1f, dockSize.x);
            float dockHeight = Mathf.Max(1f, dockSize.y);
            float resolved = Mathf.Clamp(DesignHeight * (dockWidth / dockHeight), MinDesignWidth, MaxDesignWidth);
            bool changed = Mathf.Abs(resolved - currentDesignWidth) > 0.5f;
            if (changed)
                designWidth = resolved;

            // Height drives the scale so glyph sizes stay predictable; the width term only bites
            // for docks whose aspect fell outside the design-width clamp.
            scale = Mathf.Min(dockHeight / DesignHeight, dockWidth / designWidth);
            return changed;
        }

        /// <summary>Resizes the four CRT frame rules to the current design width.</summary>
        internal static void ResizeFrame(float designWidth, RectTransform top, RectTransform bottom,
            RectTransform left, RectTransform right)
        {
            Vector2 horizontal = new Vector2(designWidth - FrameInset * 2f, 2f);
            Vector2 vertical = new Vector2(2f, DesignHeight - FrameInset * 2f);

            if (top != null)
                top.sizeDelta = horizontal;
            if (bottom != null)
                bottom.sizeDelta = horizontal;
            if (left != null)
                left.sizeDelta = vertical;
            if (right != null)
                right.sizeDelta = vertical;
        }
    }
}
