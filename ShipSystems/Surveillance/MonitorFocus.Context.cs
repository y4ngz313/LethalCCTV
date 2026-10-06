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
        private static bool TryTriggerCameraContextAction()
        {
            if (Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive) return true;
            if (!IsFocused || CctvDeviceCommandLine.ConsumesInput) return true;
            if (IsHackingOverlayOpen || IsMainframeOverlayOpen) return true;

            CCTVCamera active = GetActiveCamera();
            if (active == null || active.Cam == null)
            {
                return false;
            }

            if (!TryResolveContextTarget(active.Cam, out ContextTarget target))
            {
                return false;
            }

            // Left-click first handles a supported action, otherwise squad-pings.
            if (!target.Commandable)
            {
                return false;
            }

            if (SurveillanceBootstrap.Config?.AllowTargetScanning.Value == false)
            {
                ShowTimedScanStatus("TARGET SCANNING DISABLED BY HOST", OVERLAY_SCAN_RED, 1.4f);
                return true;
            }

            _contextTarget = target;
            _scanVisibleUntil = Time.unscaledTime + OVERLAY_CONTEXT_DURATION;
            CCTVMarkerManager.PublishContextMarker(target.Position, target.Radius, target.DisplayName, 0, active.CameraIndex);

            HandleContextTargetAction(target);
            return true;
        }

        internal static void TriggerCameraPing()
        {
            if (!IsFocused || CctvDeviceCommandLine.ConsumesInput) return;
            if (IsHackingOverlayOpen) return;
            if (SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowCameraPings.Value)
            {
                ShowTimedScanStatus("CAMERA PINGS DISABLED BY HOST", OVERLAY_SCAN_RED, 1.4f);
                return;
            }

            CCTVCamera active = GetActiveCamera();
            if (active == null || active.Cam == null)
            {
                _scanVisibleUntil = Time.unscaledTime + 0.9f;
                ShowScanStatus("NO ACTIVE CAMERA", OVERLAY_SCAN_RED);
                HideScanLabels();
                return;
            }

            if (!CctvSquadPing.Request(active)) return;
            _scanVisibleUntil = Time.unscaledTime + 0.9f;
            ShowScanStatus("PING SENT", OVERLAY_ACTIVE_GREEN);
            HideScanLabels();

            PlayPingSfx();

            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Squad outline requested from CAM_{active.CameraIndex:D2}.");
        }

        private static bool TryResolveContextTarget(Camera cam, out ContextTarget target)
        {
            target = null;
            if (cam == null) return false;

            if (CctvSquadPing.TryRayHit(cam.transform.position, cam.transform.forward, out RaycastHit hit) &&
                TryResolveAllowedContextTarget(hit.transform, hit.point, out target))
            {
                return true;
            }

            // Do not turn a click on scenery into a nearby mainframe action.
            return false;
        }

        private static bool TryResolveAllowedContextTarget(Transform hitTransform, Vector3 hitPoint, out ContextTarget target)
        {
            target = null;
            if (hitTransform == null) return false;

            if (TryResolveCommandTarget(hitTransform, hitPoint, out target))
            {
                return true;
            }

            GrabbableObject item = hitTransform.GetComponentInParent<GrabbableObject>();
            if (item != null && !item.isHeld && !item.isHeldByEnemy)
            {
                target = CreateItemTarget(item);
                return true;
            }

            EnemyAI enemy = hitTransform.GetComponentInParent<EnemyAI>();
            if (enemy != null && !enemy.isEnemyDead)
            {
                target = CreateEnemyTarget(enemy);
                return true;
            }

            ScanNodeProperties node = hitTransform.GetComponent<ScanNodeProperties>() ?? hitTransform.GetComponentInParent<ScanNodeProperties>();
            if (IsAllowedInfoNode(node))
            {
                target = CreateInfoNodeTarget(node);
                return true;
            }

            return false;
        }

        private static bool TryResolveCommandTarget(Transform hitTransform, Vector3 hitPoint, out ContextTarget target)
        {
            target = null;
            if (hitTransform == null) return false;



            Landmine mine = hitTransform.GetComponentInParent<Landmine>();
            if (mine != null)
            {
                target = CreateContextTarget(mine, "MINE", "enable / disable", mine.transform.position, OVERLAY_ACTIVE_GREEN);
                return true;
            }





            if (CctvCommandTargetBridge.TryDescribeTarget(hitTransform, out CctvCommandTargetBridge.TargetInfo supportTarget))
            {
                target = CreateContextTarget(
                    supportTarget.Component,
                    supportTarget.DisplayName,
                    supportTarget.CommandHint,
                    supportTarget.Position,
                    OVERLAY_ACTIVE_GREEN,
                    supportTarget.Radius);
                return true;
            }

            return false;
        }

        private static bool TryFindSnapContextTarget(Camera cam, out ContextTarget target)
        {
            target = null;
            if (cam == null) return false;

            float bestScore = float.PositiveInfinity;

            Landmine[] mines = UnityEngine.Object.FindObjectsOfType<Landmine>();
            for (int i = 0; i < mines.Length; i++)
            {
                Landmine mine = mines[i];
                if (mine == null) continue;
                TryChooseSnapTarget(cam, CreateContextTarget(mine, "MINE", "enable / disable", mine.transform.position, OVERLAY_ACTIVE_GREEN), ref target, ref bestScore);
            }

            List<CctvCommandTargetBridge.TargetInfo> supportTargets = CctvCommandTargetBridge.GetActiveTargets();
            for (int i = 0; i < supportTargets.Count; i++)
            {
                CctvCommandTargetBridge.TargetInfo supportTarget = supportTargets[i];
                if (supportTarget == null || supportTarget.Component == null) continue;
                TryChooseSnapTarget(
                    cam,
                    CreateContextTarget(
                        supportTarget.Component,
                        supportTarget.DisplayName,
                        supportTarget.CommandHint,
                        supportTarget.Position,
                        OVERLAY_ACTIVE_GREEN,
                        supportTarget.Radius),
                    ref target,
                    ref bestScore);
            }

            GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item == null || item.isHeld || item.isHeldByEnemy) continue;
                TryChooseSnapTarget(cam, CreateItemTarget(item), ref target, ref bestScore);
            }

            RoundManager round = RoundManager.Instance;
            if (round != null && round.SpawnedEnemies != null)
            {
                for (int i = 0; i < round.SpawnedEnemies.Count; i++)
                {
                    EnemyAI enemy = round.SpawnedEnemies[i];
                    if (enemy == null || enemy.isEnemyDead || enemy.isOutside || enemy.isInsidePlayerShip) continue;
                    TryChooseSnapTarget(cam, CreateEnemyTarget(enemy), ref target, ref bestScore);
                }
            }

            ScanNodeProperties[] nodes = UnityEngine.Object.FindObjectsOfType<ScanNodeProperties>();
            for (int i = 0; i < nodes.Length; i++)
            {
                ScanNodeProperties node = nodes[i];
                if (!IsAllowedInfoNode(node)) continue;
                TryChooseSnapTarget(cam, CreateInfoNodeTarget(node), ref target, ref bestScore);
            }

            return target != null;
        }

        private static void TryChooseSnapTarget(Camera cam, ContextTarget candidate, ref ContextTarget best, ref float bestScore)
        {
            if (cam == null || candidate == null) return;
            Vector3 viewport = cam.WorldToViewportPoint(candidate.Position);
            if (viewport.z <= 0f || viewport.z > CONTEXT_RAY_DISTANCE) return;

            float dx = viewport.x - 0.5f;
            float dy = viewport.y - 0.5f;
            float viewportDistance = Mathf.Sqrt(dx * dx + dy * dy);
            if (viewportDistance > CONTEXT_SNAP_VIEWPORT_RADIUS) return;

            float score = viewportDistance * 10f + viewport.z * 0.0025f + (candidate.Commandable ? -0.08f : 0f);
            if (score >= bestScore) return;

            bestScore = score;
            best = candidate;
        }

        private static ContextTarget CreateItemTarget(GrabbableObject item)
        {
            string name = item != null && item.itemProperties != null && !string.IsNullOrEmpty(item.itemProperties.itemName)
                ? item.itemProperties.itemName.ToUpperInvariant()
                : CleanTargetName(item != null ? item.name : "ITEM");
            return new ContextTarget
            {
                Component = item,
                DisplayName = name,
                CommandHint = "",
                Position = item != null ? item.transform.position : Vector3.zero,
                Radius = EstimateContextRadius(item != null ? item.gameObject : null, item != null ? item.transform.position : Vector3.zero),
                Commandable = false,
                Color = OVERLAY_SCAN_BLUE,
                ExpiresAt = Time.unscaledTime + OVERLAY_CONTEXT_DURATION,
            };
        }

        private static ContextTarget CreateEnemyTarget(EnemyAI enemy)
        {
            string name = enemy != null && enemy.enemyType != null && !string.IsNullOrEmpty(enemy.enemyType.enemyName)
                ? enemy.enemyType.enemyName.ToUpperInvariant()
                : CleanTargetName(enemy != null ? enemy.name : "HOSTILE");
            return new ContextTarget
            {
                Component = enemy,
                DisplayName = name,
                CommandHint = "",
                Position = enemy != null ? enemy.transform.position : Vector3.zero,
                Radius = EstimateContextRadius(enemy != null ? enemy.gameObject : null, enemy != null ? enemy.transform.position : Vector3.zero),
                Commandable = false,
                Color = OVERLAY_SCAN_RED,
                ExpiresAt = Time.unscaledTime + OVERLAY_CONTEXT_DURATION,
            };
        }

        private static ContextTarget CreateInfoNodeTarget(ScanNodeProperties node)
        {
            Color color = ColorForScanNode(node);
            return new ContextTarget
            {
                Component = node,
                DisplayName = string.IsNullOrEmpty(node.headerText) ? "CCTV TARGET" : node.headerText.ToUpperInvariant(),
                CommandHint = "",
                Position = node.transform.position,
                Radius = EstimateContextRadius(node.gameObject, node.transform.position),
                Commandable = false,
                Color = color,
                ExpiresAt = Time.unscaledTime + OVERLAY_CONTEXT_DURATION,
            };
        }

        private static bool IsAllowedInfoNode(ScanNodeProperties node)
        {
            if (node == null || !node.gameObject.activeInHierarchy) return false;
            if (node.GetComponentInParent<GrabbableObject>() != null) return true;
            if (node.GetComponentInParent<EnemyAI>() != null) return true;
            if (node.GetComponentInParent<Turret>() != null) return true;
            if (node.GetComponentInParent<Landmine>() != null) return true;
            if (node.GetComponentInParent<DoorLock>() != null) return true;
            if (node.GetComponentInParent<TerminalAccessibleObject>() != null) return true;

            string header = node.headerText;
            if (string.IsNullOrWhiteSpace(header)) return false;
            if (header.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (header.IndexOf("wall connector", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return header.IndexOf("contract", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   header.IndexOf("objective", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   header.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   node.nodeType == 2;
        }

        private static ContextTarget CreateContextTarget(Component component, string displayName, string commandHint, Vector3 position, Color color, float radius = -1f)
        {
            return new ContextTarget
            {
                Component = component,
                DisplayName = displayName,
                CommandHint = commandHint,
                Position = position,
                Radius = radius > 0f ? radius : EstimateContextRadius(component != null ? component.gameObject : null, position),
                Commandable = true,
                Color = color,
                ExpiresAt = Time.unscaledTime + OVERLAY_CONTEXT_DURATION,
            };
        }

        private static float EstimateContextRadius(GameObject root, Vector3 fallbackPosition)
        {
            float radius = 0.65f;
            if (root == null) return radius;

            Collider[] colliders = root.GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null) continue;
                radius = Mathf.Max(radius, collider.bounds.extents.magnitude * 0.55f);
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                radius = Mathf.Max(radius, renderer.bounds.extents.magnitude * 0.55f);
            }

            if (radius <= 0.65f)
            {
                radius = Mathf.Max(radius, Vector3.Distance(root.transform.position, fallbackPosition) + 0.45f);
            }

            return Mathf.Clamp(radius, 0.45f, 3.0f);
        }

        private static HackingOverlay _hackingOverlay;
        internal static HackingOverlay HackingOverlay => _hackingOverlay;
        private static MainframeControlOverlay _mainframeOverlay;
        internal static MainframeControlOverlay MainframeOverlay => _mainframeOverlay;
        private static bool IsMainframeOverlayOpen => _mainframeOverlay != null && _mainframeOverlay.IsOpen;

        private static void PrepareInteractiveOverlayDock()
        {
            Vector2 dockPosition;
            Vector2 dockSize;

            bool hackingReady = _hackingOverlay == null || CCTVVanillaMonitorDisplay.TryAttachToLowerLeftOverlay(_hackingOverlay.RootObject);
            bool mainframeReady = _mainframeOverlay == null || CCTVVanillaMonitorDisplay.TryAttachToLowerLeftOverlay(_mainframeOverlay.RootObject);
            bool useVanillaDock = hackingReady && mainframeReady;

            if (useVanillaDock)
            {
                dockPosition = CCTVVanillaMonitorDisplay.GetLowerLeftOverlayDockPosition();
                dockSize = CCTVVanillaMonitorDisplay.GetLowerLeftOverlayDockSize();
            }
            else
            {
                RestoreInteractiveOverlaysToFocusOverlay();
                dockPosition = GetBottomLeftDockPosition();
                dockSize = GetBottomLeftDockSize();
            }

            _hackingOverlay?.SetDock(dockPosition, dockSize);
            _mainframeOverlay?.SetDock(dockPosition, dockSize);
        }

        private static void RestoreInteractiveOverlaysToFocusOverlay()
        {
            if (_overlayRoot == null)
                return;

            if (_hackingOverlay != null)
            {
                _hackingOverlay.SetParent(_overlayRoot.transform);
                SetLayerRecursive(_hackingOverlay.RootObject, PHYSICAL_FOCUS_UI_LAYER);
            }

            if (_mainframeOverlay != null)
            {
                _mainframeOverlay.SetParent(_overlayRoot.transform);
                SetLayerRecursive(_mainframeOverlay.RootObject, PHYSICAL_FOCUS_UI_LAYER);
            }
        }

        private static void TickInteractiveOverlays()
        {
            if (IsHackingOverlayOpen)
            {
                _hackingOverlay.Tick();
                if (_hackingOverlay.ConsumeSolved())
                {
                    OpenMainframeOverlayForCurrent();
                }
                else if (_hackingOverlay.ConsumeEscapeIfOpen())
                {
                    RefreshFocusDockLayout();
                    ShowScanStatus("HACK CANCELED", OVERLAY_SCAN_RED);
                }
                return;
            }

            if (IsMainframeOverlayOpen)
            {
                _mainframeOverlay.Tick();
            }
        }

        private static void HandleContextTargetAction(ContextTarget target)
        {
            if (target == null || target.Component == null)
                return;

            if (TryOpenMainframeFromContextTarget(target))
                return;

            if (!TryGetDefaultContextCommand(target, out string command))
            {
                ShowTimedScanStatus("NO DIRECT ACTION", OVERLAY_SCAN_RED, 1.4f);
                return;
            }

            if (TryExecuteCommand(command, out string status, out Color color))
            {
                CCTVStationEvents.RaiseActionButtonPressed("object-action-accepted");
                PlayInteractSfx();
                ShowTimedScanStatus(status, color, 1.4f);
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] CCTV direct action target='{target.DisplayName}' command='{command}' status='{status}'.");
                return;
            }

            ShowTimedScanStatus(status, color, 1.4f);
        }

        private static bool TryGetDefaultContextCommand(ContextTarget target, out string command)
        {
            command = null;
            if (target == null || target.Component == null)
                return false;

            if (target.Component is Turret || target.Component is Landmine)
            {
                command = "disable";
                return true;
            }

            if (target.Component is TerminalAccessibleObject || target.Component is DoorLock)
            {
                command = "open";
                return true;
            }

            string hint = target.CommandHint;
            if (CctvCommandTargetBridge.TryDescribeTarget(target.Component, out CctvCommandTargetBridge.TargetInfo info))
                hint = info.CommandHint;

            return TryResolveCommandHint(hint, out command);
        }

        private static bool TryResolveCommandHint(string hint, out string command)
        {
            command = null;
            string normalized = (hint ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
                return false;

            if (normalized == "hack" || normalized == "lure" || normalized == "activate")
            {
                command = normalized;
                return true;
            }

            if (normalized.IndexOf("disable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("enable", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                command = "disable";
                return true;
            }

            if (normalized.IndexOf("open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                command = "open";
                return true;
            }

            if (normalized.IndexOf("on", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("off", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                command = "on";
                return true;
            }

            return false;
        }

        private static bool TryOpenMainframeFromContextTarget(ContextTarget target)
        {
            if (!IsMainframeContextTarget(target))
                return false;

            if (Y4NGZCompany.Facility.Security.CctvSupportApi.IsMainframeHacked)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeControl] Mainframe already hacked; opening control menu from CCTV target click for target='{target.DisplayName}'.");
                ShowScanStatus("MAINFRAME ONLINE", OVERLAY_ACTIVE_GREEN);
                OpenMainframeOverlayForCurrent();
                if (IsMainframeOverlayOpen) CCTVStationEvents.RaiseActionButtonPressed("object-action-accepted");
                return true;
            }

            if (SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowRemoteHacking.Value)
            {
                ShowTimedScanStatus("REMOTE HACKING DISABLED BY HOST", OVERLAY_SCAN_RED, 1.4f);
                return true;
            }

            if (_hackingOverlay == null)
            {
                ShowTimedScanStatus("HACK UNAVAILABLE", OVERLAY_SCAN_RED, 1.4f);
                return true;
            }

            if (TryExecuteCommand("hack", out string status, out Color color))
            {
                if (!string.IsNullOrEmpty(status) &&
                    status.IndexOf("ALREADY HACKED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeControl] Mainframe returned already hacked; opening control menu from CCTV target click for target='{target.DisplayName}'.");
                    ShowScanStatus("MAINFRAME ONLINE", OVERLAY_ACTIVE_GREEN);
                    OpenMainframeOverlayForCurrent();
                    if (IsMainframeOverlayOpen) CCTVStationEvents.RaiseActionButtonPressed("object-action-accepted");
                    return true;
                }

                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Opening CCTV hack terminal from target click target='{target.DisplayName}' status='{status}'.");
                PlayInteractSfx();
                ShowScanStatus("HACK STARTED", OVERLAY_ACTIVE_GREEN);
                OpenHackingOverlayForCurrent();
                if (IsHackingOverlayOpen) CCTVStationEvents.RaiseActionButtonPressed("object-action-accepted");
                return true;
            }

            ShowTimedScanStatus(status, color, 1.4f);
            return true;
        }

        private static bool IsMainframeContextTarget(ContextTarget target)
        {
            if (target == null || target.Component == null)
                return false;

            if (string.Equals(target.CommandHint, "hack", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(target.DisplayName, "MAINFRAME", StringComparison.OrdinalIgnoreCase))
                return true;

            if (CctvCommandTargetBridge.TryDescribeTarget(target.Component, out CctvCommandTargetBridge.TargetInfo info))
            {
                if (string.Equals(info.CommandHint, "hack", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(info.DisplayName, "MAINFRAME", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static void OpenHackingOverlayForCurrent()
        {
            if (_contextTarget == null || _contextTarget.Component == null) return;
            PrepareInteractiveOverlayDock();
            _hackingOverlay?.Open(_contextTarget.Component);
            RefreshFocusDockLayout();
        }

        // Swap the (solved) hacking overlay for the mainframe control menu in the same dock.
        private static void OpenMainframeOverlayForCurrent()
        {
            if (_mainframeOverlay == null) return;
            Component mainframe = _hackingOverlay != null ? _hackingOverlay.ActiveMainframe : null;
            if (mainframe == null && _contextTarget != null) mainframe = _contextTarget.Component;
            if (mainframe == null) return;

            _hackingOverlay?.Close("hack-solved-to-mainframe");
            PrepareInteractiveOverlayDock();
            _mainframeOverlay.Open(mainframe);
            RefreshFocusDockLayout();
            PlayInteractSfx();
            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV][MainframeControl] Opened mainframe control menu after hack success.");
        }

        private static bool TryExecuteCommand(string raw, out string status, out Color color)
        {
            status = "INVALID COMMAND";
            color = OVERLAY_SCAN_RED;

            if (_contextTarget == null || _contextTarget.Component == null)
            {
                status = "TARGET LOST";
                return false;
            }

            string command = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(command)) return false;
            if (command == "hack" && SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowRemoteHacking.Value)
            {
                status = "REMOTE HACKING DISABLED BY HOST";
                color = OVERLAY_SCAN_RED;
                return false;
            }



            if (_contextTarget.Component is Landmine mine)
            {
                if (IsDisableCommand(command))
                {
                    if (!TrySpendCommandPower(out status, out color)) return false;
                    mine.ToggleMine(false);
                    status = "MINE DISABLE";
                    color = OVERLAY_ACTIVE_GREEN;
                    return true;
                }
                if (IsEnableCommand(command))
                {
                    if (!TrySpendCommandPower(out status, out color)) return false;
                    mine.ToggleMine(true);
                    status = "MINE ENABLE";
                    color = OVERLAY_ACTIVE_GREEN;
                    return true;
                }
                status = "USE ENABLE / DISABLE";
                return false;
            }





            if (CctvCommandTargetBridge.TryDescribeTarget(_contextTarget.Component, out _))
            {
                bool success = CctvCommandTargetBridge.TryExecuteCommand(_contextTarget.Component, command, out status);
                color = success ? OVERLAY_ACTIVE_GREEN : OVERLAY_SCAN_RED;
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] CCTV support command target='{_contextTarget.DisplayName}' command='{command}' success={success} status='{status}'.");
                return success;
            }

            status = "UNSUPPORTED TARGET";
            return false;
        }

        private static bool TrySpendCommandPower(out string status, out Color color)
        {
            status = "INSUFFICIENT POWER";
            color = OVERLAY_SCAN_RED;
            return CCTVShipSystemsBridge.TrySpendRemoteOperationPower();
        }

        private static bool IsDisableCommand(string command)
        {
            return command == "disable" || command == "off" || command == "deactivate";
        }

        private static bool IsEnableCommand(string command)
        {
            return command == "enable" || command == "on" || command == "activate";
        }

        private static bool IsOpenCommand(string command)
        {
            return command == "open" || command == "unlock";
        }

        private static bool IsCloseCommand(string command)
        {
            return command == "close" || command == "shut";
        }

        private static string CleanTargetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "CCTV TARGET";
            return name.Replace("(Clone)", string.Empty).Trim().ToUpperInvariant();
        }

        internal static void ClearZoomState()
        {
            _zoomByCamera.Clear();
        }

        internal static void NotifyCameraRotated(float deltaMagnitude)
        {
            if (!IsFocused || deltaMagnitude <= 0.0001f) return;
            BoostStationCameraRenderCadence();
            if (Time.unscaledTime < _nextRotateSfxTime) return;
            _nextRotateSfxTime = Time.unscaledTime + 0.16f;
            PlayFocusSfx(_rotateSfx, Mathf.Clamp(0.20f + deltaMagnitude * 0.025f, 0.20f, 0.38f));
        }

        private static void ApplyZoomToActiveCamera()
        {
            CCTVCamera active = GetActiveCamera();
            if (active == null || active.Cam == null) return;
            ApplyZoom(active);
        }

        private static float GetZoom(CCTVCamera camera)
        {
            if (camera == null) return 0f;
            return _zoomByCamera.TryGetValue(camera, out float zoom) ? zoom : 0f;
        }

        private static void ApplyZoom(CCTVCamera camera)
        {
            if (camera?.Cam == null) return;

            float baseFov = SurveillanceBootstrap.Config != null ? Mathf.Min(SurveillanceBootstrap.Config.FieldOfView.Value, Y4NGZCompany.Facility.Cameras.Placement.CameraPlacementSafety.VerticalFov) : camera.Cam.fieldOfView;
            float minFov = Mathf.Clamp(ZOOM_MIN_FOV, 8f, Mathf.Max(8f, baseFov - 1f));
            float zoom = GetZoom(camera);
            camera.Cam.fieldOfView = Mathf.Lerp(baseFov, minFov, zoom);
        }

        private static string FormatZoom(CCTVCamera camera)
        {
            if (camera?.Cam == null) return "1.0X";
            float baseFov = Mathf.Max(1f, SurveillanceBootstrap.Config != null ? Mathf.Min(SurveillanceBootstrap.Config.FieldOfView.Value, Y4NGZCompany.Facility.Cameras.Placement.CameraPlacementSafety.VerticalFov) : camera.Cam.fieldOfView);
            float fov = Mathf.Max(1f, camera.Cam.fieldOfView);
            return (baseFov / fov).ToString("0.0") + "X";
        }

    }
}
