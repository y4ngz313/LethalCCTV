using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

using Y4NGZCompany.Facility.Mainframe;
namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed class MainframeScreenController : MonoBehaviour
    {
        internal enum ScreenState
        {
            IdleUnhacked,
            HackingIntro,
            Minigame,
            HackedMainframe,
            IdleHacked
        }

        private const float PixelsPerMeter = 1000f;
        private const float IdleRefreshSeconds = 0.18f;
        private const float ScreenSizeDriftTolerance = 0.05f;
        private const float ScreenDepthDriftTolerance = 0.01f;
        private const float ScreenSizeRejectRatio = 2f;
        private static readonly Color ScreenBlack = new Color(0.003f, 0.010f, 0.004f, 0.98f);
        private static readonly Color ScreenGreen = new Color(0.16f, 1f, 0.34f, 1f);
        private static readonly Color ScreenGreenDim = new Color(0.08f, 0.45f, 0.18f, 1f);
        private static readonly Color ScreenAmber = new Color(0.92f, 0.82f, 0.30f, 1f);

        private MainframeSupport _mainframe;
        private Transform _screenSurface;
        private Transform _screenCenter;
        private Transform _screenTopLeft;
        private Transform _screenBottomRight;
        private GameObject _canvasRoot;
        private RectTransform _canvasRect;
        private GameObject _idleRoot;
        private TextMeshProUGUI _idleText;
        private HackingOverlay _hackingOverlay;
        private MainframeControlOverlay _mainframeOverlay;
        private MainframeAudio _audio;
        private ScreenState _state = ScreenState.IdleUnhacked;
        private float _nextIdleRefresh;
        private float _introUntil;
        private float _introStartedAt;
        private int _idleLineOffset;
        private int _roundRobinFinger;
        // Last effective push-to-talk state (key held AND the INTERCOM view actually showing), so
        // the poller only forwards genuine transitions.
        private bool _pushToTalkHeld;
        private bool _missingAnchorLogged;
        private Bounds _screenSurfaceLocalBounds;
        private bool _hasScreenSurfaceBounds;
        private bool _screenSizeDriftLogged;
        private Matrix4x4 _screenSurfaceMeasureMatrix;
        private bool _hasScreenSurfaceMeasure;
        private bool _screenSurfaceMeasureValid;
        private Vector3 _screenSurfaceMeasureCenter;
        private Vector2 _screenSurfaceMeasureSize;

        internal ScreenState State => _state;

        private void Awake()
        {
            EnsureInitialized();
        }

        private void OnEnable()
        {
            EnsureInitialized();
            ShowIdleForCurrentHackState();
        }

        private void OnDisable()
        {
            ReleasePushToTalk();
            _hackingOverlay?.Close("mainframe-screen-disabled");
            _mainframeOverlay?.Close("mainframe-screen-disabled");
        }

        private void LateUpdate()
        {
            if (!EnsureInitialized())
                return;

            PositionCanvasFromAnchors();

            if (_hackingOverlay != null && _hackingOverlay.IsOpen)
            {
                _hackingOverlay.Tick();
                if (_hackingOverlay.ConsumeSolved())
                {
                    OpenHackedMainframe();
                }
                else if (_hackingOverlay.ConsumeEscapeIfOpen())
                {
                    ShowIdleForCurrentHackState();
                }
                return;
            }

            if (_mainframeOverlay != null && _mainframeOverlay.IsOpen)
            {
                _mainframeOverlay.Tick();
                PollMainframeOverlayInput();
                return;
            }

            if (_state == ScreenState.HackingIntro && _introUntil > 0f &&
                (Time.unscaledTime >= _introUntil || IsIntroSkipRequested()))
            {
                OpenMinigame();
                return;
            }

            if (_state == ScreenState.IdleUnhacked || _state == ScreenState.IdleHacked || _state == ScreenState.HackingIntro)
                RefreshIdleTextIfNeeded();

            if (_state == ScreenState.IdleUnhacked && _mainframe != null && _mainframe.IsHacked)
                ShowIdleHacked();
            else if (_state == ScreenState.IdleHacked && _mainframe != null && !_mainframe.IsHacked)
                ShowIdleUnhacked();
        }

        internal void BeginHackingIntro(float seconds = 1.90f)
        {
            if (!EnsureInitialized())
                return;

            _introStartedAt = Time.unscaledTime;
            _introUntil = _introStartedAt + Mathf.Max(0.15f, seconds);
            SetState(ScreenState.HackingIntro);
            RefreshIdleText(force: true);
            _audio?.PlayScreenTransition();
            _audio?.StartIdle();
        }

        /// <summary>
        /// A confirm key drops the remaining hacking intro. The short arm delay keeps the keypress
        /// that started the session (or that dismissed a lockout notice) from skipping instantly,
        /// which is what makes a retry after a lockout cheap to get back into. Space is not a
        /// confirm key: at the CCTV station it is the held radar glance.
        /// </summary>
        private bool IsIntroSkipRequested()
        {
            if (Time.unscaledTime - _introStartedAt < 0.20f)
                return false;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return false;

            return keyboard.enterKey.wasPressedThisFrame
                   || keyboard.numpadEnterKey.wasPressedThisFrame;
        }

        internal void OpenMinigame()
        {
            if (!EnsureInitialized() || _mainframe == null)
                return;

            _introUntil = 0f;
            SetState(ScreenState.Minigame);
            ReleasePushToTalk();
            _mainframeOverlay?.Close("mainframe-screen-minigame");
            _hackingOverlay.Open(_mainframe);
            _audio?.PlayHackStart();
        }

        internal void OpenHackedMainframe()
        {
            if (!EnsureInitialized() || _mainframe == null)
                return;

            _introUntil = 0f;
            SetState(ScreenState.HackedMainframe);
            _hackingOverlay?.Close("mainframe-screen-control");
            _mainframeOverlay.Open(_mainframe);
            _audio?.PlayScreenTransition();
            _audio?.StartIdle();
        }

        internal void ShowIdleUnhacked()
        {
            _introUntil = 0f;
            SetState(ScreenState.IdleUnhacked);
            RefreshIdleText(force: true);
        }

        internal void ShowIdleHacked()
        {
            _introUntil = 0f;
            SetState(ScreenState.IdleHacked);
            RefreshIdleText(force: true);
        }

        internal void ReturnToIdleFromInteraction(string reason)
        {
            if (!EnsureInitialized())
                return;

            string closeReason = "mainframe-session-" + (string.IsNullOrWhiteSpace(reason) ? "ended" : reason);
            _introUntil = 0f;
            ReleasePushToTalk();
            _hackingOverlay?.Close(closeReason);
            _mainframeOverlay?.Close(closeReason);
            _audio?.StopIdle();
            _audio?.PlayPowerOff();
            ShowIdleForCurrentHackState();
        }

        internal void PlayDeliberateKeystroke(int fingerIndex, float strength)
        {
            int finger = Mathf.Clamp(fingerIndex, 0, 7);
            float clampedStrength = Mathf.Clamp(strength, 0f, 1.5f);
            SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV][MainframeScreen] PlayDeliberateKeystroke finger={finger} strength={clampedStrength:0.00} state={_state} mainframe='{(_mainframe != null ? _mainframe.name : "<none>")}'.");
            MainframeInteractionSession.NotifyScreenKeystroke(finger, clampedStrength);
        }

        private bool EnsureInitialized()
        {
            if (_canvasRoot != null)
                return true;

            _mainframe = GetComponent<MainframeSupport>() ?? GetComponentInParent<MainframeSupport>();
            _screenSurface = FindDeepChild(transform, "Mainframe_ScreenSurface");
            _screenCenter = FindDeepChild(transform, "MainframeScreenCenter");
            _screenTopLeft = FindDeepChild(transform, "MainframeScreenTopLeft");
            _screenBottomRight = FindDeepChild(transform, "MainframeScreenBottomRight");
            CacheScreenSurfaceBounds();

            if (_screenCenter == null || _screenTopLeft == null || _screenBottomRight == null)
            {
                if (!_missingAnchorLogged)
                {
                    _missingAnchorLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV][MainframeScreen] Missing MainframeScreenCenter/TopLeft/BottomRight anchors; physical mainframe monitor UI disabled.");
                }
                return false;
            }

            BuildCanvas();
            _hackingOverlay = new HackingOverlay(_canvasRect);
            _hackingOverlay.KeystrokeRequested = OnHackingOverlayKeystroke;
            _hackingOverlay.EnsureBuilt();
            _hackingOverlay.SetDock(Vector2.zero, _canvasRect.sizeDelta);

            _mainframeOverlay = new MainframeControlOverlay(_canvasRect);
            _mainframeOverlay.EnsureBuilt();
            _mainframeOverlay.SetDock(Vector2.zero, _canvasRect.sizeDelta);

            _audio = MainframeAudio.Ensure(transform);
            return true;
        }

        private void BuildCanvas()
        {
            _canvasRoot = new GameObject("MainframeWorldSpaceCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvasRoot.transform.SetParent(transform, false);
            _canvasRect = _canvasRoot.GetComponent<RectTransform>();
            _canvasRect.anchorMin = new Vector2(0.5f, 0.5f);
            _canvasRect.anchorMax = new Vector2(0.5f, 0.5f);
            _canvasRect.pivot = new Vector2(0.5f, 0.5f);

            Canvas canvas = _canvasRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 12;

            CanvasScaler scaler = _canvasRoot.GetComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 1f;
            scaler.referencePixelsPerUnit = 100f;

            PositionCanvasFromAnchors();
            BuildIdleLayer();
        }

        private void PositionCanvasFromAnchors()
        {
            if (_canvasRect == null || _screenCenter == null || _screenTopLeft == null || _screenBottomRight == null)
                return;

            // The mainframe's viewer side is root-local -Z (player anchor side); the screen glass
            // quad sits at the anchor plane, so the canvas must be nudged toward -Z to sit in
            // front of the glass. Identity local rotation is the readable orientation for a
            // viewer on the -Z side (a canvas reads correctly from its local -Z); the 2026-07-05
            // playtest proved the extra 180 yaw mirrored the minigame text.
            Vector3 forwardNudge = transform.InverseTransformVector(transform.forward * 0.003f);
            Vector3 anchorCenter = transform.InverseTransformPoint(_screenCenter.position);
            Vector3 localCenter = anchorCenter - forwardNudge;
            Vector3 localTopLeft = transform.InverseTransformPoint(_screenTopLeft.position);
            Vector3 localBottomRight = transform.InverseTransformPoint(_screenBottomRight.position);
            float width = Mathf.Max(0.05f, Mathf.Abs(localTopLeft.x - localBottomRight.x));
            float height = Mathf.Max(0.05f, Mathf.Abs(localTopLeft.y - localBottomRight.y));

            // The glass mesh is the thing the player actually sees, so it owns the canvas size and
            // its depth; the anchors are only the fallback for a prefab that ships without the
            // surface. Resizing the mesh without moving the anchors used to leave the UI at the old
            // size, and taking Z from the anchor left the canvas floating off a moved glass plane.
            if (TryMeasureScreenSurface(out Vector3 surfaceCenter, out Vector2 surfaceSize))
            {
                bool accepted = IsSurfaceMeasurementPlausible(width, height, surfaceSize);
                EvaluateScreenSurfaceDriftOnce(width, height, anchorCenter.z, surfaceCenter, surfaceSize, accepted);
                if (accepted)
                {
                    width = surfaceSize.x;
                    height = surfaceSize.y;
                    localCenter = new Vector3(surfaceCenter.x, surfaceCenter.y, surfaceCenter.z - forwardNudge.z);
                }
            }

            _canvasRect.localPosition = localCenter;
            _canvasRect.localRotation = Quaternion.identity;
            _canvasRect.localScale = Vector3.one / PixelsPerMeter;
            _canvasRect.sizeDelta = new Vector2(width * PixelsPerMeter, height * PixelsPerMeter);

            _hackingOverlay?.SetDock(Vector2.zero, _canvasRect.sizeDelta);
            _mainframeOverlay?.SetDock(Vector2.zero, _canvasRect.sizeDelta);
        }

        private void CacheScreenSurfaceBounds()
        {
            _hasScreenSurfaceBounds = false;
            _hasScreenSurfaceMeasure = false;
            if (_screenSurface == null)
                return;

            MeshFilter filter = _screenSurface.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
            {
                _screenSurfaceLocalBounds = filter.sharedMesh.bounds;
                _hasScreenSurfaceBounds = true;
                return;
            }

            // Skinned or otherwise mesh-less glass: the world AABB pulled back into the surface's
            // own space still tracks a resized quad. The per-axis conversion is what makes that
            // hold for a surface rotated relative to the world axes.
            Renderer renderer = _screenSurface.GetComponent<Renderer>();
            if (renderer == null)
                return;

            _screenSurfaceLocalBounds = MainframeSpawnDiagnostics.ConvertWorldBoundsToLocal(_screenSurface, renderer.bounds);
            _hasScreenSurfaceBounds = true;
        }

        /// <summary>Projects the cached surface bounds into root-local space. The surface and the
        /// root are a static transform pair in practice, so the corner sweep is cached against the
        /// surface-to-root matrix and only re-run when that matrix actually moves.</summary>
        private bool TryMeasureScreenSurface(out Vector3 center, out Vector2 size)
        {
            center = Vector3.zero;
            size = Vector2.zero;
            if (!_hasScreenSurfaceBounds || _screenSurface == null)
                return false;

            Matrix4x4 surfaceToRoot = transform.worldToLocalMatrix * _screenSurface.localToWorldMatrix;
            if (!_hasScreenSurfaceMeasure || surfaceToRoot != _screenSurfaceMeasureMatrix)
            {
                _screenSurfaceMeasureMatrix = surfaceToRoot;
                _hasScreenSurfaceMeasure = true;
                _screenSurfaceMeasureValid = MeasureScreenSurface(surfaceToRoot,
                    out _screenSurfaceMeasureCenter, out _screenSurfaceMeasureSize);
            }

            center = _screenSurfaceMeasureCenter;
            size = _screenSurfaceMeasureSize;
            return _screenSurfaceMeasureValid;
        }

        private bool MeasureScreenSurface(Matrix4x4 surfaceToRoot, out Vector3 center, out Vector2 size)
        {
            Vector3 boundsCenter = _screenSurfaceLocalBounds.center;
            Vector3 extents = _screenSurfaceLocalBounds.extents;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 offset = new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                Vector3 local = surfaceToRoot.MultiplyPoint3x4(boundsCenter + offset);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }

            size = new Vector2(max.x - min.x, max.y - min.y);
            center = (min + max) * 0.5f;
            return size.x > 0.05f && size.y > 0.05f;
        }

        /// <summary>A surface that disagrees with the anchors by more than a hard ratio is a
        /// mis-authored or wrongly named node, not a resized screen; rejecting it keeps such a
        /// node from silently taking the canvas over.</summary>
        private static bool IsSurfaceMeasurementPlausible(float anchorWidth, float anchorHeight, Vector2 surfaceSize)
        {
            return IsWithinRejectRatio(anchorWidth, surfaceSize.x) && IsWithinRejectRatio(anchorHeight, surfaceSize.y);
        }

        private static bool IsWithinRejectRatio(float anchorAxis, float surfaceAxis)
        {
            float anchor = Mathf.Max(0.0001f, anchorAxis);
            float surface = Mathf.Max(0.0001f, surfaceAxis);
            return surface <= anchor * ScreenSizeRejectRatio && anchor <= surface * ScreenSizeRejectRatio;
        }

        /// <summary>Runs once and latches, whatever the outcome: the surface and the anchors are
        /// authored data, so a second frame cannot say anything the first did not.</summary>
        private void EvaluateScreenSurfaceDriftOnce(float anchorWidth, float anchorHeight, float anchorCenterZ,
            Vector3 surfaceCenter, Vector2 surfaceSize, bool accepted)
        {
            if (_screenSizeDriftLogged)
                return;

            _screenSizeDriftLogged = true;

            float widthDrift = Mathf.Abs(surfaceSize.x - anchorWidth) / Mathf.Max(0.0001f, anchorWidth);
            float heightDrift = Mathf.Abs(surfaceSize.y - anchorHeight) / Mathf.Max(0.0001f, anchorHeight);
            float depthDrift = Mathf.Abs(surfaceCenter.z - anchorCenterZ);
            if (accepted
                && widthDrift <= ScreenSizeDriftTolerance
                && heightDrift <= ScreenSizeDriftTolerance
                && depthDrift <= ScreenDepthDriftTolerance)
            {
                return;
            }

            SurveillanceBootstrap.Log?.LogWarning(
                "[LethalCCTV][MainframeScreen] Mainframe_ScreenSurface bounds disagree with the screen anchors: " +
                $"surface={surfaceSize.x:0.0000}x{surfaceSize.y:0.0000}m anchors={anchorWidth:0.0000}x{anchorHeight:0.0000}m " +
                $"drift={widthDrift * 100f:0.0}%/{heightDrift * 100f:0.0}% " +
                $"surfaceZ={surfaceCenter.z:0.0000}m anchorZ={anchorCenterZ:0.0000}m depthDrift={depthDrift:0.0000}m; " +
                (accepted
                    ? "sizing the canvas from the surface. Re-author MainframeScreenTopLeft/BottomRight to match the glass."
                    : $"the disagreement exceeds {ScreenSizeRejectRatio:0.#}x, so the surface measurement is rejected and the anchors size the canvas. " +
                      "Check that Mainframe_ScreenSurface is the glass quad."));
        }

        private void BuildIdleLayer()
        {
            _idleRoot = new GameObject("MainframeIdleDiagnostics", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _idleRoot.transform.SetParent(_canvasRect, false);
            RectTransform idleRect = _idleRoot.GetComponent<RectTransform>();
            idleRect.anchorMin = Vector2.zero;
            idleRect.anchorMax = Vector2.one;
            idleRect.offsetMin = Vector2.zero;
            idleRect.offsetMax = Vector2.zero;

            Image back = _idleRoot.GetComponent<Image>();
            back.color = ScreenBlack;
            back.raycastTarget = false;

            GameObject textGo = new GameObject("DiagnosticsText", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(_idleRoot.transform, false);
            RectTransform textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            // Tighter margins than the 22/16 the fixed 17pt layout could afford: at the game's
            // native internal resolution every millimetre of glass is glyph pixels.
            textRect.offsetMin = new Vector2(12f, 10f);
            textRect.offsetMax = new Vector2(-12f, -10f);

            _idleText = textGo.GetComponent<TextMeshProUGUI>();
            _idleText.alignment = TextAlignmentOptions.TopLeft;
            // The canvas is sized in millimetres from the glass quad, so the usable point size
            // depends on a prefab dimension this code does not know. Auto-sizing grows the text
            // to whatever the real glass affords instead of guessing one fixed size; the floor is
            // the old fixed size, so the worst case is never smaller than before.
            _idleText.enableAutoSizing = true;
            _idleText.fontSizeMin = 17f;
            _idleText.fontSizeMax = 34f;
            _idleText.fontSize = 30f;
            _idleText.lineSpacing = 8f;
            _idleText.fontStyle = FontStyles.Bold;
            _idleText.color = ScreenGreen;
            _idleText.richText = false;
            _idleText.raycastTarget = false;
            _idleText.enableWordWrapping = false;
            _idleText.overflowMode = TextOverflowModes.Truncate;
        }

        /// <summary>
        /// #577 — the facility mainframe screen is not a CCTV station, so MonitorFocus's
        /// push-to-talk callbacks never fire here. Polling the same rebindable action keeps the
        /// INTERCOM view's transmit key identical at both hosts. Edge-triggered: the overlay's own
        /// intercom latch already ignores repeats, and this avoids an RPC every frame of a hold.
        /// </summary>
        private void PollMainframeOverlayPushToTalk()
        {
            if (_mainframeOverlay == null)
            {
                _pushToTalkHeld = false;
                return;
            }

            // Edge-latch the EFFECTIVE signal, not the raw key: the overlay discards a hold that
            // arrives outside the INTERCOM view, so latching the raw key would remember a press it
            // never acted on. Folding the view into the signal makes entering INTERCOM with the key
            // already down a rising edge, and backing out mid-hold a falling one, with no re-press.
            bool held = _mainframeOverlay.IsIntercomActive && MonitorFocus.IsWalkiePushToTalkHeld();
            if (held == _pushToTalkHeld)
                return;

            _pushToTalkHeld = held;
            _mainframeOverlay.SetPushToTalk(held);
        }

        /// <summary>Drops the transmit latch on every path that leaves the control overlay, so a
        /// close, an escape, or a teardown mid-hold cannot leave the intercom keyed open.</summary>
        private void ReleasePushToTalk()
        {
            if (!_pushToTalkHeld)
                return;

            _pushToTalkHeld = false;
            _mainframeOverlay?.SetPushToTalk(false);
        }

        private void PollMainframeOverlayInput()
        {
            PollMainframeOverlayPushToTalk();

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || _mainframeOverlay == null || !_mainframeOverlay.IsOpen)
                return;

            bool accepted = false;
            int finger = 0;
            float strength = 0.82f;
            bool isSelect = false;
            bool isEscape = false;
            int viewBefore = _mainframeOverlay.ViewToken;

            if (keyboard.upArrowKey.wasPressedThisFrame)
            {
                accepted = _mainframeOverlay.NavigateUp("mainframe-screen-up");
                finger = 2;
            }
            else if (keyboard.downArrowKey.wasPressedThisFrame)
            {
                accepted = _mainframeOverlay.NavigateDown("mainframe-screen-down");
                finger = 3;
            }
            else if (keyboard.leftArrowKey.wasPressedThisFrame)
            {
                accepted = _mainframeOverlay.NavigateLeft("mainframe-screen-left");
                finger = 1;
            }
            else if (keyboard.rightArrowKey.wasPressedThisFrame)
            {
                accepted = _mainframeOverlay.NavigateRight("mainframe-screen-right");
                finger = 4;
            }
            else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                accepted = _mainframeOverlay.Submit("mainframe-screen-enter");
                finger = 5;
                strength = 1.3f;
                isSelect = true;
            }
            else if (keyboard.escapeKey.wasPressedThisFrame)
            {
                accepted = _mainframeOverlay.ConsumeEscapeIfOpen();
                finger = 0;
                strength = 0.75f;
                isEscape = true;
            }

            if (accepted)
            {
                PlayDeliberateKeystroke(finger, strength);

                // Menu response layered over the keystroke click. Any input that lands on a new
                // page gets the screen-load sweep, because the monitor genuinely redraws; input
                // that acts in place gets the shorter select beep. An escape that closed the
                // overlay stays silent — it ends the session, whose power-off cue already plays.
                bool stillOpen = _mainframeOverlay.IsOpen;
                bool viewChanged = stillOpen && _mainframeOverlay.ViewToken != viewBefore;

                if (viewChanged)
                    _audio?.PlayScreenTransition();
                else if (isEscape)
                {
                    // Escape that closed the overlay: handled by the session-end cue.
                }
                else if (isSelect)
                    _audio?.PlaySelect();
                else
                    _audio?.PlayNavigate();
            }

            if (_mainframeOverlay != null && !_mainframeOverlay.IsOpen)
            {
                // Escape closed the overlay this frame; Close() already dropped the intercom, so
                // this only clears the local edge latch.
                _pushToTalkHeld = false;
                ShowIdleForCurrentHackState();
            }
        }

        private void OnHackingOverlayKeystroke(int fingerIndex, float strength, string source)
        {
            if (fingerIndex < 0)
            {
                _roundRobinFinger = (_roundRobinFinger + 1) % 5;
                fingerIndex = _roundRobinFinger;
            }
            PlayDeliberateKeystroke(fingerIndex, strength);
        }

        private void SetState(ScreenState state)
        {
            _state = state;
            bool idleVisible = state == ScreenState.IdleUnhacked || state == ScreenState.IdleHacked || state == ScreenState.HackingIntro;
            if (_idleRoot != null)
                _idleRoot.SetActive(idleVisible);
        }

        private void ShowIdleForCurrentHackState()
        {
            if (_mainframe != null && _mainframe.IsHacked)
                ShowIdleHacked();
            else
                ShowIdleUnhacked();
        }

        private void RefreshIdleTextIfNeeded()
        {
            if (Time.unscaledTime < _nextIdleRefresh)
                return;
            RefreshIdleText(force: false);
        }

        private void RefreshIdleText(bool force)
        {
            if (_idleText == null)
                return;

            _nextIdleRefresh = Time.unscaledTime + IdleRefreshSeconds;
            if (!force)
                _idleLineOffset = (_idleLineOffset + 1) % 64;

            if (_state == ScreenState.HackingIntro)
            {
                _idleText.color = ScreenGreen;
                _idleText.text = BuildIntroText();
                return;
            }

            if (_state == ScreenState.IdleHacked)
            {
                _idleText.color = ScreenGreen;
                _idleText.text = BuildIdleHackedText();
                return;
            }

            _idleText.color = _mainframe != null && _mainframe.IsLockedOut ? ScreenAmber : ScreenGreen;
            _idleText.text = BuildIdleUnhackedText();
        }

        // The idle screen is read from across the room at the game's native internal resolution,
        // where a glyph is only a handful of pixels tall. Every line below is kept short and the
        // line count low on purpose: the auto-sizing text grows to fill the glass, so each line
        // dropped or abbreviated is directly more pixels per glyph on the lines that remain.
        private string BuildIntroText()
        {
            float remaining = Mathf.Max(0f, _introUntil - Time.unscaledTime);
            return "MAINFRAME BOOT\n" +
                   "> KVM BUS\n" +
                   "> SPLICE\n" +
                   "> HANDSHAKE " + Mathf.CeilToInt(remaining * 10f).ToString("00");
        }

        private string BuildIdleHackedText()
        {
            return "MAINFRAME OS\n" +
                   "ACCESS GRANTED\n\n" +
                   "STORAGE  READY\n" +
                   "CHECKS   OPEN\n\n" +
                   BlinkCursor("OPERATOR?");
        }

        private string BuildIdleUnhackedText()
        {
            string lockout = _mainframe != null && _mainframe.IsLockedOut
                ? "LOCKOUT " + _mainframe.LockoutRemaining.ToString("0") + "S"
                : "TRACE ARMED";

            var sb = new StringBuilder(160);
            sb.Append("DIAGNOSTIC BUS\n");
            sb.Append(lockout).Append("\n\n");
            string[] lines =
            {
                "CRT SYNC....OK",
                "KVM SW....IDLE",
                "PATCH....NOISE",
                "RELAY...SEALED",
                "STASH...LOCKED",
                "COMMS.....COLD",
                "ALARM....READY",
                "SPLICE....WAIT"
            };

            for (int i = 0; i < 3; i++)
                sb.Append(lines[(_idleLineOffset + i) % lines.Length]).Append('\n');
            sb.Append('\n').Append(BlinkCursor("HANDSHAKE?"));
            return sb.ToString();
        }

        private static string BlinkCursor(string text)
        {
            return Time.unscaledTime % 1f < 0.5f ? text + " _" : text;
        }

        private static Transform FindDeepChild(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }
    }
}
