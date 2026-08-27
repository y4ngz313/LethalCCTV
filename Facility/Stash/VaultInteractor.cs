using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Y4NGZCompany.Facility.Stash
{
    /// <summary>
    /// Lightweight in-world interaction hook for the placeholder mini-vault. The
    /// user-provided animated vault prefab will replace this with a real
    /// <c>InteractTrigger</c> bound to <see cref="OnLocalInteract"/>; this default
    /// version detects the local player by overlap and listens for the E key so
    /// the placeholder is testable before the real assets exist.
    /// </summary>
    public sealed class VaultInteractor : MonoBehaviour
    {
        public MiniVault Vault;
        public string DisplayName = "VAULT";
        public float InteractRadius = 2.4f;

        private Transform _localPlayerTransform;
        private bool _isPlayerInRange;
        private bool _promptApplied;
        private string _capturedControlTip = string.Empty;

        private void Update()
        {
            if (Vault == null)
            {
                SetControlPromptVisible(false);
                return;
            }
            if (GameNetworkManager.Instance == null || GameNetworkManager.Instance.localPlayerController == null)
            {
                SetControlPromptVisible(false);
                return;
            }

            Transform playerTransform = GameNetworkManager.Instance.localPlayerController.transform;
            if (playerTransform == null) return;
            _localPlayerTransform = playerTransform;
            float sqr = (playerTransform.position - transform.position).sqrMagnitude;
            bool nowInRange = sqr <= InteractRadius * InteractRadius;
            if (nowInRange != _isPlayerInRange)
            {
                _isPlayerInRange = nowInRange;
                SetControlPromptVisible(_isPlayerInRange && !Vault.IsUnlocked);
            }
            else if (_isPlayerInRange)
            {
                SetControlPromptVisible(!Vault.IsUnlocked);
            }

            if (_isPlayerInRange && Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
            {
                OpenKeypadForLocalPlayer();
            }
        }

        public void OnLocalInteract()
        {
            OpenKeypadForLocalPlayer();
        }

        private void OnDisable()
        {
            SetControlPromptVisible(false);
            _isPlayerInRange = false;
        }

        private void SetControlPromptVisible(bool visible)
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null || hud.controlTipLines == null || hud.controlTipLines.Length == 0 || hud.controlTipLines[0] == null)
                return;

            if (visible)
            {
                if (!_promptApplied)
                {
                    _capturedControlTip = hud.controlTipLines[0].text ?? string.Empty;
                    _promptApplied = true;
                }

                hud.controlTipLines[0].text = "Open keypad : [E]";
                return;
            }

            if (!_promptApplied)
                return;

            hud.controlTipLines[0].text = _capturedControlTip;
            _capturedControlTip = string.Empty;
            _promptApplied = false;
        }

        private void OpenKeypadForLocalPlayer()
        {
            if (Vault == null) return;
            if (Vault.IsUnlocked) return;

            PlayerControllerB localPlayer = GameNetworkManager.Instance?.localPlayerController;
            if (localPlayer == null) return;

            SetControlPromptVisible(false);

            // Anchor the overlay under the local player so the canvas persists
            // across scene loads and is destroyed cleanly when the player leaves.
            VaultKeypadOverlay overlay = VaultKeypadOverlay.EnsureInstance(localPlayer.transform);
            overlay.Open(Vault, DisplayName);
        }
    }
}
