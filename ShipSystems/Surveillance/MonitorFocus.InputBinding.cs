using System;
using System.Runtime.CompilerServices;
using UnityEngine.InputSystem;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class MonitorFocus
    {
        /// <summary>
        /// #600. True when <see cref="LethalCCTVInputActions"/> could not be constructed, which
        /// in practice means an InputUtils older than the one we compile against won the version
        /// resolution (0.6.3 has no <c>BindingPathEnums.KeyboardControl</c>, so the type fails to
        /// load). Everything else in the focus overlay still initialises; only the keyboard
        /// bindings are gone.
        /// </summary>
        internal static bool InputBindingsUnavailable { get; private set; }

        /// <summary>
        /// Builds and wires the focus-mode key bindings, returning false when the installed
        /// InputUtils cannot support them.
        ///
        /// The two-method split is deliberate and load-bearing. A missing type is raised when the
        /// method that *references* it is JIT-compiled, not when the failing line executes, so a
        /// try/catch inside a method that names <see cref="LethalCCTVInputActions"/> would never
        /// run — the throw happens on entry to that same method. This wrapper mentions no
        /// InputUtils-dependent type, so its catch is in place before
        /// <see cref="BindInputActionsCore"/> is compiled at the call below.
        /// </summary>
        private static bool TryBindInputActions()
        {
            try
            {
                BindInputActionsCore();
                InputBindingsUnavailable = false;
                return true;
            }
            catch (Exception ex)
            {
                InputBindingsUnavailable = true;
                _inputActions = null;

                string versionNote = DependencyVersionAudit.DescribeOutdated(DependencyVersionAudit.InputUtilsGuid)
                                     ?? "the installed LethalCompanyInputUtils does not provide the key bindings this build was compiled against";

                SurveillanceBootstrap.Log?.LogError(
                    $"[LethalCCTV] CCTV monitor keyboard controls are disabled: {versionNote}. " +
                    $"{DependencyVersionAudit.UpdateAdvice} The monitor itself still works, but exiting with E/Escape, " +
                    $"pane cycling, paging, hack entry, marker pings, the walkie key and the controls overlay will not respond. " +
                    $"Underlying error: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Drops the focus-mode key bindings. Split from <see cref="Shutdown"/> for the same
        /// reason <see cref="TryBindInputActions"/> is split from Initialize: a shutdown must not
        /// throw a TypeLoadException just because the profile carries an old InputUtils.
        /// </summary>
        private static void UnbindInputActions()
        {
            if (_inputActions == null)
                return;

            try
            {
                UnbindInputActionsCore();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Failed to unbind CCTV monitor keyboard controls: {ex.Message}");
            }

            _inputActions = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UnbindInputActionsCore()
        {
            _inputActions.ExitFocusKeyE.performed -= OnExitFocusPerformed;
            _inputActions.ExitFocusKeyEscape.performed -= OnExitFocusPerformed;
            _inputActions.CycleActivePrev.performed -= OnCycleActivePrevPerformed;
            _inputActions.CycleActiveNext.performed -= OnCycleActiveNextPerformed;
            _inputActions.PagePrev.performed -= OnPagePrevPerformed;
            _inputActions.PageNext.performed -= OnPageNextPerformed;
            _inputActions.HackSubmitEnter.performed -= OnHackSubmitPerformed;
            _inputActions.HackSubmitNumpadEnter.performed -= OnHackSubmitPerformed;
            _inputActions.PingMarker.performed -= OnPingMarkerPerformed;
            _inputActions.WalkiePushToTalk.performed -= OnWalkiePushToTalkPerformed;
            _inputActions.WalkiePushToTalk.canceled -= OnWalkiePushToTalkCanceled;
            _inputActions.ToggleControlsOverlayKey.performed -= OnToggleControlsOverlayPerformed;
        }

        /// <summary>
        /// The one InputUtils-typed read outside the binding lifecycle, isolated for the same
        /// JIT reason. Callers must check <see cref="InputBindingsUnavailable"/> first.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static InputAction TryGetWalkiePushToTalkAction()
        {
            return _inputActions?.WalkiePushToTalk;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BindInputActionsCore()
        {
            _inputActions = new LethalCCTVInputActions();
            _inputActions.ExitFocusKeyE.performed += OnExitFocusPerformed;
            _inputActions.ExitFocusKeyEscape.performed += OnExitFocusPerformed;
            // Arrow rebind (T6b): ←/→ cycle the active pane, ↑/↓ page.
            // Breaking change vs Phase 1.5a paging bindings — release note.
            _inputActions.CycleActivePrev.performed += OnCycleActivePrevPerformed;
            _inputActions.CycleActiveNext.performed += OnCycleActiveNextPerformed;
            _inputActions.PagePrev.performed += OnPagePrevPerformed;
            _inputActions.PageNext.performed += OnPageNextPerformed;
            _inputActions.HackSubmitEnter.performed += OnHackSubmitPerformed;
            _inputActions.HackSubmitNumpadEnter.performed += OnHackSubmitPerformed;
            _inputActions.PingMarker.performed += OnPingMarkerPerformed;
            _inputActions.WalkiePushToTalk.performed += OnWalkiePushToTalkPerformed;
            _inputActions.WalkiePushToTalk.canceled += OnWalkiePushToTalkCanceled;
            _inputActions.ToggleControlsOverlayKey.performed += OnToggleControlsOverlayPerformed;
        }
    }
}
