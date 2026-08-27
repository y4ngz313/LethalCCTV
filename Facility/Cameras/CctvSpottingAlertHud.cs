using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Security;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// Screen-edge directional alert for "a security camera is filling its detection
    /// window on you". One arc per camera that can currently see the local player,
    /// placed on the screen edge in that camera's direction, in the same yellow -> red
    /// the camera's own warning beam and lens strobe use
    /// (<see cref="CctvDetectionPalette"/>), gaining opacity as that camera approaches
    /// setting the alarm off.
    ///
    /// Presentation only, no netcode: it reads
    /// <see cref="CctvSecurityCameraState.IsSpottingLocalPlayerForPresentation"/>, which
    /// every client derives from its own detection sweep.
    ///
    /// It is gated on exactly the state that beeps. <c>CCTVCameraVisual.ResolveMode</c>
    /// returns <c>LensMode.Detecting</c> — the tick-beep state — for an active, unbroken,
    /// suspicious camera while no alarm is up, and the same four conditions gate an arc.
    /// So an arc starts with the beeping and lets go the moment the alarm takes the lens
    /// over, without hooking the audio.
    ///
    /// The arc sprite is generated in code, so this adds no asset dependency. The
    /// procedural-arc and screen-edge-polar techniques come from Y4NGZUpgrades'
    /// <c>DirectionalDamageHud</c>; that one is a one-shot flash driven by a damage
    /// event, while this is a continuous state display, hence the per-camera slots.
    /// </summary>
    internal sealed class CctvSpottingAlertHud : MonoBehaviour
    {
        // Several cameras can be suspicious at once, and the whole point of the effect is
        // knowing *where* — so it is one arc per camera rather than one arc for the worst
        // offender. Four is the readable ceiling, and two arcs within MergeDegrees of each
        // other would draw as one smeared arc anyway, so the camera nearer to its alarm
        // takes the slot and the other is dropped.
        private const int MaxArcs = 4;
        private const float MergeDegrees = 14f;

        // The beam's own linger is 0.35 s, which reads as a flicker on a HUD element.
        private const float FadeInSeconds = 0.15f;
        private const float FadeOutSeconds = 1.2f;

        private const float MinAlpha = 0.16f;
        private const float MaxAlpha = 0.62f;
        private const float EdgeRadiusFraction = 0.36f;
        private const float FallbackEdgeRadiusPx = 270f;
        private const float ArcWidthPx = 132f;
        private const float ArcHeightPx = 54f;
        private const int FreeSlot = int.MinValue;

        private static CctvSpottingAlertHud _instance;
        private static Canvas _fallbackCanvas;
        private static Sprite _arcSprite;

        private readonly List<Hot> _hot = new List<Hot>(8);
        private readonly Hot[] _accepted = new Hot[MaxArcs];
        private readonly ArcSlot[] _slots = new ArcSlot[MaxArcs];
        private Canvas _canvas;
        private RectTransform _root;
        private int _acceptedCount;
        private bool _wasVisible;

        /// <summary>
        /// Builds the overlay if the HUD canvas is up, and rebuilds it if the canvas was
        /// replaced. Cheap enough to call every frame; <c>CctvSupportTickDriver</c> does.
        /// </summary>
        internal static void Ensure()
        {
            Canvas canvas = ResolveCanvas();
            if (canvas == null) return;
            Transform parent = ResolveParent(canvas);

            if (_instance != null)
            {
                if (_instance._canvas == canvas)
                {
                    if (_instance.transform.parent != parent)
                        _instance.transform.SetParent(parent, worldPositionStays: false);
                    return;
                }
                Destroy(_instance.gameObject);
                _instance = null;
            }

            var go = new GameObject("Y4NGZ_CctvSpottingAlertHud");
            go.transform.SetParent(parent, worldPositionStays: false);
            CctvSpottingAlertHud hud = go.AddComponent<CctvSpottingAlertHud>();
            hud._canvas = canvas;
            hud.Build();
            _instance = hud;
        }

        // Canvas preference, best to worst: the themed Y4NGZ HUD host (contracts plugin,
        // absent in a CCTV-only profile), then vanilla's own player-screen canvas, then a
        // canvas this plugin owns. The last one exists so the arcs still draw before
        // HUDManager has published its texture; sortingOrder sits above vanilla's HUD
        // (which renders at 0) without competing with the pause menu's own overlays.
        private static Canvas ResolveCanvas()
        {
            Canvas themed = GameplayHudHostBridge.SourceCanvas;
            if (themed != null)
                return themed;

            HUDManager hud = HUDManager.Instance;
            if (hud != null && hud.playerScreenTexture != null)
                return hud.playerScreenTexture.canvas;

            return EnsureFallbackCanvas();
        }

        private static Canvas EnsureFallbackCanvas()
        {
            if (_fallbackCanvas != null)
                return _fallbackCanvas;

            var go = new GameObject("Y4NGZ_CctvSpottingAlertCanvas");
            DontDestroyOnLoad(go);
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 120;
            go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _fallbackCanvas = canvas;
            return canvas;
        }

        private static Transform ResolveParent(Canvas canvas) =>
            GameplayHudHostBridge.EdgeAlertLayer ?? canvas.transform;

        private void Build()
        {
            _root = gameObject.AddComponent<RectTransform>();
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;

            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = BuildArc(i);
        }

        private ArcSlot BuildArc(int index)
        {
            var go = new GameObject("SpottingArc" + index.ToString());
            go.transform.SetParent(transform, worldPositionStays: false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(ArcWidthPx, ArcHeightPx);

            Image image = go.AddComponent<Image>();
            image.sprite = GetArcSprite();
            image.raycastTarget = false;
            image.color = new Color(1f, 1f, 1f, 0f);
            image.enabled = false;

            return new ArcSlot(rect, image);
        }

        // LateUpdate so the direction and progress read this frame's camera transforms,
        // after CctvSupportTickDriver's Update has run the detection sweep.
        private void LateUpdate()
        {
            if (!GameplayHudHostBridge.GameplayUiVisible)
            {
                HideImmediately();
                return;
            }

            if (!(CctvModuleConfig.CctvSecuritySpottingAlertEnabled?.Value ?? true))
            {
                HideImmediately();
                return;
            }

            float intensity = Mathf.Max(0f, CctvModuleConfig.CctvSecuritySpottingAlertIntensity?.Value ?? 1f);
            PlayerControllerB player = StartOfRound.Instance != null
                ? StartOfRound.Instance.localPlayerController
                : null;
            if (intensity <= 0f || player == null || player.gameplayCamera == null ||
                player.isPlayerDead || !player.isPlayerControlled)
            {
                HideImmediately();
                return;
            }

            CollectSpottingCameras(player);
            AssignSlots(intensity);

            float radius = ResolveEdgeRadius();
            float delta = Time.unscaledDeltaTime;
            bool anyVisible = false;
            for (int i = 0; i < _slots.Length; i++)
            {
                ArcSlot slot = _slots[i];
                float seconds = slot.TargetAlpha > slot.Alpha ? FadeInSeconds : FadeOutSeconds;
                slot.Alpha = Mathf.MoveTowards(slot.Alpha, slot.TargetAlpha, delta / seconds);
                if (!slot.Live && slot.Alpha <= 0.001f)
                    slot.CameraIndex = FreeSlot;
                RenderSlot(slot, player, radius);
                anyVisible |= slot.Alpha > 0.001f;
            }

            // Other overlays parent to this same canvas; claim the top only on the
            // transition into visible rather than reordering siblings every frame.
            if (anyVisible && !_wasVisible)
                transform.SetAsLastSibling();
            _wasVisible = anyVisible;
        }

        private void CollectSpottingCameras(PlayerControllerB player)
        {
            _hot.Clear();
            _acceptedCount = 0;

            // The alarm carries its own presentation — red lens strobe, sirens, blackout.
            // Once it trips, the arcs have said what they existed to say and let go.
            if (CctvSecurityDirector.IsTimedAlarmActive ||
                MainframeProtocolDirector.IsProtocolAlarmVisualActive)
            {
                return;
            }

            IReadOnlyList<CctvSecurityCameraState> cameras = CctvSecurityCameraRegistry.RegisteredCameras;
            float detectionSeconds = CctvSecurityConfig.Current.DetectionSeconds;
            for (int i = 0; i < cameras.Count; i++)
            {
                CctvSecurityCameraState state = cameras[i];
                if (state == null || state.IsBroken || !state.IsDetectionLive) continue;
                if (!state.IsSpottingLocalPlayerForPresentation) continue;
                if (state.Transform == null) continue;

                Vector3 worldPos = state.Transform.position;
                _hot.Add(new Hot(
                    state.CameraIndex,
                    worldPos,
                    ScreenDirection(player, worldPos),
                    state.DetectionProgress01(detectionSeconds)));
            }

            SortByProgressDescending();

            for (int i = 0; i < _hot.Count && _acceptedCount < MaxArcs; i++)
            {
                Hot candidate = _hot[i];
                bool merged = false;
                for (int j = 0; j < _acceptedCount; j++)
                {
                    if (Vector2.Angle(_accepted[j].ScreenDir, candidate.ScreenDir) > MergeDegrees) continue;
                    merged = true;
                    break;
                }

                if (!merged)
                    _accepted[_acceptedCount++] = candidate;
            }
        }

        // Insertion sort, highest progress first: n is the number of cameras spotting you
        // at once, and it avoids the per-call comparer allocation List<T>.Sort(Comparison)
        // makes on this runtime.
        private void SortByProgressDescending()
        {
            for (int i = 1; i < _hot.Count; i++)
            {
                Hot value = _hot[i];
                int j = i - 1;
                while (j >= 0 && _hot[j].Progress01 < value.Progress01)
                {
                    _hot[j + 1] = _hot[j];
                    j--;
                }

                _hot[j + 1] = value;
            }
        }

        private void AssignSlots(float intensity)
        {
            for (int i = 0; i < _slots.Length; i++)
                _slots[i].Live = false;

            for (int i = 0; i < _acceptedCount; i++)
            {
                Hot hot = _accepted[i];
                ArcSlot slot = FindSlot(hot.CameraIndex) ?? TakeSlot();
                // Null means every slot is held by a camera nearer to its alarm than this
                // one, which is the cap doing its job.
                if (slot == null) continue;

                if (slot.CameraIndex != hot.CameraIndex)
                {
                    // Fresh camera in this slot: start from zero so it fades in at its own
                    // direction instead of sliding across the screen from the last one.
                    slot.CameraIndex = hot.CameraIndex;
                    slot.Alpha = 0f;
                }

                slot.WorldPos = hot.WorldPos;
                slot.Progress01 = hot.Progress01;
                slot.TargetAlpha = Mathf.Clamp01(Mathf.Lerp(MinAlpha, MaxAlpha, hot.Progress01) * intensity);
                slot.Live = true;
            }

            for (int i = 0; i < _slots.Length; i++)
                if (!_slots[i].Live)
                    _slots[i].TargetAlpha = 0f;
        }

        private ArcSlot FindSlot(int cameraIndex)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].CameraIndex == cameraIndex)
                    return _slots[i];
            return null;
        }

        private ArcSlot TakeSlot()
        {
            ArcSlot faintest = null;
            for (int i = 0; i < _slots.Length; i++)
            {
                ArcSlot slot = _slots[i];
                if (slot.Live) continue;
                if (slot.CameraIndex == FreeSlot) return slot;
                if (faintest == null || slot.Alpha < faintest.Alpha) faintest = slot;
            }

            return faintest;
        }

        // Fading arcs keep tracking their camera's last known world position, so turning
        // around during the fade still points at where the camera was rather than freezing
        // a screen direction that now points at nothing.
        private void RenderSlot(ArcSlot slot, PlayerControllerB player, float radius)
        {
            if (slot.Alpha <= 0.001f)
            {
                if (slot.Image.enabled) slot.Image.enabled = false;
                return;
            }

            Vector2 dir = ScreenDirection(player, slot.WorldPos);
            slot.Rect.anchoredPosition = dir * radius;
            slot.Rect.localRotation = Quaternion.Euler(
                0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f);

            Color color = CctvDetectionPalette.ForProgress(slot.Progress01);
            color.a = slot.Alpha;
            slot.Image.color = color;
            if (!slot.Image.enabled) slot.Image.enabled = true;
        }

        private void HideImmediately()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                ArcSlot slot = _slots[i];
                if (slot == null) continue;
                slot.Live = false;
                slot.CameraIndex = FreeSlot;
                slot.TargetAlpha = 0f;
                slot.Alpha = 0f;
                if (slot.Image != null && slot.Image.enabled)
                    slot.Image.enabled = false;
            }

            _wasVisible = false;
        }

        private float ResolveEdgeRadius()
        {
            if (_root == null) return FallbackEdgeRadiusPx;
            Rect rect = _root.rect;
            if (rect.width <= 0f || rect.height <= 0f) return FallbackEdgeRadiusPx;
            return Mathf.Min(rect.width, rect.height) * EdgeRadiusFraction;
        }

        // Yaw-only polar placement: right/forward dot products against the flattened
        // direction to the camera, so an arc behind the player lands at the bottom of the
        // screen and swings correctly as they turn.
        private static Vector2 ScreenDirection(PlayerControllerB player, Vector3 worldPos)
        {
            Transform view = player.gameplayCamera.transform;
            Vector3 toSource = worldPos - player.transform.position;
            toSource.y = 0f;
            if (toSource.sqrMagnitude < 0.001f) return Vector2.down;
            toSource.Normalize();

            Vector3 forward = view.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = player.transform.forward;
            forward.Normalize();

            Vector3 right = view.right;
            right.y = 0f;
            if (right.sqrMagnitude < 0.001f) right = player.transform.right;
            right.Normalize();

            var dir = new Vector2(Vector3.Dot(right, toSource), Vector3.Dot(forward, toSource));
            return dir.sqrMagnitude < 0.001f ? Vector2.down : dir.normalized;
        }

        private static Sprite GetArcSprite()
        {
            if (_arcSprite != null) return _arcSprite;

            const int width = 192;
            const int height = 96;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            var clear = new Color(1f, 1f, 1f, 0f);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    tex.SetPixel(x, y, clear);
            }

            // Arc struck from a centre well below the sprite, so the band curves gently
            // and its ends taper out instead of ending in hard caps.
            var center = new Vector2(width * 0.5f, -height * 0.7f);
            float radius = height * 1.42f;
            const float thickness = 12f;
            const float halfArcRadians = 0.46f;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    Vector2 fromCenter = p - center;
                    float radialDelta = Mathf.Abs(fromCenter.magnitude - radius);
                    if (radialDelta > thickness) continue;

                    float angle = Mathf.Atan2(fromCenter.y, fromCenter.x);
                    float angleDelta = Mathf.Abs(Mathf.DeltaAngle(angle * Mathf.Rad2Deg, 90f)) * Mathf.Deg2Rad;
                    if (angleDelta > halfArcRadians) continue;

                    float radialAlpha = 1f - Mathf.Clamp01(radialDelta / thickness);
                    float arcAlpha = 1f - Mathf.Clamp01(
                        (angleDelta - halfArcRadians * 0.72f) / (halfArcRadians * 0.28f));
                    float alpha = Mathf.Clamp01(radialAlpha * arcAlpha);
                    alpha = alpha * alpha * (3f - 2f * alpha);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply(false, true);
            _arcSprite = Sprite.Create(tex, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
            _arcSprite.hideFlags = HideFlags.HideAndDontSave;
            return _arcSprite;
        }

        private readonly struct Hot
        {
            internal Hot(int cameraIndex, Vector3 worldPos, Vector2 screenDir, float progress01)
            {
                CameraIndex = cameraIndex;
                WorldPos = worldPos;
                ScreenDir = screenDir;
                Progress01 = progress01;
            }

            internal readonly int CameraIndex;
            internal readonly Vector3 WorldPos;
            internal readonly Vector2 ScreenDir;
            internal readonly float Progress01;
        }

        private sealed class ArcSlot
        {
            internal ArcSlot(RectTransform rect, Image image)
            {
                Rect = rect;
                Image = image;
                CameraIndex = FreeSlot;
            }

            internal readonly RectTransform Rect;
            internal readonly Image Image;
            internal int CameraIndex;
            internal Vector3 WorldPos;
            internal float Progress01;
            internal float TargetAlpha;
            internal float Alpha;
            internal bool Live;
        }
    }
}
