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
    internal static partial class MonitorFocus
    {
        /// <summary>
        /// Runs the one-shot HUD scene scans (Resources.FindObjectsOfTypeAll +
        /// full-hierarchy Graphic walks, ~64ms) ahead of the first focus so the
        /// enter frame skips them. Collection only — nothing is dimmed here.
        /// </summary>
        internal static void PrewarmFocusHudScan()
        {
            if (_focusHudSceneScanCompleted)
                return;
            try
            {
                long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                CollectFocusHudDimTargets(includeSceneScans: true);
                _focusHudSceneScanCompleted = true;
                double totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency;
                // Warmup step 2 rose to 106ms (#291): report the sub-scans so the
                // owner is visible without a second profiling round.
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][EntryPerf] PrewarmFocusHudScan total={totalMs:F1}ms " +
                    $"hudFields={_hudScanFieldsMs:F1} canvasSiblings={_hudScanCanvasSiblingsMs:F1} " +
                    $"cursor={_hudScanCursorMs:F1} prompts={_hudScanPromptsMs:F1} " +
                    $"namedChrome={_hudScanNamedChromeMs:F1} screenSpace={_hudScanScreenSpaceMs:F1} " +
                    $"(groups={_focusHudDimTargets.Count} graphics={_focusHudGraphicTargets.Count}).");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Focus HUD scan prewarm failed (will scan on first focus): {ex.Message}");
            }
        }

        private static void ApplyFocusHudDimming(bool force)
        {
            float now = Time.unscaledTime;
            bool hasCachedTargets = _focusHudDimTargets.Count > 0 || _focusHudGraphicTargets.Count > 0;
            if (!force && hasCachedTargets && now < _nextFocusHudDimRefreshAt)
            {
                ApplyStoredFocusHudDimTargets();
                EntryPerfMark("HudDim.apply-stored");
                return;
            }

            bool includeSceneScans = force && !_focusHudSceneScanCompleted;
            _nextFocusHudDimRefreshAt = now + FOCUS_PLAYER_HUD_REFRESH_INTERVAL;
            CollectFocusHudDimTargets(includeSceneScans);
            if (includeSceneScans)
                _focusHudSceneScanCompleted = true;
            ApplyStoredFocusHudDimTargets();
            EntryPerfMark("HudDim.apply-stored");
            _focusHudDimActive = _focusHudDimTargets.Count > 0 || _focusHudGraphicTargets.Count > 0;
        }

        // Sub-costs of the last CollectFocusHudDimTargets pass (#291). Written on
        // every pass (five Stopwatch reads, and the pass itself is throttled to
        // FOCUS_PLAYER_HUD_REFRESH_INTERVAL) and read by PrewarmFocusHudScan.
        private static double _hudScanFieldsMs;
        private static double _hudScanCanvasSiblingsMs;
        private static double _hudScanCursorMs;
        private static double _hudScanPromptsMs;
        private static double _hudScanNamedChromeMs;
        private static double _hudScanScreenSpaceMs;

        private static double HudScanStepMs(ref long lastAt)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = (now - lastAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lastAt = now;
            return ms;
        }

        private static void CollectFocusHudDimTargets(bool includeSceneScans)
        {
            long hudScanAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _hudScanFieldsMs = 0.0;
            _hudScanCanvasSiblingsMs = 0.0;
            _hudScanCursorMs = 0.0;
            _hudScanPromptsMs = 0.0;
            _hudScanNamedChromeMs = 0.0;
            _hudScanScreenSpaceMs = 0.0;
            HUDManager hud = HUDManager.Instance;
            if (hud != null)
            {
                AddHudFieldTargets(hud, "weightCounter");
                AddHudFieldTargets(hud, "healthText");
                AddHudFieldTargets(hud, "batteryInventoryNumber");
                AddHudFieldTargets(hud, "utilitySlotKeyText");
                AddHudFieldTargets(hud, "holdingTwoHandedItem");
                AddHudFieldTargets(hud, "totalValueText");
                AddHudFieldTargets(hud, "statusEffectText");
                AddHudFieldTargets(hud, "clockNumber");
                AddHudFieldTargets(hud, "buildModeControlTip");
                AddHudFieldTargets(hud, "healthMeter");
                AddHudFieldTargets(hud, "sprintMeterUI");
                AddHudFieldTargets(hud, "batteryIcon");
                AddHudFieldTargets(hud, "batteryMeter");
                AddHudFieldTargets(hud, "PTTIcon");
                AddHudFieldTargets(hud, "holdInteractionFillAmount");
                AddHudFieldTargets(hud, "itemOnlySlotIconFrame", preferParent: true);
                AddHudFieldTargets(hud, "itemOnlySlotIcon", preferParent: true);
                AddHudFieldTargets(hud, "itemSlotIconFrames", preferParent: true);
                AddHudFieldTargets(hud, "itemSlotIcons", preferParent: true);
                AddHudFieldTargets(hud, "itemSlotNumbers", preferParent: true);
                AddHudFieldTargets(hud, "inventorySlotIcons", preferParent: true);
                AddHudFieldTargets(hud, "HUDContainer", preferParent: false);

                if (hud.controlTipLines != null)
                {
                    for (int i = 0; i < hud.controlTipLines.Length; i++)
                    {
                        AddFocusHudDimTarget(hud.controlTipLines[i], preferParent: false);
                    }
                }
            }

            _hudScanFieldsMs = HudScanStepMs(ref hudScanAt);
            EntryPerfMark("HudScan.hud-fields");

            AddHudCanvasSiblingTargets(hud);
            _hudScanCanvasSiblingsMs = HudScanStepMs(ref hudScanAt);
            EntryPerfMark("HudScan.canvas-siblings");

            PlayerControllerB player = _focusedPlayer != null
                ? _focusedPlayer
                : (GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null);
            if (player != null)
            {
                AddFocusHudDimTarget(player.cursorTip, preferParent: false);
                AddFocusHudDimTarget(player.cursorIcon, preferParent: false);
            }
            _hudScanCursorMs = HudScanStepMs(ref hudScanAt);
            EntryPerfMark("HudScan.cursor");

            if (includeSceneScans)
            {
                AddY4ngzPromptOverlayTargets();
                _hudScanPromptsMs = HudScanStepMs(ref hudScanAt);
                EntryPerfMark("HudScan.prompt-overlays");

                AddNamedHudChromeTargets(hud);
                _hudScanNamedChromeMs = HudScanStepMs(ref hudScanAt);
                EntryPerfMark("HudScan.named-chrome");

                AddScreenSpaceHudOverlayTargets();
                _hudScanScreenSpaceMs = HudScanStepMs(ref hudScanAt);
                EntryPerfMark("HudScan.screen-space");
            }
        }

        private static void AddHudCanvasSiblingTargets(HUDManager hud)
        {
            if (hud == null)
                return;

            Transform hudContainer = ResolveHudTransform(hud, "HUDContainer");
            Transform canvasRoot = hudContainer != null && hudContainer.parent != null
                ? hudContainer.parent
                : (hud.playerScreenTexture != null && hud.playerScreenTexture.canvas != null
                    ? hud.playerScreenTexture.canvas.transform
                    : null);
            if (canvasRoot == null)
                return;

            int childCount = canvasRoot.childCount;
            for (int i = 0; i < childCount; i++)
            {
                AddFocusHudDimTarget(canvasRoot.GetChild(i), preferParent: false);
            }
        }

        private static void AddHudFieldTargets(HUDManager hud, string fieldName, bool preferParent = false)
        {
            if (hud == null || string.IsNullOrEmpty(fieldName)) return;

            try
            {
                FieldInfo field = hud.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    AddFocusHudDimValue(field.GetValue(hud), preferParent);
                    return;
                }

                PropertyInfo property = hud.GetType().GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    AddFocusHudDimValue(property.GetValue(hud), preferParent);
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Focus HUD dim field '{fieldName}' skipped: {ex.Message}");
            }
        }

        private static void AddFocusHudDimValue(object value, bool preferParent)
        {
            if (value == null) return;

            if (value is Component component)
            {
                AddFocusHudDimTarget(component, preferParent);
                return;
            }

            if (value is GameObject gameObject)
            {
                AddFocusHudDimTarget(gameObject.transform, preferParent);
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                foreach (object item in enumerable)
                {
                    AddFocusHudDimValue(item, preferParent);
                }
            }
        }

        // The overlay is bound by canvas name because LethalCCTV only soft-depends on the
        // owning plugin. The first token is the Y4NGZUI canvas (post-#660 home); the legacy
        // token still matches installs running a pre-split Y4NGZUpgrades overlay.
        private static readonly string[] PromptOverlayCanvasNameTokens =
        {
            "Y4NGZUI_PromptOverlayCanvas",
            "Y4NGZInteractive_PromptOverlayCanvas",
        };

        private static bool NameContainsPromptOverlayToken(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (string token in PromptOverlayCanvasNameTokens)
            {
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static void AddY4ngzPromptOverlayTargets()
        {
            try
            {
                Canvas[] canvases = Resources.FindObjectsOfTypeAll<Canvas>();
                foreach (Canvas canvas in canvases)
                {
                    if (canvas == null || canvas.gameObject == null || !canvas.gameObject.activeInHierarchy)
                        continue;
                    if (NameContainsPromptOverlayToken(canvas.gameObject.name))
                    {
                        AddFocusHudDimTarget(canvas.transform, preferParent: false);
                    }
                }

                TextMeshProUGUI[] texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                foreach (TextMeshProUGUI text in texts)
                {
                    if (text == null || text.gameObject == null || !text.gameObject.activeInHierarchy)
                        continue;

                    foreach (string token in PromptOverlayCanvasNameTokens)
                    {
                        Transform promptRoot = FindNamedAncestor(text.transform, token);
                        if (promptRoot != null)
                        {
                            AddFocusHudDimTarget(promptRoot, preferParent: false);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Focus HUD prompt dim scan skipped: {ex.Message}");
            }
        }

        private static void AddNamedHudChromeTargets(HUDManager hud)
        {
            if (hud == null)
                return;

            Transform hudContainer = ResolveHudTransform(hud, "HUDContainer");
            AddNamedHudChromeTargetsUnder(hudContainer);

            Transform hudCanvas = hud.playerScreenTexture != null && hud.playerScreenTexture.canvas != null
                ? hud.playerScreenTexture.canvas.transform
                : null;
            AddNamedHudChromeTargetsUnder(hudCanvas);
        }

        private static void AddNamedHudChromeTargetsUnder(Transform root)
        {
            if (root == null)
                return;

            try
            {
                Graphic[] graphics = root.GetComponentsInChildren<Graphic>(includeInactive: true);
                foreach (Graphic graphic in graphics)
                {
                    if (graphic == null || graphic.gameObject == null)
                        continue;

                    Transform transform = graphic.transform;
                    if (transform == null)
                        continue;
                    if (ShouldSkipFocusHudDimTarget(transform))
                        continue;

                    string hierarchy = BuildTransformPath(transform);
                    if (!ContainsFocusHudNameToken(hierarchy))
                        continue;

                    AddFocusHudDimTarget(transform, preferParent: false);
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Focus HUD named chrome scan skipped: {ex.Message}");
            }
        }

        private static void AddScreenSpaceHudOverlayTargets()
        {
            try
            {
                Canvas[] canvases = Resources.FindObjectsOfTypeAll<Canvas>();
                foreach (Canvas canvas in canvases)
                {
                    if (canvas == null || canvas.gameObject == null || !canvas.gameObject.activeInHierarchy)
                        continue;
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && canvas.renderMode != RenderMode.ScreenSpaceCamera)
                        continue;

                    Transform root = canvas.transform;
                    if (!ShouldSkipFocusHudDimTarget(root))
                    {
                        AddFocusHudDimTarget(root, preferParent: false);
                        continue;
                    }

                    Graphic[] graphics = canvas.GetComponentsInChildren<Graphic>(includeInactive: true);
                    for (int i = 0; i < graphics.Length; i++)
                    {
                        AddFocusHudGraphicTarget(graphics[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Focus HUD screen-space scan skipped: {ex.Message}");
            }
        }

        private static Transform ResolveHudTransform(HUDManager hud, string fieldName)
        {
            if (hud == null || string.IsNullOrEmpty(fieldName)) return null;

            try
            {
                FieldInfo field = hud.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object value = field != null ? field.GetValue(hud) : null;
                if (value == null)
                {
                    PropertyInfo property = hud.GetType().GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        value = property.GetValue(hud);
                }

                if (value is Transform transform) return transform;
                if (value is Component component) return component.transform;
                if (value is GameObject gameObject) return gameObject.transform;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Focus HUD transform '{fieldName}' skipped: {ex.Message}");
            }

            return null;
        }

        private static string BuildTransformPath(Transform transform)
        {
            if (transform == null) return string.Empty;

            string path = transform.name ?? string.Empty;
            Transform current = transform.parent;
            while (current != null)
            {
                path = (current.name ?? string.Empty) + "/" + path;
                current = current.parent;
            }

            return path;
        }

        private static bool ContainsFocusHudNameToken(string hierarchy)
        {
            if (string.IsNullOrEmpty(hierarchy)) return false;

            for (int i = 0; i < FocusHudNameTokens.Length; i++)
            {
                if (hierarchy.IndexOf(FocusHudNameTokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static Transform FindNamedAncestor(Transform transform, string nameToken)
        {
            Transform current = transform;
            while (current != null)
            {
                string objectName = current.gameObject != null ? current.gameObject.name : string.Empty;
                if (objectName.IndexOf(nameToken, StringComparison.OrdinalIgnoreCase) >= 0)
                    return current;
                current = current.parent;
            }

            return null;
        }

        private static void AddFocusHudDimTarget(Component component, bool preferParent)
        {
            if (component == null) return;
            AddFocusHudDimTarget(component.transform, preferParent);
        }

        private static void AddFocusHudDimTarget(Transform transform, bool preferParent)
        {
            if (transform == null) return;

            Transform candidate = preferParent && transform.parent != null
                ? transform.parent
                : transform;
            if (WouldDimPlayerScreenTexture(candidate) && !WouldDimPlayerScreenTexture(transform))
            {
                candidate = transform;
            }
            if (ShouldSkipFocusHudDimTarget(candidate))
                return;
            if (HasRegisteredFocusHudAncestor(candidate))
                return;

            CanvasGroup group = candidate.GetComponent<CanvasGroup>();
            bool added = false;
            if (group == null)
            {
                group = candidate.gameObject.AddComponent<CanvasGroup>();
                added = true;
            }
            if (group == null) return;

            int id = group.GetInstanceID();
            if (!_focusHudDimTargetIds.Add(id))
                return;

            _focusHudDimTargets.Add(new FocusHudDimTarget
            {
                Group = group,
                OriginalAlpha = group.alpha,
                InstanceId = id,
                AddedByUs = added,
            });
        }

        private static void AddFocusHudGraphicTarget(Graphic graphic)
        {
            if (graphic == null || graphic.gameObject == null) return;
            Transform transform = graphic.transform;
            if (ShouldSkipFocusHudDimTarget(transform)) return;
            if (HasRegisteredFocusHudAncestor(transform)) return;

            int id = graphic.GetInstanceID();
            if (!_focusHudGraphicTargetIds.Add(id))
                return;

            _focusHudGraphicTargets.Add(new FocusHudGraphicTarget
            {
                Graphic = graphic,
                OriginalAlpha = graphic.color.a,
                InstanceId = id,
            });
        }

        private static bool HasRegisteredFocusHudAncestor(Transform transform)
        {
            if (transform == null) return true;

            for (int i = 0; i < _focusHudDimTargets.Count; i++)
            {
                CanvasGroup group = _focusHudDimTargets[i]?.Group;
                if (group == null) continue;
                Transform existing = group.transform;
                if (existing != null && transform != existing && transform.IsChildOf(existing))
                    return true;
            }

            return false;
        }

        private static bool ShouldSkipFocusHudDimTarget(Transform transform)
        {
            if (transform == null || transform.gameObject == null) return true;
            if (_overlayRoot != null)
            {
                Transform overlay = _overlayRoot.transform;
                if (WouldDimProtectedTransform(transform, overlay))
                    return true;
            }
            Transform focusControlsOverlay = CCTVFocusControlsOverlay.RootTransform;
            if (WouldDimProtectedTransform(transform, focusControlsOverlay))
                return true;

            return WouldDimPlayerScreenTexture(transform);
        }

        private static bool WouldDimProtectedTransform(Transform candidate, Transform protectedRoot)
        {
            if (candidate == null || protectedRoot == null) return false;
            return candidate == protectedRoot ||
                   candidate.IsChildOf(protectedRoot) ||
                   protectedRoot.IsChildOf(candidate);
        }

        private static bool WouldDimPlayerScreenTexture(Transform transform)
        {
            if (transform == null) return false;
            HUDManager hud = HUDManager.Instance;
            Transform playerScreen = hud != null && hud.playerScreenTexture != null
                ? hud.playerScreenTexture.transform
                : null;
            return playerScreen != null && (transform == playerScreen || playerScreen.IsChildOf(transform) || transform.IsChildOf(playerScreen));
        }

        private static void ApplyStoredFocusHudDimTargets()
        {
            for (int i = _focusHudGraphicTargets.Count - 1; i >= 0; i--)
            {
                FocusHudGraphicTarget target = _focusHudGraphicTargets[i];
                Graphic graphic = target?.Graphic;
                if (graphic == null)
                {
                    if (target != null) _focusHudGraphicTargetIds.Remove(target.InstanceId);
                    _focusHudGraphicTargets.RemoveAt(i);
                    continue;
                }

                float dimAlpha = Mathf.Clamp01(target.OriginalAlpha * FOCUS_PLAYER_HUD_ALPHA);
                Color color = graphic.color;
                if (color.a > dimAlpha)
                {
                    color.a = dimAlpha;
                    graphic.color = color;
                }
            }

            for (int i = _focusHudDimTargets.Count - 1; i >= 0; i--)
            {
                FocusHudDimTarget target = _focusHudDimTargets[i];
                if (target?.Group == null)
                {
                    if (target != null) _focusHudDimTargetIds.Remove(target.InstanceId);
                    _focusHudDimTargets.RemoveAt(i);
                    continue;
                }

                float dimAlpha = Mathf.Clamp01(target.OriginalAlpha * FOCUS_PLAYER_HUD_ALPHA);
                if (target.AddedByUs || target.Group.alpha > dimAlpha)
                {
                    target.Group.alpha = dimAlpha;
                }
            }
        }

        private static void RestoreFocusHudDimming()
        {
            // #305 — called every unfocused frame from TickFrameCore. Without this
            // gate it cleared the prewarmed scan cache (and reset
            // _focusHudSceneScanCompleted) once per frame, so every prewarm was
            // discarded immediately and re-ran at 30-70ms a pass whenever the
            // station warmup or rearm fired. Only restore when dimming was
            // actually applied; a prewarm-only cache must survive untouched.
            if (!_focusHudDimActive)
                return;

            if (_focusHudGraphicTargets.Count > 0)
            {
                for (int i = _focusHudGraphicTargets.Count - 1; i >= 0; i--)
                {
                    FocusHudGraphicTarget target = _focusHudGraphicTargets[i];
                    Graphic graphic = target?.Graphic;
                    if (graphic == null) continue;

                    Color color = graphic.color;
                    color.a = target.OriginalAlpha;
                    graphic.color = color;
                }
            }

            if (_focusHudDimTargets.Count > 0)
            {
                for (int i = _focusHudDimTargets.Count - 1; i >= 0; i--)
                {
                    FocusHudDimTarget target = _focusHudDimTargets[i];
                    CanvasGroup group = target?.Group;
                    if (group == null) continue;

                    if (target.AddedByUs)
                    {
                        UnityEngine.Object.Destroy(group);
                    }
                    else
                    {
                        group.alpha = target.OriginalAlpha;
                    }
                }
            }

            _focusHudDimTargets.Clear();
            _focusHudDimTargetIds.Clear();
            _focusHudGraphicTargets.Clear();
            _focusHudGraphicTargetIds.Clear();
            _nextFocusHudDimRefreshAt = 0f;
            _focusHudDimActive = false;
            _focusHudSceneScanCompleted = false;
            // #305 — the reset above previously made every RE-entry pay the ~80ms
            // scene scans (prompt-overlays + named-chrome + screen-space) inside
            // the enter frame; the prewarm only ever covered the first entry after
            // station spawn. Re-run the prewarm on a quiet unfocused frame instead.
            _focusHudScanRearmAt = Time.unscaledTime + 1f;
        }

        private static float _focusHudScanRearmAt;

        /// <summary>Called from TickFrameCore while unfocused; re-runs the HUD
        /// scene-scan prewarm shortly after each focus exit so the next entry
        /// finds the scan already completed.</summary>
        private static void TickFocusHudScanRearm()
        {
            if (_focusHudScanRearmAt <= 0f || Time.unscaledTime < _focusHudScanRearmAt)
                return;
            _focusHudScanRearmAt = 0f;
            PrewarmFocusHudScan();
        }

    }
}
