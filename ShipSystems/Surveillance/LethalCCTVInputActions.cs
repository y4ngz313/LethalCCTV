using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DunGen;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using LethalCompanyInputUtils.Api;
using LethalCompanyInputUtils.BindingPathEnums;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    // [InputAction] does not stack on a single property — two separate properties wire
    // E and Escape independently and both subscribe to the same handler (PLAN tighten #5).
    internal class LethalCCTVInputActions : LcInputActions
    {
        [InputAction(KeyboardControl.E, Name = "Exit CCTV Focus (E)")]
        public InputAction ExitFocusKeyE { get; set; }

        [InputAction(KeyboardControl.Escape, Name = "Exit CCTV Focus (Escape)")]
        public InputAction ExitFocusKeyEscape { get; set; }

        // T6b arrow rebind. ←/→ cycle the active pane (the pane that mouselook
        // drives); ↑/↓ change page. Breaking change vs Phase 1.5a — the
        // previous LeftArrow/RightArrow page bindings no longer exist under
        // those names. Users with customized arrow rebinds need to redo them.
        [InputAction(KeyboardControl.LeftArrow, Name = "CCTV Navigate Left")]
        public InputAction CycleActivePrev { get; set; }

        [InputAction(KeyboardControl.RightArrow, Name = "CCTV Navigate Right")]
        public InputAction CycleActiveNext { get; set; }

        [InputAction(KeyboardControl.UpArrow, Name = "CCTV Navigate Up")]
        public InputAction PagePrev { get; set; }

        [InputAction(KeyboardControl.DownArrow, Name = "CCTV Navigate Down")]
        public InputAction PageNext { get; set; }

        [InputAction(KeyboardControl.Enter, Name = "CCTV Hack Submit")]
        public InputAction HackSubmitEnter { get; set; }

        [InputAction(KeyboardControl.NumpadEnter, Name = "CCTV Hack Submit Numpad")]
        public InputAction HackSubmitNumpadEnter { get; set; }

        [InputAction("<Mouse>/leftButton", Name = "CCTV Ping")]
        public InputAction PingMarker { get; set; }

        [InputAction(KeyboardControl.V, Name = "CCTV Walkie Push To Talk")]
        public InputAction WalkiePushToTalk { get; set; }

        // #541 — runtime toggle for the controls tooltip. H is free in vanilla
        // and unused by every other focus-mode binding above (E/ESC, arrows,
        // ENTER, LMB, V) as well as SPACE/RMB/F1/F2, which are handled outside
        // this asset.
        [InputAction("<Keyboard>/h", Name = "CCTV Toggle Controls Overlay")]
        public InputAction ToggleControlsOverlayKey { get; set; }

    }
}
