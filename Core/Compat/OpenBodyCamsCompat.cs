using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using GameNetcodeStuff;
using HarmonyLib;
using Y4NGZCompany.ShipSystems.Surveillance;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Core.Compat
{
    // Soft-dependency bridge for Zaggy1024/OpenBodyCams.
    //
    // LethalCCTV owns the integration whenever both plugins are installed:
    // - OpenBodyCams must not draw to the ship monitor or sell a second receiver.
    // - Purchasing LethalCCTV's CCTV Terminal unlocks one focus feed per connected player.
    // - Player bodycams lead the unified feed roster; facility CCTV cameras follow them.
    // - Ship Systems may sell that terminal, but it does not own a separate Bodycams entitlement.
    // - Focus audio follows OpenBodyCams' selected target through LethalCCTV's one listener proxy.
    public static class OpenBodyCamsCompat
    {
        public const string PLUGIN_GUID = "Zaggy1024.OpenBodyCams";

        private const float OverlaySuppressIntervalSeconds = 1.5f;
        private const float StoreSuppressIntervalSeconds = 2.0f;
        private const float ShipSystemsRebindIntervalSeconds = 2.5f;
        private const float PlayerFeedRefreshIntervalSeconds = 0.5f;

        private sealed class PlayerBodyCamFeed
        {
            internal int PlayerSlot;
            internal PlayerControllerB Player;
            internal GameObject Host;
            internal object BodyCam;
            internal bool WasSelected;
        }

        private static readonly bool _isLoaded = Chainloader.PluginInfos.ContainsKey(PLUGIN_GUID);
        private static readonly List<PlayerBodyCamFeed> _playerFeeds = new List<PlayerBodyCamFeed>();
        private static readonly HashSet<int> _playerFeedCreationFailuresLogged = new HashSet<int>();

        private static bool _resolved;
        private static Type _apiBodyCamType;
        private static Type _shipObjectsType;
        private static PropertyInfo _mainBodyCamProperty;
        private static FieldInfo _shipObjectsMainBodyCamField;

        private static Type _bodyCamType;
        private static Type _shipUpgradesType;
        private static PropertyInfo _forceEnableCameraProperty;
        private static PropertyInfo _framerateProperty;
        private static MethodInfo _createBodyCamMethod;
        private static MethodInfo _setTargetToPlayerMethod;
        private static MethodInfo _getCameraMethod;
        private static MethodInfo _updateSettingsMethod;
        private static MethodInfo _ensureMaterialsExistMethod;
        private static FieldInfo _cameraField;
        private static PropertyInfo _currentTargetProperty;
        private static FieldInfo _currentActualTargetField;
        private static FieldInfo _monitorRendererField;
        private static FieldInfo _monitorMaterialIndexField;
        private static FieldInfo _monitorDisabledMaterialField;
        private static FieldInfo _monitorOnMaterialField;
        private static PropertyInfo _monitorOnMaterialProperty;
        private static FieldInfo _bodyCamUnlockableIsPlacedField;

        private static object _mainBodyCam;
        private static Renderer _capturedMonitorRenderer;
        private static int _capturedMonitorMaterialIndex = -1;
        private static Material _capturedMonitorMaterial;
        private static bool _capturedMonitorBinding;
        private static bool _suppressionLogged;
        private static bool _storeSuppressionLogged;
        private static bool _focusLogged;
        private static bool _missingLogged;
        private static bool _focusAudioLogged;
        private static bool _audioTargetFailureLogged;
        private static float _nextOverlaySuppressAt;
        private static float _nextStoreSuppressAt;
        private static float _nextShipSystemsRebindAt;
        private static Renderer _lastShipSystemsRebindRenderer;
        private static int _lastShipSystemsRebindMaterialIndex = -1;
        private static Material _lastShipSystemsRebindMaterial;
        private static Texture _lastFocusTexture;
        private static Harmony _harmony;
        private static bool _screenMaterialPatched;
        private static bool _screenMaterialPatchLogged;
        private static bool _mapCameraConditionsPatched;
        private static bool _mapCameraConditionsPatchUnavailable;
        private static bool _mapCameraConditionsPatchLogged;
        private static bool _mapCameraOffscreenGateLogged;
        private static bool _mainBodyCamSettingsPatched;
        private static bool _mainBodyCamSettingsPatchUnavailable;
        private static bool _mainBodyCamSettingsPatchLogged;
        private static bool _mainBodyCamMaterialGuardFailureLogged;
        private static bool _ownershipGateLogged;
        private static bool _framerateUnavailableLogged;
        private static float _lastAppliedBodycamRenderHz = -1f;
        private static float _lastScreenMaterialSuppressLogAt;
        private static int _lastTickFrame = -1;
        private static float _nextPlayerFeedRefreshAt;
        private static int _playerFeedRosterRevision;
        private static bool _playerFeedFactoryUnavailableLogged;
        private static Transform _monitorWallTransform;
        private static Terminal _cachedTerminal;
        private static bool _storeEntriesClean;

        public static bool IsLoaded => _isLoaded;

        internal static int PlayerFeedCount
        {
            get
            {
                Tick();
                return _playerFeeds.Count;
            }
        }

        internal static int RosterRevision
        {
            get
            {
                Tick();
                return _playerFeedRosterRevision;
            }
        }

        internal static void Initialize()
        {
            if (!IsLoaded) return;

            // Patch at plugin Awake, before OpenBodyCams' player-connect finalizer can
            // enter LateInitialization and leave MainBodyCam half-initialized.
            Resolve();
            EnsureHarmonyPatch();
        }

        internal static bool ShouldShowFocusSlot()
        {
            return IsLoaded && CCTVTerminalUnlockable.IsPurchased();
        }

        internal static bool TryGetPlayerSlotAtFeedIndex(int feedIndex, out int playerSlot)
        {
            Tick();
            if (feedIndex < 0 || feedIndex >= _playerFeeds.Count)
            {
                playerSlot = -1;
                return false;
            }

            playerSlot = _playerFeeds[feedIndex].PlayerSlot;
            return true;
        }

        internal static int IndexOfPlayerSlot(int playerSlot)
        {
            for (int index = 0; index < _playerFeeds.Count; index++)
            {
                if (_playerFeeds[index].PlayerSlot == playerSlot)
                    return index;
            }
            return -1;
        }

        internal static bool TryGetPlayerName(int feedIndex, out string playerName)
        {
            Tick();
            playerName = null;
            if (feedIndex < 0 || feedIndex >= _playerFeeds.Count)
                return false;

            PlayerBodyCamFeed feed = _playerFeeds[feedIndex];
            PlayerControllerB player = feed.Player;
            if (player == null)
                return false;

            string resolved = player.playerUsername;
            if (string.IsNullOrWhiteSpace(resolved))
                resolved = $"PLAYER {feed.PlayerSlot + 1}";
            playerName = resolved.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return playerName.Length > 0;
        }

        internal static void Tick()
        {
            if (!IsLoaded) return;
            int frame = Time.frameCount;
            if (_lastTickFrame == frame) return;
            _lastTickFrame = frame;

            Resolve();
            EnsureHarmonyPatch();

            bool bodycamRouteAvailable = ShouldShowFocusSlot();
            SuppressOpenBodyCamsStoreEntries();
            SetOpenBodyCamsReceiverAvailable(bodycamRouteAvailable);

            object bodyCam = GetMainBodyCam();
            if (bodyCam == null)
            {
                ReconcilePlayerFeeds(allowFeeds: false);
                SetLastFocusTexture(null);
                LogMissingOnce();
                return;
            }

            CaptureNewBodyCam(bodyCam);
            SuppressShipMonitor(bodyCam);
            ApplyBodyCamRenderBudget(bodyCam);

            // The native MainBodyCam remains the ship-monitor implementation that CCTV owns
            // and suppresses. LethalCCTV creates its own unbound OBC components below so each
            // connected player has an independently addressable focus feed.
            SetForceEnableCamera(bodyCam, false);
            SetBodyCamBehaviourEnabled(bodyCam, false);
            SetBodyCamCameraEnabled(bodyCam, false);

            ReconcilePlayerFeeds(bodycamRouteAvailable);
            ApplySelectedPlayerFeedRendering(
                bodycamRouteAvailable && MonitorFocus.IsFocused && MonitorFocus.IsBodycamFeedActive
                    ? MonitorFocus.ActiveBodycamFeedIndex
                    : -1);
            LogOwnershipGateOnce(bodycamRouteAvailable);
        }

        internal static bool TryGetFocusTexture(out Texture texture)
        {
            Tick();
            texture = _lastFocusTexture;
            return ShouldShowFocusSlot() && MonitorFocus.IsBodycamFeedActive && texture != null;
        }

        internal static void SetFocusRenderingActive(bool active)
        {
            if (!IsLoaded) return;

            object bodyCam = GetMainBodyCam();
            if (bodyCam != null)
            {
                SetForceEnableCamera(bodyCam, false);
                SetBodyCamBehaviourEnabled(bodyCam, false);
                SetBodyCamCameraEnabled(bodyCam, false);
            }

            ApplySelectedPlayerFeedRendering(
                active && ShouldShowFocusSlot() && MonitorFocus.IsFocused && MonitorFocus.IsBodycamFeedActive
                    ? MonitorFocus.ActiveBodycamFeedIndex
                    : -1);
        }

        private static object GetMainBodyCam()
        {
            Resolve();
            if (!_resolved) return null;

            try
            {
                object bodyCam = _mainBodyCamProperty?.GetValue(null);
                if (bodyCam != null) return bodyCam;

                return _shipObjectsMainBodyCamField?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static void CaptureNewBodyCam(object bodyCam)
        {
            if (ReferenceEquals(_mainBodyCam, bodyCam)) return;

            _mainBodyCam = bodyCam;
            _capturedMonitorRenderer = null;
            _capturedMonitorMaterialIndex = -1;
            _capturedMonitorMaterial = null;
            _capturedMonitorBinding = false;
            _suppressionLogged = false;
            _focusLogged = false;
            _focusAudioLogged = false;
            _audioTargetFailureLogged = false;
            _nextShipSystemsRebindAt = 0f;
            _lastShipSystemsRebindRenderer = null;
            _lastShipSystemsRebindMaterialIndex = -1;
            _lastShipSystemsRebindMaterial = null;
            ResolveBodyCamMembers(bodyCam.GetType());
        }

        private static void ReconcilePlayerFeeds(bool allowFeeds)
        {
            StartOfRound round = StartOfRound.Instance;
            if (!allowFeeds || round == null)
            {
                DestroyAllPlayerFeeds();
                return;
            }
            if (_createBodyCamMethod == null || _setTargetToPlayerMethod == null)
            {
                DestroyAllPlayerFeeds();
                if (!_playerFeedFactoryUnavailableLogged)
                {
                    _playerFeedFactoryUnavailableLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV] OpenBodyCams player-feed factory unavailable; " +
                        "CreateBodyCam or SetTargetToPlayer could not be resolved.");
                }
                return;
            }

            if (Time.unscaledTime < _nextPlayerFeedRefreshAt)
                return;
            _nextPlayerFeedRefreshAt = Time.unscaledTime + PlayerFeedRefreshIntervalSeconds;

            var playerSlots = new List<int>();
            var players = new List<PlayerControllerB>();
            PlayerControllerB[] allPlayers = round.allPlayerScripts;
            if (allPlayers != null)
            {
                for (int slot = 0; slot < allPlayers.Length; slot++)
                {
                    PlayerControllerB player = allPlayers[slot];
                    if (player == null || (!player.isPlayerControlled && !player.isHostPlayerObject))
                        continue;
                    playerSlots.Add(slot);
                    players.Add(player);
                }
            }

            bool rosterChanged = players.Count != _playerFeeds.Count;
            if (!rosterChanged)
            {
                for (int index = 0; index < players.Count; index++)
                {
                    PlayerBodyCamFeed current = _playerFeeds[index];
                    if (current.PlayerSlot != playerSlots[index]
                        || !ReferenceEquals(current.Player, players[index])
                        || current.Host == null
                        || !IsUnityObjectAlive(current.BodyCam))
                    {
                        rosterChanged = true;
                        break;
                    }
                }
            }

            if (!rosterChanged)
                return;

            var previousOrder = new List<PlayerBodyCamFeed>(_playerFeeds);
            var unusedFeeds = new List<PlayerBodyCamFeed>(_playerFeeds);
            var nextFeeds = new List<PlayerBodyCamFeed>(players.Count);
            for (int index = 0; index < players.Count; index++)
            {
                PlayerBodyCamFeed feed = FindReusablePlayerFeed(
                    unusedFeeds,
                    playerSlots[index],
                    players[index]);
                if (feed != null)
                    unusedFeeds.Remove(feed);
                else
                    feed = CreatePlayerFeed(playerSlots[index], players[index]);
                if (feed != null)
                    nextFeeds.Add(feed);
            }

            for (int index = 0; index < unusedFeeds.Count; index++)
                DestroyPlayerFeed(unusedFeeds[index]);

            bool feedListChanged = previousOrder.Count != nextFeeds.Count;
            if (!feedListChanged)
            {
                for (int index = 0; index < previousOrder.Count; index++)
                {
                    if (!ReferenceEquals(previousOrder[index], nextFeeds[index]))
                    {
                        feedListChanged = true;
                        break;
                    }
                }
            }

            _playerFeeds.Clear();
            _playerFeeds.AddRange(nextFeeds);
            if (feedListChanged)
            {
                _playerFeedRosterRevision++;
                _focusLogged = false;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] OpenBodyCams player roster rebuilt with {_playerFeeds.Count} independent feed(s).");
            }
        }

        private static PlayerBodyCamFeed FindReusablePlayerFeed(
            List<PlayerBodyCamFeed> candidates,
            int playerSlot,
            PlayerControllerB player)
        {
            for (int index = 0; index < candidates.Count; index++)
            {
                PlayerBodyCamFeed candidate = candidates[index];
                if (candidate.PlayerSlot == playerSlot
                    && ReferenceEquals(candidate.Player, player)
                    && candidate.Host != null
                    && IsUnityObjectAlive(candidate.BodyCam))
                    return candidate;
            }
            return null;
        }

        private static PlayerBodyCamFeed CreatePlayerFeed(int playerSlot, PlayerControllerB player)
        {
            GameObject host = null;
            try
            {
                host = new GameObject($"LethalCCTV_PlayerBodyCam_{playerSlot}");
                if (SurveillanceBootstrap.Instance != null)
                    host.transform.SetParent(SurveillanceBootstrap.Instance.transform, worldPositionStays: false);
                else
                    UnityEngine.Object.DontDestroyOnLoad(host);

                object bodyCam = _createBodyCamMethod.Invoke(
                    null,
                    new object[] { host, null, -1, null });
                if (!IsUnityObjectAlive(bodyCam))
                    throw new InvalidOperationException("CreateBodyCam returned no live component.");

                _setTargetToPlayerMethod.Invoke(bodyCam, new object[] { player });
                ApplyBodyCamRenderBudget(bodyCam);
                SetForceEnableCamera(bodyCam, false);
                // Start dormant. Selection enables exactly one component before its
                // next LateUpdate, avoiding target/weather setup and update work for
                // feeds that have never been viewed.
                SetBodyCamBehaviourEnabled(bodyCam, false);
                SetBodyCamCameraEnabled(bodyCam, false);
                _playerFeedCreationFailuresLogged.Remove(playerSlot);

                return new PlayerBodyCamFeed
                {
                    PlayerSlot = playerSlot,
                    Player = player,
                    Host = host,
                    BodyCam = bodyCam,
                };
            }
            catch (Exception ex)
            {
                if (host != null)
                    UnityEngine.Object.Destroy(host);
                Exception cause = ex is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : ex;
                if (_playerFeedCreationFailuresLogged.Add(playerSlot))
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Could not create OpenBodyCams feed for player slot {playerSlot}: " +
                    $"{cause.GetType().Name}: {cause.Message}");
                }
                return null;
            }
        }

        private static void DestroyAllPlayerFeeds(bool incrementRevision = true)
        {
            bool hadFeeds = _playerFeeds.Count > 0;
            for (int index = 0; index < _playerFeeds.Count; index++)
                DestroyPlayerFeed(_playerFeeds[index]);
            _playerFeeds.Clear();
            SetLastFocusTexture(null);
            if (hadFeeds && incrementRevision)
                _playerFeedRosterRevision++;
        }

        private static void DestroyPlayerFeed(PlayerBodyCamFeed feed)
        {
            if (feed == null)
                return;
            SetForceEnableCamera(feed.BodyCam, false);
            SetBodyCamBehaviourEnabled(feed.BodyCam, false);
            SetBodyCamCameraEnabled(feed.BodyCam, false);
            if (feed.Host != null)
                UnityEngine.Object.Destroy(feed.Host);
        }

        private static bool IsUnityObjectAlive(object value)
        {
            if (value == null)
                return false;
            return !(value is UnityEngine.Object unityObject) || unityObject != null;
        }

        private static bool TryGetPlayerFeed(int feedIndex, out PlayerBodyCamFeed feed)
        {
            feed = null;
            if (feedIndex < 0 || feedIndex >= _playerFeeds.Count)
                return false;
            PlayerBodyCamFeed candidate = _playerFeeds[feedIndex];
            if (candidate == null || candidate.Player == null || !IsUnityObjectAlive(candidate.BodyCam))
                return false;
            feed = candidate;
            return true;
        }

        private static void ApplySelectedPlayerFeedRendering(int selectedFeedIndex)
        {
            Texture selectedTexture = null;
            for (int index = 0; index < _playerFeeds.Count; index++)
            {
                PlayerBodyCamFeed feed = _playerFeeds[index];
                if (feed == null || !IsUnityObjectAlive(feed.BodyCam))
                    continue;

                bool selected = index == selectedFeedIndex;
                bool wasSelected = feed.WasSelected;
                // Only the selected feed runs OpenBodyCams' LateUpdate. Keeping every
                // player component active made each one perform target/weather work on
                // every gameplay frame even though only one feed can be observed.
                // Selection re-enables the component before its next LateUpdate.
                SetBodyCamBehaviourEnabled(feed.BodyCam, selected);
                SetForceEnableCamera(feed.BodyCam, selected);
                feed.WasSelected = selected;
                if (!selected)
                {
                    SetBodyCamCameraEnabled(feed.BodyCam, false);
                    continue;
                }

                ApplyBodyCamRenderBudget(feed.BodyCam);
                Camera camera = EnsureBodyCamCamera(feed.BodyCam);
                // Selection can happen after OpenBodyCams' LateUpdate. Request one
                // immediate warm frame on the transition; subsequent frames remain
                // governed by OpenBodyCams' configured Framerate cadence.
                if (!wasSelected && camera != null)
                    SetBodyCamCameraEnabled(feed.BodyCam, true);
                selectedTexture = camera != null ? camera.targetTexture : null;
                if (!_focusLogged && selectedTexture != null)
                {
                    _focusLogged = true;
                    string playerName = !string.IsNullOrWhiteSpace(feed.Player.playerUsername)
                        ? feed.Player.playerUsername
                        : $"PLAYER {feed.PlayerSlot + 1}";
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] OpenBodyCams player feed '{playerName}' routed to CCTV focus " +
                        $"({selectedTexture.width}x{selectedTexture.height}).");
                }
            }

            SetLastFocusTexture(selectedTexture);
        }

        private static void SuppressShipMonitor(object bodyCam)
        {
            if (bodyCam == null || _monitorRendererField == null) return;

            try
            {
                Renderer renderer = _monitorRendererField.GetValue(bodyCam) as Renderer;
                int materialIndex = ReadIntField(_monitorMaterialIndexField, bodyCam, -1);
                Material disabledMaterial = _monitorDisabledMaterialField?.GetValue(bodyCam) as Material;

                if (!_capturedMonitorBinding && renderer != null)
                {
                    _capturedMonitorRenderer = renderer;
                    _capturedMonitorMaterialIndex = materialIndex;
                    _capturedMonitorMaterial = disabledMaterial ?? TryReadRendererMaterial(renderer, materialIndex);
                    _capturedMonitorBinding = true;
                }

                bool shipSystemsOwnsMonitor = CCTVShipSystemsBridge.IsPowerMonitorDisplayEnabled();
                Renderer targetRenderer = renderer != null ? renderer : _capturedMonitorRenderer;
                int targetIndex = renderer != null ? materialIndex : _capturedMonitorMaterialIndex;
                Material restoreMaterial = disabledMaterial ?? _capturedMonitorMaterial;
                Material currentMaterial = TryReadRendererMaterial(targetRenderer, targetIndex);
                bool currentIsBodyCam = MaterialLooksLikeBodyCam(currentMaterial);
                if (targetRenderer != null
                    && restoreMaterial != null
                    && (!shipSystemsOwnsMonitor || currentIsBodyCam))
                {
                    TrySetRendererMaterial(targetRenderer, targetIndex, restoreMaterial);
                }

                // Keep OBC's renderer reference intact until its OverlayManager.Start has
                // consumed the source geometry. Nulling it here caused the observed startup
                // exception. The UpdateScreenMaterial prefix plus the disabled native
                // component already prevent any monitor write or render ownership leak.

                if (shipSystemsOwnsMonitor &&
                    currentIsBodyCam &&
                    ShouldRequestShipSystemsRebind(targetRenderer, targetIndex, currentMaterial))
                {
                    CCTVShipSystemsBridge.ForceRebindPowerDisplay();
                }

                SuppressOverlayMeshes();

                if (!_suppressionLogged)
                {
                    _suppressionLogged = true;
                    SurveillanceBootstrap.Log?.LogInfo(shipSystemsOwnsMonitor
                        ? "[LethalCCTV] OpenBodyCams ship-monitor output suppressed; LGUShipSystems keeps the large right monitor."
                        : "[LethalCCTV] OpenBodyCams ship-monitor output suppressed; feed reserved for CCTV focus.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] OpenBodyCams monitor suppression failed: {ex.Message}");
            }
        }

        internal static void Shutdown()
        {
            DestroyAllPlayerFeeds();
            _nextPlayerFeedRefreshAt = 0f;
            _playerFeedRosterRevision = 0;
            _playerFeedFactoryUnavailableLogged = false;
            _playerFeedCreationFailuresLogged.Clear();
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch
            {
            }
            _harmony = null;
            _screenMaterialPatched = false;
            _screenMaterialPatchLogged = false;
            _mapCameraConditionsPatched = false;
            _mapCameraConditionsPatchUnavailable = false;
            _mapCameraConditionsPatchLogged = false;
            _mapCameraOffscreenGateLogged = false;
            _mainBodyCamSettingsPatched = false;
            _mainBodyCamSettingsPatchUnavailable = false;
            _mainBodyCamSettingsPatchLogged = false;
            _mainBodyCamMaterialGuardFailureLogged = false;
            _ownershipGateLogged = false;
            _framerateUnavailableLogged = false;
            _focusAudioLogged = false;
            _audioTargetFailureLogged = false;
            _lastAppliedBodycamRenderHz = -1f;
            _lastTickFrame = -1;
            _lastShipSystemsRebindRenderer = null;
            _lastShipSystemsRebindMaterialIndex = -1;
            _lastShipSystemsRebindMaterial = null;
            _monitorWallTransform = null;
            _cachedTerminal = null;
            _storeEntriesClean = false;
        }

        private static void SuppressOpenBodyCamsStoreEntries()
        {
            if (Time.unscaledTime < _nextStoreSuppressAt) return;
            _nextStoreSuppressAt = Time.unscaledTime + StoreSuppressIntervalSeconds;

            try
            {
                if (_cachedTerminal == null)
                {
                    _cachedTerminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                    // New Terminal instance (scene reload) may have fresh entries.
                    _storeEntriesClean = false;
                }
                Terminal terminal = _cachedTerminal;
                if (terminal == null) return;
                if (_storeEntriesClean) return;

                int removed = 0;
                removed += RemoveBodycamCompatibleNouns(terminal);
                removed += RemoveBodycamShipDecorNodes(terminal);
                removed += RemoveBodycamBuyableItems(terminal);

                if (removed > 0 && !_storeSuppressionLogged)
                {
                    _storeSuppressionLogged = true;
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV] OpenBodyCams terminal store entry hidden; bodycam routing is included with the CCTV Terminal.");
                }

                // Entries only reappear when the Terminal itself is rebuilt; the
                // Unity-null check on _cachedTerminal above re-arms this scan then.
                if (removed == 0)
                    _storeEntriesClean = true;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] OpenBodyCams store suppression failed: {ex.Message}");
            }
        }

        private static int RemoveBodycamCompatibleNouns(Terminal terminal)
        {
            TerminalKeyword buyKeyword = FindBuyKeyword(terminal);
            CompatibleNoun[] compatibleNouns = buyKeyword?.compatibleNouns;
            if (compatibleNouns == null || compatibleNouns.Length == 0)
                return 0;

            var kept = new System.Collections.Generic.List<CompatibleNoun>(compatibleNouns.Length);
            for (int i = 0; i < compatibleNouns.Length; i++)
            {
                CompatibleNoun compatible = compatibleNouns[i];
                if (!IsBodycamCompatibleNoun(terminal, compatible))
                    kept.Add(compatible);
            }

            int removed = compatibleNouns.Length - kept.Count;
            if (removed > 0)
                buyKeyword.compatibleNouns = kept.ToArray();

            return removed;
        }

        private static int RemoveBodycamShipDecorNodes(Terminal terminal)
        {
            if (terminal.ShipDecorSelection == null || terminal.ShipDecorSelection.Count == 0)
                return 0;

            int before = terminal.ShipDecorSelection.Count;
            terminal.ShipDecorSelection.RemoveAll(IsBodycamTerminalNode);
            return before - terminal.ShipDecorSelection.Count;
        }

        private static int RemoveBodycamBuyableItems(Terminal terminal)
        {
            Item[] items = terminal.buyableItemsList;
            if (items == null || items.Length == 0)
                return 0;

            bool[] remove = new bool[items.Length];
            int removeCount = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (IsBodycamName(items[i]?.itemName))
                {
                    remove[i] = true;
                    removeCount++;
                }
            }

            if (removeCount == 0)
                return 0;

            int[] indexMap = new int[items.Length];
            var keptItems = new System.Collections.Generic.List<Item>(items.Length - removeCount);
            for (int i = 0; i < items.Length; i++)
            {
                if (remove[i])
                {
                    indexMap[i] = -1;
                    continue;
                }

                indexMap[i] = keptItems.Count;
                keptItems.Add(items[i]);
            }

            terminal.buyableItemsList = keptItems.ToArray();
            RemapItemSalesPercentages(terminal, items.Length, remove, keptItems.Count);
            RemapBuyCompatibleNounsAfterItemRemoval(terminal, indexMap);
            return removeCount;
        }

        private static void RemapItemSalesPercentages(
            Terminal terminal,
            int oldItemCount,
            bool[] removedItems,
            int newItemCount)
        {
            int[] oldSales = terminal.itemSalesPercentages;
            if (oldSales == null || oldSales.Length == 0)
                return;

            int vehicleCount = 0;
            try
            {
                vehicleCount = terminal.buyableVehicles != null
                    ? terminal.buyableVehicles.Length
                    : Math.Max(0, oldSales.Length - oldItemCount);
            }
            catch
            {
                vehicleCount = Math.Max(0, oldSales.Length - oldItemCount);
            }

            int[] newSales = new int[newItemCount + vehicleCount];
            int nextItemSale = 0;
            for (int i = 0; i < oldItemCount; i++)
            {
                if (removedItems[i]) continue;
                newSales[nextItemSale++] = i < oldSales.Length ? oldSales[i] : 100;
            }

            for (int i = 0; i < vehicleCount; i++)
            {
                int oldIndex = oldItemCount + i;
                int newIndex = newItemCount + i;
                newSales[newIndex] = oldIndex < oldSales.Length ? oldSales[oldIndex] : 100;
            }

            terminal.itemSalesPercentages = newSales;
        }

        private static void RemapBuyCompatibleNounsAfterItemRemoval(Terminal terminal, int[] indexMap)
        {
            TerminalKeyword buyKeyword = FindBuyKeyword(terminal);
            CompatibleNoun[] compatibleNouns = buyKeyword?.compatibleNouns;
            if (compatibleNouns == null || compatibleNouns.Length == 0)
                return;

            var kept = new System.Collections.Generic.List<CompatibleNoun>(compatibleNouns.Length);
            for (int i = 0; i < compatibleNouns.Length; i++)
            {
                CompatibleNoun compatible = compatibleNouns[i];
                TerminalNode node = compatible?.result;
                if (node == null || node.buyItemIndex < 0)
                {
                    kept.Add(compatible);
                    continue;
                }

                int oldIndex = node.buyItemIndex;
                if (oldIndex >= indexMap.Length)
                {
                    kept.Add(compatible);
                    continue;
                }

                int newIndex = indexMap[oldIndex];
                if (newIndex < 0)
                    continue;

                node.buyItemIndex = newIndex;
                kept.Add(compatible);
            }

            buyKeyword.compatibleNouns = kept.ToArray();
        }

        private static TerminalKeyword FindBuyKeyword(Terminal terminal)
        {
            TerminalKeyword[] keywords = terminal?.terminalNodes?.allKeywords;
            if (keywords == null)
                return null;

            for (int i = 0; i < keywords.Length; i++)
            {
                TerminalKeyword keyword = keywords[i];
                if (keyword != null && keyword.isVerb && string.Equals(keyword.word, "buy", StringComparison.OrdinalIgnoreCase))
                    return keyword;
            }

            return null;
        }

        private static bool IsBodycamCompatibleNoun(Terminal terminal, CompatibleNoun compatible)
        {
            if (compatible == null)
                return false;

            TerminalNode node = compatible.result;
            Item item = null;
            if (node != null
                && node.buyItemIndex >= 0
                && terminal?.buyableItemsList != null
                && node.buyItemIndex < terminal.buyableItemsList.Length)
            {
                item = terminal.buyableItemsList[node.buyItemIndex];
            }

            return IsBodycamName(compatible.noun?.word)
                || IsBodycamName(item?.itemName)
                || IsBodycamTerminalNode(node);
        }

        private static bool IsBodycamTerminalNode(TerminalNode node)
        {
            if (node == null)
                return false;

            if (IsBodycamName(node.creatureName) || IsBodycamName(node.name))
                return true;

            int unlockableId = node.shipUnlockableID;
            var unlockables = StartOfRound.Instance?.unlockablesList?.unlockables;
            if (unlockables != null && unlockableId >= 0 && unlockableId < unlockables.Count)
                return IsBodycamName(unlockables[unlockableId]?.unlockableName);

            return false;
        }

        private static bool IsBodycamName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = string.Empty;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c))
                    normalized += char.ToLowerInvariant(c);
            }

            return normalized == "bodycam" || normalized == "bodycams";
        }

        private static void SetOpenBodyCamsReceiverAvailable(bool available)
        {
            Resolve();
            try
            {
                _bodyCamUnlockableIsPlacedField?.SetValue(null, available);
            }
            catch
            {
            }
        }

        private static Camera EnsureBodyCamCamera(object bodyCam)
        {
            if (bodyCam == null) return null;

            try
            {
                if (bodyCam is Behaviour behaviour && !behaviour.enabled)
                    behaviour.enabled = true;

                Camera camera = _getCameraMethod?.Invoke(bodyCam, Array.Empty<object>()) as Camera;
                if (camera == null)
                    camera = _cameraField?.GetValue(bodyCam) as Camera;

                if (camera != null && camera.targetTexture == null)
                {
                    _updateSettingsMethod?.Invoke(bodyCam, Array.Empty<object>());
                    camera = _cameraField?.GetValue(bodyCam) as Camera ?? camera;
                }

                return camera;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] OpenBodyCams camera resolve failed: {ex.Message}");
                return null;
            }
        }

        internal static bool TryGetFocusAudioTarget(out Transform target)
        {
            target = null;
            if (!IsLoaded ||
                !ShouldShowFocusSlot() ||
                !MonitorFocus.IsFocused ||
                !MonitorFocus.IsBodycamFeedActive)
            {
                return false;
            }

            Tick();
            if (!TryGetPlayerFeed(MonitorFocus.ActiveBodycamFeedIndex, out PlayerBodyCamFeed feed))
                return false;
            object bodyCam = feed.BodyCam;

            if (_bodyCamType == null || !_bodyCamType.IsInstanceOfType(bodyCam))
                ResolveBodyCamMembers(bodyCam.GetType());

            try
            {
                Transform currentTarget = _currentTargetProperty?.GetValue(bodyCam) as Transform
                    ?? _currentActualTargetField?.GetValue(bodyCam) as Transform;
                if (currentTarget == null)
                    return false;

                Camera camera = EnsureBodyCamCamera(bodyCam);
                target = camera != null ? camera.transform : currentTarget;
                if (target == null)
                    return false;

                if (!_focusAudioLogged)
                {
                    _focusAudioLogged = true;
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV] OpenBodyCams positional audio routed through the CCTV focus proxy.");
                }
                return true;
            }
            catch (Exception ex)
            {
                if (!_audioTargetFailureLogged)
                {
                    _audioTargetFailureLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] OpenBodyCams audio target resolve failed: {ex.Message}");
                }
                return false;
            }
        }

        private static void SetBodyCamBehaviourEnabled(object bodyCam, bool enabled)
        {
            try
            {
                if (bodyCam is Behaviour behaviour && behaviour.enabled != enabled)
                    behaviour.enabled = enabled;
            }
            catch
            {
            }
        }

        private static void SetBodyCamCameraEnabled(object bodyCam, bool enabled)
        {
            try
            {
                Camera camera = _cameraField?.GetValue(bodyCam) as Camera;
                // GetCamera() creates the camera in OpenBodyCams 3.x. It is valid when
                // enabling a purchased feed, but the ownership gate must never create
                // the very camera it is trying to keep dormant.
                if (camera == null && enabled)
                    camera = _getCameraMethod?.Invoke(bodyCam, Array.Empty<object>()) as Camera;
                if (camera != null && camera.enabled != enabled)
                    camera.enabled = enabled;
            }
            catch
            {
            }
        }

        private static void ApplyBodyCamRenderBudget(object bodyCam)
        {
            if (bodyCam == null) return;

            if (_framerateProperty == null || !_framerateProperty.CanWrite)
            {
                if (!_framerateUnavailableLogged)
                {
                    _framerateUnavailableLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV] OpenBodyCams framerate property unavailable; focus render cap was not applied.");
                }
                return;
            }

            float requestedHz = SurveillanceBootstrap.Config?.BodycamRenderHz != null
                ? SurveillanceBootstrap.Config.BodycamRenderHz.Value
                : 15f;
            float renderHz = Mathf.Clamp(requestedHz, 5f, 30f);
            try
            {
                object currentValue = _framerateProperty.GetValue(bodyCam);
                float currentHz = currentValue is float value ? value : float.NaN;
                if (float.IsNaN(currentHz) || float.IsInfinity(currentHz) || !Mathf.Approximately(currentHz, renderHz))
                    _framerateProperty.SetValue(bodyCam, renderHz);

                if (!Mathf.Approximately(_lastAppliedBodycamRenderHz, renderHz))
                {
                    _lastAppliedBodycamRenderHz = renderHz;
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] OpenBodyCams focus rendering capped at {renderHz:0.#} Hz; " +
                        "camera remains disabled outside the active CCTV-owned bodycam feed.");
                }
            }
            catch (Exception ex)
            {
                if (!_framerateUnavailableLogged)
                {
                    _framerateUnavailableLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] OpenBodyCams focus render cap failed: {ex.Message}");
                }
            }
        }

        private static void SetForceEnableCamera(object bodyCam, bool active)
        {
            try
            {
                _forceEnableCameraProperty?.SetValue(bodyCam, active);
            }
            catch
            {
            }
        }

        private static void SuppressOverlayMeshes()
        {
            if (Time.unscaledTime < _nextOverlaySuppressAt) return;
            _nextOverlaySuppressAt = Time.unscaledTime + OverlaySuppressIntervalSeconds;

            // OBC parents BodyCamOverlayMesh(Clone) under MonitorWall — walk that one
            // transform instead of every MeshRenderer in the scene (the full scan cost
            // ~20ms per pass on a moon with a facility loaded).
            if (_monitorWallTransform == null)
            {
                GameObject wall = GameObject.Find("Environment/HangarShip/ShipModels2b/MonitorWall");
                _monitorWallTransform = wall != null ? wall.transform : null;
                if (_monitorWallTransform == null) return;
            }

            // #599: OBC parents the overlay next to the screen it is bound to.
            // Under GeneralImprovements' UseBetterMonitors that screen sits at
            // MonitorWall/MonitorGroup(Clone)/Monitors/..., so the overlay is a
            // grandchild and the old direct-children loop walked straight past
            // it. Recurse instead — the MonitorWall subtree is small either way.
            SuppressOverlayMeshesUnder(_monitorWallTransform);
        }

        private static void SuppressOverlayMeshesUnder(Transform parent)
        {
            foreach (Transform child in parent)
            {
                if (child == null) continue;
                if (child.name != null &&
                    child.name.StartsWith("BodyCamOverlayMesh", StringComparison.OrdinalIgnoreCase))
                {
                    MeshRenderer renderer = child.GetComponent<MeshRenderer>();
                    if (renderer != null) renderer.enabled = false;
                    if (child.gameObject.activeSelf) child.gameObject.SetActive(false);
                    continue;
                }

                SuppressOverlayMeshesUnder(child);
            }
        }

        private static Material TryReadRendererMaterial(Renderer renderer, int materialIndex)
        {
            if (renderer == null || materialIndex < 0) return null;
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materialIndex >= materials.Length) return null;
            return materials[materialIndex];
        }

        private static bool ShouldRequestShipSystemsRebind(Renderer renderer, int materialIndex, Material currentMaterial)
        {
            if (renderer == null || materialIndex < 0) return false;
            if (currentMaterial == null) return false;
            if (RendererSlotLooksLikeShipPower(renderer, materialIndex)) return false;
            if (ReferenceEquals(renderer, _lastShipSystemsRebindRenderer) &&
                materialIndex == _lastShipSystemsRebindMaterialIndex &&
                currentMaterial == _lastShipSystemsRebindMaterial)
            {
                return false;
            }
            if (Time.unscaledTime < _nextShipSystemsRebindAt) return false;

            _nextShipSystemsRebindAt = Time.unscaledTime + ShipSystemsRebindIntervalSeconds;
            _lastShipSystemsRebindRenderer = renderer;
            _lastShipSystemsRebindMaterialIndex = materialIndex;
            _lastShipSystemsRebindMaterial = currentMaterial;
            return true;
        }

        private static bool RendererSlotLooksLikeShipPower(Renderer renderer, int materialIndex)
        {
            Material material = TryReadRendererMaterial(renderer, materialIndex);
            if (material == null) return false;

            string materialName = material.name ?? string.Empty;
            if (materialName.IndexOf("LGUShipPower", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return TextureLooksLikeShipPower(material.mainTexture)
                || TextureLooksLikeShipPower(GetTextureIfExists(material, "_UnlitColorMap"))
                || TextureLooksLikeShipPower(GetTextureIfExists(material, "_BaseColorMap"))
                || TextureLooksLikeShipPower(GetTextureIfExists(material, "_BaseMap"))
                || TextureLooksLikeShipPower(GetTextureIfExists(material, "_EmissiveColorMap"));
        }

        private static bool MaterialLooksLikeBodyCam(Material material)
        {
            if (material == null) return false;
            string name = material.name ?? string.Empty;
            if (name.IndexOf("BodyCamMaterial", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("OpenBodyCams", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return TextureLooksLikeBodyCam(material.mainTexture)
                || TextureLooksLikeBodyCam(GetTextureIfExists(material, "_UnlitColorMap"))
                || TextureLooksLikeBodyCam(GetTextureIfExists(material, "_BaseColorMap"))
                || TextureLooksLikeBodyCam(GetTextureIfExists(material, "_BaseMap"))
                || TextureLooksLikeBodyCam(GetTextureIfExists(material, "_EmissiveColorMap"));
        }

        private static Texture GetTextureIfExists(Material material, string propertyName)
        {
            if (material == null || string.IsNullOrEmpty(propertyName) || !material.HasProperty(propertyName))
                return null;
            try
            {
                return material.GetTexture(propertyName);
            }
            catch
            {
                return null;
            }
        }

        private static bool TextureLooksLikeShipPower(Texture texture)
        {
            return texture != null
                && texture.name != null
                && texture.name.IndexOf("LGUShipSystems_PowerMonitorRT", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TextureLooksLikeBodyCam(Texture texture)
        {
            return texture != null
                && texture.name != null
                && texture.name.IndexOf("BodyCam", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void TrySetRendererMaterial(Renderer renderer, int materialIndex, Material material)
        {
            if (renderer == null || material == null || materialIndex < 0) return;
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materialIndex >= materials.Length) return;
            if (materials[materialIndex] == material) return;
            materials[materialIndex] = material;
            renderer.sharedMaterials = materials;
        }

        private static int ReadIntField(FieldInfo field, object instance, int fallback)
        {
            try
            {
                object value = field?.GetValue(instance);
                return value is int intValue ? intValue : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static void SetLastFocusTexture(Texture texture)
        {
            _lastFocusTexture = texture;
        }

        private static void Resolve()
        {
            if (_resolved && (_apiBodyCamType != null || _shipObjectsType != null || _shipUpgradesType != null || _bodyCamType != null))
                return;

            _resolved = true;
            if (!IsLoaded) return;

            _apiBodyCamType = Type.GetType("OpenBodyCams.API.BodyCam, OpenBodyCams", throwOnError: false);
            _shipObjectsType = Type.GetType("OpenBodyCams.ShipObjects, OpenBodyCams", throwOnError: false);
            _shipUpgradesType = Type.GetType("OpenBodyCams.ShipUpgrades, OpenBodyCams", throwOnError: false);
            _bodyCamType = Type.GetType("OpenBodyCams.BodyCamComponent, OpenBodyCams", throwOnError: false)
                ?? Type.GetType("OpenBodyCams.Components.BodyCamComponent, OpenBodyCams", throwOnError: false);

            BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _mainBodyCamProperty = _apiBodyCamType?.GetProperty("MainBodyCam", staticFlags);
            _createBodyCamMethod = ResolveCreateBodyCamMethod(_apiBodyCamType, staticFlags);
            _shipObjectsMainBodyCamField = _shipObjectsType?.GetField("MainBodyCam", staticFlags);
            _bodyCamUnlockableIsPlacedField = _shipUpgradesType?.GetField("BodyCamUnlockableIsPlaced", staticFlags);
            if (_bodyCamType != null)
                ResolveBodyCamMembers(_bodyCamType);
            EnsureHarmonyPatch();
        }

        private static void ResolveBodyCamMembers(Type bodyCamType)
        {
            if (bodyCamType == null) return;
            _bodyCamType = bodyCamType;

            BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _forceEnableCameraProperty = bodyCamType.GetProperty("ForceEnableCamera", instanceFlags);
            _framerateProperty = bodyCamType.GetProperty("Framerate", instanceFlags);
            _setTargetToPlayerMethod = bodyCamType.GetMethod("SetTargetToPlayer", instanceFlags, null, new[] { typeof(PlayerControllerB) }, null);
            _getCameraMethod = bodyCamType.GetMethod("GetCamera", instanceFlags);
            _updateSettingsMethod = bodyCamType.GetMethod("UpdateSettings", instanceFlags);
            _ensureMaterialsExistMethod = bodyCamType.GetMethod("EnsureMaterialsExist", instanceFlags);
            _cameraField = bodyCamType.GetField("Camera", instanceFlags);
            _currentTargetProperty = bodyCamType.GetProperty("CurrentTarget", instanceFlags);
            _currentActualTargetField = bodyCamType.GetField("currentActualTarget", instanceFlags);
            _monitorRendererField = bodyCamType.GetField("MonitorRenderer", instanceFlags);
            _monitorMaterialIndexField = bodyCamType.GetField("MonitorMaterialIndex", instanceFlags);
            _monitorDisabledMaterialField = bodyCamType.GetField("MonitorDisabledMaterial", instanceFlags);
            _monitorOnMaterialField = bodyCamType.GetField("MonitorOnMaterial", instanceFlags);
            _monitorOnMaterialProperty = bodyCamType.GetProperty("MonitorOnMaterial", instanceFlags);
            EnsureHarmonyPatch();
        }

        private static MethodInfo ResolveCreateBodyCamMethod(Type apiType, BindingFlags staticFlags)
        {
            if (apiType == null)
                return null;

            MethodInfo[] methods = apiType.GetMethods(staticFlags);
            for (int index = 0; index < methods.Length; index++)
            {
                MethodInfo candidate = methods[index];
                if (!string.Equals(candidate.Name, "CreateBodyCam", StringComparison.Ordinal))
                    continue;
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length == 4
                    && parameters[0].ParameterType == typeof(GameObject)
                    && parameters[1].ParameterType == typeof(Renderer))
                    return candidate;
            }

            return null;
        }

        private static void EnsureHarmonyPatch()
        {
            EnsureScreenMaterialPatch();
            EnsureMapCameraConditionsPatch();
            EnsureMainBodyCamSettingsPatch();
        }

        private static void EnsureMapCameraConditionsPatch()
        {
            if (_mapCameraConditionsPatched || _mapCameraConditionsPatchUnavailable)
                return;

            try
            {
                BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                MethodInfo target = typeof(ManualCameraRenderer).GetMethod(
                    "MeetsCameraEnabledConditions",
                    instanceFlags,
                    binder: null,
                    types: new[] { typeof(PlayerControllerB) },
                    modifiers: null);
                MethodInfo postfix = typeof(OpenBodyCamsCompat).GetMethod(
                    nameof(GateOpenBodyCamsMapCameraPostfix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (target == null || postfix == null)
                {
                    LogMapCameraConditionsPatchUnavailableOnce(
                        target == null
                            ? "ManualCameraRenderer.MeetsCameraEnabledConditions target was not found"
                            : "map-camera observability postfix was not found");
                    return;
                }

                _harmony = _harmony ?? new Harmony(SurveillancePluginInfo.PLUGIN_GUID + ".OpenBodyCamsCompat");
                var postfixPatch = new HarmonyMethod(postfix)
                {
                    priority = Priority.Last,
                    after = new[] { PLUGIN_GUID },
                };
                _harmony.Patch(target, postfix: postfixPatch);
                _mapCameraConditionsPatched = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] OpenBodyCams vanilla map-camera observability guard patched.");
            }
            catch (Exception ex)
            {
                LogMapCameraConditionsPatchUnavailableOnce(
                    $"patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static ManualCameraRenderer _headMountedRenderer;

        // #303 — vanilla v70's HeadMountedCamera is driven by its own
        // ManualCameraRenderer manual-render path, so cam.enabled sweeps never see
        // it (the 2026-08-05 follow-up session still showed 67-166 renders per
        // ~700-frame window after the enabled-state guard landed). Until the CCTV
        // Terminal is purchased the OBC integration must be inert and the rig has
        // no visible consumer; refuse the render at the source.
        private static bool IsHeadMountedRenderer(ManualCameraRenderer renderer)
        {
            if (_headMountedRenderer != null)
                return ReferenceEquals(renderer, _headMountedRenderer);
            Camera cam = renderer.cam != null ? renderer.cam : renderer.mapCamera;
            if (cam != null && string.Equals(cam.name, "HeadMountedCamera", StringComparison.Ordinal))
            {
                _headMountedRenderer = renderer;
                return true;
            }
            return false;
        }

        private static void GateOpenBodyCamsMapCameraPostfix(
            ManualCameraRenderer __instance,
            PlayerControllerB player,
            ref bool __result)
        {
            if (!__result || __instance == null)
                return;

            if (IsHeadMountedRenderer(__instance) && !ShouldShowFocusSlot())
            {
                __result = false;
                return;
            }

            if (player == null)
                return;

            StartOfRound startOfRound = StartOfRound.Instance;
            if (startOfRound == null || __instance != startOfRound.mapScreen)
                return;

            // OpenBodyCams turns a false vanilla result back on whenever its terminal UI
            // object remains active. That object can stay active after the local player
            // leaves the ship, which makes the vanilla MapCamera render every gameplay
            // frame while nobody can see it. Reapply only vanilla's hard disable/location
            // conditions; keep OBC's intended terminal/monitor visibility override while
            // the radar is genuinely observable, and preserve explicit always-on cameras.
            bool disabledByVanilla = __instance.currentCameraDisabled;
            if (!startOfRound.inShipPhase)
            {
                disabledByVanilla |= !player.isInHangarShipRoom && !__instance.overrideRadarCameraOnAlways;
                disabledByVanilla |= !startOfRound.shipDoorsEnabled
                    && (startOfRound.currentPlanetPrefab == null || !startOfRound.currentPlanetPrefab.activeSelf);
            }

            if (!disabledByVanilla)
                return;

            __result = false;
            if (_mapCameraOffscreenGateLogged)
                return;

            _mapCameraOffscreenGateLogged = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Suppressed OpenBodyCams' stale terminal override for the off-screen vanilla MapCamera.");
        }

        private static void LogMapCameraConditionsPatchUnavailableOnce(string reason)
        {
            _mapCameraConditionsPatchUnavailable = true;
            if (_mapCameraConditionsPatchLogged) return;
            _mapCameraConditionsPatchLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV] OpenBodyCams map-camera observability guard unavailable; no-op: {reason}.");
        }

        private static void EnsureScreenMaterialPatch()
        {
            if (_screenMaterialPatched || _bodyCamType == null) return;

            try
            {
                BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                MethodInfo target = _bodyCamType.GetMethod("UpdateScreenMaterial", instanceFlags);
                MethodInfo prefix = typeof(OpenBodyCamsCompat).GetMethod(
                    nameof(SuppressUpdateScreenMaterialPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (target == null || prefix == null)
                    return;

                _harmony = _harmony ?? new Harmony(SurveillancePluginInfo.PLUGIN_GUID + ".OpenBodyCamsCompat");
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                _screenMaterialPatched = true;
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] OpenBodyCams monitor material writer patched for LethalCCTV-owned routing.");
            }
            catch (Exception ex)
            {
                if (!_screenMaterialPatchLogged)
                {
                    _screenMaterialPatchLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] OpenBodyCams monitor material patch failed: {ex.Message}");
                }
            }
        }

        private static void EnsureMainBodyCamSettingsPatch()
        {
            if (_mainBodyCamSettingsPatched || _mainBodyCamSettingsPatchUnavailable)
                return;

            if (_shipObjectsType == null)
            {
                LogMainBodyCamSettingsPatchUnavailableOnce("ShipObjects type was not found");
                return;
            }

            try
            {
                BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                MethodInfo target = _shipObjectsType.GetMethod(
                    "UpdateMainBodyCamSettings",
                    staticFlags,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);
                MethodInfo prefix = typeof(OpenBodyCamsCompat).GetMethod(
                    nameof(EnsureMainBodyCamMaterialPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo postfix = typeof(OpenBodyCamsCompat).GetMethod(
                    nameof(ApplyMainBodyCamRenderBudgetPostfix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (target == null || prefix == null || postfix == null)
                {
                    string missing = target == null
                        ? "UpdateMainBodyCamSettings target was not found"
                        : prefix == null
                            ? "initialization-guard prefix was not found"
                            : "render-budget postfix was not found";
                    LogMainBodyCamSettingsPatchUnavailableOnce(missing);
                    return;
                }

                _harmony = _harmony ?? new Harmony(SurveillancePluginInfo.PLUGIN_GUID + ".OpenBodyCamsCompat");
                _harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                _mainBodyCamSettingsPatched = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] OpenBodyCams MainBodyCam initialization and render-budget guards patched.");
            }
            catch (Exception ex)
            {
                LogMainBodyCamSettingsPatchUnavailableOnce(
                    $"patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void EnsureMainBodyCamMaterialPrefix()
        {
            try
            {
                if (!TryGetMainBodyCamForInitializationGuard(out object bodyCam) || bodyCam == null)
                    return;

                bool bodycamRouteAvailable = ShouldShowFocusSlot();
                SetOpenBodyCamsReceiverAvailable(bodycamRouteAvailable);
                if (!bodycamRouteAvailable)
                {
                    SetForceEnableCamera(bodyCam, false);
                    SetBodyCamBehaviourEnabled(bodyCam, false);
                    SetBodyCamCameraEnabled(bodyCam, false);
                    LogOwnershipGateOnce(bodycamRouteAvailable: false);
                }

                if (_bodyCamType == null || !_bodyCamType.IsInstanceOfType(bodyCam))
                    ResolveBodyCamMembers(bodyCam.GetType());

                if (!TryReadMonitorOnMaterial(bodyCam, out Material monitorOnMaterial))
                    return;
                if (monitorOnMaterial != null)
                    return;

                string repairPath = "EnsureMaterialsExist";
                Exception ensureFailure = null;
                if (_ensureMaterialsExistMethod != null)
                {
                    try
                    {
                        _ensureMaterialsExistMethod.Invoke(bodyCam, Array.Empty<object>());
                    }
                    catch (Exception ex)
                    {
                        ensureFailure = ex;
                    }
                }

                if (!TryReadMonitorOnMaterial(bodyCam, out monitorOnMaterial))
                    return;

                if (monitorOnMaterial == null)
                {
                    repairPath = _ensureMaterialsExistMethod == null
                        ? "direct fallback (EnsureMaterialsExist unavailable)"
                        : ensureFailure == null
                            ? "direct fallback (EnsureMaterialsExist returned without a material)"
                            : $"direct fallback (EnsureMaterialsExist failed: {ensureFailure.GetType().Name})";

                    if (!TryAssignFallbackMonitorMaterial(bodyCam, out string failure))
                    {
                        LogMainBodyCamMaterialGuardFailureOnce(failure);
                        return;
                    }
                }

                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] OpenBodyCams initialization guard fired: repaired missing " +
                    $"MainBodyCam.MonitorOnMaterial via {repairPath} before UpdateMainBodyCamSettings.");
            }
            catch (Exception ex)
            {
                LogMainBodyCamMaterialGuardFailureOnce(
                    $"unexpected guard failure: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ApplyMainBodyCamRenderBudgetPostfix()
        {
            try
            {
                if (TryGetMainBodyCamForInitializationGuard(out object bodyCam) && bodyCam != null)
                    ApplyBodyCamRenderBudget(bodyCam);
            }
            catch (Exception ex)
            {
                if (!_framerateUnavailableLogged)
                {
                    _framerateUnavailableLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] OpenBodyCams post-settings render cap failed: {ex.Message}");
                }
            }
        }

        private static void LogOwnershipGateOnce(bool bodycamRouteAvailable)
        {
            if (bodycamRouteAvailable || _ownershipGateLogged) return;
            _ownershipGateLogged = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] CCTV Terminal is unpurchased; OpenBodyCams component and render camera forced off.");
        }

        private static bool TryGetMainBodyCamForInitializationGuard(out object bodyCam)
        {
            bodyCam = null;
            if (_mainBodyCamProperty == null && _shipObjectsMainBodyCamField == null)
            {
                LogMainBodyCamMaterialGuardFailureOnce("MainBodyCam accessor was not found");
                return false;
            }

            try
            {
                bodyCam = _mainBodyCamProperty?.GetValue(null)
                    ?? _shipObjectsMainBodyCamField?.GetValue(null);
                return true;
            }
            catch (Exception ex)
            {
                LogMainBodyCamMaterialGuardFailureOnce(
                    $"MainBodyCam read failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool TryReadMonitorOnMaterial(object bodyCam, out Material material)
        {
            material = null;
            if (_monitorOnMaterialField == null && _monitorOnMaterialProperty == null)
            {
                LogMainBodyCamMaterialGuardFailureOnce("MonitorOnMaterial member was not found");
                return false;
            }

            try
            {
                object value = _monitorOnMaterialField != null
                    ? _monitorOnMaterialField.GetValue(bodyCam)
                    : _monitorOnMaterialProperty.GetValue(bodyCam);
                material = value as Material;
                if (value != null && material == null)
                {
                    LogMainBodyCamMaterialGuardFailureOnce(
                        $"MonitorOnMaterial had unexpected type {value.GetType().FullName}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LogMainBodyCamMaterialGuardFailureOnce(
                    $"MonitorOnMaterial read failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool TryAssignFallbackMonitorMaterial(object bodyCam, out string failure)
        {
            failure = null;
            Material fallback = null;
            try
            {
                Shader shader = Shader.Find("HDRP/Unlit");
                if (shader == null)
                {
                    failure = "fallback shader HDRP/Unlit was not found";
                    return false;
                }

                fallback = new Material(shader) { name = "BodyCamMaterial" };
                if (fallback.HasProperty("_AlbedoAffectEmissive"))
                    fallback.SetFloat("_AlbedoAffectEmissive", 1f);
                if (fallback.HasProperty("_EmissiveColor"))
                    fallback.SetColor("_EmissiveColor", Color.white);

                if (_monitorOnMaterialField != null)
                {
                    _monitorOnMaterialField.SetValue(bodyCam, fallback);
                }
                else if (_monitorOnMaterialProperty?.CanWrite == true)
                {
                    _monitorOnMaterialProperty.SetValue(bodyCam, fallback);
                }
                else
                {
                    failure = "MonitorOnMaterial was not writable for fallback assignment";
                    UnityEngine.Object.Destroy(fallback);
                    return false;
                }

                if (!TryReadMonitorOnMaterial(bodyCam, out Material assigned) || assigned == null)
                {
                    failure = "fallback MonitorOnMaterial assignment did not persist";
                    UnityEngine.Object.Destroy(fallback);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                if (fallback != null)
                    UnityEngine.Object.Destroy(fallback);
                failure = $"fallback assignment failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static void LogMainBodyCamSettingsPatchUnavailableOnce(string reason)
        {
            _mainBodyCamSettingsPatchUnavailable = true;
            if (_mainBodyCamSettingsPatchLogged) return;
            _mainBodyCamSettingsPatchLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV] OpenBodyCams MainBodyCam material initialization guard unavailable; no-op: {reason}.");
        }

        private static void LogMainBodyCamMaterialGuardFailureOnce(string reason)
        {
            if (_mainBodyCamMaterialGuardFailureLogged) return;
            _mainBodyCamMaterialGuardFailureLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV] OpenBodyCams MainBodyCam material initialization guard could not repair the material; no-op: {reason}.");
        }

        private static bool SuppressUpdateScreenMaterialPrefix(object __instance)
        {
            if (!ShouldSuppressScreenMaterialUpdate(__instance))
                return true;

            SuppressShipMonitor(__instance);
            LogScreenMaterialSuppressed();
            return false;
        }

        private static bool ShouldSuppressScreenMaterialUpdate(object bodyCam)
        {
            if (!IsLoaded || bodyCam == null)
                return false;

            object mainBodyCam = GetMainBodyCam();
            if (ReferenceEquals(mainBodyCam, bodyCam))
                return true;

            // Ask OBC which renderer it is actually bound to. OBC has native
            // GeneralImprovements support: with UseBetterMonitors on it rebinds
            // MonitorRenderer to one of GI's replacement screens, whose
            // GameObject is not named "Cube.001" — so the legacy name check
            // below silently stopped suppressing and OBC kept writing over the
            // ship monitor CCTV owns (#599). GI parents its group under the same
            // MonitorWall, so the ancestor test covers vanilla and GI alike.
            if (_monitorRendererField != null)
            {
                try
                {
                    if (_monitorRendererField.GetValue(bodyCam) is Renderer bound
                        && bound != null
                        && IsUnderMonitorWall(bound.transform))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            // Fallback for an OBC build whose MonitorRenderer we could not read,
            // or a bound renderer already cleared by a previous suppression pass.
            if (bodyCam is Component component)
            {
                string name = component.gameObject != null ? component.gameObject.name : string.Empty;
                if (string.Equals(name, "Cube.001", StringComparison.OrdinalIgnoreCase)
                    && GetTransformPath(component.transform).IndexOf("MonitorWall", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (IsUnderMonitorWall(component.transform))
                    return true;
            }

            return false;
        }

        private static bool IsUnderMonitorWall(Transform transform)
        {
            Transform cursor = transform;
            while (cursor != null)
            {
                if (string.Equals(cursor.name, "MonitorWall", StringComparison.OrdinalIgnoreCase))
                    return true;
                cursor = cursor.parent;
            }
            return false;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name ?? string.Empty;
            Transform cursor = transform.parent;
            while (cursor != null)
            {
                path = (cursor.name ?? string.Empty) + "/" + path;
                cursor = cursor.parent;
            }

            return path;
        }

        private static void LogScreenMaterialSuppressed()
        {
            if (_lastScreenMaterialSuppressLogAt > 0f && Time.unscaledTime - _lastScreenMaterialSuppressLogAt < 5f)
                return;

            _lastScreenMaterialSuppressLogAt = Time.unscaledTime;
            SurveillanceBootstrap.Log?.LogDebug("[LethalCCTV] Blocked OpenBodyCams monitor material write for main bodycam.");
        }

        private static void LogMissingOnce()
        {
            if (_missingLogged) return;
            _missingLogged = true;
            SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] OpenBodyCams is loaded, but MainBodyCam is not ready yet.");
        }
    }
}
