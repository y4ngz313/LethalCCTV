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
    /// <summary>
    /// Phase 1.5 standalone CCTV monitor: 4 Quad MeshRenderers bound to runtime-cloned
    /// HDRP/Unlit materials, each material's texture slot pointing at one of 4
    /// RenderTextures. Rendering goes through the same HDRP shader path vanilla LC's
    /// on-wall map screen uses — closing the wrong-pipeline display bug (Bug B / Mistake
    /// 12) that Phase 1's Canvas+RawImage architecture surfaced as uniform whiteout.
    ///
    /// Phase 1.5a adds: HDMaterial.ValidateMaterial to honour the _DOUBLESIDED_ON keyword,
    /// a static 4×4 black RT shared by empty slots on the last page, a TMP 3D page
    /// indicator below the 2×2 grid, and BindRTToSlot / BindEmptyToSlot APIs that
    /// QuadCameraAssignment calls during page rotation.
    /// </summary>
    internal static partial class QuadMonitor
    {
        // World-space quadrant face for the physical terminal console. The
        // generated prefab is screen-centered and scaled so this 2x2 4:3 grid
        // sits inside the CRT glass with a small bezel margin.
        private const float QUADRANT_WIDTH_M = 0.33f;
        private const float QUADRANT_HEIGHT_M = 0.25f;
        private const float MONITOR_ROOT_SCALE = 0.8f;
        private const float MONITOR_FORWARD_OFFSET_M = -0.14f;
        private const float LIVE_SCREEN_Z_M = -0.014f;
        private const float SCREEN_BACKING_Z_M = LIVE_SCREEN_Z_M + 0.004f;
        private const float FOCUS_SCREEN_Z_M = LIVE_SCREEN_Z_M - 0.010f;
        private const float QUADRANT_Z_OFFSET_STEP = 0.001f;
        private const float FOCUS_OVERLAY_Z_M = LIVE_SCREEN_Z_M + 0.010f;
        private const float FOCUS_CHROME_Z_M = FOCUS_OVERLAY_Z_M + 0.004f;
        private const float FOCUS_CHROME_TEXT_Z_M = FOCUS_CHROME_Z_M + 0.003f;
        private const float FOCUS_CHROME_LAYOUT_WIDTH_PX = 960f;
        private const float FOCUS_CHROME_LAYOUT_HEIGHT_PX = 720f;
        private const float FOCUS_VIEW_CAMERA_X_M = -QUADRANT_WIDTH_M * 0.95f;
        private const float FOCUS_VIEW_CAMERA_Y_M = QUADRANT_HEIGHT_M * 0.20f;
        private const float FOCUS_VIEW_CAMERA_Z_M = -0.78f;

        private const int DEFAULT_RT_WIDTH = 512;
        private const int MIN_RT_WIDTH = 64;
        private const int MAX_RT_WIDTH = 1024;

        // Empty-slot fallback RT. Deliberately plain black: the old generated
        // standby target looked like a strange camera overlay in focus mode.
        private const int EMPTY_RT_SIZE = 256;

        // TMP page indicator placement: just below the 2×2 grid (grid bottom = -QUADRANT_HEIGHT_M),
        // with a small margin. Font size in world units (3D TextMeshPro). 0.0.12 — scaled
        // ×0.30 with the grid; the indicator sits below the TV chassis. Phase 3 may move
        // it into a header strip inside the TV bezel.
        private const float PAGE_INDICATOR_MARGIN_M = 0.035f;
        private const float PAGE_INDICATOR_FONT_SIZE = 0.034f;

        // Phase 1.7 — per-quad camera-number label. Parented to MonitorRoot (not the
        // Quadrant) at the quad's top-left interior corner with a small +Z offset so it
        // sits microscopically in front of the quad mesh. Identity localScale avoids the
        // Quadrant's non-uniform scale stretching the text vertically. The night-vision
        // shader applies only to the quad's HDRP/Unlit-cloned material; the label uses
        // TMP's own fontMaterial and is therefore unaffected by grayscale/gain. 0.0.12 —
        // scaled ×0.30 with the grid. Phase 2 will revisit label sync + sizing.
        private const float LABEL_INSET_X_M = 0.018f;
        private const float LABEL_INSET_Y_M = 0.018f;
        private const float LABEL_FORWARD_Z_OFFSET_M = 0.005f;
        private const float LABEL_FONT_SIZE = 0.032f;
        private const float LABEL_BOX_WIDTH_M = 0.12f;
        private const float LABEL_BOX_HEIGHT_M = 0.035f;

        internal static RenderTexture[] QuadRTs { get; private set; }
        // Phase 1.7b — per-slot RAW RTs that the CCTV cameras render INTO. Non-null
        // iff the night-vision shader bundle loaded (NightVisionBaker.IsActive at
        // Spawn time). When non-null, cameras target RawRTs[i] and the bake handler
        // blits through the night-vision material into QuadRTs[i]. When null, the
        // bundle is missing and cameras target QuadRTs[i] directly (raw passthrough,
        // wall + overlay show unfiltered feed — graceful fallback).
        internal static RenderTexture[] RawRTs { get; private set; }
        internal static GameObject MonitorRoot { get; private set; }
        internal static Material[] QuadMaterials { get; private set; }

        // Phase 1.7b — current display texture per slot, mirrored to MonitorFocus
        // overlay RawImages on every transition. With dual-RT there are three RTs
        // per slot (RawRTs[i], QuadRTs[i] display, shared _emptySlotBlackRT); this
        // tracks which one is the active display target so the focus overlay never
        // shows a stale baked frame after a slot goes empty.
        private static Texture[] _slotCurrentDisplayTex = new Texture[4];

        // T8 — per-slot wall-side outline material. NEVER bound to any RT and
        // NEVER consumed by BindRTToSlot / BindEmptyToSlot / ApplyNightVisionParams.
        // Requirement B: outline lives on a separate mesh with a separate material;
        // the shared QuadRTs are untouched. PaneHighlight reads this array to
        // tint the active slot.
        internal static Material[] QuadOutlineMaterials { get; private set; }

        // Outline visual constants. The outline quad sits microscopically behind
        // the main quad in monitor-local Z (smaller Z = farther from viewer
        // given the main-quad +Z normal), and is slightly larger in XY so the
        // border peeks out around the main quad's edges when colored. 0.0.12 —
        // border scaled ×0.30 with the grid; Z offset unchanged (it's a
        // z-fight clearance margin, not a visual size).
        private const float OUTLINE_BORDER_M = 0.012f;
        private const float OUTLINE_Z_OFFSET_M = 0.0005f;
        private static readonly string[] DisplayTexturePropertyNames =
        {
            "_UnlitColorMap",
            "_BaseColorMap",
            "_BaseMap",
            "_MainTex"
        };
        private static readonly string[] ClearTexturePropertyNames =
        {
            "_EmissiveColorMap"
        };

        private static Mesh _cachedQuadMesh;
        private static RenderTexture _emptySlotBlackRT;
        private static TextMeshPro _pageIndicator;
        private static TextMesh _pageIndicatorLegacy;
        private static TextMeshPro[] _cameraLabels;
        private static GameObject _screenBacking;
        private static GameObject _focusScreen;
        private static GameObject _focusChromeRoot;
        private static Material _screenBackingMaterial;
        private static Material _focusScreenMaterial;
        private static readonly List<Material> _focusChromeMaterials = new List<Material>(8);
        private static bool _focusScreenLogged;
        private static bool _physicalMonitorVisualsHidden;

        private static Transform _runtimeFocusViewAnchor;

    }
}
