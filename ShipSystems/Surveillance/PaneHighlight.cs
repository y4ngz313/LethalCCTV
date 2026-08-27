using UnityEngine;
using Y4NGZCompany.Facility.Cameras;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    // T8 — paints the active pane on both the screen-space overlay and the
    // world-space wall monitor. Read by MonitorFocus.EnterFocus + ExitFocus +
    // SetActiveSlot.
    //
    // Requirement B (PLAN): the visible border lives on dedicated outline
    // GameObjects (Image for the overlay, MeshRenderer for the wall). The
    // shared RenderTextures in QuadMonitor.QuadRTs are never touched by this
    // class — so a wall-side highlight cannot bleed into the overlay's
    // RawImage view, and vice versa.
    internal static class PaneHighlight
    {
        // Muted terminal-green active tint. Hardcoded for now; promote to config if
        // testing surfaces a need.
        private static readonly Color ACTIVE_TINT = new Color(0.06f, 0.62f, 0.20f, 0.90f);
        private static readonly Color INACTIVE_OVERLAY_TINT = new Color(0.025f, 0.12f, 0.07f, 0.24f);
        private const string HDRP_UNLIT_COLOR_PROPERTY = "_UnlitColor";

        internal static void SetActive(int activeSlot)
        {
            ApplyToOverlay(activeSlot);
            // The physical focus texture already has its own active pane border.
            // Leave the world monitor outlines dark so they cannot peek around
            // the edge of the terminal model during focus view.
            ApplyToWall(-1);
            MonitorFocus.UpdateActiveSlotLabel(activeSlot);
        }

        internal static void ClearAll()
        {
            // -1 = no slot active → all outlines transparent.
            ApplyToOverlay(-1);
            ApplyToWall(-1);
            MonitorFocus.UpdateActiveSlotLabel(-1);
        }

        private static void ApplyToOverlay(int activeSlot)
        {
            var outlines = MonitorFocus.OverlayOutlines;
            if (outlines == null) return;
            for (int i = 0; i < outlines.Length; i++)
            {
                if (outlines[i] == null) continue;
                Color tint = activeSlot < 0
                    ? Color.clear
                    : (i == activeSlot ? ACTIVE_TINT : INACTIVE_OVERLAY_TINT);
                outlines[i].color = Color.clear;
                MonitorFocus.SetOverlayFrameTint(i, tint);
            }
        }

        private static void ApplyToWall(int activeSlot)
        {
            var materials = QuadMonitor.QuadOutlineMaterials;
            if (materials == null) return;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) continue;
                materials[i].SetColor(
                    HDRP_UNLIT_COLOR_PROPERTY,
                    (i == activeSlot) ? ACTIVE_TINT : Color.clear);
            }
        }
    }
}
