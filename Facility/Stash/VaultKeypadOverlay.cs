using System;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Facility.Stash
{
    public sealed class VaultKeypadOverlay
    {
        public const int CodeDigits = 4;

        private static VaultKeypadOverlay _instance;
        public static VaultKeypadOverlay Instance => _instance;

        private readonly Transform _parent;
        private GameObject _root;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _displayText;
        private TextMeshProUGUI _feedbackText;
        private TextMeshProUGUI _helpText;
        private readonly Button[] _digitButtons = new Button[10];
        private Button _clearButton;
        private Button _submitButton;
        private Button _closeButton;
        private NetworkBehaviour _activeVault;
        private readonly int[] _entered = new int[CodeDigits];
        private int _enteredCount;

        public bool IsOpen => _root != null && _root.activeSelf;

        private VaultKeypadOverlay(Transform parent)
        {
            _parent = parent;
        }

        public static VaultKeypadOverlay EnsureInstance(Transform parent)
        {
            if (_instance != null) return _instance;
            _instance = new VaultKeypadOverlay(parent);
            _instance.EnsureBuilt();
            return _instance;
        }

        public static void ResetInstance()
        {
            if (_instance != null && _instance._root != null)
            {
                UnityEngine.Object.Destroy(_instance._root);
            }
            _instance = null;
        }

        private void EnsureBuilt()
        {
            if (_root != null) return;

            _root = new GameObject("LGU_VaultKeypadOverlay");
            _root.transform.SetParent(_parent, worldPositionStays: false);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 110;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var backdrop = NewRect(_root.transform, "Backdrop",
                Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.color = new Color(0.01f, 0.02f, 0.03f, 0.78f);
            backdropImage.raycastTarget = false;

            var panel = NewRect(_root.transform, "Panel", Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f));
            panel.sizeDelta = new Vector2(440f, 600f);
            panel.anchoredPosition = Vector2.zero;
            var panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = new Color(0.04f, 0.08f, 0.10f, 0.96f);
            panelImage.raycastTarget = true;

            _titleText = NewText(panel, "Title", "COMPANY STASH KEYPAD", 22f, TextAlignmentOptions.Center,
                new Color(0.20f, 1f, 0.40f, 1f),
                new Vector2(16f, -560f), new Vector2(-16f, -16f));
            _displayText = NewText(panel, "Display", "_ _ _ _", 36f, TextAlignmentOptions.Center,
                Color.white,
                new Vector2(16f, -440f), new Vector2(-16f, -350f));
            _feedbackText = NewText(panel, "Feedback", "ENTER 4 DIGITS", 16f, TextAlignmentOptions.Center,
                new Color(0.7f, 0.7f, 0.7f, 1f),
                new Vector2(16f, -350f), new Vector2(-16f, -300f));

            BuildDigitGrid(panel);

            _helpText = NewText(panel, "Help", "1-9 / 0 ENTERS DIGIT    CLEAR RESETS    SUBMIT TRIES", 13f, TextAlignmentOptions.Center,
                new Color(0.6f, 0.6f, 0.6f, 1f),
                new Vector2(16f, 18f), new Vector2(-16f, 8f));

            _root.SetActive(false);
        }

        private void BuildDigitGrid(Transform parent)
        {
            float buttonSize = 70f;
            float gap = 8f;
            float gridWidth = buttonSize * 3 + gap * 2;
            float startX = -gridWidth * 0.5f + buttonSize * 0.5f;
            float startY = -300f;

            for (int digit = 0; digit < 10; digit++)
            {
                int row;
                int col;
                if (digit == 0)
                {
                    row = 3;
                    col = 1;
                }
                else
                {
                    int n = digit - 1;
                    row = n / 3;
                    col = n % 3;
                }
                float x = startX + col * (buttonSize + gap);
                float y = startY - row * (buttonSize + gap);
                _digitButtons[digit] = CreateButton(parent, $"Digit_{digit}", digit.ToString(),
                    new Vector2(buttonSize, buttonSize),
                    new Vector2(x, y),
                    () => OnDigitPressed(digit));
            }

            float actionButtonWidth = 120f;
            float actionButtonHeight = 38f;
            float actionY = startY - 4 * (buttonSize + gap) - 6f;
            float actionStartX = -((actionButtonWidth + gap) * 2.5f) + actionButtonWidth * 0.5f;
            _clearButton = CreateButton(parent, "Clear", "CLEAR",
                new Vector2(actionButtonWidth, actionButtonHeight),
                new Vector2(actionStartX, actionY),
                OnClearPressed);
            _submitButton = CreateButton(parent, "Submit", "SUBMIT",
                new Vector2(actionButtonWidth, actionButtonHeight),
                new Vector2(actionStartX + (actionButtonWidth + gap) * 2, actionY),
                OnSubmitPressed);
            _closeButton = CreateButton(parent, "Close", "CLOSE",
                new Vector2(actionButtonWidth, actionButtonHeight),
                new Vector2(actionStartX + (actionButtonWidth + gap), actionY),
                OnClosePressed);
        }

        private Button CreateButton(Transform parent, string name, string label,
            Vector2 size, Vector2 anchoredPosition, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPosition;

            var image = go.AddComponent<Image>();
            image.color = new Color(0.10f, 0.20f, 0.18f, 0.96f);

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = new Color(0.10f, 0.20f, 0.18f, 0.96f);
            colors.highlightedColor = new Color(0.16f, 0.30f, 0.26f, 1f);
            colors.pressedColor = new Color(0.20f, 0.50f, 0.40f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
            button.onClick.AddListener(() => onClick?.Invoke());

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
            var labelText = labelGo.AddComponent<TextMeshProUGUI>();
            labelText.text = label;
            labelText.fontSize = 18f;
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.color = Color.white;
            labelText.richText = false;
            return button;
        }

        public void Open(NetworkBehaviour vault, string displayLabel)
        {
            if (vault == null) return;
            EnsureBuilt();
            _activeVault = vault;
            _enteredCount = 0;
            for (int i = 0; i < _entered.Length; i++) _entered[i] = -1;
            if (_titleText != null)
            {
                _titleText.text = string.IsNullOrWhiteSpace(displayLabel) ? "COMPANY STASH KEYPAD" : displayLabel;
            }
            if (_displayText != null) _displayText.text = "_ _ _ _";
            if (_feedbackText != null)
            {
                _feedbackText.text = "ENTER 4 DIGITS";
                _feedbackText.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            }
            _root.SetActive(true);
        }

        public void Close()
        {
            if (_root != null) _root.SetActive(false);
            _activeVault = null;
        }

        private void OnDigitPressed(int digit)
        {
            if (_enteredCount >= CodeDigits) return;
            _entered[_enteredCount++] = digit;
            RefreshDisplay();
            if (_enteredCount == CodeDigits && _feedbackText != null)
            {
                _feedbackText.text = "PRESS SUBMIT";
                _feedbackText.color = new Color(0.85f, 0.85f, 0.4f, 1f);
            }
        }

        private void OnClearPressed()
        {
            _enteredCount = 0;
            for (int i = 0; i < _entered.Length; i++) _entered[i] = -1;
            RefreshDisplay();
            if (_feedbackText != null)
            {
                _feedbackText.text = "ENTER 4 DIGITS";
                _feedbackText.color = new Color(0.7f, 0.7f, 0.7f, 1f);
            }
        }

        private void OnSubmitPressed()
        {
            if (_enteredCount < CodeDigits)
            {
                if (_feedbackText != null)
                {
                    _feedbackText.text = $"NEED {CodeDigits - _enteredCount} MORE";
                    _feedbackText.color = new Color(1f, 0.6f, 0.3f, 1f);
                }
                return;
            }
            int code = 0;
            for (int i = 0; i < CodeDigits; i++) code = code * 10 + _entered[i];
            SubmitCode(code);
        }

        private void OnClosePressed()
        {
            Close();
        }

        private void SubmitCode(int code)
        {
            if (_activeVault == null) return;
            bool unlocked = false;
            string localStatus = string.Empty;

            try
            {
                System.Reflection.MethodInfo localMethod = _activeVault.GetType().GetMethod("TrySubmitCodeLocally",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (localMethod != null)
                {
                    object[] args = { code, null };
                    object result = localMethod.Invoke(_activeVault, args);
                    if (result is bool ok)
                        unlocked = ok;
                    if (args[1] is string status)
                        localStatus = status;
                }
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Vault local validation failed: {ex.Message}");
            }

            try
            {
                System.Reflection.MethodInfo method = _activeVault.GetType().GetMethod("SubmitCodeServerRpc",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (method == null) return;
                var rpcParams = default(Unity.Netcode.ServerRpcParams);
                method.Invoke(_activeVault, new object[] { code, rpcParams });
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Vault submit failed: {ex.Message}");
            }

            if (unlocked)
            {
                if (_feedbackText != null)
                {
                    _feedbackText.text = "UNLOCKED";
                    _feedbackText.color = new Color(0.2f, 1f, 0.4f, 1f);
                }
                if (_displayText != null)
                {
                    _displayText.text = code.ToString("D" + CodeDigits);
                    _displayText.color = new Color(0.2f, 1f, 0.4f, 1f);
                }
                _root.SetActive(false);
            }
            else
            {
                if (_feedbackText != null)
                {
                    _feedbackText.text = string.IsNullOrWhiteSpace(localStatus) ? "DENIED" : localStatus;
                    _feedbackText.color = new Color(1f, 0.4f, 0.4f, 1f);
                }
                OnClearPressed();
            }
        }

        private void RefreshDisplay()
        {
            if (_displayText == null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < CodeDigits; i++)
            {
                if (i > 0) sb.Append(" ");
                sb.Append(_entered[i] < 0 ? "_" : _entered[i].ToString());
            }
            _displayText.text = sb.ToString();
            _displayText.color = Color.white;
        }

        private static RectTransform NewRect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            return rt;
        }

        private TextMeshProUGUI NewText(Transform parent, string name, string text, float size, TextAlignmentOptions alignment,
            Color color, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, offsetMax.y);
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.richText = false;
            return tmp;
        }
    }
}
