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
using Y4NGZCompany.Facility.Cameras;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class ShipTurretController
    {
        private const string BundleFileName = "shipturret.turretbundle";
        private const string MsgShotRequest = "LethalCCTV.TurretShotRequest";
        private const string MsgShotResult = "LethalCCTV.TurretShotResult";
        private const float ShotCooldownSeconds = 3f;
        private const float ShotDistance = 260f;
        private const int ShotDamage = 10;
        private const int TurretHitId = 4107;
        private const int PlayerCameraMask = 0x233B17FF;
        private const int UiLayer = 5;
        private const int HelmetVisorLayer = 7;
        private const int ScanNodeLayer = 22;
        private const int TurretCameraMask = PlayerCameraMask & ~((1 << UiLayer) | (1 << HelmetVisorLayer) | (1 << ScanNodeLayer));
        private const float VisualTargetMaxDimensionMeters = 4.35f;
        private const float RotateCueMaxSeconds = 0.38f;
        private const float ZoomCueMaxSeconds = 0.46f;
        private const float AudioDirectionResetSeconds = 0.55f;
        private const float RecoilDurationSeconds = 0.34f;
        private const float RecoilDistance = 0.82f;
        private const float RecoilPitchKick = -4.6f;
        private const float CameraRecoilDistance = 0.16f;
        private const float CameraRecoilPitch = -3.4f;
        private const float EnemyShotRadius = 0.72f;
        private const float EnemyPriorityLeeway = 1.15f;
        private const float TurretInteractiveWindowSeconds = 0.15f;

        private static readonly Dictionary<ulong, float> ServerNextShotAt = new Dictionary<ulong, float>();
        private static readonly RaycastHit[] ShotHits = new RaycastHit[48];
        private static readonly RaycastHit[] ShotSphereHits = new RaycastHit[96];
        private static readonly string[] LayoutPresets = { "Vanilla", "Wider", "TwoStory", "WiderAndTwoStory" };
        private static readonly Vector3 PitchPivotLocalPosition = new Vector3(0f, 0.55f, 0f);

        private static GameObject _root;
        private static Transform _yawPivot;
        private static Transform _pitchPivot;
        private static Transform _fixtureVisualRoot;
        private static Transform _gunVisualRoot;
        private static Transform _muzzle;
        private static Camera _camera;
        private static RenderTexture _renderTexture;
        private static AudioSource _fireSource;
        private static AudioSource _rotateSource;
        private static AudioSource _zoomSource;
        private static AudioClip[] _fireClips;
        private static AudioClip _rotateClip;
        private static AudioClip _zoomClip;
        private static GameObject _visualPrefab;
        private static GameObject _dustExplosionPrefab;
        private static Material _impactDustMaterial;
        private static Material _muzzleFlashMaterial;
        private static Material _muzzleSmokeMaterial;
        private static Texture2D _softParticleTexture;
        private static AssetBundle _bundle;
        private static bool _bundleLoadAttempted;
        private static bool _networkHandlersRegistered;
        private static NetworkManager _registeredNetworkManager;
        private static float _yawDeg;
        private static float _pitchDeg = 8f;
        private static float _zoom;
        private static float _nextLocalShotAt;
        private static float _lastRotateAt;
        private static float _lastZoomAt;
        private static float _lastRotateCueAt;
        private static float _lastZoomCueAt;
        private static float _rotateCueStopAt;
        private static float _zoomCueStopAt;
        private static int _lastRotateDirection;
        private static int _lastZoomDirection;
        private static float _recoilStartedAt = -100f;
        private static string _lastAppliedLayout;
        private static string _lastStatus = "READY";
        private static float _statusVisibleUntil;

        private static TurretPlacementProfile _profile;
        private static bool _profileLoaded;
        private static bool _placementActive;
        private static string _placementLayout = "Vanilla";
        private static Vector3 _placementStartLocalPosition;
        private static Quaternion _placementStartLocalRotation;
        private static PlayerControllerB _placementPlayer;
        private static Camera _placementCamera;
        private static bool _placementCapturedInputState;
        private static CursorLockMode _placementSavedLock;
        private static bool _placementSavedCursorVisible;
        private static bool _placementSavedDisableLookInput;
        private static bool _placementSavedDisableMoveInput;
        private static bool _placementCapturedCameraTransform;
        private static Vector3 _placementSavedCameraLocalPosition;
        private static Quaternion _placementSavedCameraLocalRotation;

        internal static bool IsUnlocked => CCTVShipSystemsBridge.HasTurretUpgrade();
        internal static bool PlacementActive => _placementActive;
        internal static Texture ViewTexture => _renderTexture;
        internal static float Zoom => _zoom;

        internal static void Initialize()
        {
            EnsurePlacementProfileLoaded();
        }

        internal static void Shutdown()
        {
            UnregisterNetworkHandlers();
            RestorePlacementInputCapture();
            // #502 — unbind before DestroyTurret: Object.Destroy is deferred to
            // the end of the frame, so the turret camera outlives this call and
            // would still hold _renderTexture as its targetTexture when it is
            // released below. DestroyTurret nulls _camera, so this must run first.
            if (_camera != null)
                _camera.targetTexture = null;
            DestroyTurret();
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                UnityEngine.Object.Destroy(_renderTexture);
                _renderTexture = null;
            }
            if (_bundle != null)
            {
                _bundle.Unload(false);
                _bundle = null;
            }
            if (_impactDustMaterial != null)
            {
                UnityEngine.Object.Destroy(_impactDustMaterial);
                _impactDustMaterial = null;
            }
            if (_muzzleFlashMaterial != null)
            {
                UnityEngine.Object.Destroy(_muzzleFlashMaterial);
                _muzzleFlashMaterial = null;
            }
            if (_muzzleSmokeMaterial != null)
            {
                UnityEngine.Object.Destroy(_muzzleSmokeMaterial);
                _muzzleSmokeMaterial = null;
            }
            if (_softParticleTexture != null)
            {
                UnityEngine.Object.Destroy(_softParticleTexture);
                _softParticleTexture = null;
            }
            _bundleLoadAttempted = false;
            _profileLoaded = false;
            _profile = null;
            ServerNextShotAt.Clear();
        }

        internal static void Tick()
        {
            EnsureNetworkHandlers();

            bool shouldExist = IsUnlocked || _placementActive;
            if (!shouldExist)
            {
                CctvRenderScheduler.Cancel(CctvRenderClient.Turret);
                DestroyTurret();
                return;
            }

            EnsureTurretPresent();
            ApplyLayoutPlacementIfNeeded();
            TickPlacementEditor();
            TickAudioCues();
            TickRecoil();

            if (MonitorFocus.IsFocused && MonitorFocus.IsTurretPageActive)
            {
                RequestView();
            }
            else
            {
                CctvRenderScheduler.Cancel(CctvRenderClient.Turret);
            }
        }

        internal static void ApplyLookDelta(Vector2 rawLook)
        {
            if (_root == null || rawLook.sqrMagnitude < 1e-8f) return;

            float sens = IngamePlayerSettings.Instance.settings.lookSensitivity * 0.008f;
            if (IngamePlayerSettings.Instance.flipCamera) rawLook.x = -rawLook.x;
            if (IngamePlayerSettings.Instance.settings.invertYAxis) rawLook.y *= -1f;

            Vector2 delta = rawLook * sens * 0.70f;
            float maxDelta = 82f * Mathf.Max(Time.unscaledDeltaTime, 1f / 120f);
            if (delta.magnitude > maxDelta) delta = delta.normalized * maxDelta;

            _yawDeg = WrapAngle(_yawDeg + delta.x);
            _pitchDeg = Mathf.Clamp(_pitchDeg - delta.y, -28f, 54f);
            ApplyAim();

            if (delta.sqrMagnitude > 0.0001f)
            {
                float now = Time.unscaledTime;
                int direction = ResolveDominantDirection(delta);
                _lastRotateAt = now;
                EnsureAudioSources();
                if (direction != 0 && direction != _lastRotateDirection)
                {
                    _lastRotateDirection = direction;
                    _lastRotateCueAt = now;
                    _rotateCueStopAt = now + RotateCueMaxSeconds;
                    PlayShortCue(_rotateSource, _rotateClip, 0.34f);
                }
            }
        }

        internal static void HandleZoomScroll(float scroll)
        {
            if (Mathf.Abs(scroll) < 0.01f) return;

            _zoom = Mathf.Clamp01(_zoom + Mathf.Sign(scroll) * 0.08f);
            if (_camera != null)
                _camera.fieldOfView = Mathf.Lerp(72f, 28f, _zoom);

            float now = Time.unscaledTime;
            int direction = scroll > 0f ? 1 : -1;
            _lastZoomAt = now;
            EnsureAudioSources();
            if (direction != _lastZoomDirection)
            {
                _lastZoomDirection = direction;
                _lastZoomCueAt = now;
                _zoomCueStopAt = now + ZoomCueMaxSeconds;
                PlayShortCue(_zoomSource, _zoomClip, 0.30f);
            }
        }

        internal static void TryFire()
        {
            if (!MonitorFocus.IsTurretPageActive || !IsUnlocked)
                return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextLocalShotAt)
            {
                SetStatus($"COOLDOWN {Mathf.CeilToInt(_nextLocalShotAt - now)}S", 0.6f);
                return;
            }

            EnsureTurretPresent();
            if (_camera == null)
                return;

            _nextLocalShotAt = now + ShotCooldownSeconds;
            Vector3 origin = _camera.transform.position;
            Vector3 direction = _camera.transform.forward.normalized;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                ProcessShotRequest(nm.LocalClientId, origin, direction);
                return;
            }

            if (nm == null || nm.CustomMessagingManager == null || !nm.IsClient || !nm.IsListening)
            {
                SetStatus("NETWORK OFFLINE", 1.2f);
                return;
            }

            FastBufferWriter writer = new FastBufferWriter(96, Allocator.Temp);
            try
            {
                WriteVector(ref writer, origin);
                WriteVector(ref writer, direction);
                nm.CustomMessagingManager.SendNamedMessage(MsgShotRequest, NetworkManager.ServerClientId, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        internal static string BuildStatsBlock(string walkie)
        {
            float power = Mathf.Clamp01(CCTVShipSystemsBridge.GetPowerFraction());
            float cooldown = Mathf.Max(0f, _nextLocalShotAt - Time.realtimeSinceStartup);
            string status = Time.unscaledTime <= _statusVisibleUntil ? _lastStatus : (cooldown > 0f ? "CHARGING" : "READY");
            return
                "TURRET\n" +
                "SHIP PWR " + Mathf.RoundToInt(power * 100f) + "%\n" +
                "SHOT     10%\n" +
                "STATUS   " + status + "\n" +
                "COOLDOWN " + cooldown.ToString("0.0") + "S\n" +
                "ZOOM     " + FormatZoom() + "\n" +
                "WALKIE   " + walkie;
        }

        internal static string GetDebugStatus()
        {
            string layout = ResolveLayoutName();
            bool hasPlacement = TryGetPlacement(layout, out TurretPlacementRecord _);
            return $"layout={layout} bought={IsUnlocked} current={(hasPlacement ? "saved" : "default")} editing={_placementActive} presets={BuildPresetStatus()}";
        }

        public static void StartDebugPlacement()
        {
            StartDebugPlacement(null);
        }

        public static void StartDebugPlacement(string layoutName)
        {
            EnsurePlacementProfileLoaded();
            _placementLayout = NormalizeLayoutName(layoutName);
            _placementActive = true;
            EnsureTurretPresent();
            ApplyPlacementForLayout(_placementLayout);
            if (_root != null)
            {
                _placementStartLocalPosition = _root.transform.localPosition;
                _placementStartLocalRotation = _root.transform.localRotation;
            }
            BeginPlacementInputCapture();
            EnsurePlacementCamera();
            UpdatePlacementCameraPose();
            SetStatus("PLACEMENT ACTIVE", 2f);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV.Turret] Placement editor started for layout {_placementLayout}.");
        }

        public static void ShowDebugPreview()
        {
            EnsurePlacementProfileLoaded();
            EnsureTurretPresent();
            ApplyLayoutPlacementIfNeeded(force: true);
            SetStatus("PREVIEW SHOWN", 1.5f);
        }

        public static void ClearDebugPreview()
        {
            _placementActive = false;
            RestorePlacementInputCapture();
            if (!IsUnlocked)
                DestroyTurret();
            SetStatus("PREVIEW CLEARED", 1.5f);
        }

        private static void EnsureTurretPresent()
        {
            if (_root != null) return;

            EnsureAssetsLoaded();
            EnsureRenderTexture();

            Transform shipRoot = ResolveShipRoot();
            _root = new GameObject("LethalCCTV_ShipTurret");
            if (shipRoot != null)
                _root.transform.SetParent(shipRoot, worldPositionStays: false);

            _yawPivot = new GameObject("YawPivot").transform;
            _yawPivot.SetParent(_root.transform, worldPositionStays: false);

            _fixtureVisualRoot = new GameObject("FixtureVisualRoot").transform;
            _fixtureVisualRoot.SetParent(_root.transform, worldPositionStays: false);

            _pitchPivot = new GameObject("PitchPivot").transform;
            _pitchPivot.SetParent(_yawPivot, worldPositionStays: false);
            _pitchPivot.localPosition = PitchPivotLocalPosition;

            _gunVisualRoot = new GameObject("GunVisualRoot").transform;
            _gunVisualRoot.SetParent(_pitchPivot, worldPositionStays: false);

            BuildVisual(_fixtureVisualRoot, _gunVisualRoot);

            _muzzle = new GameObject("Muzzle").transform;
            _muzzle.SetParent(_pitchPivot, worldPositionStays: false);
            _muzzle.localPosition = new Vector3(0f, 0.22f, 3.05f);

            GameObject cameraGo = new GameObject("TurretCamera");
            cameraGo.transform.SetParent(_pitchPivot, worldPositionStays: false);
            cameraGo.transform.localPosition = new Vector3(0f, 1.12f, 2.35f);
            cameraGo.transform.localRotation = Quaternion.identity;
            _camera = cameraGo.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.targetTexture = _renderTexture;
            _camera.clearFlags = CameraClearFlags.Color;
            _camera.backgroundColor = Color.black;
            _camera.cullingMask = TurretCameraMask;
            _camera.nearClipPlane = 0.03f;
            _camera.farClipPlane = ShotDistance;
            _camera.fieldOfView = 72f;

            HDAdditionalCameraData hdrp = cameraGo.AddComponent<HDAdditionalCameraData>();
            hdrp.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hdrp.backgroundColorHDR = Color.black;

            EnsureAudioSources();
            ApplyLayoutPlacementIfNeeded(force: true);
            ApplyAim();
        }

        private static void DestroyTurret()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _root = null;
            _yawPivot = null;
            _pitchPivot = null;
            _fixtureVisualRoot = null;
            _gunVisualRoot = null;
            _muzzle = null;
            _camera = null;
            _fireSource = null;
            _rotateSource = null;
            _zoomSource = null;
            _lastAppliedLayout = null;
            _lastRotateDirection = 0;
            _lastZoomDirection = 0;
            _recoilStartedAt = -100f;
        }

        private static void ApplyAim()
        {
            float recoil = GetCurrentRecoil01();
            if (_yawPivot != null)
                _yawPivot.localRotation = Quaternion.Euler(0f, _yawDeg, 0f);
            if (_pitchPivot != null)
                _pitchPivot.localRotation = Quaternion.Euler(_pitchDeg + RecoilPitchKick * recoil, 0f, 0f);
            if (_gunVisualRoot != null)
                _gunVisualRoot.localPosition = new Vector3(0f, 0f, -RecoilDistance * recoil);
            if (_camera != null)
            {
                _camera.transform.localPosition = new Vector3(0f, 1.12f, 2.35f - CameraRecoilDistance * recoil);
                _camera.transform.localRotation = Quaternion.Euler(CameraRecoilPitch * recoil, 0f, 0f);
            }
        }

        /// <summary>
        /// #1219 G4. The turret view renders at the configured feed rate through
        /// CctvRenderScheduler; aiming, zooming and recoil request the capped dirty bypass so
        /// the view keeps up with input without claiming every frame.
        /// </summary>
        private static void RequestView()
        {
            if (_camera == null || _renderTexture == null) return;
            float now = Time.unscaledTime;
            bool interacting = now - _lastRotateAt < TurretInteractiveWindowSeconds
                || now - _lastZoomAt < TurretInteractiveWindowSeconds
                || GetCurrentRecoil01() > 0f;
            CctvRenderScheduler.Request(
                CctvRenderClient.Turret,
                QuadCameraAssignment.ResolveActiveFeedIntervalSeconds(),
                interacting);
        }

        /// <summary>CctvRenderScheduler callback for <see cref="CctvRenderClient.Turret"/>.</summary>
        internal static bool RenderScheduledView()
        {
            if (_camera == null || _renderTexture == null) return false;
            if (!MonitorFocus.IsFocused || !MonitorFocus.IsTurretPageActive) return false;
            _camera.targetTexture = _renderTexture;
            _camera.Render();
            return true;
        }

        private static void SetStatus(string status, float seconds)
        {
            _lastStatus = status ?? "READY";
            _statusVisibleUntil = Time.unscaledTime + Mathf.Max(0.1f, seconds);
        }

        private static string BuildPresetStatus()
        {
            string result = string.Empty;
            for (int i = 0; i < LayoutPresets.Length; i++)
            {
                string layout = LayoutPresets[i];
                bool saved = TryGetPlacement(layout, out TurretPlacementRecord _);
                if (i > 0) result += ",";
                result += layout + ":" + (saved ? "saved" : "default");
            }
            return result;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
        }

        private static string FormatZoom()
        {
            float fov = Mathf.Lerp(72f, 28f, _zoom);
            return (72f / Mathf.Max(1f, fov)).ToString("0.0") + "X";
        }

        private static float WrapAngle(float angle)
        {
            while (angle > 180f) angle -= 360f;
            while (angle < -180f) angle += 360f;
            return angle;
        }

    }
}
