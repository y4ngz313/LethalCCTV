using UnityEngine;
using UnityEngine.InputSystem;

namespace Y4NGZCompany.Core
{
    /// <summary>
    /// #716 E7: the player's bound interact key, formatted as a prompt token such as
    /// <c>[E]</c> or <c>[F]</c>.
    ///
    /// The game only rewrites the <c>[LMB]</c> placeholder when it copies an
    /// <c>InteractTrigger.hoverTip</c> into the cursor tip. A prompt this mod writes straight
    /// into <c>cursorTip.text</c> or into a HUD control-tip line is rendered verbatim - and the
    /// Y4NGZUI prompt overlay renders it verbatim too - so a placeholder left in one of those
    /// strings reaches the player as the literal text "[LMB]". Resolving the key ourselves is
    /// the only way those surfaces can follow a rebind.
    ///
    /// Only for prompts the mod writes directly. A string handed to an <c>InteractTrigger</c>
    /// must keep <c>[LMB]</c> and let the game do the substitution.
    /// </summary>
    internal static class CctvInteractKey
    {
        /// <summary>Shown when the binding cannot be read; also the game's own default.</summary>
        private const string FallbackToken = "[E]";

        private const string InteractActionName = "Interact";
        private const string KeyboardDevicePrefix = "<Keyboard>";

        /// <summary>
        /// Re-reading the binding walks the action's binding list and formats a string, which
        /// is far too much to do per prompt per frame. A rebind is a menu action, so noticing
        /// it a second late costs nothing.
        /// </summary>
        private const float RefreshIntervalSeconds = 1f;

        private static string _cachedToken = FallbackToken;
        private static float _nextRefreshAt;

        /// <summary>
        /// The bound interact key wrapped in brackets, ready to concatenate into a prompt.
        /// Never null or empty: every failure path yields <c>[E]</c>.
        /// </summary>
        internal static string Token()
        {
            float now = Time.unscaledTime;
            if (now < _nextRefreshAt)
                return _cachedToken;

            _nextRefreshAt = now + RefreshIntervalSeconds;
            _cachedToken = ResolveToken();
            return _cachedToken;
        }

        private static string ResolveToken()
        {
            try
            {
                InputAction action = IngamePlayerSettings.Instance?.playerInput?.actions
                    ?.FindAction(InteractActionName, throwIfNotFound: false);
                if (action == null)
                    return FallbackToken;

                string path = ResolveKeyboardBindingPath(action);
                if (string.IsNullOrEmpty(path))
                    return FallbackToken;

                string display = InputControlPath.ToHumanReadableString(
                    path,
                    InputControlPath.HumanReadableStringOptions.OmitDevice);
                if (string.IsNullOrWhiteSpace(display))
                    return FallbackToken;

                display = display.Trim();
                // Single letters read as keycaps, so "e" becomes "E"; longer names such as
                // "Left Shift" keep the casing the input system gave them.
                if (display.Length == 1)
                    display = display.ToUpperInvariant();
                return $"[{display}]";
            }
            catch
            {
                // A missing or restructured input asset must not take a prompt down with it.
                return FallbackToken;
            }
        }

        /// <summary>
        /// Prefers the keyboard binding, because these prompts name a key. A composite and its
        /// parts are skipped: only a leaf binding has a path worth formatting.
        /// </summary>
        private static string ResolveKeyboardBindingPath(InputAction action)
        {
            string firstUsable = null;
            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding binding = action.bindings[i];
                if (binding.isComposite || binding.isPartOfComposite)
                    continue;

                string path = binding.effectivePath;
                if (string.IsNullOrEmpty(path))
                    continue;

                if (path.StartsWith(KeyboardDevicePrefix, System.StringComparison.OrdinalIgnoreCase))
                    return path;
                if (firstUsable == null)
                    firstUsable = path;
            }

            return firstUsable;
        }
    }
}
