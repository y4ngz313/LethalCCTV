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
        private static void TickPlacementEditorInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f2Key.wasPressedThisFrame)
            {
                ToggleCameraPlacementEditMode();
                return;
            }

            if (_stationEditMenuOpen || Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive || CCTVOperatorStation.IsDebugPlacementActive)
                return;

            if (_placementEditorOpen)
            {
                TickOpenPlacementEditor(keyboard);
                return;
            }

            if (_reviewMenuOpen) return;
            if (_hackingOverlay != null && _hackingOverlay.IsOpen) return;
            if (!_cameraPlacementEditModeOpen) return;

            if (keyboard.f5Key.wasPressedThisFrame || keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] PLACEMENT_EDITOR_F5 activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'");
                OpenPlacementEditor();
            }
            else if (keyboard.f6Key.wasPressedThisFrame || keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] PLACEMENT_EDITOR_F6 activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'");
                CreateManualCameraAndEdit();
            }
            else if (keyboard.f4Key.wasPressedThisFrame)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] CAMERA_REVIEW_OPEN_KEY activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'");
                OpenCameraReviewMenu();
            }
            else if (keyboard.deleteKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame)
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] PLACEMENT_EDITOR_DELETE_KEY activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'");
                DeleteActiveCamera();
            }
        }

        private static void ToggleCameraPlacementEditMode()
        {
            if (_stationEditMenuOpen || Y4NGZPlayerAnimationBridge.IsFirstPersonHandEditModeActive || CCTVOperatorStation.IsDebugPlacementActive)
                return;

            if (_cameraPlacementEditModeOpen)
            {
                if (_placementEditorOpen)
                {
                    CancelPlacementEditor(restore: true);
                    ShowTimedScanStatus("PLACEMENT CANCELED", OVERLAY_SCAN_RED, 1.2f);
                }
                CloseCameraReviewMenu();
                _cameraPlacementEditModeOpen = false;
                ShowTimedScanStatus("CAMERA EDIT OFF", OVERLAY_SCAN_BLUE, 1.0f);
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Camera placement edit mode closed.");
                return;
            }

            _cameraPlacementEditModeOpen = true;
            ShowTimedScanStatus("CAMERA EDIT", OVERLAY_ACTIVE_GREEN, 1.0f);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Camera placement edit mode opened activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'.");
        }

        private static void TickOpenPlacementEditor(Keyboard keyboard)
        {
            if (_placementEditSession?.Transform == null)
            {
                CancelPlacementEditor(restore: false);
                ShowTimedScanStatus("PLACEMENT TARGET LOST", OVERLAY_SCAN_RED, 1.4f);
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame || keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame)
            {
                CancelPlacementEditor(restore: true);
                ShowTimedScanStatus("PLACEMENT CANCELED", OVERLAY_SCAN_RED, 1.4f);
                return;
            }

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] PLACEMENT_EDITOR_ENTER label='{_placementEditSession.Label}' kind={_placementEditSession.ObjectKind} activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'");
                SavePlacementEditor();
                return;
            }

            bool changed = ApplyPlacementEditorInput(keyboard);
            if (changed)
                RefreshPlacementEditorText();
        }

        private static void OpenPlacementEditor()
        {
            if (!TryResolvePlacementEditSession(out PlacementEditSession session, out string reason))
            {
                ShowTimedScanStatus(reason, OVERLAY_SCAN_RED, 1.4f);
                return;
            }

            CloseCameraReviewMenu();
            _hackingOverlay?.Close();

            _placementEditSession = session;
            _placementEditorOpen = true;
            _cameraPlacementEditModeOpen = true;
            _placementEditorRoot?.SetActive(true);
            HideScanLabels();
            RefreshPlacementEditorText();
            ShowTimedScanStatus("PLACEMENT EDIT", OVERLAY_ACTIVE_GREEN, 1.0f);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] PLACEMENT_EDITOR_OPEN kind={session.ObjectKind} label='{session.Label}' tile='{session.Tile?.name}' pos=({session.StartPosition.x:F2},{session.StartPosition.y:F2},{session.StartPosition.z:F2}) yaw={session.StartRotation.eulerAngles.y:F1}");
        }

        private static void CreateManualCameraAndEdit()
        {
            CCTVCamera active = GetActiveCamera();
            if (active == null || active.transform == null || active.OwningTile == null)
            {
                ShowTimedScanStatus("NO CAMERA TO CLONE", OVERLAY_SCAN_RED, 1.4f);
                return;
            }

            int previousPage = QuadCameraAssignment.CurrentPage;
            int previousSlot = ActiveSlot;
            active.ResetOffsets();
            active.ApplyOffsets();
            CCTVCamera manual = CreateManualCamera(active);
            if (manual == null)
            {
                ShowTimedScanStatus("CAMERA ADD FAILED", OVERLAY_SCAN_RED, 1.4f);
                return;
            }

            QuadCameraAssignment.RegisterSupplementaryCamera(manual);
            bool focusedNewCamera = FocusCamera(manual, "manual-camera-created");
            CloseCameraReviewMenu();
            _hackingOverlay?.Close();

            _placementEditSession = new PlacementEditSession
            {
                Transform = manual.transform,
                Camera = manual,
                Tile = manual.OwningTile,
                ObjectKind = "camera",
                ObjectId = manual.AuthoredPlacementId,
                Label = manual.ResolvedLabel,
                StartPosition = manual.transform.position,
                StartRotation = manual.transform.rotation,
                CreatedDuringEdit = true,
                PreviousPage = previousPage,
                PreviousSlot = previousSlot,
            };
            _placementEditorOpen = true;
            _cameraPlacementEditModeOpen = true;
            _placementEditorRoot?.SetActive(true);
            HideScanLabels();
            RefreshPlacementEditorText();
            ShowTimedScanStatus(focusedNewCamera ? "NEW CAMERA SELECTED" : "NEW CAMERA", OVERLAY_ACTIVE_GREEN, 1.0f);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] PLACEMENT_EDITOR_ADD_CAMERA label='{manual.ResolvedLabel}' id='{manual.AuthoredPlacementId}' tile='{manual.OwningTile?.name}' previousPage={previousPage + 1} previousSlot={previousSlot} focusedNewCamera={focusedNewCamera} activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'");
        }

        private static CCTVCamera CreateManualCamera(CCTVCamera source)
        {
            if (source == null || source.OwningTile == null || source.transform == null)
                return null;

            Tile tile = source.OwningTile;
            int index = NextCameraIndex();
            var go = new GameObject($"LethalCCTVCamera_{index}_Manual_{tile.name}");
            go.transform.SetParent(tile.transform, worldPositionStays: false);
            Vector3 offset = source.transform.right * 0.25f;
            go.transform.SetPositionAndRotation(source.transform.position + offset, source.transform.rotation);

            Camera cam = go.AddComponent<Camera>();
            CCTVCamera holder = go.AddComponent<CCTVCamera>();
            holder.CameraIndex = index;
            holder.DisplayLabel = BuildUniqueManualCameraLabel(index);
            holder.OwningTile = tile;
            holder.Cam = cam;
            holder.HdrpData = null;
            holder.PlacementCornerLocal = Vector3.zero;
            holder.PlacementCornerId = 888;
            holder.MountMode = source.MountMode;
            holder.DrivingRoomHeightM = source.DrivingRoomHeightM;
            holder.PlacementSource = "manual-unsaved";
            holder.PlacementSurfaceKind = "manual";
            holder.AuthoredPlacementId = "manual-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            holder.ConfigureDisabled();
            holder.CaptureBaseline();
            CCTVCameraVisual.AttachTo(holder);
            return holder;
        }

        // PLACE MAINFRAME — live preview/placement of the authored mainframe.
        //
        // Tradeoff (documented per the spec): the CCTV architecture renders cameras to
        // RenderTextures shown on the focus overlay; the player's real view stays locked on the
        // monitor wall. Rather than teleport the player body to a third-person camera (which would
        // disturb the player's world state), we move a TEMPORARY framing camera to look at the
        // mainframe preview and bind it to the active pane. The operator sees the mainframe in
        // third-person through the CCTV feed while their body stays put. The preview IS the real
        // mainframe instance (correct visual), moved directly; commit just saves its authored pose.
        private static string BuildUniqueManualCameraLabel(int cameraIndex)
        {
            int displayNumber = Mathf.Max(0, cameraIndex + 1);
            for (int attempt = 0; attempt < 200; attempt++)
            {
                string candidate = "CAM_" + (displayNumber + attempt).ToString("D2") + "M";
                if (!CameraLabelExists(candidate))
                    return candidate;
            }

            return "CAM_" + cameraIndex.ToString("D2") + "M";
        }

        private static bool CameraLabelExists(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            CCTVCamera[] cameras = UnityEngine.Object.FindObjectsOfType<CCTVCamera>(includeInactive: true);
            for (int i = 0; i < cameras.Length; i++)
            {
                CCTVCamera camera = cameras[i];
                if (camera == null)
                    continue;
                if (string.Equals(camera.ResolvedLabel, label, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static int NextCameraIndex()
        {
            CCTVCamera[] cameras = UnityEngine.Object.FindObjectsOfType<CCTVCamera>(includeInactive: true);
            int max = -1;
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].CameraIndex > max)
                    max = cameras[i].CameraIndex;
            }
            return max + 1;
        }

        private static void DeleteActiveCamera()
        {
            CCTVCamera active = GetActiveCamera();
            if (active == null || active.OwningTile == null)
            {
                ShowTimedScanStatus("NO CAMERA TO DELETE", OVERLAY_SCAN_RED, 1.4f);
                return;
            }

            bool saved = InteriorAuthoredPlacementStore.RecordCameraDeletion(active, out string message);
            if (!saved)
            {
                ShowTimedScanStatus("DELETE SAVE FAILED", OVERLAY_SCAN_RED, 1.6f);
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] PLACEMENT_EDITOR_DELETE_FAILED message='{message}'");
                return;
            }

            string label = active.ResolvedLabel;
            CancelPlacementEditor(restore: false);
            QuadCameraAssignment.RemoveCamera(active);
            UnityEngine.Object.Destroy(active.gameObject);
            ShowTimedScanStatus("CAMERA DELETED", OVERLAY_ACTIVE_GREEN, 1.6f);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] PLACEMENT_EDITOR_DELETE_CAMERA label='{label}' message='{message}'");
        }

        private static bool TryResolvePlacementEditSession(out PlacementEditSession session, out string reason)
        {
            session = null;
            reason = "NO EDIT TARGET";

            bool contextUsable = _contextTarget != null
                && _contextTarget.Commandable
                && _contextTarget.Component != null
                && Time.unscaledTime <= _contextTarget.ExpiresAt + 3f;
            if (contextUsable && CctvCommandTargetBridge.TryDescribeTarget(_contextTarget.Component, out CctvCommandTargetBridge.TargetInfo info))
            {
                Transform targetTransform = info.Component != null ? info.Component.transform : _contextTarget.Component.transform;
                Tile tile = FindTileForWorldPosition(targetTransform.position);
                if (tile == null)
                {
                    reason = "NO TILE FOR TARGET";
                    return false;
                }

                string kind = PlacementKindForSupportTarget(info.DisplayName);
                session = new PlacementEditSession
                {
                    Transform = targetTransform,
                    Tile = tile,
                    ObjectKind = kind,
                    ObjectId = kind,
                    Label = info.DisplayName ?? _contextTarget.DisplayName ?? "SUPPORT",
                    StartPosition = targetTransform.position,
                    StartRotation = targetTransform.rotation,
                };
                return true;
            }

            CCTVCamera active = GetActiveCamera();
            if (active != null && active.transform != null && active.OwningTile != null)
            {
                active.ResetOffsets();
                active.ApplyOffsets();
                session = new PlacementEditSession
                {
                    Transform = active.transform,
                    Camera = active,
                    Tile = active.OwningTile,
                    ObjectKind = "camera",
                    ObjectId = !string.IsNullOrWhiteSpace(active.AuthoredPlacementId)
                        ? active.AuthoredPlacementId
                        : "manual-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"),
                    Label = active.ResolvedLabel,
                    StartPosition = active.transform.position,
                    StartRotation = active.transform.rotation,
                };
                return true;
            }

            reason = "NO ACTIVE CAMERA";
            return false;
        }

        private static bool ApplyPlacementEditorInput(Keyboard keyboard)
        {
            PlacementEditSession session = _placementEditSession;
            if (session?.Transform == null) return false;

            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
            float moveSpeed = fast ? 8.0f : 0.75f;
            float verticalSpeed = fast ? 4.0f : 0.35f;
            float yawSpeed = fast ? 180f : 32f;

            ResolvePlacementEditorAxes(out Vector3 forward, out Vector3 right);
            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move += forward;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move -= forward;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move += right;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move -= right;
            if (keyboard.rKey.isPressed || keyboard.pageUpKey.isPressed) move += Vector3.up;
            if (keyboard.fKey.isPressed || keyboard.pageDownKey.isPressed) move -= Vector3.up;

            bool changed = false;
            if (move.sqrMagnitude > 1e-6f)
            {
                session.Transform.position += move.normalized * moveSpeed * dt;
                changed = true;
            }

            float yaw = 0f;
            if (keyboard.qKey.isPressed) yaw -= yawSpeed * dt;
            if (keyboard.eKey.isPressed) yaw += yawSpeed * dt;
            if (Mathf.Abs(yaw) > 0.001f)
            {
                session.Transform.rotation = Quaternion.AngleAxis(yaw, Vector3.up) * session.Transform.rotation;
                changed = true;
            }

            if (changed && session.Camera != null)
            {
                session.Camera.ResetOffsets();
                session.Camera.CaptureBaseline();
            }

            return changed;
        }

        private static void ResolvePlacementEditorAxes(out Vector3 forward, out Vector3 right)
        {
            Transform basis = GetActiveCamera()?.Cam != null ? GetActiveCamera().Cam.transform : _placementEditSession?.Transform;
            forward = basis != null ? basis.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            right = basis != null ? basis.right : Vector3.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(Vector3.up, forward);
            right.Normalize();
        }

        private static void SavePlacementEditor()
        {
            PlacementEditSession session = _placementEditSession;
            if (session?.Transform == null || session.Tile == null)
            {
                CancelPlacementEditor(restore: false);
                ShowTimedScanStatus("PLACEMENT SAVE FAILED", OVERLAY_SCAN_RED, 1.6f);
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] PLACEMENT_EDITOR_SAVE_ABORT reason=missing-session-or-tile.");
                return;
            }

            if (session.Camera != null)
            {
                session.Camera.ResetOffsets();
                session.Camera.CaptureBaseline();
                Tile currentTile = FindTileForWorldPosition(session.Transform.position);
                if (currentTile == null)
                {
                    ShowTimedScanStatus("NO TILE AT CAMERA", OVERLAY_SCAN_RED, 1.6f);
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] PLACEMENT_EDITOR_SAVE_ABORT label='{session.Label}' reason=no-tile-at-camera pos=({session.Transform.position.x:F2},{session.Transform.position.y:F2},{session.Transform.position.z:F2}).");
                    return;
                }
                if (!session.CreatedDuringEdit && TileHasCamera(currentTile, session.Camera))
                {
                    ShowTimedScanStatus("TILE ALREADY HAS CAMERA", OVERLAY_SCAN_RED, 1.6f);
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] PLACEMENT_EDITOR_SAVE_ABORT label='{session.Label}' reason=tile-already-has-camera tile='{currentTile.name}'.");
                    return;
                }
                session.Tile = currentTile;
                session.Camera.OwningTile = currentTile;
            }

            // Placement previews may have moved into a different tile than they started in;
            // resolve the tile under its committed position so the authored record targets the right tile.

            bool saved = InteriorAuthoredPlacementStore.RecordPlacement(
                session.Transform,
                session.Tile,
                session.ObjectKind,
                session.ObjectId,
                out string message);
            if (saved && session.Camera != null)
            {
                session.Camera.AuthoredPlacementId = session.ObjectId;
                session.Camera.PlacementSource = "authored-local";
                session.Camera.PlacementSurfaceKind = "authored";
            }
            if (session.Camera != null)
                FocusCamera(session.Camera, "placement-save");
            string label = session.Label;
            CancelPlacementEditor(restore: false);
            ShowTimedScanStatus(saved ? "PLACEMENT SAVED" : "PLACEMENT SAVE FAILED", saved ? OVERLAY_ACTIVE_GREEN : OVERLAY_SCAN_RED, 1.8f);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] PLACEMENT_EDITOR_SAVE label='{label}' saved={saved} message='{message}' activeSlot={ActiveSlot} active='{DescribeCameraForLog(GetActiveCamera())}'");
        }

        private static bool TileHasCamera(Tile tile, CCTVCamera ignore)
        {
            if (tile == null) return false;
            CCTVCamera[] cameras = UnityEngine.Object.FindObjectsOfType<CCTVCamera>(includeInactive: true);
            for (int i = 0; i < cameras.Length; i++)
            {
                CCTVCamera camera = cameras[i];
                if (camera == null || ReferenceEquals(camera, ignore)) continue;
                if (ReferenceEquals(camera.OwningTile, tile)) return true;
                if (camera.transform != null && tile.transform != null && camera.transform.IsChildOf(tile.transform))
                    return true;
            }
            return false;
        }

        private static void CancelPlacementEditor(bool restore)
        {
            PlacementEditSession session = _placementEditSession;
            if (restore && _placementEditSession?.CreatedDuringEdit == true && _placementEditSession.Camera != null)
            {
                CCTVCamera camera = _placementEditSession.Camera;
                int restorePage = _placementEditSession.PreviousPage;
                int restoreSlot = _placementEditSession.PreviousSlot;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] PLACEMENT_EDITOR_CANCEL_NEW_CAMERA camera='{DescribeCameraForLog(camera)}' restorePage={restorePage + 1} restoreSlot={restoreSlot}.");
                QuadCameraAssignment.RemoveCamera(camera);
                if (camera != null && camera.gameObject != null)
                    UnityEngine.Object.Destroy(camera.gameObject);
                if (QuadCameraAssignment.TotalPages > 0)
                    QuadCameraAssignment.GoToPage(restorePage);
                SetActiveSlot(restoreSlot);
            }
            else if (restore && _placementEditSession?.Transform != null)
            {
                _placementEditSession.Transform.SetPositionAndRotation(
                    _placementEditSession.StartPosition,
                    _placementEditSession.StartRotation);
                if (_placementEditSession.Camera != null)
                {
                    _placementEditSession.Camera.ResetOffsets();
                    _placementEditSession.Camera.CaptureBaseline();
                }
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] PLACEMENT_EDITOR_CANCEL_RESTORE label='{_placementEditSession.Label}' kind={_placementEditSession.ObjectKind}.");
            }
            else if (session != null)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] PLACEMENT_EDITOR_CLOSE label='{session.Label}' kind={session.ObjectKind} restore={restore}.");
            }

            // Placement-preview teardown: remove temporary framing cameras/previews and restore
            // the operator's prior page/slot/view. Runs for both commit and cancel.

            _placementEditorOpen = false;
            _placementEditorRoot?.SetActive(false);
            _placementEditSession = null;
        }

        private static void RefreshPlacementEditorText()
        {
            if (_placementEditorText == null) return;
            _placementEditorText.text = BuildPlacementEditorOverlayText();
        }

        private static string BuildPlacementEditorOverlayText()
        {
            PlacementEditSession session = _placementEditSession;
            if (session?.Transform == null)
            {
                return "<color=#ff5555>PLACEMENT TARGET LOST</color>";
            }

            Vector3 pos = session.Transform.position;
            Vector3 euler = session.Transform.rotation.eulerAngles;
            const string header = "PLACEMENT EDIT";
            return
                $"<color=#33ff66>{header}</color>\n" +
                $"{session.Label}  [{session.ObjectKind}]\n" +
                $"{(session.Tile != null ? session.Tile.name : "NO TILE")}\n\n" +
                $"POS  {pos.x:F2}, {pos.y:F2}, {pos.z:F2}\n" +
                $"ROT  {euler.x:F1}, {euler.y:F1}, {euler.z:F1}\n\n" +
                "<color=#ffffff>WASD/ARROWS MOVE   R/F HEIGHT   Q/E ROTATE</color>\n" +
                "<color=#aaaaaa>SHIFT FAST   ENTER SAVE   ESC/0 CANCEL   F2 CLOSE</color>";
        }

        private static string PlacementKindForSupportTarget(string displayName)
        {
            string value = (displayName ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Contains("mainframe")) return "mainframe";
            if (value.Contains("alarm")) return "alarm";
            if (value.Contains("vault")) return "vault";
            return "support";
        }

        private static Tile FindTileForWorldPosition(Vector3 worldPosition)
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0) return null;
            Tile best = null;
            float bestScore = float.PositiveInfinity;
            for (int i = 0; i < dungeon.AllTiles.Count; i++)
            {
                Tile tile = dungeon.AllTiles[i];
                if (tile == null) continue;
                Bounds bounds = tile.Bounds;
                bool inXz = worldPosition.x >= bounds.min.x - 0.25f &&
                            worldPosition.x <= bounds.max.x + 0.25f &&
                            worldPosition.z >= bounds.min.z - 0.25f &&
                            worldPosition.z <= bounds.max.z + 0.25f;
                if (!inXz) continue;
                float yPenalty = worldPosition.y < bounds.min.y - 4f || worldPosition.y > bounds.max.y + 6f ? 1000f : 0f;
                float score = yPenalty + Vector3.Distance(new Vector3(bounds.center.x, worldPosition.y, bounds.center.z), worldPosition);
                if (score >= bestScore) continue;
                bestScore = score;
                best = tile;
            }
            return best;
        }

        private static void TickCameraReviewInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (_placementEditorOpen) return;

            if (_reviewMenuOpen)
            {
                TickOpenCameraReviewMenu(keyboard);
                return;
            }

            if (_hackingOverlay != null && _hackingOverlay.IsOpen) return;
            if (!_cameraPlacementEditModeOpen) return;

            if (keyboard.f4Key.wasPressedThisFrame)
            {
                OpenCameraReviewMenu();
            }
        }

        private static void TickOpenCameraReviewMenu(Keyboard keyboard)
        {
            if (keyboard.tabKey.wasPressedThisFrame)
            {
                _reviewMenuTagMode = !_reviewMenuTagMode;
                _reviewMenuSelectedIndex = 0;
                RefreshCameraReviewMenuText();
                return;
            }

            if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame)
            {
                MoveCameraReviewSelection(-1);
            }
            else if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame)
            {
                MoveCameraReviewSelection(+1);
            }

            int selected = ReadCameraReviewNumberSelection(keyboard);
            if (selected >= 0)
            {
                SelectCameraReviewIndex(selected);
                return;
            }

            if (keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame)
            {
                CloseCameraReviewMenu();
                return;
            }

            if (_reviewMenuTagMode && keyboard.spaceKey.wasPressedThisFrame)
            {
                ToggleCameraReviewTag(_reviewMenuSelectedIndex);
                return;
            }

            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                if (_reviewMenuTagMode)
                {
                    _reviewMenuTagMode = false;
                    _reviewMenuSelectedIndex = 0;
                    RefreshCameraReviewMenuText();
                    return;
                }

                SelectCameraReviewIndex(_reviewMenuSelectedIndex);
            }
        }

        private static int ReadCameraReviewNumberSelection(Keyboard keyboard)
        {
            if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame) return 0;
            if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame) return 1;
            if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame) return 2;
            if (keyboard.digit4Key.wasPressedThisFrame || keyboard.numpad4Key.wasPressedThisFrame) return 3;
            if (keyboard.digit5Key.wasPressedThisFrame || keyboard.numpad5Key.wasPressedThisFrame) return 4;
            if (keyboard.digit6Key.wasPressedThisFrame || keyboard.numpad6Key.wasPressedThisFrame) return 5;
            if (keyboard.digit7Key.wasPressedThisFrame || keyboard.numpad7Key.wasPressedThisFrame) return 6;
            if (keyboard.digit8Key.wasPressedThisFrame || keyboard.numpad8Key.wasPressedThisFrame) return 7;
            if (keyboard.digit9Key.wasPressedThisFrame || keyboard.numpad9Key.wasPressedThisFrame) return 8;
            return -1;
        }

        private static void MoveCameraReviewSelection(int delta)
        {
            int count = _reviewMenuTagMode ? ReviewTagValues.Length : ReviewMenuRatings.Length;
            if (count <= 0) return;
            _reviewMenuSelectedIndex = ((_reviewMenuSelectedIndex + delta) % count + count) % count;
            RefreshCameraReviewMenuText();
        }

        private static void ToggleCameraReviewTag(int index)
        {
            if (index < 0 || index >= ReviewTagValues.Length) return;
            string tag = ReviewTagValues[index];
            for (int i = 0; i < _reviewSelectedTags.Count; i++)
            {
                if (!string.Equals(_reviewSelectedTags[i], tag, StringComparison.OrdinalIgnoreCase))
                    continue;

                _reviewSelectedTags.RemoveAt(i);
                RefreshCameraReviewMenuText();
                return;
            }

            _reviewSelectedTags.Add(tag);
            RefreshCameraReviewMenuText();
        }

        private static void SelectCameraReviewIndex(int index)
        {
            if (index < 0 || index >= ReviewMenuRatings.Length) return;
            CameraPlacementReviewRating rating = ReviewMenuRatings[index];
            string label = ReviewMenuLabels[index];
            RecordActiveCameraReview(rating, label);
            CloseCameraReviewMenu();
        }

        private static void OpenCameraReviewMenu()
        {
            _cameraPlacementEditModeOpen = true;
            _reviewMenuSelectedIndex = 0;
            _reviewSelectedTags.Clear();
            _reviewMenuTagMode = false;
            _reviewMenuOpen = true;
            if (_reviewMenuRoot != null)
                _reviewMenuRoot.SetActive(true);
            RefreshCameraReviewMenuText();
            _scanVisibleUntil = 0f;
            HideScanLabels();
        }

        private static void CloseCameraReviewMenu()
        {
            _reviewMenuOpen = false;
            _reviewSelectedTags.Clear();
            _reviewMenuTagMode = false;
            if (_reviewMenuRoot != null)
                _reviewMenuRoot.SetActive(false);
        }

        private static void RefreshCameraReviewMenuText()
        {
            if (_reviewMenuText == null) return;
            _reviewMenuText.text = BuildCameraReviewMenuOverlayText();
        }

        private static string BuildCameraReviewMenuOverlayText()
        {
            CCTVCamera active = GetActiveCamera();
            string label = active != null ? active.ResolvedLabel : "NO CAMERA";
            string text = $"<color=#33ff66>CAMERA REVIEW</color>\n{label}\n\n";

            if (_reviewMenuTagMode)
            {
                text += "<color=#aaaaaa>TAB RATINGS   SPACE TOGGLE TAG   ENTER RATINGS</color>\n";
                for (int i = 0; i < ReviewTagLabels.Length; i++)
                {
                    bool selected = i == _reviewMenuSelectedIndex;
                    string prefix = selected ? ">" : " ";
                    string color = selected ? "#33ff66" : "#ffffff";
                    string check = ReviewTagSelected(ReviewTagValues[i]) ? "[x]" : "[ ]";
                    text += $"<color={color}>{prefix} {check} {ReviewTagLabels[i]}</color>\n";
                }
            }
            else
            {
                text += "<color=#aaaaaa>TAB TAGS   ENTER SAVE RATING</color>\n";
                for (int i = 0; i < ReviewMenuLabels.Length; i++)
                {
                    bool selected = i == _reviewMenuSelectedIndex;
                    string prefix = selected ? ">" : " ";
                    string color = selected ? "#33ff66" : "#ffffff";
                    string hotkey = i < 9 ? $"{i + 1}." : "  ";
                    text += $"<color={color}>{prefix} {hotkey} {ReviewMenuLabels[i]}</color>\n";
                }
            }

            text += $"\n<color=#aaaaaa>SELECTED TAGS {FormatSelectedReviewTags()}</color>";
            text += "\n<color=#aaaaaa>1-9 RATING   0 CANCEL   F2 CLOSE</color>";
            return text;
        }

        private static bool ReviewTagSelected(string tag)
        {
            for (int i = 0; i < _reviewSelectedTags.Count; i++)
            {
                if (string.Equals(_reviewSelectedTags[i], tag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string FormatSelectedReviewTags()
        {
            return _reviewSelectedTags.Count > 0
                ? string.Join(",", _reviewSelectedTags.ToArray())
                : "&lt;none&gt;";
        }

        private static void RecordActiveCameraReview(CameraPlacementReviewRating rating, string label)
        {
            CCTVCamera active = GetActiveCamera();
            bool saved = CameraPlacementReviewStore.RecordReview(active, rating, _reviewSelectedTags, out string message);
            _scanVisibleUntil = Time.unscaledTime + 1.6f;
            ShowScanStatus(saved ? "REVIEW " + label + " SAVED" : "REVIEW FAILED", saved ? OVERLAY_ACTIVE_GREEN : OVERLAY_SCAN_RED);
            UpdateCameraScanOverlay();
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] CAMERA_REVIEW_INPUT rating={rating} saved={saved} message={message}");
        }

    }
}
