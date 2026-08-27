using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using GameNetcodeStuff;
using Y4NGZCompany.Core.Compat;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class ShipTurretController
    {
        private static void TickPlacementEditor()
        {
            if (!_placementActive) return;

            EnsureTurretPresent();
            Transform shipRoot = ResolveShipRoot();
            if (shipRoot == null || _root == null) return;

            if (UnityEngine.InputSystem.Keyboard.current == null) return;
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                SavePlacementFromCurrentTransform(shipRoot);
                return;
            }
            if (keyboard.escapeKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame || keyboard.deleteKey.wasPressedThisFrame)
            {
                CancelPlacement();
                return;
            }

            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float moveSpeed = fast ? 8.0f : 1.8f;
            float verticalSpeed = fast ? 2.0f : 0.55f;
            float yawSpeed = fast ? 120f : 42f;
            Vector3 move = Vector3.zero;

            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move += GetPlacementEditForward();
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move -= GetPlacementEditForward();
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move += GetPlacementEditRight();
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move -= GetPlacementEditRight();

            float vertical = 0f;
            if (keyboard.eKey.isPressed || keyboard.pageUpKey.isPressed) vertical += verticalSpeed * dt;
            if (keyboard.qKey.isPressed || keyboard.pageDownKey.isPressed) vertical -= verticalSpeed * dt;

            float yaw = 0f;
            if (keyboard.rKey.isPressed) yaw -= yawSpeed * dt;
            if (keyboard.tKey.isPressed) yaw += yawSpeed * dt;

            Vector3 delta = move.sqrMagnitude > 0.0001f ? move.normalized * moveSpeed * dt : Vector3.zero;
            if (Mathf.Abs(vertical) > 0.0001f)
                delta += Vector3.up * vertical;
            if (delta.sqrMagnitude > 0f || Mathf.Abs(yaw) > 0.001f)
                NudgePlacementPreview(delta, yaw);
        }

        private static void NudgePlacementPreview(Vector3 worldDelta, float yawDegrees)
        {
            if (_root == null) return;
            Transform shipRoot = ResolveShipRoot();
            if (shipRoot != null && _root.transform.parent != shipRoot)
                _root.transform.SetParent(shipRoot, worldPositionStays: true);

            _root.transform.position += worldDelta;
            if (Mathf.Abs(yawDegrees) > 0.001f)
                _root.transform.rotation = Quaternion.Euler(0f, _root.transform.eulerAngles.y + yawDegrees, 0f);

            SetStatus($"EDIT {_placementLayout} {FormatVector(_root.transform.localPosition)}", 0.25f);
            UpdatePlacementCameraPose();
        }

        private static Vector3 GetPlacementEditForward()
        {
            Transform basis = GetPlacementControlBasis();
            Vector3 forward = basis != null ? basis.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            return forward.normalized;
        }

        private static Vector3 GetPlacementEditRight()
        {
            Transform basis = GetPlacementControlBasis();
            Vector3 right = basis != null ? basis.right : Vector3.right;
            right.y = 0f;
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
            return right.normalized;
        }

        private static Transform GetPlacementControlBasis()
        {
            return _placementCamera != null
                ? _placementCamera.transform
                : _root != null ? _root.transform : null;
        }

        private static void BeginPlacementInputCapture()
        {
            PlayerControllerB player = ResolvePlacementPlayer();
            if (player == null) return;

            if (_placementCapturedInputState)
                RestorePlacementInputCapture();

            _placementPlayer = player;
            _placementSavedLock = Cursor.lockState;
            _placementSavedCursorVisible = Cursor.visible;
            _placementSavedDisableLookInput = player.disableLookInput;
            _placementSavedDisableMoveInput = player.disableMoveInput;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            player.disableLookInput = true;
            player.disableMoveInput = true;
            _placementCapturedInputState = true;
        }

        private static void RestorePlacementInputCapture()
        {
            RestorePlacementCameraTransform();

            if (_placementCapturedInputState)
            {
                if (_placementPlayer != null)
                {
                    _placementPlayer.disableLookInput = _placementSavedDisableLookInput;
                    _placementPlayer.disableMoveInput = _placementSavedDisableMoveInput;
                }

                Cursor.lockState = _placementSavedLock;
                Cursor.visible = _placementSavedCursorVisible;
            }

            _placementCapturedInputState = false;
            _placementPlayer = null;
            _placementCamera = null;
        }

        private static void EnsurePlacementCamera()
        {
            Camera source = ResolvePlacementSourceCamera();
            if (source == null) return;

            if (!_placementCapturedCameraTransform)
            {
                _placementSavedCameraLocalPosition = source.transform.localPosition;
                _placementSavedCameraLocalRotation = source.transform.localRotation;
                _placementCapturedCameraTransform = true;
            }

            _placementCamera = source;
            _placementCamera.cullingMask |= 1 << 0;
            _placementCamera.enabled = true;
        }

        private static Camera ResolvePlacementSourceCamera()
        {
            PlayerControllerB player = _placementPlayer ?? ResolvePlacementPlayer();
            if (player?.gameplayCamera != null)
                return player.gameplayCamera;

            Camera main = Camera.main;
            if (main != null)
                return main;

            Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera != null && camera.enabled)
                    return camera;
            }

            return null;
        }

        private static PlayerControllerB ResolvePlacementPlayer()
        {
            if (GameNetworkManager.Instance != null && GameNetworkManager.Instance.localPlayerController != null)
                return GameNetworkManager.Instance.localPlayerController;
            return StartOfRound.Instance != null ? StartOfRound.Instance.localPlayerController : null;
        }

        private static void UpdatePlacementCameraPose()
        {
            if (_placementCamera == null || _root == null) return;
            if (!TryGetRendererBounds(_root, out Bounds bounds))
                bounds = new Bounds(_root.transform.position + Vector3.up * 1.4f, new Vector3(4.35f, 2.4f, 4.35f));

            Vector3 focus = bounds.center;
            Vector3 front = _root.transform.forward;
            front.y = 0f;
            if (front.sqrMagnitude < 0.0001f) front = Vector3.forward;
            front.Normalize();

            float horizontalSpan = Mathf.Max(bounds.size.x, bounds.size.z);
            float distance = Mathf.Clamp(horizontalSpan * 0.95f + 3.0f, 6.0f, 22.0f);
            float heightOffset = Mathf.Clamp(bounds.size.y * 0.75f + horizontalSpan * 0.14f, 2.6f, 8.0f);
            Vector3 cameraPosition = focus + front * distance + Vector3.up * heightOffset;
            Vector3 lookPoint = focus + Vector3.up * Mathf.Clamp(bounds.size.y * 0.05f, 0.15f, 0.65f);
            Vector3 lookDirection = lookPoint - cameraPosition;
            if (lookDirection.sqrMagnitude < 0.0001f)
                lookDirection = -front;

            _placementCamera.transform.SetPositionAndRotation(
                cameraPosition,
                Quaternion.LookRotation(lookDirection.normalized, Vector3.up));
        }

        private static void RestorePlacementCameraTransform()
        {
            if (!_placementCapturedCameraTransform) return;

            if (_placementCamera != null)
            {
                Transform cameraTransform = _placementCamera.transform;
                cameraTransform.localPosition = _placementSavedCameraLocalPosition;
                cameraTransform.localRotation = _placementSavedCameraLocalRotation;
            }

            _placementCapturedCameraTransform = false;
            _placementSavedCameraLocalPosition = Vector3.zero;
            _placementSavedCameraLocalRotation = Quaternion.identity;
        }

        private static bool TryGetRendererBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            Renderer[] renderers = root != null ? root.GetComponentsInChildren<Renderer>(true) : null;
            if (renderers == null || renderers.Length == 0) return false;

            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        private static void SavePlacementFromCurrentTransform(Transform shipRoot)
        {
            if (_root == null || shipRoot == null) return;

            TurretPlacementRecord record = new TurretPlacementRecord
            {
                layout = string.IsNullOrWhiteSpace(_placementLayout) ? ResolveLayoutName() : _placementLayout,
                localPosition = _root.transform.localPosition,
                localEuler = _root.transform.localEulerAngles,
            };

            UpsertPlacement(record);
            SavePlacementProfile();
            _placementActive = false;
            _lastAppliedLayout = null;
            ApplyLayoutPlacementIfNeeded(force: true);
            RestorePlacementInputCapture();
            SetStatus("PLACEMENT SAVED", 2f);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV.Turret] Saved placement layout={record.layout} pos={record.localPosition} euler={record.localEuler}.");
        }

        private static void CancelPlacement()
        {
            if (_root != null)
            {
                _root.transform.localPosition = _placementStartLocalPosition;
                _root.transform.localRotation = _placementStartLocalRotation;
            }
            _placementActive = false;
            RestorePlacementInputCapture();
            if (!IsUnlocked)
                DestroyTurret();
            SetStatus("PLACEMENT CANCELLED", 1.5f);
        }

        /// <summary>2026-08-06 steady-cost gate: this ran every LateUpdate while the turret
        /// exists, and ResolveLayoutName() reflects into ShipSystems
        /// (CCTVShipSystemsBridge.InvokeString, uncached) plus allocates via
        /// NormalizeLayoutName's Trim/Replace chain — per-frame reflection and GC for a
        /// value that only changes on a rare ship-layout swap. A 1s re-check is
        /// indistinguishable; explicit layout changes still apply immediately through
        /// force:true / ApplyPlacementForLayout.</summary>
        private const float LayoutRecheckIntervalSeconds = 1.0f;
        private static float _nextLayoutRecheckAt;

        private static void ApplyLayoutPlacementIfNeeded(bool force = false)
        {
            if (_placementActive || _root == null) return;

            if (!force && Time.unscaledTime < _nextLayoutRecheckAt)
                return;
            _nextLayoutRecheckAt = Time.unscaledTime + LayoutRecheckIntervalSeconds;

            Transform shipRoot = ResolveShipRoot();
            if (shipRoot != null && _root.transform.parent != shipRoot)
                _root.transform.SetParent(shipRoot, worldPositionStays: false);

            string layout = ResolveLayoutName();
            if (!force && string.Equals(_lastAppliedLayout, layout, StringComparison.Ordinal))
                return;

            TurretPlacementRecord placement;
            if (!TryGetPlacement(layout, out placement))
                placement = GetDefaultPlacement(layout);

            _root.transform.localPosition = placement.localPosition;
            _root.transform.localRotation = Quaternion.Euler(placement.localEuler);
            _lastAppliedLayout = layout;
        }

        private static void ApplyPlacementForLayout(string layout)
        {
            if (_root == null) return;
            Transform shipRoot = ResolveShipRoot();
            if (shipRoot != null && _root.transform.parent != shipRoot)
                _root.transform.SetParent(shipRoot, worldPositionStays: false);

            layout = NormalizeLayoutName(layout);
            TurretPlacementRecord placement;
            if (!TryGetPlacement(layout, out placement))
                placement = GetDefaultPlacement(layout);
            _root.transform.localPosition = placement.localPosition;
            _root.transform.localRotation = Quaternion.Euler(placement.localEuler);
            _lastAppliedLayout = layout;
        }

        private static string ResolveLayoutName()
        {
            string layout = CCTVShipSystemsBridge.GetCurrentLayoutName();
            return NormalizeLayoutName(layout);
        }

        private static string NormalizeLayoutName(string layout)
        {
            if (string.IsNullOrWhiteSpace(layout))
                layout = CCTVShipSystemsBridge.GetCurrentLayoutName();
            if (string.IsNullOrWhiteSpace(layout))
                return "Vanilla";

            string compact = layout.Trim()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace("+", string.Empty);

            bool wider = compact.IndexOf("Wider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         compact.IndexOf("Wide", StringComparison.OrdinalIgnoreCase) >= 0;
            bool twoStory = compact.IndexOf("TwoStory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            compact.IndexOf("2Story", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            compact.IndexOf("2Floor", StringComparison.OrdinalIgnoreCase) >= 0;
            if (wider && twoStory) return "WiderAndTwoStory";
            if (wider) return "Wider";
            if (twoStory) return "TwoStory";
            return "Vanilla";
        }

        private static Transform ResolveShipRoot()
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor != null && sor.elevatorTransform != null)
                return sor.elevatorTransform;

            GameObject ship = GameObject.Find("Environment/HangarShip");
            return ship != null ? ship.transform : null;
        }

        private static TurretPlacementRecord GetDefaultPlacement(string layout)
        {
            layout = NormalizeLayoutName(layout);
            bool twoStory = string.Equals(layout, "TwoStory", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layout, "WiderAndTwoStory", StringComparison.OrdinalIgnoreCase);
            bool wider = string.Equals(layout, "Wider", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(layout, "WiderAndTwoStory", StringComparison.OrdinalIgnoreCase);
            return new TurretPlacementRecord
            {
                layout = layout,
                localPosition = new Vector3(wider ? 1.35f : 0f, twoStory ? 8.8f : 5.4f, wider ? -14.6f : -13.0f),
                localEuler = Vector3.zero,
            };
        }

        private static void EnsurePlacementProfileLoaded()
        {
            if (_profileLoaded) return;
            _profileLoaded = true;
            _profile = new TurretPlacementProfile();

            try
            {
                if (!File.Exists(PlacementProfilePath)) return;
                string json = File.ReadAllText(PlacementProfilePath);
                TurretPlacementProfile loaded = ReadPlacementProfile(json);
                if (loaded != null && loaded.placements != null)
                    _profile = loaded;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] Failed reading placement profile: {ex.Message}");
                _profile = new TurretPlacementProfile();
            }
        }

        private static bool TryGetPlacement(string layout, out TurretPlacementRecord placement)
        {
            EnsurePlacementProfileLoaded();
            placement = null;
            if (_profile?.placements == null) return false;
            layout = NormalizeLayoutName(layout);

            for (int i = 0; i < _profile.placements.Count; i++)
            {
                TurretPlacementRecord record = _profile.placements[i];
                if (record == null) continue;
                if (string.Equals(NormalizeLayoutName(record.layout), layout, StringComparison.OrdinalIgnoreCase))
                {
                    placement = record;
                    placement.layout = layout;
                    return true;
                }
            }
            return false;
        }

        private static void UpsertPlacement(TurretPlacementRecord record)
        {
            EnsurePlacementProfileLoaded();
            if (_profile.placements == null)
                _profile.placements = new List<TurretPlacementRecord>();
            record.layout = NormalizeLayoutName(record.layout);

            for (int i = 0; i < _profile.placements.Count; i++)
            {
                if (string.Equals(NormalizeLayoutName(_profile.placements[i]?.layout), record.layout, StringComparison.OrdinalIgnoreCase))
                {
                    _profile.placements[i] = record;
                    return;
                }
            }
            _profile.placements.Add(record);
        }

        private static void SavePlacementProfile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PlacementProfilePath));
                File.WriteAllText(PlacementProfilePath, WritePlacementProfile(_profile));
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV.Turret] Failed writing placement profile: {ex.Message}");
            }
        }

        /// <summary>
        /// #591 — explicit mapping in place of JsonUtility, whose field discovery
        /// depends on a preloader patcher that is not guaranteed to be running.
        /// Field names and order match the previous output.
        /// </summary>
        private static TurretPlacementProfile ReadPlacementProfile(string json)
        {
            Core.Y4NGZJsonObject root = Core.Y4NGZJson.Parse(json);
            if (root == null) return null;

            TurretPlacementProfile profile = new TurretPlacementProfile();
            Core.Y4NGZJsonArray placements = root.GetArray("placements");
            if (placements == null) return profile;

            for (int i = 0; i < placements.Count; i++)
            {
                Core.Y4NGZJsonObject entry = placements.GetObject(i);
                if (entry == null) continue;

                profile.placements.Add(new TurretPlacementRecord
                {
                    layout = entry.GetString("layout"),
                    localPosition = entry.GetVector3("localPosition"),
                    localEuler = entry.GetVector3("localEuler"),
                });
            }

            return profile;
        }

        private static string WritePlacementProfile(TurretPlacementProfile profile)
        {
            Core.Y4NGZJsonObject root = new Core.Y4NGZJsonObject();
            Core.Y4NGZJsonArray placements = root.SetArray("placements");
            if (profile?.placements != null)
            {
                for (int i = 0; i < profile.placements.Count; i++)
                {
                    TurretPlacementRecord record = profile.placements[i];
                    if (record == null) continue;

                    Core.Y4NGZJsonObject entry = placements.AddObject();
                    entry.SetString("layout", record.layout);
                    entry.SetVector3("localPosition", record.localPosition);
                    entry.SetVector3("localEuler", record.localEuler);
                }
            }

            return Core.Y4NGZJson.Write(root);
        }

        private static string PlacementProfilePath =>
            Core.Y4NGZCompanyPaths.LocalDataFile("ship-turret-placements.local.json");

        [Serializable]
        private sealed class TurretPlacementProfile
        {
            public List<TurretPlacementRecord> placements = new List<TurretPlacementRecord>();
        }

        [Serializable]
        private sealed class TurretPlacementRecord
        {
            public string layout;
            public Vector3 localPosition;
            public Vector3 localEuler;
        }
    }
}
