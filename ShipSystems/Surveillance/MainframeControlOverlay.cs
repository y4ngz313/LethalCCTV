using System;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed class MainframeControlOverlay
    {
        private enum View
        {
            Menu,
            Storage,
            StashCodes,
            ScrapCheck,
            SystemChecks,
            CctvSecurity,
            ProtocolLockdown,
            Intercom
        }

        // Same stretch model as the splice overlay, shared through CrtDockLayout: fixed design
        // height, runtime design width, and every column expressed as a fraction of the content
        // span so any dock aspect fills.
        private const float DesignHeight = CrtDockLayout.DesignHeight;
        private const float ContentInset = CrtDockLayout.ContentInset;
        private const float StatusColumnFraction = 0.533f;
        private const float MenuColumnFraction = 0.47f;
        private const float ColumnGap = 8f;
        // ASCII only: the label rides the ship terminal's font asset, whose glyph table is not ours.
        private const string IdleTransmitLabel = "[ TRANSMIT - HOLD ]";
        private const string HeldTransmitLabel = "[ TRANSMITTING ]";

        private static readonly Color ScreenBlack = new Color(0.003f, 0.012f, 0.006f, 0.98f);
        private static readonly Color ScreenGreen = new Color(0.16f, 1f, 0.34f, 1f);
        // Brightened from 0.10/0.56/0.23: the dim green carries the descriptive column and the
        // unselected menu rows, and at the native internal resolution a half-lit thin stroke on
        // near-black is the first thing to disappear.
        private static readonly Color ScreenGreenDim = new Color(0.16f, 0.78f, 0.34f, 1f);
        private static readonly Color ScreenAmber = new Color(0.92f, 0.82f, 0.30f, 1f);
        private static readonly Color ScreenRed = new Color(1f, 0.28f, 0.20f, 1f);
        // Held-state wash behind the transmit button: bright enough to read as lit glass at the
        // game's native internal resolution, dim enough that the amber label stays legible on it.
        private static readonly Color ScreenAmberFill = new Color(0.36f, 0.30f, 0.06f, 1f);

        private Transform _parent;
        private readonly System.Collections.Generic.List<TextMeshProUGUI> _textElements = new System.Collections.Generic.List<TextMeshProUGUI>(16);

        private GameObject _root;
        private RectTransform _rootRect;
        private RectTransform _frameTopRect;
        private RectTransform _frameBottomRect;
        private RectTransform _frameLeftRect;
        private RectTransform _frameRightRect;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _menuText;
        private TextMeshProUGUI _bodyText;
        private TextMeshProUGUI _statusText;
        private TextMeshProUGUI _helpText;
        private RectTransform _intercomButtonRect;
        private Image _intercomButtonBorder;
        private Image _intercomButtonFill;
        private TextMeshProUGUI _intercomButtonLabel;
        private MainframeIntercomPushButton _intercomButtonInput;
        private Texture2D _scanlineTexture;
        private Sprite _scanlineSprite;
        private Material _terminalFontMaterial;
        private Color _accentColor = ScreenGreen;
        private Color _dimColor = ScreenGreenDim;
        private float _designWidth = CrtDockLayout.DefaultDesignWidth;

        private Component _mainframeComponent;
        private Y4NGZCompany.Facility.Mainframe.MainframeSupport _mainframe;
        private View _view = View.Menu;
        private int _selectedIndex;
        private bool _isOpen;
        // Two independent hold sources feed one transmit state: the rebindable key (either host's
        // poller/callbacks) and a pointer hold on the transmit button. They must not cancel each
        // other, so _intercomSpeaking is always their OR, recomputed rather than assigned.
        private bool _pttKeyHeld;
        private bool _pttPointerHeld;
        private bool _intercomSpeaking;
        private int _openedFrame;
        private float _inputArmedAt;
        private string _terminalBeforeOpen;

        internal bool IsOpen => _isOpen;

        /// <summary>Opaque handle on the current view, so callers can tell whether an input
        /// actually changed the page (screen transition cue) or acted in place (select cue).</summary>
        internal int ViewToken => (int)_view;
        internal bool IsIntercomActive => _isOpen && _view == View.Intercom;
        internal GameObject RootObject
        {
            get
            {
                EnsureBuilt();
                return _root;
            }
        }

        internal MainframeControlOverlay(Transform parent)
        {
            _parent = parent;
        }

        internal void EnsureBuilt()
        {
            if (_root != null) return;

            _root = new GameObject("LethalCCTV_MainframeControlTerminal");
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
            backdropImage.color = new Color(0f, 0f, 0f, 0.64f);
            backdropImage.raycastTarget = false;

            var panel = CreateRect(_root.transform, "TerminalPanel", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = ScreenBlack;
            panelImage.raycastTarget = false;

            CreateFrame(panel);

            // ApplyLayout owns every size here and the positions of the right-hand columns; the
            // zeros keep creation from advertising a second, stale set of numbers.
            // Every point size on this panel was raised for the game's native internal resolution
            // (roughly 860x520 upscaled), where the old sizes put a capital letter on about four
            // pixels. The design height is fixed, so a larger size is paid for in density: the
            // title lost its legal boilerplate and the body strings below were shortened to match.
            _titleText = CreateText(panel, "Title",
                "Y4NGZ MAINFRAME OS\nACCESS GRANTED",
                19f, TextAlignmentOptions.TopLeft, ScreenGreen, new Vector2(ContentInset, -14f), Vector2.zero);

            _statusText = CreateText(panel, "Status", "SELECT FUNCTION",
                16f, TextAlignmentOptions.TopRight, ScreenGreen, Vector2.zero, Vector2.zero);

            _menuText = CreateText(panel, "Menu", "",
                18f, TextAlignmentOptions.TopLeft, ScreenGreen, new Vector2(ContentInset, -96f), Vector2.zero);
            _menuText.lineSpacing = 7f;

            _bodyText = CreateText(panel, "Body", "",
                15.5f, TextAlignmentOptions.TopLeft, ScreenGreenDim, Vector2.zero, Vector2.zero);
            _bodyText.lineSpacing = 5f;
            // The body column narrows on squarer docks, so its prose wraps instead of running
            // off the panel edge the way the fixed-width layout used to allow.
            _bodyText.enableWordWrapping = true;

            _helpText = CreateText(panel, "Help",
                "ARROWS  ENTER  ESC",
                12.5f, TextAlignmentOptions.Center, ScreenGreenDim, new Vector2(ContentInset, -331f), Vector2.zero);

            CreateIntercomButton(panel);

            CreateScanlines(_root.transform);
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

            if (_titleText != null)
                _titleText.rectTransform.sizeDelta = new Vector2(contentWidth, 52f);

            if (_statusText != null)
            {
                float statusX = contentLeft + contentWidth * StatusColumnFraction;
                _statusText.rectTransform.anchoredPosition = new Vector2(statusX, -62f);
                _statusText.rectTransform.sizeDelta = new Vector2(contentRight - statusX, 26f);
            }

            float menuWidth = contentWidth * MenuColumnFraction;
            if (_menuText != null)
                _menuText.rectTransform.sizeDelta = new Vector2(menuWidth, 214f);

            if (_bodyText != null)
            {
                float bodyX = contentLeft + menuWidth + ColumnGap;
                _bodyText.rectTransform.anchoredPosition = new Vector2(bodyX, -96f);
                _bodyText.rectTransform.sizeDelta = new Vector2(Mathf.Max(120f, contentRight - bodyX), 214f);
            }

            if (_helpText != null)
                _helpText.rectTransform.sizeDelta = new Vector2(contentWidth, 17f);

            // The transmit button lives in the menu column, which in the INTERCOM view carries only
            // the one-line BACK hint, so it never collides with a menu list.
            if (_intercomButtonRect != null)
            {
                _intercomButtonRect.anchoredPosition = new Vector2(contentLeft, -134f);
                _intercomButtonRect.sizeDelta = new Vector2(menuWidth, 42f);
            }
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

            _terminalBeforeOpen = DescribeShipTerminal();
            ApplyTerminalStyle();

            _mainframeComponent = mainframeComponent;
            _mainframe = mainframeComponent as Y4NGZCompany.Facility.Mainframe.MainframeSupport
                         ?? mainframeComponent.GetComponent<Y4NGZCompany.Facility.Mainframe.MainframeSupport>()
                         ?? Y4NGZCompany.Facility.Mainframe.MainframeSupport.Active;
            _view = View.Menu;
            _selectedIndex = 0;
            // Through the setter, never by assignment: a re-Open while a previous session left the
            // bridge transmitting has to actually release it, not just forget about it locally.
            ClearPushToTalkSources();
            _openedFrame = Time.frameCount;
            _inputArmedAt = Time.unscaledTime + 0.18f;
            _isOpen = true;

            _root.SetActive(true);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            SetStatus("SELECT FUNCTION", _accentColor);
            Refresh();
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][MainframeControl] OPENED " +
                $"component='{mainframeComponent.GetType().FullName}' instance={mainframeComponent.GetInstanceID()} " +
                $"shipTerminalBefore={_terminalBeforeOpen}.");
        }

        internal void Close()
        {
            Close("unspecified");
        }

        internal void Close(string reason)
        {
            if (!_isOpen)
            {
                if (_root != null) _root.SetActive(false);
                return;
            }

            ClearPushToTalkSources();
            if (_root != null) _root.SetActive(false);
            string after = DescribeShipTerminal();
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][MainframeControl] CLOSED reason='{reason ?? "unspecified"}' " +
                $"shipTerminalUnchanged={string.Equals(after, _terminalBeforeOpen, StringComparison.Ordinal)} terminalNow={after}.");

            _isOpen = false;
            _mainframeComponent = null;
            _mainframe = null;
            _view = View.Menu;
            _selectedIndex = 0;
            RefreshIntercomButton();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        internal void Tick()
        {
            if (!_isOpen) return;
            if (_view == View.Menu || _view == View.SystemChecks)
                RefreshMenu();
        }

        internal bool ConsumeEscapeIfOpen()
        {
            if (!_isOpen) return false;
            if (_view == View.Menu)
            {
                Close("escape");
                return true;
            }

            BackOneLevel();
            Refresh();
            return true;
        }

        internal bool NavigateUp(string source) => Move(-1, source);
        internal bool NavigateDown(string source) => Move(1, source);
        internal bool NavigateLeft(string source) => false;
        internal bool NavigateRight(string source) => false;

        internal bool Submit(string source)
        {
            if (!IsInputArmed()) return false;
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeControl] ACTIVATE view={_view} item={_selectedIndex} source='{source}'.");

            switch (_view)
            {
                case View.Menu:
                    return ActivateRoot();
                case View.Storage:
                    return ActivateStorage();
                case View.SystemChecks:
                    return ActivateSystemChecks();
                case View.ProtocolLockdown:
                    return ActivateProtocol("LockdownDrill", "LOCKDOWN DRILL");
                case View.StashCodes:
                case View.ScrapCheck:
                case View.CctvSecurity:
                case View.Intercom:
                    BackOneLevel();
                    Refresh();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Key-driven push-to-talk hold (station focus callbacks, mainframe screen poller).
        /// Independent of the pointer hold: releasing one source must not cut the other short.</summary>
        internal void SetPushToTalk(bool held)
        {
            if (_pttKeyHeld == held) return;
            _pttKeyHeld = held;
            RecomputeIntercomSpeaking();
        }

        /// <summary>Pointer-driven push-to-talk hold from the INTERCOM transmit button.</summary>
        internal void SetPointerPushToTalk(bool held)
        {
            if (_pttPointerHeld == held) return;
            _pttPointerHeld = held;
            RecomputeIntercomSpeaking();
        }

        /// <summary>Drops every hold source. Every path that leaves the INTERCOM view or the
        /// overlay entirely routes through here, so neither source can survive the transition.</summary>
        private void ClearPushToTalkSources()
        {
            _pttKeyHeld = false;
            _pttPointerHeld = false;
            RecomputeIntercomSpeaking();
        }

        private void RecomputeIntercomSpeaking()
        {
            SetIntercomSpeaking(IsIntercomActive && (_pttKeyHeld || _pttPointerHeld));
        }

        private bool ActivateRoot()
        {
            switch (_selectedIndex)
            {
                case 0: EnterView(View.Storage, "STORAGE"); return true;
                case 1: EnterView(View.SystemChecks, "SYSTEM CHECKS"); return true;
                case 2: EnterView(View.Intercom, "INTERCOM"); return true;
                case 3: Close("exit-item"); return true;
                default: return false;
            }
        }

        private bool ActivateStorage()
        {
            switch (_selectedIndex)
            {
                case 0: EnterView(View.StashCodes, "STASH CODES"); return true;
                case 1: EnterView(View.ScrapCheck, "SCRAP CHECK"); return true;
                case 2: EnterView(View.Menu, "SELECT FUNCTION"); return true;
                default: return false;
            }
        }

        private bool ActivateSystemChecks()
        {
            switch (_selectedIndex)
            {
                case 0: ToggleAlarm(); Refresh(); return true;
                case 1: EnterView(View.CctvSecurity, "CCTV SECURITY"); return true;
                case 2: EnterView(View.ProtocolLockdown, "LOCKDOWN"); return true;
                case 3: EnterView(View.Menu, "SELECT FUNCTION"); return true;
                default: return false;
            }
        }

        private bool ActivateProtocol(string eventName, string displayName)
        {
            if (_selectedIndex == 2)
            {
                EnterView(View.SystemChecks, "SYSTEM CHECKS");
                return true;
            }

            string method = _selectedIndex == 0 ? "BeginManualEvent" : "EndManualEvent";
            bool invoked = InvokeMainframeProtocol(method, eventName);
            SetStatus((invoked ? (_selectedIndex == 0 ? "BEGIN " : "END ") : "OFFLINE ") + displayName, invoked ? ScreenAmber : ScreenRed);
            RefreshBody();
            return true;
        }

        private void EnterView(View view, string status)
        {
            ClearPushToTalkSources();
            _view = view;
            _selectedIndex = 0;
            SetStatus(status, _accentColor);
            Refresh();
        }

        private void BackOneLevel()
        {
            ClearPushToTalkSources();
            switch (_view)
            {
                case View.StashCodes:
                case View.ScrapCheck:
                    _view = View.Storage;
                    break;
                case View.CctvSecurity:
                case View.ProtocolLockdown:
                    _view = View.SystemChecks;
                    break;
                default:
                    _view = View.Menu;
                    break;
            }
            _selectedIndex = 0;
            SetStatus(_view == View.Menu ? "SELECT FUNCTION" : HeaderForView(_view), _accentColor);
        }

        private bool Move(int delta, string source)
        {
            if (!IsInputArmed()) return false;
            int count = CurrentItemCount();
            if (count <= 0) return false;
            _selectedIndex = ((_selectedIndex + delta) % count + count) % count;
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeControl] SELECT view={_view} item={_selectedIndex} source='{source}'.");
            Refresh();
            return true;
        }

        private int CurrentItemCount()
        {
            switch (_view)
            {
                case View.Menu: return 4;
                case View.Storage: return 3;
                case View.SystemChecks: return 4;
                case View.ProtocolLockdown: return 3;
                default: return 0;
            }
        }

        private void ToggleAlarm()
        {
            bool current = IsAlarmOn();
            bool target = !current;
            bool invoked = false;
            try
            {
                if (_mainframe != null)
                {
                    _mainframe.SetAlarmServerRpc(target);
                    invoked = true;
                }
                else if (_mainframeComponent != null)
                {
                    MethodInfo method = _mainframeComponent.GetType().GetMethod("SetAlarmServerRpc", BindingFlags.Public | BindingFlags.Instance);
                    if (method != null)
                    {
                        method.Invoke(_mainframeComponent, new object[] { target, default(Unity.Netcode.ServerRpcParams) });
                        invoked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeControl] Alarm toggle RPC failed: {ex.Message}");
            }

            SetStatus(target ? "ALARM ENGAGED" : "ALARM SILENCED", target ? ScreenAmber : _accentColor);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeControl] ALARM_TOGGLE target={target} invoked={invoked}.");
        }

        private bool IsAlarmOn()
        {
            if (_mainframe != null) return _mainframe.IsAlarmOn;
            return Y4NGZCompany.Facility.Security.CctvSupportApi.IsAlarmActive;
        }

        private static bool InvokeMainframeProtocol(string methodName, string eventName)
        {
            try
            {
                Type directorType = FindLoadedType("Y4NGZCompany.Facility.Security.MainframeProtocolDirector");
                Type eventType = FindLoadedType("Y4NGZCompany.Facility.Security.MainframeProtocolEvent");
                if (directorType == null || eventType == null) return false;
                object evt = Enum.Parse(eventType, eventName);
                MethodInfo method = directorType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) return false;
                object result = method.Invoke(null, new[] { evt });
                return result is bool b && b;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeControl] Protocol reflection failed event='{eventName}' method='{methodName}': {ex.Message}");
                return false;
            }
        }

        private static Type FindLoadedType(string fullName)
        {
            Type direct = Type.GetType(fullName, throwOnError: false);
            if (direct != null) return direct;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type match = assemblies[i].GetType(fullName, throwOnError: false);
                if (match != null) return match;
            }
            return null;
        }

        private void SetIntercomSpeaking(bool speaking)
        {
            if (_intercomSpeaking == speaking) return;
            _intercomSpeaking = speaking;
            CCTVWalkieTalkieBridge.SetLocalSpeaking(speaking);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV][MainframeControl] INTERCOM speaking={speaking}.");
            RefreshIntercomButton();
            if (_view == View.Intercom) RefreshBody();
        }

        private void Refresh()
        {
            RefreshMenu();
            RefreshBody();
            RefreshHelp();
            RefreshIntercomButton();
        }

        private void RefreshMenu()
        {
            if (_menuText == null) return;
            var sb = new StringBuilder();
            switch (_view)
            {
                case View.Menu:
                    AppendMenuLine(sb, 0, "Storage");
                    AppendMenuLine(sb, 1, "System Checks");
                    AppendMenuLine(sb, 2, "Intercom");
                    AppendMenuLine(sb, 3, "EXIT");
                    break;
                case View.Storage:
                    AppendMenuLine(sb, 0, "Stash Codes");
                    AppendMenuLine(sb, 1, "Scrap Check");
                    AppendMenuLine(sb, 2, "BACK");
                    break;
                case View.SystemChecks:
                    AppendMenuLine(sb, 0, IsAlarmOn() ? "ALARM: ON" : "ALARM: OFF");
                    AppendMenuLine(sb, 1, "CCTV SECURITY");
                    AppendMenuLine(sb, 2, "Lockdown");
                    AppendMenuLine(sb, 3, "BACK");
                    break;
                case View.ProtocolLockdown:
                    AppendProtocolMenu(sb, "Lockdown Drill");
                    break;
                default:
                    sb.Append("<color=#").Append(ColorHex(_dimColor)).Append(">ENTER/ESC BACK</color>");
                    break;
            }
            _menuText.text = sb.ToString();
        }

        private void AppendProtocolMenu(StringBuilder sb, string label)
        {
            AppendMenuLine(sb, 0, "Begin " + label);
            AppendMenuLine(sb, 1, "End " + label);
            AppendMenuLine(sb, 2, "BACK");
        }

        private void AppendMenuLine(StringBuilder sb, int index, string label)
        {
            bool selected = CurrentItemCount() > 0 && index == _selectedIndex;
            string marker = selected ? "> " : "  ";
            string color = selected ? ColorHex(_accentColor) : ColorHex(_dimColor);
            sb.Append("<color=#").Append(color).Append('>').Append(marker).Append(label).Append("</color>\n");
        }

        private void RefreshBody()
        {
            if (_bodyText == null) return;
            switch (_view)
            {
                case View.Menu:
                    _bodyText.text = BuildRootText();
                    break;
                case View.Storage:
                    _bodyText.text = BuildStorageText();
                    break;
                case View.StashCodes:
                    _bodyText.text = BuildStashCodesText();
                    break;
                case View.ScrapCheck:
                    _bodyText.text = "<color=#" + ColorHex(_accentColor) + ">SCRAP CHECK</color>\n\n" +
                                     "TOTAL VALUE: OFFLINE\n" +
                                     "ITEM COUNT: OFFLINE\n\n" +
                                     "<color=#" + ColorHex(_dimColor) + ">INVENTORY INDEX\nNOT MOUNTED.</color>";
                    break;
                case View.SystemChecks:
                    _bodyText.text = BuildSystemChecksText();
                    break;
                case View.CctvSecurity:
                    _bodyText.text = BuildSecurityText();
                    break;
                case View.ProtocolLockdown:
                    _bodyText.text = BuildProtocolText("LOCKDOWN DRILL", "Cycles containment gates\nfor a drill window.");
                    break;
                case View.Intercom:
                    _bodyText.text = BuildIntercomText();
                    break;
            }
        }

        // The body column is roughly half the panel and now carries 15.5pt type, so these strings
        // are deliberately terse. A wrapped explanatory sentence is unreadable at native internal
        // resolution anyway; a short labelled line is not.
        private string BuildRootText()
        {
            return "<color=#" + ColorHex(_dimColor) + ">MAINFRAME ONLINE\n\n" +
                   "Storage   stash records\n" +
                   "Checks    alarm + security\n" +
                   "Intercom  broadcast\n" +
                   "EXIT      leave OS</color>";
        }

        private string BuildStorageText()
        {
            return "<color=#" + ColorHex(_accentColor) + ">STORAGE</color>\n\n" +
                   "Stash Codes  company codes\n" +
                   "Scrap Check  offline\n\n" +
                   "<color=#" + ColorHex(_dimColor) + ">ENTER SELECT   ESC BACK</color>";
        }

        private string BuildSystemChecksText()
        {
            return "<color=#" + ColorHex(_accentColor) + ">SYSTEM CHECKS</color>\n\n" +
                   "ALARM: " + (IsAlarmOn() ? "ON" : "OFF") + "\n" +
                   "CCTV SECURITY console\n" +
                   "Lockdown drill\n\n" +
                   "<color=#" + ColorHex(_dimColor) + ">ENTER SELECT   ESC BACK</color>";
        }

        private string BuildProtocolText(string title, string description)
        {
            return "<color=#" + ColorHex(_accentColor) + ">" + title + "</color>\n\n" +
                   description + "\n\n" +
                   "<color=#" + ColorHex(_dimColor) + ">ENTER SELECT   ESC BACK</color>";
        }

        private string BuildStashCodesText()
        {
            var codes = Y4NGZCompany.Facility.Security.CctvSupportApi.MainframeControlStashCodes;
            var sb = new StringBuilder("<color=#" + ColorHex(_accentColor) + ">STASH CODES</color>\n\n");
            if (codes == null || codes.Count == 0)
            {
                sb.Append("<color=#").Append(ColorHex(_dimColor)).Append(">NO CODES ON FILE.</color>");
            }
            else
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    sb.Append("<color=#").Append(ColorHex(_accentColor)).Append(">  ")
                      .Append(Y4NGZCompany.Facility.Security.CctvSupportApi.FormatStashCode(codes[i]))
                      .Append("</color>\n");
                }
            }
            sb.Append("\n<color=#").Append(ColorHex(_dimColor)).Append(">ENTER/ESC BACK</color>");
            return sb.ToString();
        }

        private string BuildSecurityText()
        {
            string hacked = Y4NGZCompany.Facility.Security.CctvSupportApi.IsMainframeHacked ? "OFFLINE" : "ARMED";
            string alarm = Y4NGZCompany.Facility.Security.CctvSupportApi.IsTimedSecurityAlarmActive
                ? "ALARM " + Y4NGZCompany.Facility.Security.CctvSupportApi.SecurityAlarmRemaining.ToString("0") + "s"
                : "NO ALARM";
            return "<color=#" + ColorHex(_accentColor) + ">CCTV SECURITY</color>\n\n" +
                   "CAMERAS: " + hacked + "\n" +
                   "STATUS: " + alarm + "\n\n" +
                   "<color=#" + ColorHex(_dimColor) + ">HACK DISABLES CAMERA\nDETECTION AND EVENTS.</color>";
        }

        private string BuildIntercomText()
        {
            string state = _intercomSpeaking
                ? "<color=#" + ColorHex(ScreenAmber) + ">>> TRANSMITTING <<<</color>"
                : "<color=#" + ColorHex(_dimColor) + ">IDLE</color>";
            return "<color=#" + ColorHex(_accentColor) + ">INTERCOM</color>\n\n" +
                   "HOLD PUSH-TO-TALK TO\nBROADCAST FACILITY-WIDE.\n\n" +
                   "STATUS: " + state + "\n\n" +
                   "<color=#" + ColorHex(_dimColor) + ">ENTER/ESC BACK</color>";
        }

        private void RefreshHelp()
        {
            if (_helpText == null) return;
            _helpText.text = _view == View.Menu
                ? "ARROWS MOVE   ENTER OK   ESC EXIT"
                : "ARROWS MOVE   ENTER OK   ESC BACK";
        }

        private static string HeaderForView(View view)
        {
            switch (view)
            {
                case View.Storage: return "STORAGE";
                case View.SystemChecks: return "SYSTEM CHECKS";
                case View.Intercom: return "INTERCOM";
                default: return "SELECT FUNCTION";
            }
        }

        private void SetStatus(string text, Color color)
        {
            if (_statusText == null) return;
            _statusText.text = text ?? string.Empty;
            _statusText.color = color;
        }

        private bool IsInputArmed()
        {
            return Time.frameCount > _openedFrame && Time.unscaledTime >= _inputArmedAt;
        }

        private void ApplyTerminalStyle()
        {
            TMP_FontAsset font = null;
            Material material = null;
            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                TMP_Text source = terminal != null ? ReadTmp(terminal, "topRightText") ?? ReadTmp(terminal, "inputFieldText") : null;
                if (source == null && HUDManager.Instance?.controlTipLines != null && HUDManager.Instance.controlTipLines.Length > 0)
                    source = HUDManager.Instance.controlTipLines[0];
                if (source != null)
                {
                    font = source.font;
                    material = source.fontSharedMaterial;
                }
                else
                {
                    font = TMP_Settings.defaultFontAsset;
                }
            }
            catch
            {
            }

            if (material != null)
            {
                if (_terminalFontMaterial != null) UnityEngine.Object.Destroy(_terminalFontMaterial);
                _terminalFontMaterial = new Material(material) { name = "LethalCCTV_MainframeControl_FontMat" };
                // Our clone only — the source material is the ship terminal's own.
                CrtDockLayout.ApplyLowResFaceThickening(_terminalFontMaterial);
            }

            for (int i = 0; i < _textElements.Count; i++)
            {
                TextMeshProUGUI text = _textElements[i];
                if (text == null) continue;
                if (font != null) text.font = font;
                if (_terminalFontMaterial != null) text.fontSharedMaterial = _terminalFontMaterial;
            }
        }

        private static TMP_Text ReadTmp(object target, string memberName)
        {
            if (target == null) return null;
            FieldInfo field = target.GetType().GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object value = field?.GetValue(target);
            if (value is TMP_Text tmp) return tmp;
            if (value is TMP_InputField input) return input.textComponent;
            return null;
        }

        private static string DescribeShipTerminal()
        {
            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                if (terminal == null) return "{terminal=<none>}";
                return "{id=" + terminal.GetInstanceID() + " inUse=" + terminal.terminalInUse + " credits=" + terminal.groupCredits + "}";
            }
            catch
            {
                return "{terminal=<error>}";
            }
        }

        /// <summary>
        /// #577 — the INTERCOM view's transmit control. The outer image is the border and the only
        /// raycast target; the inset child is the fill, so the pressed state reads as lit glass
        /// inside a bezel rather than a flat colour swap.
        /// </summary>
        private void CreateIntercomButton(RectTransform parent)
        {
            // ApplyLayout owns the position and size; CreateRect's zeroed offsets are the placeholder.
            _intercomButtonRect = CreateRect(parent, "IntercomPushToTalk",
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
            GameObject go = _intercomButtonRect.gameObject;

            _intercomButtonBorder = go.AddComponent<Image>();
            _intercomButtonBorder.raycastTarget = true;

            _intercomButtonInput = go.AddComponent<MainframeIntercomPushButton>();
            _intercomButtonInput.HoldChanged = OnIntercomButtonHoldChanged;

            RectTransform fill = CreateRect(go.transform, "Fill", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            fill.offsetMin = new Vector2(2f, 2f);
            fill.offsetMax = new Vector2(-2f, -2f);
            _intercomButtonFill = fill.gameObject.AddComponent<Image>();
            _intercomButtonFill.raycastTarget = false;

            _intercomButtonLabel = CreateText(fill, "Label", IdleTransmitLabel,
                14f, TextAlignmentOptions.Center, ScreenGreenDim, Vector2.zero, Vector2.zero);
            RectTransform labelRect = _intercomButtonLabel.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(0.5f, 0.5f);
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            go.SetActive(false);
            RefreshIntercomButton();
        }

        private void OnIntercomButtonHoldChanged(bool held)
        {
            SetPointerPushToTalk(held);
        }

        private void RefreshIntercomButton()
        {
            if (_intercomButtonRect == null) return;

            GameObject go = _intercomButtonRect.gameObject;
            bool visible = IsIntercomActive;
            if (go.activeSelf != visible)
            {
                // Deactivating fires the latch's OnDisable, which releases any live pointer hold.
                go.SetActive(visible);
            }
            if (!visible) return;

            if (_intercomButtonBorder != null)
            {
                _intercomButtonBorder.color = _intercomSpeaking
                    ? ScreenAmber
                    : new Color(_dimColor.r, _dimColor.g, _dimColor.b, 0.75f);
            }
            if (_intercomButtonFill != null)
                _intercomButtonFill.color = _intercomSpeaking ? ScreenAmberFill : ScreenBlack;
            if (_intercomButtonLabel != null)
            {
                _intercomButtonLabel.color = _intercomSpeaking ? ScreenAmber : _dimColor;
                _intercomButtonLabel.text = _intercomSpeaking ? HeldTransmitLabel : IdleTransmitLabel;
                // One pixel of travel is the whole "pushed in" read at this size.
                _intercomButtonLabel.rectTransform.anchoredPosition = _intercomSpeaking
                    ? new Vector2(0f, -1f)
                    : Vector2.zero;
            }
        }

        private void CreateFrame(RectTransform parent)
        {
            // Sizes come from CrtDockLayout.ResizeFrame in ApplyLayout; only the anchors and the
            // inset offsets are authored here.
            _frameTopRect = CreateLine(parent, "FrameTop", new Vector2(0.5f, 1f), new Vector2(0f, -8f), Vector2.zero);
            _frameBottomRect = CreateLine(parent, "FrameBottom", new Vector2(0.5f, 0f), new Vector2(0f, 8f), Vector2.zero);
            _frameLeftRect = CreateLine(parent, "FrameLeft", new Vector2(0f, 0.5f), new Vector2(10f, 0f), Vector2.zero);
            _frameRightRect = CreateLine(parent, "FrameRight", new Vector2(1f, 0.5f), new Vector2(-10f, 0f), Vector2.zero);
        }

        private RectTransform CreateLine(RectTransform parent, string name, Vector2 anchor, Vector2 anchoredPos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            var image = go.AddComponent<Image>();
            image.color = new Color(ScreenGreen.r, ScreenGreen.g, ScreenGreen.b, 0.34f);
            image.raycastTarget = false;
            return rt;
        }

        private void CreateScanlines(Transform parent)
        {
            _scanlineTexture = new Texture2D(1, 4, TextureFormat.RGBA32, false)
            {
                name = "LethalCCTV_MainframeControl_Scanlines",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            _scanlineTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
            _scanlineTexture.SetPixel(0, 1, new Color(0f, 0.20f, 0.06f, 0.10f));
            // Halved from 0.20: at the game's native internal resolution a dark scanline row can
            // land on the single row of pixels a glyph stroke gets. The banding still reads.
            _scanlineTexture.SetPixel(0, 2, new Color(0f, 0f, 0f, 0.10f));
            _scanlineTexture.SetPixel(0, 3, new Color(0f, 0.45f, 0.12f, 0.08f));
            _scanlineTexture.Apply(false, true);
            _scanlineSprite = Sprite.Create(_scanlineTexture, new Rect(0f, 0f, 1f, 4f), new Vector2(0.5f, 0.5f));

            var scan = CreateRect(parent, "Scanlines", Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            var image = scan.gameObject.AddComponent<Image>();
            image.sprite = _scanlineSprite;
            image.type = Image.Type.Tiled;
            image.color = Color.white;
            image.raycastTarget = false;
        }

        private TextMeshProUGUI CreateText(Transform parent, string name, string text, float size,
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
            tmp.fontStyle = FontStyles.Bold;
            _textElements.Add(tmp);
            return tmp;
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

        private static string ColorHex(Color color)
        {
            return ColorUtility.ToHtmlStringRGB(color);
        }
    }
}
