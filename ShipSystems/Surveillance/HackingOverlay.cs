using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Y4NGZCompany.Core.Compat;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed class HackingOverlay
    {
        // The stretch model (design height, runtime design width, dock resolve, frame resize)
        // lives in CrtDockLayout because the control terminal uses the identical model.
        // Every X position below is a fraction of the content span rather than a slice of 680.
        private const float DesignHeight = CrtDockLayout.DesignHeight;
        private const float ContentInset = CrtDockLayout.ContentInset;
        private const float StatusColumnFraction = 0.566f;
        private const float TargetColumnFraction = 0.509f;
        private const float LogColumnFraction = 0.308f;
        private const float LogColumnMinWidth = 168f;
        private const float LogColumnMaxWidth = 260f;
        private const float LogColumnTextInset = 20f;
        private const float GraphLeft = 30f;
        private const float GraphHeight = 208f;
        private const float GraphTop = -102f;
        private const float BootDurationSeconds = 1.45f;
        private const float BootSkipArmSeconds = 0.16f;
        private const int MaxLogLines = 4;
        private const int MaxNodeViews = 12;
        private const int MaxEdgeViews = 18;

        // The slice of normalized space the layout renormalizes into the graph rect is measured
        // from the live node positions at Open, so nothing here has to agree with CctvHackingGame's
        // graph builders. These are only the fallback used when a game has no nodes to measure.
        private const float GraphSourceFallbackMinX = 0.07f;
        private const float GraphSourceFallbackMaxX = 0.91f;
        private const float GraphSourceFallbackMinY = 0.18f;
        private const float GraphSourceFallbackMaxY = 0.82f;
        // A fully collapsed axis (every node on one line) would divide by zero when renormalized.
        private const float MinGraphSourceSpan = 0.05f;
        // Derived from NodeView's own geometry rather than guessed, so the worst case (the selected
        // origin diamond at full rotate pop with the return-to-origin outline at its widest) still
        // cannot reach past the graph rect edge.
        private static readonly float NodeEdgePadding = NodeView.MaxEdgeExtent();
        private const float RotateFeedbackSeconds = 0.34f;

        private const string ObjectiveLine = "ROUTE THE PULSE FROM ORIGIN TO ALL TARGETS";

        // One colour, one meaning.
        //   Green  - infrastructure: frame, header, idle relays, idle edges, idle arrows.
        //   Cyan   - uncompleted TARGETS only.
        //   Amber  - the player's SELECTION only.
        //   White  - the pulse, the segment it is crossing, and the origin while it is home.
        //   Red    - trace danger and error flashes only.
        private static readonly Color ScreenBlack = new Color(0.003f, 0.012f, 0.006f, 0.92f);
        private static readonly Color ScreenGreen = new Color(0.16f, 1f, 0.34f, 1f);
        private static readonly Color ScreenGreenDim = new Color(0.16f, 0.78f, 0.34f, 1f);
        private static readonly Color ScreenGreenFaint = new Color(0.04f, 0.22f, 0.10f, 1f);
        private static readonly Color ScreenAmber = new Color(0.92f, 0.82f, 0.30f, 1f);
        private static readonly Color ScreenCyan = new Color(0.18f, 0.86f, 1f, 1f);
        private static readonly Color ScreenRed = new Color(1f, 0.28f, 0.20f, 1f);
        private static readonly Color ScreenWhiteGreen = new Color(0.72f, 1f, 0.72f, 1f);
        private static readonly Color ScreenInk = new Color(0.012f, 0.045f, 0.020f, 1f);

        private static readonly string[] BootLines =
        {
            "> run splice --target mainframe",
            "> carrier: cctv monitor feed",
            "> handshake: relay map",
            "> trace guard: armed",
            "> input: arrows/enter"
        };

        private Transform _parent;
        private readonly List<TextMeshProUGUI> _textElements = new List<TextMeshProUGUI>(24);
        private readonly List<string> _resultLog = new List<string>(MaxLogLines);

        private GameObject _root;
        private RectTransform _rootRect;
        private RectTransform _panelRect;
        private RectTransform _graphRect;
        private RectTransform _frameTopRect;
        private RectTransform _frameBottomRect;
        private RectTransform _frameLeftRect;
        private RectTransform _frameRightRect;
        private RectTransform _logFrameRect;
        private GameObject _graphRoot;
        private RectTransform _traceBarBackRect;
        private RectTransform _traceBarFillRect;
        private Image _traceBarFillImage;
        private TextMeshProUGUI _objectiveText;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _traceText;
        private TextMeshProUGUI _targetText;
        private TextMeshProUGUI _statusText;
        private TextMeshProUGUI _actionText;
        private TextMeshProUGUI _logText;
        private TextMeshProUGUI _helpText;
        private TextMeshProUGUI _bootText;
        private EdgeView[] _edgeViews;
        private RouteArrowView[] _routeArrowViews;
        private NodeView[] _nodeViews;
        private Image _pulseImage;
        private Texture2D _scanlineTexture;
        private Texture2D _vignetteTexture;
        private Sprite _scanlineSprite;
        private Sprite _vignetteSprite;
        private Material _terminalFontMaterial;
        private Color _accentColor = ScreenGreen;
        private Color _dimColor = ScreenGreenDim;
        private float _designWidth = CrtDockLayout.DefaultDesignWidth;
        private int _sessionSeed;
        private string _targetDisplayName = "MAINFRAME";
        private string _lastStashCodeLine;
        private bool _failureNotified;
        private int _openedFrame;
        private float _inputArmedAt;
        private bool _inputArmedLogSent;
        private int _lastActionInputFrame = -1;
        private int _lastRotatedNodeId = -1;
        private float _rotateFlashUntil;
        private float _solvedAt;
        private bool _solvedConsumed;
        private float _bootStartedAt;
        private float _bootUntil;
        private bool _gameViewShown;
        private TerminalStateSnapshot _terminalBeforeOpen;
        private Y4NGZCompany.Facility.Security.CctvHackingGame _game;
        private Component _mainframeComponent;
        private bool _isOpen;
        private Vector2 _graphSourceMin = new Vector2(GraphSourceFallbackMinX, GraphSourceFallbackMinY);
        private Vector2 _graphSourceMax = new Vector2(GraphSourceFallbackMaxX, GraphSourceFallbackMaxY);

        // The relay disc is identical for every overlay instance and never changes, so one texture
        // and sprite are built on first use and shared process-wide. That also removes the teardown
        // question: nothing per-instance owns them, so nothing can leak them per session.
        private static Texture2D s_discTexture;
        private static Sprite s_discSprite;

        private static Sprite DiscSprite
        {
            get
            {
                if (s_discSprite != null)
                    return s_discSprite;

                s_discTexture = BuildDiscTexture(64);
                s_discSprite = Sprite.Create(s_discTexture,
                    new Rect(0f, 0f, s_discTexture.width, s_discTexture.height), new Vector2(0.5f, 0.5f));
                return s_discSprite;
            }
        }

        internal Action<int, float, string> KeystrokeRequested { get; set; }

        internal bool IsOpen => _isOpen;
        internal Component ActiveMainframe => _mainframeComponent;
        internal GameObject RootObject
        {
            get
            {
                EnsureBuilt();
                return _root;
            }
        }

        internal bool ConsumeSolved()
        {
            if (!_isOpen || _game == null || !_game.Solved || _solvedConsumed) return false;
            if (_solvedAt <= 0f || Time.unscaledTime < _solvedAt + 1.15f) return false;
            _solvedConsumed = true;
            return true;
        }

        internal HackingOverlay(Transform parent)
        {
            _parent = parent;
        }

        internal void EnsureBuilt()
        {
            if (_root != null) return;

            _root = new GameObject("LethalCCTV_CCTVFocusSignalSplice");
            _root.transform.SetParent(_parent, worldPositionStays: false);
            var canvasGroup = _root.AddComponent<CanvasGroup>();
            canvasGroup.blocksRaycasts = true;
            canvasGroup.interactable = true;

            _rootRect = _root.AddComponent<RectTransform>();
            _rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            _rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            _rootRect.pivot = new Vector2(0.5f, 0.5f);
            _rootRect.anchoredPosition = Vector2.zero;
            _rootRect.sizeDelta = new Vector2(_designWidth, DesignHeight);

            var backdrop = CreateRect(_root.transform, "CRTBackdrop", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.color = new Color(0f, 0f, 0f, 0.50f);
            backdropImage.raycastTarget = false;

            _panelRect = CreateRect(_root.transform, "TerminalPanel", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var panelImage = _panelRect.gameObject.AddComponent<Image>();
            panelImage.color = ScreenBlack;
            panelImage.raycastTarget = false;

            CreateCrtFrame(_panelRect);
            CreateHeader(_panelRect);
            CreateGraph(_panelRect);
            CreateLog(_panelRect);
            CreateCrtOverlays(_root.transform);
            ApplyLayout();

            _root.SetActive(false);
        }

        internal void SetDock(Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            EnsureBuilt();
            if (_rootRect == null) return;

            if (CrtDockLayout.TryResolveDock(_rootRect, anchoredPosition, sizeDelta, _designWidth,
                    out float designWidth, out float scale))
            {
                _designWidth = designWidth;
                ApplyLayout();
            }

            _rootRect.localScale = new Vector3(scale, scale, 1f);
        }

        private void ApplyLayout()
        {
            if (_rootRect == null) return;

            float width = _designWidth;
            float contentLeft = ContentInset;
            float contentRight = width - ContentInset;
            float contentWidth = Mathf.Max(120f, contentRight - contentLeft);

            _rootRect.sizeDelta = new Vector2(width, DesignHeight);

            CrtDockLayout.ResizeFrame(width, _frameTopRect, _frameBottomRect, _frameLeftRect, _frameRightRect);

            float statusX = contentLeft + contentWidth * StatusColumnFraction;
            float targetX = contentLeft + contentWidth * TargetColumnFraction;

            // The title and the trace readout sit at the fixed content-left inset, so their widths
            // are the gap up to the right-hand column on the same row.
            if (_titleText != null)
                _titleText.rectTransform.sizeDelta = new Vector2(Mathf.Max(120f, statusX - contentLeft), 44f);

            if (_traceText != null)
                _traceText.rectTransform.sizeDelta = new Vector2(Mathf.Max(90f, targetX - contentLeft), 23f);

            if (_statusText != null)
            {
                _statusText.rectTransform.anchoredPosition = new Vector2(statusX, -20f);
                _statusText.rectTransform.sizeDelta = new Vector2(contentRight - statusX, 25f);
            }

            if (_targetText != null)
            {
                _targetText.rectTransform.anchoredPosition = new Vector2(targetX, -56f);
                _targetText.rectTransform.sizeDelta = new Vector2(contentRight - targetX, 23f);
            }

            // The trace readout owns a bar as well as a percentage so the danger reads without
            // recolouring anything else on the screen.
            float traceBarWidth = Mathf.Max(90f, targetX - contentLeft - 10f);
            if (_traceBarBackRect != null)
            {
                _traceBarBackRect.anchoredPosition = new Vector2(contentLeft, -76f);
                _traceBarBackRect.sizeDelta = new Vector2(traceBarWidth, 6f);
            }
            if (_traceBarFillRect != null)
                _traceBarFillRect.sizeDelta = new Vector2(traceBarWidth * CurrentTraceNormalized(), 6f);

            if (_objectiveText != null)
                _objectiveText.rectTransform.sizeDelta = new Vector2(contentWidth, 17f);

            float logWidth = Mathf.Clamp(contentWidth * LogColumnFraction, LogColumnMinWidth, LogColumnMaxWidth);
            logWidth = Mathf.Min(logWidth, contentWidth - 140f);
            float logX = contentRight - logWidth;
            if (_logFrameRect != null)
            {
                _logFrameRect.anchoredPosition = new Vector2(logX, GraphTop);
                _logFrameRect.sizeDelta = new Vector2(logWidth, GraphHeight);
            }
            if (_actionText != null)
                _actionText.rectTransform.sizeDelta = new Vector2(logWidth - LogColumnTextInset, 82f);
            if (_logText != null)
                _logText.rectTransform.sizeDelta = new Vector2(logWidth - LogColumnTextInset, 118f);

            float graphRight = logX - 2f;
            float graphWidth = Mathf.Max(120f, graphRight - GraphLeft);
            if (_graphRect != null)
            {
                _graphRect.anchoredPosition = new Vector2(GraphLeft + graphWidth * 0.5f, GraphTop - GraphHeight * 0.5f);
                _graphRect.sizeDelta = new Vector2(graphWidth, GraphHeight);
            }

            if (_bootText != null)
                _bootText.rectTransform.sizeDelta = new Vector2(Mathf.Max(160f, graphRight - 50f), 190f);

            if (_helpText != null)
                _helpText.rectTransform.sizeDelta = new Vector2(contentWidth, 20f);
        }

        internal void SetParent(Transform parent)
        {
            if (parent == null) return;
            EnsureBuilt();
            _parent = parent;
            if (_root.transform.parent != parent)
                _root.transform.SetParent(parent, worldPositionStays: false);
            _root.transform.SetAsLastSibling();
        }

        internal void Open(Component mainframeComponent)
        {
            EnsureBuilt();
            if (mainframeComponent == null) return;

            _terminalBeforeOpen = TerminalStateSnapshot.Capture();
            ApplyTerminalStyleRefs(TerminalStyleRefs.Resolve());

            _mainframeComponent = mainframeComponent;
            _targetDisplayName = ResolveTargetDisplayName(mainframeComponent);
            _sessionSeed = Time.frameCount ^ (mainframeComponent.GetInstanceID() * 397);
            _game = new Y4NGZCompany.Facility.Security.CctvHackingGame(_sessionSeed);
            ResolveGraphSourceBounds();
            _failureNotified = false;
            _solvedAt = 0f;
            _solvedConsumed = false;
            _lastStashCodeLine = null;
            _openedFrame = Time.frameCount;
            _bootStartedAt = Time.unscaledTime;
            _bootUntil = _bootStartedAt + BootDurationSeconds;
            _inputArmedAt = _bootUntil + 0.05f;
            _inputArmedLogSent = false;
            _lastActionInputFrame = -1;
            _lastRotatedNodeId = -1;
            _rotateFlashUntil = 0f;
            _gameViewShown = false;
            ResetResultLog();
            _isOpen = true;

            _root.SetActive(true);
            SetGameViewVisible(false);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            RefreshHeader();
            RefreshBootText();

            TerminalStateSnapshot afterOpen = TerminalStateSnapshot.Capture();
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][SignalSplice] Opened CCTV-focus signal splice " +
                $"target='{_targetDisplayName}' component='{mainframeComponent.GetType().FullName}' instance={mainframeComponent.GetInstanceID()} " +
                $"seed={_sessionSeed} difficulty={_game.DifficultyLabel} risk='{_game.MoonRiskLabel}' targets={_game.RequiredTargetCount} " +
                $"inputArmedAt={_inputArmedAt:0.000} shipTerminalChanged={afterOpen.HasChangedFrom(_terminalBeforeOpen)} " +
                $"before={_terminalBeforeOpen.Describe()} after={afterOpen.Describe()}.");
        }

        internal void Close()
        {
            Close("unspecified");
        }

        internal void Close(string reason)
        {
            if (_root != null) _root.SetActive(false);
            if (_isOpen)
            {
                TerminalStateSnapshot afterClose = TerminalStateSnapshot.Capture();
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][SignalSplice] Closed CCTV-focus signal splice " +
                    $"target='{_targetDisplayName}' reason='{reason ?? "unspecified"}' shipTerminalChangedSinceOpen={afterClose.HasChangedFrom(_terminalBeforeOpen)} " +
                    $"terminalNow={afterClose.Describe()}.");
            }

            _isOpen = false;
            _game = null;
            _mainframeComponent = null;
            _targetDisplayName = "MAINFRAME";
            _resultLog.Clear();
            _inputArmedLogSent = false;
            _lastActionInputFrame = -1;
            _lastRotatedNodeId = -1;
            _rotateFlashUntil = 0f;
            _gameViewShown = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        internal void Tick()
        {
            if (!_isOpen) return;

            if (IsBooting())
            {
                HandleBootSkipInput();
                RefreshBootText();
                RefreshHeader();
                return;
            }

            if (!_gameViewShown)
            {
                _gameViewShown = true;
                SetGameViewVisible(true);
                SetStatus("PICK A RELAY", _accentColor);
                AppendLog(">Splice ready.");
            }

            if (IsInputArmed())
                HandleKeyboardPollingFallback();

            if (_game != null && !_game.Solved && !_game.Locked)
                HandleGameResult(_game.Update(Time.unscaledDeltaTime));

            RefreshHeader();
            RefreshGraph();
        }

        internal bool ConsumeEscapeIfOpen()
        {
            if (!_isOpen) return false;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                NotifyKeystroke(0, 0.75f, "keyboard-escape");
                Close("escape");
                return true;
            }
            return false;
        }

        internal bool NavigateUp(string source)
        {
            return TryNavigate(Vector2.up, source ?? "unknown");
        }

        internal bool NavigateDown(string source)
        {
            return TryNavigate(Vector2.down, source ?? "unknown");
        }

        internal bool NavigateLeft(string source)
        {
            return TryNavigate(Vector2.left, source ?? "unknown");
        }

        internal bool NavigateRight(string source)
        {
            return TryNavigate(Vector2.right, source ?? "unknown");
        }

        internal bool Submit(string source)
        {
            if (!_isOpen) return false;
            // Confirm during the boot crawl means "skip it", whichever driver delivered it, so the
            // action-bound submit at the station and the polled key at the mainframe agree.
            if (IsBooting())
                return RequestBootSkip(source ?? "submit");
            if (!IsInputArmed())
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Ignored submit before input armed source='{source}' target='{_targetDisplayName}'.");
                return false;
            }

            if (_game == null || _game.Solved || _game.Locked)
            {
                SetStatus(_game != null && _game.Solved ? "ACCESS GRANTED" : "TRACE LOCK", _game != null && _game.Solved ? _accentColor : ScreenRed);
                return false;
            }

            _lastActionInputFrame = Time.frameCount;
            NotifyKeystroke(5, 1f, source ?? "submit");
            bool rotated = _game.RotateSelectedRelay();
            if (rotated)
            {
                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode node = _game.GetNode(_game.SelectedNodeId);
                int active = node != null ? node.GetActiveNeighbor() : -1;
                _lastRotatedNodeId = _game.SelectedNodeId;
                _rotateFlashUntil = Time.unscaledTime + RotateFeedbackSeconds;
                SetStatus($"{FormatNodeShort(_game.SelectedNodeId)} NOW AIMS {FormatNodeShort(active)}", _accentColor);
                AppendLog($">{FormatNodeLabel(node)} now aims {FormatNodeLabel(_game.GetNode(active))}.");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Rotate source='{source}' target='{_targetDisplayName}' node={_game.SelectedNodeId} active={active}.");
            }
            else
            {
                SetStatus("FIXED - CANNOT AIM", ScreenRed);
                AppendLog(">Selected node is fixed.");
            }

            RefreshActionText();
            RefreshGraph();
            RefreshHeader();
            return rotated;
        }

        private void CreateCrtFrame(RectTransform parent)
        {
            _frameTopRect = CreateLine(parent, "FrameTop", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));
            _frameTopRect.anchoredPosition = new Vector2(0f, -8f);

            _frameBottomRect = CreateLine(parent, "FrameBottom", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            _frameBottomRect.anchoredPosition = new Vector2(0f, 8f);

            _frameLeftRect = CreateLine(parent, "FrameLeft", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _frameLeftRect.anchoredPosition = new Vector2(10f, 0f);

            _frameRightRect = CreateLine(parent, "FrameRight", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _frameRightRect.anchoredPosition = new Vector2(-10f, 0f);
        }

        private void CreateHeader(RectTransform parent)
        {
            // ApplyLayout owns every size here and the positions of the right-hand columns; the
            // zeros keep creation from advertising a second, stale set of numbers.
            _titleText = CreateTerminalText(parent, "Header",
                "CCTV MAINFRAME SPLICE\nUPLINK BOOT",
                16.5f, TextAlignmentOptions.TopLeft, ScreenGreen, new Vector2(ContentInset, -12f), Vector2.zero);

            _statusText = CreateTerminalText(parent, "Status", "LINKING",
                15f, TextAlignmentOptions.TopRight, ScreenGreen, Vector2.zero, Vector2.zero);

            _traceText = CreateTerminalText(parent, "Trace", "TRACE 00%",
                15f, TextAlignmentOptions.TopLeft, ScreenGreen, new Vector2(ContentInset, -56f), Vector2.zero);

            _targetText = CreateTerminalText(parent, "Targets", "",
                14f, TextAlignmentOptions.TopRight, ScreenGreen, Vector2.zero, Vector2.zero);

            _traceBarBackRect = CreateRect(parent, "TraceBarBack", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
            var traceBackImage = _traceBarBackRect.gameObject.AddComponent<Image>();
            traceBackImage.color = new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.16f);
            traceBackImage.raycastTarget = false;

            _traceBarFillRect = CreateRect(_traceBarBackRect, "TraceBarFill", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _traceBarFillRect.anchoredPosition = Vector2.zero;
            _traceBarFillImage = _traceBarFillRect.gameObject.AddComponent<Image>();
            _traceBarFillImage.color = ScreenGreen;
            _traceBarFillImage.raycastTarget = false;

            _objectiveText = CreateTerminalText(parent, "Objective", ObjectiveLine,
                12.5f, TextAlignmentOptions.TopLeft, ScreenGreenDim, new Vector2(ContentInset, -87f), Vector2.zero);
            // The objective is the one line that must survive the narrowest dock intact, and
            // wrapping is off for every terminal element, so let it shrink instead of overrun.
            _objectiveText.enableAutoSizing = true;
            _objectiveText.fontSizeMin = 10.5f;
            _objectiveText.fontSizeMax = 12.5f;
        }

        private void CreateGraph(RectTransform parent)
        {
            _graphRoot = new GameObject("SignalGraph");
            _graphRoot.transform.SetParent(parent, false);
            _graphRect = _graphRoot.AddComponent<RectTransform>();
            _graphRect.anchorMin = new Vector2(0f, 1f);
            _graphRect.anchorMax = new Vector2(0f, 1f);
            _graphRect.pivot = new Vector2(0.5f, 0.5f);

            var graphBack = _graphRoot.AddComponent<Image>();
            graphBack.color = new Color(0f, 0.08f, 0.025f, 0.16f);
            graphBack.raycastTarget = false;

            _edgeViews = new EdgeView[MaxEdgeViews];
            for (int i = 0; i < _edgeViews.Length; i++)
                _edgeViews[i] = new EdgeView(_graphRect, i);

            _routeArrowViews = new RouteArrowView[MaxNodeViews];
            for (int i = 0; i < _routeArrowViews.Length; i++)
                _routeArrowViews[i] = new RouteArrowView(_graphRect, i, RegisterText);

            Sprite discSprite = DiscSprite;
            _nodeViews = new NodeView[MaxNodeViews];
            for (int i = 0; i < _nodeViews.Length; i++)
                _nodeViews[i] = new NodeView(_graphRect, i, RegisterText, discSprite);

            GameObject pulseGo = new GameObject("Pulse", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            pulseGo.transform.SetParent(_graphRect, false);
            RectTransform pulseRect = pulseGo.GetComponent<RectTransform>();
            pulseRect.anchorMin = new Vector2(0.5f, 0.5f);
            pulseRect.anchorMax = new Vector2(0.5f, 0.5f);
            pulseRect.pivot = new Vector2(0.5f, 0.5f);
            pulseRect.sizeDelta = new Vector2(13f, 13f);
            _pulseImage = pulseGo.GetComponent<Image>();
            _pulseImage.sprite = discSprite;
            _pulseImage.color = ScreenWhiteGreen;
            _pulseImage.raycastTarget = false;

            _bootText = CreateTerminalText(parent, "BootText", "",
                15.5f, TextAlignmentOptions.TopLeft, ScreenGreen, new Vector2(44f, -112f), Vector2.zero);
            _bootText.lineSpacing = 5f;
        }

        private void CreateLog(RectTransform parent)
        {
            var logFrame = CreateRect(parent, "SignalLogFrame", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
            _logFrameRect = logFrame;
            var logFrameImage = logFrame.gameObject.AddComponent<Image>();
            logFrameImage.color = new Color(0.005f, 0.035f, 0.012f, 0.50f);
            logFrameImage.raycastTarget = false;

            _actionText = CreateTerminalText(logFrame, "SelectedRelay", "",
                14f, TextAlignmentOptions.TopLeft, ScreenAmber, new Vector2(10f, -8f), Vector2.zero);
            _actionText.lineSpacing = 3f;

            _logText = CreateTerminalText(logFrame, "SignalLog", "",
                12f, TextAlignmentOptions.TopLeft, ScreenGreen, new Vector2(10f, -96f), Vector2.zero);
            _logText.lineSpacing = 1.5f;
            // Every other terminal element is a short fixed-length string, but the log carries
            // arbitrary-length lines (the revealed stash codes above all), and the column shrinks
            // to LogColumnMinWidth on the squarest docks. Wrapping keeps those lines inside the
            // log frame instead of bleeding across the graph; truncation caps the wrapped height.
            _logText.enableWordWrapping = true;
            _logText.overflowMode = TextOverflowModes.Truncate;

            _helpText = CreateTerminalText(parent, "Help",
                "LINKING...",
                13f, TextAlignmentOptions.Center, ScreenGreenDim, new Vector2(ContentInset, -330f), Vector2.zero);
        }

        private void CreateCrtOverlays(Transform parent)
        {
            var glow = CreateRect(parent, "PhosphorGlow", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var glowImage = glow.gameObject.AddComponent<Image>();
            glowImage.color = new Color(0f, 0.55f, 0.18f, 0.045f);
            glowImage.raycastTarget = false;

            _scanlineTexture = BuildScanlineTexture();
            _scanlineSprite = Sprite.Create(_scanlineTexture, new Rect(0f, 0f, _scanlineTexture.width, _scanlineTexture.height), new Vector2(0.5f, 0.5f));
            var scanlines = CreateRect(parent, "Scanlines", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var scanlineImage = scanlines.gameObject.AddComponent<Image>();
            scanlineImage.sprite = _scanlineSprite;
            scanlineImage.type = Image.Type.Tiled;
            scanlineImage.color = Color.white;
            scanlineImage.raycastTarget = false;

            _vignetteTexture = BuildVignetteTexture(128, 72);
            _vignetteSprite = Sprite.Create(_vignetteTexture, new Rect(0f, 0f, _vignetteTexture.width, _vignetteTexture.height), new Vector2(0.5f, 0.5f));
            var vignette = CreateRect(parent, "Vignette", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var vignetteImage = vignette.gameObject.AddComponent<Image>();
            vignetteImage.sprite = _vignetteSprite;
            vignetteImage.color = Color.white;
            vignetteImage.raycastTarget = false;
        }

        private void HandleKeyboardPollingFallback()
        {
            if (Time.frameCount == _lastActionInputFrame) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.upArrowKey.wasPressedThisFrame)
                NavigateUp("keyboard-up");
            else if (keyboard.downArrowKey.wasPressedThisFrame)
                NavigateDown("keyboard-down");
            else if (keyboard.leftArrowKey.wasPressedThisFrame)
                NavigateLeft("keyboard-left");
            else if (keyboard.rightArrowKey.wasPressedThisFrame)
                NavigateRight("keyboard-right");
            // Space is not a confirm key here: at the CCTV station it is the held radar glance, and
            // the on-screen help advertises ENTER.
            else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                Submit("keyboard-enter");
        }

        private bool TryNavigate(Vector2 direction, string source)
        {
            if (!_isOpen) return false;
            if (!IsInputArmed())
            {
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Ignored navigate before input armed source='{source}' target='{_targetDisplayName}'.");
                return false;
            }
            if (_game == null || _game.Solved || _game.Locked)
            {
                SetStatus(_game != null && _game.Solved ? "ACCESS GRANTED" : "TRACE LOCK", _game != null && _game.Solved ? _accentColor : ScreenRed);
                return false;
            }

            _lastActionInputFrame = Time.frameCount;
            NotifyKeystroke(FingerForDirection(direction), 0.82f, source ?? "navigate");
            bool moved = _game.MoveSelection(direction);
            if (moved)
            {
                // The status line names the immediate next action, not just the cursor position.
                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode picked = _game.GetNode(_game.SelectedNodeId);
                SetStatus($"ENTER AIMS {FormatNodeShort(_game.SelectedNodeId)}>{FormatNodeShort(GetNextNeighborId(picked))}", ScreenAmber);
                RefreshActionText();
                RefreshGraph();
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Navigate source='{source}' target='{_targetDisplayName}' selected={_game.SelectedNodeId}.");
            }
            else
            {
                SetStatus("NO RELAY THAT WAY", ScreenRed);
            }
            return moved;
        }

        private void NotifyKeystroke(int fingerIndex, float strength, string source)
        {
            try
            {
                KeystrokeRequested?.Invoke(fingerIndex, Mathf.Clamp01(strength), source ?? "unknown");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV][SignalSplice] Keystroke callback failed: {ex.Message}");
            }
        }

        private static int FingerForDirection(Vector2 direction)
        {
            if (direction.x < -0.5f) return 1;
            if (direction.x > 0.5f) return 4;
            if (direction.y > 0.5f) return 2;
            if (direction.y < -0.5f) return 3;
            return 0;
        }

        private void HandleGameResult(Y4NGZCompany.Facility.Security.SignalSpliceUpdateResult result)
        {
            if (_game == null || result == Y4NGZCompany.Facility.Security.SignalSpliceUpdateResult.None)
                return;

            if (result == Y4NGZCompany.Facility.Security.SignalSpliceUpdateResult.TargetHit)
            {
                MainframeAudio.Current?.PlayHackProgress();
                SetStatus(_game.MustReturnToOrigin ? "RETURN TO ORIGIN" : "TARGET LINKED",
                    _game.MustReturnToOrigin ? ScreenWhiteGreen : _accentColor);
                AppendLog($">Target {_game.CompletedTargetCount}/{_game.RequiredTargetCount} ack.");
                if (_game.MustReturnToOrigin)
                    AppendLog(">Return to origin.");
                return;
            }

            if (result == Y4NGZCompany.Facility.Security.SignalSpliceUpdateResult.ReturnedToOrigin)
            {
                SetStatus("ORIGIN LIVE", _accentColor);
                return;
            }

            if (result == Y4NGZCompany.Facility.Security.SignalSpliceUpdateResult.TraceLocked)
            {
                MainframeAudio.Current?.PlayHackFail();
                TryNotifyMainframeFailure();
                SetStatus("TRACE LOCK", ScreenRed);
                AppendLog(">Trace locked.");
                AppendLog(">Mainframe alarmed.");
                return;
            }

            if (result == Y4NGZCompany.Facility.Security.SignalSpliceUpdateResult.Solved && _solvedAt <= 0f)
            {
                MainframeAudio.Current?.PlayHackSuccess();
                TryMarkMainframeHacked();
                _solvedAt = Time.unscaledTime;
                SetStatus("ACCESS GRANTED", _accentColor);
                AppendLog(">Origin handshake.");
                AppendLog(">Access granted.");
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Solved target='{_targetDisplayName}' difficulty={_game.DifficultyLabel}.");
            }
        }

        private void TryMarkMainframeHacked()
        {
            if (_mainframeComponent == null) return;

            bool invoked = false;
            try
            {
                if (_mainframeComponent is Y4NGZCompany.Facility.Mainframe.MainframeSupport mainframe)
                {
                    mainframe.MarkHackedServerRpc();
                    invoked = true;
                }
                else
                {
                    MethodInfo method = _mainframeComponent.GetType().GetMethod("MarkHackedServerRpc",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (method != null)
                    {
                        method.Invoke(_mainframeComponent, new object[] { default(ServerRpcParams) });
                        invoked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][SignalSplice] Hack success RPC failed for target='{_targetDisplayName}': {ex.Message}");
            }

            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Hack success path target='{_targetDisplayName}' invoked={invoked}.");
        }

        private void TryNotifyMainframeFailure()
        {
            if (_failureNotified || _mainframeComponent == null) return;
            _failureNotified = true;

            bool invoked = false;
            try
            {
                if (_mainframeComponent is Y4NGZCompany.Facility.Mainframe.MainframeSupport mainframe)
                {
                    mainframe.MarkLockoutServerRpc(Y4NGZCompany.Facility.Security.CctvSupportState.MainframeLockoutSeconds);
                    invoked = true;
                }
                else
                {
                    MethodInfo method = _mainframeComponent.GetType().GetMethod("MarkLockoutServerRpc",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (method != null)
                    {
                        method.Invoke(_mainframeComponent, new object[]
                        {
                            Y4NGZCompany.Facility.Security.CctvSupportState.MainframeLockoutSeconds,
                            default(ServerRpcParams)
                        });
                        invoked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][SignalSplice] Hack failure RPC failed for target='{_targetDisplayName}': {ex.Message}");
            }

            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Hack failure/lockout path target='{_targetDisplayName}' invoked={invoked}.");
        }

        private void RefreshHeader()
        {
            if (_game == null) return;

            bool booting = IsBooting();
            if (_titleText != null)
            {
                _titleText.text = booting
                    ? "CCTV MAINFRAME SPLICE\nUPLINK BOOT"
                    : $"CCTV MAINFRAME SPLICE\n{_targetDisplayName}";
            }

            float trace = _game.TraceNormalized;
            // Below the danger threshold the readout only slides from accent green toward red; it
            // never borrows amber, which now belongs to the player's selection alone.
            Color traceColor = trace >= 0.82f
                ? ScreenRed
                : Color.Lerp(_accentColor, ScreenRed, Mathf.Clamp01(trace / 0.82f) * 0.85f);

            if (_traceText != null)
            {
                int tracePercent = Mathf.RoundToInt(trace * 100f);
                _traceText.text = trace >= 0.82f ? $"TRACE {tracePercent:00}% !" : $"TRACE {tracePercent:00}%";
                _traceText.color = traceColor;
            }

            if (_traceBarFillRect != null && _traceBarBackRect != null)
            {
                _traceBarFillRect.sizeDelta = new Vector2(_traceBarBackRect.sizeDelta.x * Mathf.Clamp01(trace), 6f);
                if (_traceBarFillImage != null)
                    _traceBarFillImage.color = traceColor;
            }

            if (_targetText != null)
            {
                _targetText.text = $"{BuildGoalSummary()}  {_game.DifficultyLabel}";
                _targetText.color = _accentColor;
            }

            // The status line is a one-shot reaction to the last input, so the return phase states
            // itself on the objective line instead, where it persists for as long as it is true.
            if (_objectiveText != null)
            {
                bool returning = IsReturnPhase();
                _objectiveText.text = returning
                    ? "ALL TARGETS LINKED - RETURN THE PULSE TO ORIGIN (O)"
                    : ObjectiveLine;
                _objectiveText.color = returning ? ScreenWhiteGreen : _dimColor;
            }

            if (_game.Solved)
                SetStatus("ACCESS GRANTED", _accentColor);
            else if (_game.Locked)
                SetStatus("TRACE LOCK", ScreenRed);

            if (Y4NGZCompany.Facility.Security.CctvSupportApi.IsMainframeHacked)
            {
                string codesLine = BuildStashCodeLogLine();
                if (!string.IsNullOrEmpty(codesLine) && !string.Equals(codesLine, _lastStashCodeLine, StringComparison.Ordinal))
                {
                    _lastStashCodeLine = codesLine;
                    AppendLog(codesLine);
                }
            }

            RefreshActionText();
        }

        private void RefreshActionText()
        {
            if (_actionText == null)
                return;

            if (_game == null || !_gameViewShown)
            {
                _actionText.text = "BOOTING\nWAIT FOR LINK";
                _actionText.color = _dimColor;
                return;
            }

            if (_game.Solved)
            {
                _actionText.text = "ACCESS GRANTED\nOPENING CONTROL";
                _actionText.color = _accentColor;
                return;
            }

            if (_game.Locked)
            {
                _actionText.text = "TRACE LOCKED\nLINK FAILED";
                _actionText.color = ScreenRed;
                return;
            }

            Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode selected = _game.GetNode(_game.SelectedNodeId);
            if (selected == null)
            {
                _actionText.text = "NO RELAY\nARROWS SELECT";
                _actionText.color = ScreenRed;
                return;
            }

            int active = selected.GetActiveNeighbor();
            int next = GetNextNeighborId(selected);

            if (!_game.CanRotateNode(selected.Id))
            {
                _actionText.text =
                    $"SELECTED {FormatNodeLabel(selected)}\n" +
                    "FIXED - CANNOT AIM\n" +
                    BuildGoalSummary();
                _actionText.color = ScreenRed;
                return;
            }

            // The selection panel is amber because it describes the selection and nothing else;
            // the rotate acknowledgement is motion on the graph, not a colour swap here.
            _actionText.text =
                $"SELECTED {FormatNodeLabel(selected)}\n" +
                $"AIMS AT {FormatNodeLabel(_game.GetNode(active))}\n" +
                $"ENTER AIMS {FormatNodeLabel(_game.GetNode(next))}";
            _actionText.color = ScreenAmber;
        }

        private void RefreshGraph()
        {
            if (_game == null || _graphRect == null || !_gameViewShown)
                return;

            int edgeCount = _game.Edges != null ? _game.Edges.Count : 0;
            for (int i = 0; i < _edgeViews.Length; i++)
            {
                if (i >= edgeCount)
                {
                    _edgeViews[i].Hide();
                    continue;
                }

                Y4NGZCompany.Facility.Security.SignalEdge edge = _game.Edges[i];
                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode from = _game.GetNode(edge.From);
                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode to = _game.GetNode(edge.To);
                if (from == null || to == null)
                {
                    _edgeViews[i].Hide();
                    continue;
                }

                bool current = _game.IsCurrentSegment(edge.From, edge.To);
                bool selectedRoute = IsSelectedActiveEdge(edge.From, edge.To);
                // Idle wiring is infrastructure green and heavy enough to read as topology; the
                // hierarchy selected > current > idle survives as thickness and alpha only.
                _edgeViews[i].Render(
                    ToGraphLocal(from.Position),
                    ToGraphLocal(to.Position),
                    selectedRoute ? ScreenAmber : current ? ScreenWhiteGreen : _accentColor,
                    selectedRoute ? 6.2f : current ? 5.0f : 3.5f,
                    selectedRoute ? 1f : current ? 0.96f : 0.65f);
            }

            RefreshRouteArrows();

            int nodeCount = _game.Nodes != null ? _game.Nodes.Count : 0;
            for (int i = 0; i < _nodeViews.Length; i++)
            {
                if (i >= nodeCount || _game.Nodes[i] == null)
                {
                    _nodeViews[i].Hide();
                    continue;
                }

                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode node = _game.Nodes[i];
                bool selected = node.Id == _game.SelectedNodeId;
                bool current = node.Id == _game.FromNodeId || node.Id == _game.ToNodeId;
                bool completed = _game.IsTargetCompleted(node.Id);
                float pop = node.Id == _lastRotatedNodeId ? RotateFeedback01() : 0f;
                bool originAlert = node.IsOrigin && IsReturnPhase();
                _nodeViews[i].Render(node, ToGraphLocal(node.Position), selected, current, completed,
                    _game.CanRotateNode(node.Id), pop, originAlert, _accentColor, _dimColor);
            }

            if (_pulseImage != null)
            {
                _pulseImage.gameObject.SetActive(!_game.Locked);
                RectTransform pulseRect = _pulseImage.rectTransform;
                pulseRect.anchoredPosition = ToGraphLocal(_game.GetPulsePosition());
                float pulse = 13f + Mathf.Sin(Time.unscaledTime * 18f) * 2.0f;
                pulseRect.sizeDelta = new Vector2(pulse, pulse);
                // The pulse never changes colour: the "return to origin" cue is the origin node's
                // pulsing outline, so white-green always means "this is the signal".
                _pulseImage.color = ScreenWhiteGreen;
            }
        }

        /// <summary>
        /// True while every target is linked and the pulse still owes the origin a visit. A locked
        /// board is over, so it shows no urgent beacon and no return instruction.
        /// </summary>
        private bool IsReturnPhase()
        {
            return _game != null && _game.MustReturnToOrigin && !_game.Solved && !_game.Locked;
        }

        /// <summary>1 immediately after a rotate, falling to 0 over <see cref="RotateFeedbackSeconds"/>.</summary>
        private float RotateFeedback01()
        {
            if (_lastRotatedNodeId < 0 || _rotateFlashUntil <= 0f)
                return 0f;

            float remaining = _rotateFlashUntil - Time.unscaledTime;
            if (remaining <= 0f)
                return 0f;

            return Mathf.Clamp01(remaining / RotateFeedbackSeconds);
        }

        private void RefreshRouteArrows()
        {
            if (_routeArrowViews == null)
                return;

            int arrowIndex = 0;
            int nodeCount = _game.Nodes != null ? _game.Nodes.Count : 0;
            for (int i = 0; i < nodeCount && arrowIndex < _routeArrowViews.Length; i++)
            {
                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode node = _game.Nodes[i];
                if (node == null || node.IsTarget || node.NeighborCount <= 0)
                    continue;

                int active = node.GetActiveNeighbor();
                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode to = _game.GetNode(active);
                if (to == null || to.Id == node.Id)
                    continue;

                bool selected = node.Id == _game.SelectedNodeId;
                bool currentDeparture = node.Id == _game.FromNodeId && active == _game.ToNodeId;
                bool nextArrivalDecision = node.Id == _game.ToNodeId && !_game.IsRequiredTarget(node.Id);

                // Arrows are direction, not identity: amber only while the arrow belongs to the
                // selection, white-green only while the pulse is actually crossing it, otherwise
                // infrastructure green with the imminent decision merely brighter and larger.
                Color color = selected ? ScreenAmber : currentDeparture ? ScreenWhiteGreen : _accentColor;
                float alpha = selected || currentDeparture ? 1f : nextArrivalDecision ? 0.95f : 0.72f;
                float size = selected ? 21f : currentDeparture ? 20f : nextArrivalDecision ? 18f : 16f;
                float sweep = node.Id == _lastRotatedNodeId ? RotateFeedback01() : 0f;

                int slot = arrowIndex++;
                _routeArrowViews[slot].Render(
                    ToGraphLocal(node.Position),
                    ToGraphLocal(to.Position),
                    color,
                    alpha,
                    size,
                    slot,
                    sweep);
            }

            for (int i = arrowIndex; i < _routeArrowViews.Length; i++)
                _routeArrowViews[i].Hide();
        }

        private void SetGameViewVisible(bool visible)
        {
            if (_graphRoot != null)
                _graphRoot.SetActive(visible);
            if (_bootText != null)
                _bootText.gameObject.SetActive(!visible);
            if (_objectiveText != null)
                _objectiveText.gameObject.SetActive(visible);
            if (_traceBarBackRect != null)
                _traceBarBackRect.gameObject.SetActive(visible);
            if (_helpText != null)
                _helpText.text = visible ? "ARROWS PICK RELAY   ENTER AIMS IT   ESC EXIT" : "LINKING...";
            RefreshActionText();
            if (visible)
                RefreshGraph();
        }

        private bool IsBooting()
        {
            return _isOpen && Time.unscaledTime < _bootUntil;
        }

        /// <summary>
        /// Raw-keyboard boot skip, used only where no action-based driver reaches the overlay (the
        /// physical mainframe screen). At the CCTV station the rebindable submit action arrives
        /// through <see cref="Submit"/>, which routes to <see cref="RequestBootSkip"/> itself.
        /// Space is deliberately not a skip key: at the station it is the held radar glance.
        /// </summary>
        private void HandleBootSkipInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                return;
            if (!keyboard.enterKey.wasPressedThisFrame && !keyboard.numpadEnterKey.wasPressedThisFrame)
                return;

            RequestBootSkip("keyboard-enter");
        }

        /// <summary>
        /// Drops the rest of the boot crawl once the first line is on screen. The frame and time
        /// guards stop the keypress that opened the splice from skipping instantly. Input arms
        /// immediately and only the skipping frame is consumed, so a fast second confirm press
        /// lands on the board instead of being swallowed by a re-arm delay.
        /// </summary>
        internal bool RequestBootSkip(string source)
        {
            if (!IsBooting())
                return false;
            if (Time.frameCount <= _openedFrame + 1)
                return false;

            float now = Time.unscaledTime;
            if (now - _bootStartedAt < BootSkipArmSeconds)
                return false;

            _bootUntil = now;
            _inputArmedAt = now;
            _lastActionInputFrame = Time.frameCount;
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Boot skipped source='{source ?? "unknown"}' target='{_targetDisplayName}' after {now - _bootStartedAt:0.00}s.");
            return true;
        }

        private void RefreshBootText()
        {
            if (_bootText == null) return;

            float elapsed = Mathf.Max(0f, Time.unscaledTime - _bootStartedAt);
            int visibleLines = Mathf.Clamp(Mathf.FloorToInt(elapsed / 0.23f) + 1, 1, BootLines.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < visibleLines; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(BootLines[i]);
            }

            if (visibleLines >= BootLines.Length && Mathf.FloorToInt(Time.unscaledTime * 4f) % 2 == 0)
                sb.Append("\n> _");

            if (elapsed >= BootSkipArmSeconds)
                sb.Append("\n\n> [ENTER] skip");

            _bootText.text = sb.ToString();
        }

        private bool IsInputArmed()
        {
            if (Time.frameCount <= _openedFrame || Time.unscaledTime < _inputArmedAt)
                return false;

            if (!_inputArmedLogSent)
            {
                _inputArmedLogSent = true;
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Input armed target='{_targetDisplayName}' frame={Time.frameCount} selected={_game?.SelectedNodeId}.");
            }

            return true;
        }

        private void ResetResultLog()
        {
            _resultLog.Clear();
            AppendLog(">Carrier found.");
            if (_game != null)
            {
                AppendLog($">Risk {_game.MoonRiskLabel}.");
                AppendLog($">Targets {_game.RequiredTargetCount}.");
            }
        }

        private void AppendLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            while (_resultLog.Count >= MaxLogLines)
                _resultLog.RemoveAt(0);
            _resultLog.Add(line);
            RefreshLogText();
        }

        private void RefreshLogText()
        {
            if (_logText == null) return;
            var sb = new StringBuilder();
            for (int i = 0; i < _resultLog.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(_resultLog[i]);
            }
            _logText.text = sb.ToString();
        }

        private void SetStatus(string text, Color color)
        {
            if (_statusText == null) return;
            _statusText.text = text ?? string.Empty;
            _statusText.color = color;
        }

        private string BuildStashCodeLogLine()
        {
            var codes = Y4NGZCompany.Facility.Security.CctvSupportApi.RevealedStashCodes;
            if (codes == null || codes.Count == 0) return null;

            var sb = new StringBuilder(">Stash codes ");
            for (int i = 0; i < codes.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Y4NGZCompany.Facility.Security.CctvSupportApi.FormatStashCode(codes[i]));
            }
            return sb.ToString();
        }

        private string BuildGoalSummary()
        {
            if (_game == null)
                return "GOAL --";

            if (_game.MustReturnToOrigin)
                return "GOAL RETURN O";

            var sb = new StringBuilder("GOAL");
            int remaining = 0;
            for (int i = 0; i < _game.Nodes.Count; i++)
            {
                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode node = _game.Nodes[i];
                if (node == null || !node.IsTarget || _game.IsTargetCompleted(node.Id))
                    continue;

                remaining++;
                if (remaining <= 3)
                    sb.Append(' ').Append(FormatNodeLabel(node));
            }

            if (remaining <= 0)
                return "GOAL RETURN O";
            if (remaining > 3)
                return "GOAL " + remaining + " TARGETS";

            return sb.ToString();
        }

        private float CurrentTraceNormalized()
        {
            return _game != null ? Mathf.Clamp01(_game.TraceNormalized) : 0f;
        }

        /// <summary>
        /// Measures the slice of normalized space this session's graph actually occupies, so the
        /// renormalization below owns no constant that has to stay in step with the game's
        /// generators. Read-only: the game data is never touched.
        /// </summary>
        private void ResolveGraphSourceBounds()
        {
            _graphSourceMin = new Vector2(GraphSourceFallbackMinX, GraphSourceFallbackMinY);
            _graphSourceMax = new Vector2(GraphSourceFallbackMaxX, GraphSourceFallbackMaxY);

            IReadOnlyList<Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode> nodes = _game?.Nodes;
            if (nodes == null)
                return;

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            int measured = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode node = nodes[i];
                if (node == null)
                    continue;

                min = Vector2.Min(min, node.Position);
                max = Vector2.Max(max, node.Position);
                measured++;
            }

            if (measured <= 0)
                return;

            ExpandToMinimumSpan(ref min.x, ref max.x);
            ExpandToMinimumSpan(ref min.y, ref max.y);
            _graphSourceMin = min;
            _graphSourceMax = max;
        }

        /// <summary>Widens a collapsed axis about its centre so renormalizing it stays finite.</summary>
        private static void ExpandToMinimumSpan(ref float min, ref float max)
        {
            float span = max - min;
            if (span >= MinGraphSourceSpan)
                return;

            float centre = (min + max) * 0.5f;
            min = centre - MinGraphSourceSpan * 0.5f;
            max = centre + MinGraphSourceSpan * 0.5f;
        }

        /// <summary>
        /// Maps a game-space node position into the graph rect. The occupied slice measured at Open
        /// is renormalized to fill the rect (inset by the largest node's worst-case half extent, so
        /// no node can clip the rect edge). The game data is untouched; this is presentation only.
        /// </summary>
        private Vector2 ToGraphLocal(Vector2 normalized)
        {
            if (_graphRect == null)
                return Vector2.zero;

            Vector2 size = _graphRect.rect.size;
            if (size.x <= 0.01f || size.y <= 0.01f)
                size = _graphRect.sizeDelta;

            float usableX = Mathf.Max(16f, size.x - NodeEdgePadding * 2f);
            float usableY = Mathf.Max(16f, size.y - NodeEdgePadding * 2f);
            float tx = Mathf.InverseLerp(_graphSourceMin.x, _graphSourceMax.x, normalized.x);
            float ty = Mathf.InverseLerp(_graphSourceMin.y, _graphSourceMax.y, normalized.y);
            return new Vector2((tx - 0.5f) * usableX, (ty - 0.5f) * usableY);
        }

        private bool IsSelectedActiveEdge(int first, int second)
        {
            if (_game == null)
                return false;

            Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode selected = _game.GetNode(_game.SelectedNodeId);
            if (selected == null)
                return false;

            int active = selected.GetActiveNeighbor();
            return (selected.Id == first && active == second) || (selected.Id == second && active == first);
        }

        private static int GetNextNeighborId(Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode node)
        {
            if (node == null || node.NeighborCount <= 0)
                return -1;

            int index = PositiveModulo(node.ActiveNeighborIndex + 1, node.NeighborCount);
            return node.Neighbors[index];
        }

        private static int PositiveModulo(int value, int modulus)
        {
            if (modulus <= 0)
                return 0;

            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private string FormatNodeShort(int nodeId)
        {
            return FormatNodeLabel(_game != null ? _game.GetNode(nodeId) : null);
        }

        private static string FormatNodeLabel(Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode node)
        {
            if (node == null)
                return "--";
            if (node.IsOrigin)
                return "O";
            if (node.IsTarget)
                return "T" + node.TargetSlot;
            return "R" + node.Id.ToString("00");
        }

        private void ApplyTerminalStyleRefs(TerminalStyleRefs refs)
        {
            _accentColor = refs.Accent;
            _dimColor = refs.Dim;

            if (refs.FontMaterial != null && _terminalFontMaterial != null)
            {
                UnityEngine.Object.Destroy(_terminalFontMaterial);
                _terminalFontMaterial = null;
            }

            if (refs.FontMaterial != null)
            {
                _terminalFontMaterial = new Material(refs.FontMaterial)
                {
                    name = "LethalCCTV_SignalSplice_FontMat"
                };
                // Our clone only — the source material is the ship terminal's own.
                CrtDockLayout.ApplyLowResFaceThickening(_terminalFontMaterial);
            }

            for (int i = 0; i < _textElements.Count; i++)
            {
                TextMeshProUGUI text = _textElements[i];
                if (text == null) continue;
                if (refs.Font != null) text.font = refs.Font;
                if (_terminalFontMaterial != null) text.fontSharedMaterial = _terminalFontMaterial;
            }

            if (_titleText != null) _titleText.color = _accentColor;
            if (_traceText != null) _traceText.color = _accentColor;
            if (_targetText != null) _targetText.color = _accentColor;
            if (_statusText != null) _statusText.color = _accentColor;
            if (_actionText != null) _actionText.color = ScreenAmber;
            if (_logText != null) _logText.color = _accentColor;
            if (_helpText != null) _helpText.color = _dimColor;
            if (_bootText != null) _bootText.color = _accentColor;
            if (_objectiveText != null) _objectiveText.color = _dimColor;

            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][SignalSplice] Read-only terminal style refs: {refs.SourceDescription}.");
        }

        private TextMeshProUGUI CreateTerminalText(Transform parent, string name, string text, float size,
            TextAlignmentOptions alignment, Color color, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = sizeDelta;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.richText = true;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            tmp.characterSpacing = 0f;
            tmp.fontStyle = FontStyles.Bold;
            RegisterText(tmp);
            return tmp;
        }

        private void RegisterText(TextMeshProUGUI text)
        {
            if (text == null) return;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.characterSpacing = 0f;
            if (!_textElements.Contains(text))
                _textElements.Add(text);
        }

        private static RectTransform CreateRect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return rt;
        }

        private static RectTransform CreateLine(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot)
        {
            RectTransform line = CreateRect(parent, name, anchorMin, anchorMax, pivot);
            var image = line.gameObject.AddComponent<Image>();
            image.color = new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.34f);
            image.raycastTarget = false;
            return line;
        }

        private static Texture2D BuildScanlineTexture()
        {
            var tex = new Texture2D(1, 4, TextureFormat.RGBA32, false)
            {
                name = "LethalCCTV_SignalSplice_Scanlines",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            // The dark row was 0.20 alpha. A 4px-tall repeat over a screen that is itself only a
            // few hundred native pixels tall means a scanline can land on the one row of pixels a
            // glyph gets, so the darkening is halved: the CRT banding still reads, but it no
            // longer eats a stroke outright.
            tex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.00f));
            tex.SetPixel(0, 1, new Color(0f, 0.20f, 0.06f, 0.10f));
            tex.SetPixel(0, 2, new Color(0f, 0f, 0f, 0.10f));
            tex.SetPixel(0, 3, new Color(0f, 0.45f, 0.12f, 0.08f));
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return tex;
        }

        /// <summary>Soft-edged white disc used for relay nodes and the pulse, so role reads as shape.</summary>
        private static Texture2D BuildDiscTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "LethalCCTV_SignalSplice_Disc",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            float radius = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - radius;
                    float dy = y + 0.5f - radius;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / radius;
                    float alpha = Mathf.Clamp01((0.97f - d) * radius * 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return tex;
        }

        private static Texture2D BuildVignetteTexture(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "LethalCCTV_SignalSplice_Vignette",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float nx = (x / (float)(width - 1)) * 2f - 1f;
                    float ny = (y / (float)(height - 1)) * 2f - 1f;
                    float d = Mathf.Sqrt(nx * nx * 0.78f + ny * ny * 1.22f);
                    float alpha = Mathf.Clamp01((d - 0.42f) / 0.72f) * 0.58f;
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
                }
            }

            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return tex;
        }

        private static string ResolveTargetDisplayName(Component component)
        {
            if (component == null) return "MAINFRAME";
            if (CctvCommandTargetBridge.TryDescribeTarget(component, out CctvCommandTargetBridge.TargetInfo info)
                && !string.IsNullOrWhiteSpace(info.DisplayName))
            {
                return info.DisplayName;
            }

            return string.IsNullOrWhiteSpace(component.name) ? component.GetType().Name : component.name;
        }

        private static Terminal FindShipTerminal()
        {
            try
            {
                return UnityEngine.Object.FindObjectOfType<Terminal>();
            }
            catch
            {
                return null;
            }
        }

        private static object ReadMember(object target, string memberName)
        {
            if (target == null || string.IsNullOrEmpty(memberName)) return null;
            Type type = target.GetType();
            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                try { return property.GetValue(target); }
                catch { return null; }
            }

            FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return null;
            try { return field.GetValue(target); }
            catch { return null; }
        }

        private sealed class EdgeView
        {
            private readonly RectTransform _rect;
            private readonly Image _image;
            private bool _visible;

            internal EdgeView(Transform parent, int index)
            {
                var go = new GameObject($"Edge_{index:D2}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(parent, false);
                _rect = go.GetComponent<RectTransform>();
                _rect.anchorMin = new Vector2(0.5f, 0.5f);
                _rect.anchorMax = new Vector2(0.5f, 0.5f);
                _rect.pivot = new Vector2(0f, 0.5f);
                _image = go.GetComponent<Image>();
                _image.raycastTarget = false;
                go.SetActive(false);
            }

            internal void Render(Vector2 start, Vector2 end, Color color, float thickness, float alpha)
            {
                _visible = true;
                _rect.gameObject.SetActive(true);
                Vector2 delta = end - start;
                float length = Mathf.Max(1f, delta.magnitude);
                _rect.anchoredPosition = start;
                _rect.sizeDelta = new Vector2(length, thickness);
                _rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
                _image.color = new Color(color.r, color.g, color.b, alpha);
            }

            internal void Hide()
            {
                if (!_visible) return;
                _rect.gameObject.SetActive(false);
                _visible = false;
            }
        }

        private sealed class RouteArrowView
        {
            /// <summary>Share of the sweep spent easing into, and back out of, the idle drift.</summary>
            private const float SweepBlendFraction = 0.2f;

            private readonly RectTransform _rect;
            private readonly TextMeshProUGUI _text;
            private bool _visible;

            internal RouteArrowView(Transform parent, int index, Action<TextMeshProUGUI> registerText)
            {
                var go = new GameObject($"RouteArrow_{index:D2}", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                go.transform.SetParent(parent, false);
                _rect = go.GetComponent<RectTransform>();
                _rect.anchorMin = new Vector2(0.5f, 0.5f);
                _rect.anchorMax = new Vector2(0.5f, 0.5f);
                _rect.pivot = new Vector2(0.5f, 0.5f);
                _rect.sizeDelta = new Vector2(34f, 26f);

                _text = go.GetComponent<TextMeshProUGUI>();
                _text.text = ">";
                _text.alignment = TextAlignmentOptions.Center;
                _text.fontSize = 16f;
                _text.fontStyle = FontStyles.Bold;
                _text.enableWordWrapping = false;
                _text.overflowMode = TextOverflowModes.Overflow;
                _text.raycastTarget = false;
                registerText?.Invoke(_text);

                go.SetActive(false);
            }

            /// <summary>
            /// Draws the flow glyph at the midpoint of the edge with a small repeating drift along
            /// the edge direction, so the direction of travel is legible even while the graph is
            /// static. <paramref name="sweep01"/> (1 falling to 0) replaces the drift with a single
            /// sweep from tail to head, which is the acknowledgement for a rotate.
            /// </summary>
            internal void Render(Vector2 start, Vector2 end, Color color, float alpha, float fontSize,
                int phaseIndex, float sweep01)
            {
                Vector2 delta = end - start;
                float length = delta.magnitude;
                if (length <= 0.1f)
                {
                    Hide();
                    return;
                }

                _visible = true;
                _rect.gameObject.SetActive(true);

                Vector2 direction = delta / length;
                float travel = Mathf.Min(length * 0.30f, 22f);
                float drift = Mathf.Min(length * 0.06f, 5f);
                float driftOffset = Mathf.Sin(Time.unscaledTime * 2.6f + phaseIndex * 0.7f) * drift;
                float offset = driftOffset;
                if (sweep01 > 0f)
                {
                    // sweep01 runs 1 -> 0 over the feedback window, so progress runs 0 -> 1 and the
                    // glyph crosses tail to head once. The sweep is blended in and back out of the
                    // idle drift over the first and last fifth of the window, so the acknowledgement
                    // never begins or ends with the glyph teleporting.
                    float progress = 1f - sweep01;
                    float sweepOffset = Mathf.Lerp(-travel, travel, progress);
                    float blend = Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01(Mathf.Min(progress, 1f - progress) / SweepBlendFraction));
                    offset = Mathf.Lerp(driftOffset, sweepOffset, blend);
                    alpha = Mathf.Lerp(alpha, 1f, blend);
                    fontSize += 3f * blend;
                }

                _rect.anchoredPosition = Vector2.Lerp(start, end, 0.5f) + direction * offset;
                _rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
                _text.fontSize = fontSize;
                _text.color = new Color(color.r, color.g, color.b, alpha);
            }

            internal void Hide()
            {
                if (!_visible) return;
                _rect.gameObject.SetActive(false);
                _visible = false;
            }
        }

        /// <summary>
        /// A node is a root rect that carries the label upright plus a child "Shape" image that
        /// carries the fill, the border and any rotation. Splitting the two is what lets the origin
        /// be a diamond (a 45-degree square) without tipping its label over with it.
        /// </summary>
        private sealed class NodeView
        {
            private const float OriginSize = 30f;
            private const float TargetSize = 30f;
            private const float RelaySize = 27f;
            private const float SelectedGrow = 7f;
            /// <summary>The origin diamond is this fraction of the root square, then turned 45 degrees.</summary>
            private const float OriginShapeScale = 0.74f;
            private const float SelectedBorderWidth = 3.2f;
            /// <summary>Widest outline any state draws: the peak of the return-to-origin beat.</summary>
            private const float MaxBorderWidth = 5.0f;
            /// <summary>Extra scale at the peak of the rotate acknowledgement pop.</summary>
            private const float RotatePopScale = 0.26f;

            /// <summary>
            /// Half the axis extent of the largest node any state can draw, outline included. The
            /// origin is the worst case: a selected diamond is <see cref="OriginShapeScale"/> of the
            /// grown root square turned 45 degrees, so its axis extent is the half-diagonal, and the
            /// rotate pop scales the whole node - outline and all - about its centre.
            /// </summary>
            internal static float MaxEdgeExtent()
            {
                float root = Mathf.Max(OriginSize, Mathf.Max(TargetSize, RelaySize)) + SelectedGrow;
                float diamondHalf = root * OriginShapeScale * 0.5f * Mathf.Sqrt(2f);
                float squareHalf = root * 0.5f;
                return (Mathf.Max(diamondHalf, squareHalf) + MaxBorderWidth) * (1f + RotatePopScale);
            }

            private readonly RectTransform _rect;
            private readonly RectTransform _shapeRect;
            private readonly Image _shapeImage;
            private readonly Outline _outline;
            private readonly TextMeshProUGUI _label;
            private readonly Sprite _discSprite;
            private bool _visible;

            internal NodeView(Transform parent, int index, Action<TextMeshProUGUI> registerText, Sprite discSprite)
            {
                _discSprite = discSprite;

                var go = new GameObject($"Node_{index:D2}", typeof(RectTransform));
                go.transform.SetParent(parent, false);
                _rect = go.GetComponent<RectTransform>();
                _rect.anchorMin = new Vector2(0.5f, 0.5f);
                _rect.anchorMax = new Vector2(0.5f, 0.5f);
                _rect.pivot = new Vector2(0.5f, 0.5f);

                var shapeGo = new GameObject("Shape", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Outline));
                shapeGo.transform.SetParent(go.transform, false);
                _shapeRect = shapeGo.GetComponent<RectTransform>();
                _shapeRect.anchorMin = new Vector2(0.5f, 0.5f);
                _shapeRect.anchorMax = new Vector2(0.5f, 0.5f);
                _shapeRect.pivot = new Vector2(0.5f, 0.5f);
                _shapeImage = shapeGo.GetComponent<Image>();
                _shapeImage.raycastTarget = false;
                _outline = shapeGo.GetComponent<Outline>();
                _outline.effectDistance = new Vector2(1.5f, -1.5f);

                var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                labelGo.transform.SetParent(go.transform, false);
                RectTransform labelRect = labelGo.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
                _label = labelGo.GetComponent<TextMeshProUGUI>();
                _label.fontSize = 13.5f;
                _label.alignment = TextAlignmentOptions.Center;
                _label.raycastTarget = false;
                _label.enableWordWrapping = false;
                _label.richText = false;
                _label.fontStyle = FontStyles.Bold;
                registerText?.Invoke(_label);

                go.SetActive(false);
            }

            internal void Render(Y4NGZCompany.Facility.Security.CctvHackingGame.SignalNode node, Vector2 position, bool selected,
                bool current, bool completed, bool canRotate, float rotatePop, bool originAlert, Color accent, Color dim)
            {
                if (node == null) return;

                _visible = true;
                _rect.gameObject.SetActive(true);
                _rect.anchoredPosition = position;

                float size = node.IsOrigin ? OriginSize : node.IsTarget ? TargetSize : RelaySize;
                if (selected) size += SelectedGrow;
                _rect.sizeDelta = new Vector2(size, size);

                // Rotate acknowledgement is a scale pop, never a recolour.
                float pop = 1f + RotatePopScale * Mathf.Clamp01(rotatePop);
                _rect.localScale = new Vector3(pop, pop, 1f);

                // Shape carries the role. Origin is a diamond, targets are hard squares with a
                // heavy border, relays are discs.
                if (node.IsOrigin)
                {
                    _shapeImage.sprite = null;
                    _shapeRect.sizeDelta = new Vector2(size * OriginShapeScale, size * OriginShapeScale);
                    _shapeRect.localRotation = Quaternion.Euler(0f, 0f, 45f);
                }
                else if (node.IsTarget)
                {
                    _shapeImage.sprite = null;
                    _shapeRect.sizeDelta = new Vector2(size, size);
                    _shapeRect.localRotation = Quaternion.identity;
                }
                else
                {
                    _shapeImage.sprite = _discSprite;
                    _shapeRect.sizeDelta = new Vector2(size, size);
                    _shapeRect.localRotation = Quaternion.identity;
                }

                Color fill;
                Color ink;
                Color border;
                float borderWidth = 1.8f;
                // Negative means "no state-specific outline alpha, use the shape default below".
                float authoredBorderAlpha = -1f;
                string label;

                if (node.IsOrigin)
                {
                    // White-green only while the signal is actually here; otherwise infrastructure.
                    fill = current ? ScreenWhiteGreen : accent;
                    ink = ScreenInk;
                    border = current ? ScreenWhiteGreen : accent;
                    label = "O";
                }
                else if (node.IsTarget && completed)
                {
                    // Hollow, dim, and acknowledged: nothing left to do here.
                    fill = new Color(ScreenInk.r, ScreenInk.g, ScreenInk.b, 0.88f);
                    ink = accent;
                    border = dim;
                    borderWidth = 2.4f;
                    label = "OK";
                }
                else if (node.IsTarget)
                {
                    // Cyan is reserved for exactly this: a target still owed a visit.
                    fill = ScreenCyan;
                    ink = ScreenInk;
                    border = ScreenCyan;
                    borderWidth = 3.0f;
                    label = "T" + node.TargetSlot;
                }
                else if (current)
                {
                    fill = ScreenWhiteGreen;
                    ink = ScreenInk;
                    border = ScreenWhiteGreen;
                    label = "R" + node.Id.ToString("00");
                }
                else
                {
                    // Idle relays are dark green plates with bright green type, so the label is
                    // readable and the bright fills stay meaningful.
                    fill = ScreenGreenFaint;
                    ink = canRotate ? accent : dim;
                    border = canRotate ? accent : dim;
                    // The outline alpha is the "you can aim this one" tell, so it is carried
                    // through to the Outline colour rather than being overwritten by the default.
                    authoredBorderAlpha = canRotate ? 0.75f : 0.55f;
                    label = "R" + node.Id.ToString("00");
                }

                if (selected)
                {
                    // Amber means "this is what you are holding" and nothing else.
                    fill = ScreenAmber;
                    ink = ScreenInk;
                    border = ScreenAmber;
                    borderWidth = SelectedBorderWidth;
                    authoredBorderAlpha = -1f;
                }

                float borderAlpha = authoredBorderAlpha >= 0f
                    ? authoredBorderAlpha
                    : selected ? 1f : node.IsTarget ? 0.95f : 0.8f;

                if (originAlert)
                {
                    // "Return to origin" is a pulsing outline on the origin, so the pulse itself
                    // never has to change colour. It keeps beating while the origin is selected:
                    // the amber fill still says "you are holding this", the white-green outline
                    // still says "the signal belongs here", so neither colour loses its meaning.
                    float beat = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6.5f);
                    border = ScreenWhiteGreen;
                    borderAlpha = 1f;
                    borderWidth = Mathf.Lerp(2.2f, MaxBorderWidth, beat);
                }

                _shapeImage.color = fill;
                _outline.effectDistance = new Vector2(borderWidth, -borderWidth);
                _outline.effectColor = new Color(border.r, border.g, border.b, borderAlpha);
                _label.text = label;
                _label.color = ink;
            }

            internal void Hide()
            {
                if (!_visible) return;
                _rect.gameObject.SetActive(false);
                _visible = false;
            }
        }

        private readonly struct TerminalStyleRefs
        {
            internal readonly TMP_FontAsset Font;
            internal readonly Material FontMaterial;
            internal readonly Color Accent;
            internal readonly Color Dim;
            internal readonly string SourceDescription;

            private TerminalStyleRefs(TMP_FontAsset font, Material fontMaterial, Color accent, Color dim, string sourceDescription)
            {
                Font = font;
                FontMaterial = fontMaterial;
                Accent = accent;
                Dim = dim;
                SourceDescription = sourceDescription;
            }

            internal static TerminalStyleRefs Resolve()
            {
                TMP_Text sourceText = null;
                Terminal terminal = FindShipTerminal();
                if (terminal != null)
                {
                    sourceText =
                        ReadMember(terminal, "inputFieldText") as TMP_Text ??
                        ReadMember(terminal, "topRightText") as TMP_Text;

                    if (sourceText == null && ReadMember(terminal, "screenText") is TMP_InputField inputField)
                        sourceText = inputField.textComponent;
                }

                if (sourceText == null)
                {
                    try
                    {
                        HUDManager hud = HUDManager.Instance;
                        if (hud != null && hud.controlTipLines != null && hud.controlTipLines.Length > 0)
                            sourceText = hud.controlTipLines[0];
                    }
                    catch
                    {
                        sourceText = null;
                    }
                }

                if (sourceText == null)
                {
                    try
                    {
                        TMP_FontAsset defaultFont = TMP_Settings.defaultFontAsset;
                        if (defaultFont != null)
                        {
                            return new TerminalStyleRefs(defaultFont, null, ScreenGreen, ScreenGreenDim,
                                $"fallback TMP_Settings.defaultFontAsset='{defaultFont.name}'");
                        }
                    }
                    catch
                    {
                    }

                    return new TerminalStyleRefs(null, null, ScreenGreen, ScreenGreenDim, "fallback built-in TMP style");
                }

                Color accent = ChooseGreenTerminalColor(sourceText.color);
                Color dim = new Color(accent.r * 0.78f, accent.g * 0.78f, accent.b * 0.78f, 1f);
                string sourceName = sourceText.name ?? sourceText.GetType().Name;
                string fontName = sourceText.font != null ? sourceText.font.name : "<none>";
                string materialName = sourceText.fontSharedMaterial != null ? sourceText.fontSharedMaterial.name : "<none>";
                return new TerminalStyleRefs(sourceText.font, sourceText.fontSharedMaterial, accent, dim,
                    $"source='{sourceName}' font='{fontName}' material='{materialName}' terminalFound={terminal != null}");
            }

            private static Color ChooseGreenTerminalColor(Color sampled)
            {
                if (sampled.g >= sampled.r && sampled.g >= sampled.b && sampled.g > 0.20f)
                    return new Color(Mathf.Max(sampled.r, 0.08f), Mathf.Max(sampled.g, 0.82f), Mathf.Max(sampled.b, 0.12f), 1f);
                return ScreenGreen;
            }
        }

        private readonly struct TerminalStateSnapshot
        {
            private readonly bool _exists;
            private readonly int _instanceId;
            private readonly string _name;
            private readonly string _currentNode;
            private readonly int _groupCredits;
            private readonly bool _terminalInUse;
            private readonly int _screenHash;
            private readonly int _inputHash;
            private readonly int _topRightHash;

            private TerminalStateSnapshot(bool exists, int instanceId, string name, string currentNode, int groupCredits,
                bool terminalInUse, int screenHash, int inputHash, int topRightHash)
            {
                _exists = exists;
                _instanceId = instanceId;
                _name = name;
                _currentNode = currentNode;
                _groupCredits = groupCredits;
                _terminalInUse = terminalInUse;
                _screenHash = screenHash;
                _inputHash = inputHash;
                _topRightHash = topRightHash;
            }

            internal static TerminalStateSnapshot Capture()
            {
                Terminal terminal = FindShipTerminal();
                if (terminal == null)
                    return new TerminalStateSnapshot(false, 0, "<none>", "<none>", 0, false, 0, 0, 0);

                object screen = ReadMember(terminal, "screenText");
                object input = ReadMember(terminal, "inputFieldText");
                object topRight = ReadMember(terminal, "topRightText");
                object currentNode = ReadMember(terminal, "currentNode");

                return new TerminalStateSnapshot(
                    true,
                    terminal.GetInstanceID(),
                    terminal.name ?? "<unnamed>",
                    DescribeObject(currentNode),
                    ReadInt(ReadMember(terminal, "groupCredits")),
                    ReadBool(ReadMember(terminal, "terminalInUse")),
                    StableHash(ReadText(screen)),
                    StableHash(ReadText(input)),
                    StableHash(ReadText(topRight)));
            }

            internal bool HasChangedFrom(TerminalStateSnapshot other)
            {
                return _exists != other._exists
                       || _instanceId != other._instanceId
                       || !string.Equals(_name, other._name, StringComparison.Ordinal)
                       || !string.Equals(_currentNode, other._currentNode, StringComparison.Ordinal)
                       || _groupCredits != other._groupCredits
                       || _terminalInUse != other._terminalInUse
                       || _screenHash != other._screenHash
                       || _inputHash != other._inputHash
                       || _topRightHash != other._topRightHash;
            }

            internal string Describe()
            {
                if (!_exists) return "{terminal=<none>}";
                return "{terminal='" + _name + "' id=" + _instanceId +
                       " node='" + _currentNode + "' credits=" + _groupCredits +
                       " inUse=" + _terminalInUse +
                       " textHashes=" + _screenHash + "/" + _inputHash + "/" + _topRightHash + "}";
            }

            private static string ReadText(object target)
            {
                if (target == null) return string.Empty;
                if (target is TMP_InputField inputField) return inputField.text ?? string.Empty;
                if (target is TMP_Text text) return text.text ?? string.Empty;
                object textValue = ReadMember(target, "text");
                return textValue as string ?? string.Empty;
            }

            private static int ReadInt(object value)
            {
                if (value is int i) return i;
                if (value is short s) return s;
                if (value is long l) return unchecked((int)l);
                return 0;
            }

            private static bool ReadBool(object value)
            {
                return value is bool b && b;
            }

            private static string DescribeObject(object target)
            {
                if (target == null) return "<none>";
                if (target is UnityEngine.Object unityObject)
                    return (unityObject.name ?? unityObject.GetType().Name) + "#" + unityObject.GetInstanceID();
                return target.GetType().Name + "#" + target.GetHashCode();
            }

            private static int StableHash(string text)
            {
                unchecked
                {
                    int hash = 17;
                    if (text != null)
                    {
                        for (int i = 0; i < text.Length; i++)
                            hash = hash * 31 + text[i];
                    }
                    return hash;
                }
            }
        }
    }
}

