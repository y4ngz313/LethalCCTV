using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// #577 — pointer hold latch for the mainframe INTERCOM transmit button. Deliberately dumb:
    /// it reports press and release, and the overlay decides what a hold means. A host that
    /// renders the overlay without a GraphicRaycaster, without an EventSystem, or with the cursor
    /// locked simply never calls these handlers; the button still lights from the keybind path
    /// because its visual state is driven by the overlay, not by this component.
    /// </summary>
    internal sealed class MainframeIntercomPushButton : MonoBehaviour,
        IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        internal Action<bool> HoldChanged;

        private bool _held;

        public void OnPointerDown(PointerEventData eventData)
        {
            // A locked cursor is a frozen, hidden pointer: whatever it happens to be sitting on
            // still receives the press from an unrelated left click. Only an actually steerable
            // pointer may start a transmit. Release is never gated, so a hold cannot get stuck if
            // the cursor locks mid-press.
            if (Cursor.lockState == CursorLockMode.Locked) return;
            SetHeld(true);
        }

        public void OnPointerUp(PointerEventData eventData) => SetHeld(false);

        public void OnPointerExit(PointerEventData eventData) => SetHeld(false);

        // Hiding the button on a view switch, or tearing the overlay down mid-hold, must not leave
        // the transmit latch stuck on.
        private void OnDisable() => SetHeld(false);

        private void SetHeld(bool held)
        {
            if (_held == held) return;
            _held = held;
            HoldChanged?.Invoke(held);
        }
    }
}
