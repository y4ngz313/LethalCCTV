using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Security;
namespace Y4NGZCompany.Facility.Stash
{
    public sealed class CompanyStashController : NetworkBehaviour, ICompanyStashKeypadClient
    {
        public const int CodeDigits = 4;
        internal const float BodyHeightMultiplier = 1.15f;
        internal const float BodyHeight = 2f * BodyHeightMultiplier;

        private const int ScanNodeLayer = 22;
        private const float ResultDisplaySeconds = 1.15f;
        private const float LootSpawnDelaySeconds = 0.65f;
        private const float LootLateralOffsetStep = 0.25f;
        private const float AuthoredCabinetScaleY = 1.0035087f;
        private const string AccessDeniedText = "Denied";
        private const string AccessGrantedText = "Granted";
        private static readonly Vector3 KeypadMountedRootLocalPosition = new Vector3(-0.17f, 1.45f, 0.26f);
        private static readonly Vector3 GoldBarStableRootLocalPosition = new Vector3(0f, 0.75f, -0.08f);

        private readonly NetworkVariable<bool> _isUnlocked = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private int _assignedCode = -1;
        private string _displayName = "Company Stash";
        private string _localInput = string.Empty;
        private float _resultDisplayUntil;

        private CompanyStashKeypadInteractor _keypad;
        private Transform _keypadVisualRoot;
        private bool _keypadScanNodeCleanupComplete;
        private Animator _animator;
        private Animation _legacyAnimation;
        private Transform _leftDoor;
        private Transform _rightDoor;
        private Transform _goldBarSpawnAnchor;
        private Quaternion _leftClosedRotation;
        private Quaternion _rightClosedRotation;
        private bool _capturedDoorRotations;
        private bool _fallbackOpening;
        private float _fallbackOpenProgress;
        private bool _openVisualStarted;
        private bool _lootSpawned;
        private bool _bodySizeInitialized;
        private bool _keypadMounted;
        private bool _localSubmitPending;
        private Coroutine _lootSpawnRoutine;

        private sealed class StashLootEntry
        {
            internal readonly string ItemName;
            internal readonly int Weight;
            internal readonly int Count;
            internal readonly Item Item;

            internal StashLootEntry(string itemName, int weight, int count, Item item)
            {
                ItemName = itemName;
                Weight = weight;
                Count = count;
                Item = item;
            }
        }

        public bool IsUnlocked => _isUnlocked.Value;
        public string DisplayName => string.IsNullOrWhiteSpace(_displayName) ? "Company Stash" : _displayName;
        internal bool HasLocalKeypadInput => !string.IsNullOrEmpty(_localInput);

        public void InitializeAssignedCode(int code, string displayName)
        {
            _assignedCode = Mathf.Clamp(code, 0, 9999);
            if (!string.IsNullOrWhiteSpace(displayName))
                _displayName = displayName;
        }

        private void Awake()
        {
            EnsureRuntimeComponents();
        }

        private void Start()
        {
            EnsureRuntimeComponents();
            ApplyUnlockedStateLocal(force: true);
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            EnsureRuntimeComponents();
            CctvSupportState.RegisterRuntimeStash(this);
            _isUnlocked.OnValueChanged += OnUnlockedChanged;

            if (IsServer && _assignedCode < 0)
            {
                if (CctvSupportState.TryGetAssignedStashCode(0, out int assignedCode))
                    _assignedCode = assignedCode;
            }

            ApplyUnlockedStateLocal(force: true);
        }

        public override void OnNetworkDespawn()
        {
            if (_lootSpawnRoutine != null)
            {
                StopCoroutine(_lootSpawnRoutine);
                _lootSpawnRoutine = null;
            }

            CctvSupportState.UnregisterRuntimeStash(this);
            _isUnlocked.OnValueChanged -= OnUnlockedChanged;
            base.OnNetworkDespawn();
        }

        private void Update()
        {
            EnsureRuntimeComponents();
            UpdateFallbackOpenAnimation();

            if (_resultDisplayUntil > 0f && Time.time >= _resultDisplayUntil && !IsUnlocked)
            {
                _resultDisplayUntil = 0f;
                UpdateKeypadEntryDisplay();
            }
        }

        internal void PressKeypadButtonLocally(int button, PlayerControllerB player)
        {
            EnsureRuntimeComponents();

            if (IsUnlocked)
            {
                ShowKeypadStatus(AccessGrantedText, CompanyStashKeypadDisplayState.Granted, ResultDisplaySeconds);
                return;
            }
            if (_localSubmitPending)
                return;

            if (button == CompanyStashKeypadInteractor.EnterButton)
            {
                SubmitLocalInput();
                return;
            }

            int digit = Mathf.Clamp(button, 0, 9);
            if (_localInput.Length >= CodeDigits)
                _localInput = string.Empty;

            _localInput += digit.ToString();
            UpdateKeypadEntryDisplay();
            if (_localInput.Length >= CodeDigits)
                SubmitLocalInput();
        }

        internal string GetKeypadButtonTip(int button)
        {
            if (button == CompanyStashKeypadInteractor.EnterButton)
                return "Press [Enter]";

            return $"Press [{Mathf.Clamp(button, 0, 9)}]";
        }

        internal string GetKeypadDisabledTip()
        {
            return IsUnlocked ? "[ Granted ]" : "[ Keypad unavailable ]";
        }

        // ICompanyStashKeypadClient (#393). The generic keypad hardware lives in Y4NGZCore
        // and no longer names its owners; these forward its questions to the members the
        // stash already had, so in-game behaviour is unchanged. Explicit implementations
        // keep the members internal - nothing outside the family calls them by interface.
        bool ICompanyStashKeypadClient.IsKeypadInteractable() => IsKeypadInteractable();

        bool ICompanyStashKeypadClient.HasLocalKeypadInput => HasLocalKeypadInput;

        string ICompanyStashKeypadClient.GetKeypadButtonTip(int button) => GetKeypadButtonTip(button);

        string ICompanyStashKeypadClient.GetKeypadDisabledTip() => GetKeypadDisabledTip();

        void ICompanyStashKeypadClient.PressKeypadButtonLocally(int button, PlayerControllerB player) =>
            PressKeypadButtonLocally(button, player);

        // Pressing a locked stash's keypad reports the vault's own state, exactly as the
        // interactor used to inline for this owner.
        string ICompanyStashKeypadClient.GetKeypadLockedDisplayText() => IsUnlocked ? "GRANTED" : "LOCKED";

        CompanyStashKeypadDisplayState ICompanyStashKeypadClient.GetKeypadLockedDisplayState() =>
            IsUnlocked ? CompanyStashKeypadDisplayState.Granted : CompanyStashKeypadDisplayState.Denied;

        // The stash prefab does not always ship a display or a full button set, and a
        // synthesized one reads correctly on it, so both fallbacks stay enabled here.
        bool ICompanyStashKeypadClient.AllowsFallbackDisplayText => true;

        bool ICompanyStashKeypadClient.AllowsFallbackButtonVisuals => true;

        string ICompanyStashKeypadClient.MissingButtonVisualsWarning => null;

        internal bool IsKeypadInteractable()
        {
            return !IsUnlocked;
        }

        private void SubmitLocalInput()
        {
            if (_localInput.Length != CodeDigits)
            {
                _localInput = string.Empty;
                ShowKeypadStatus(AccessDeniedText, CompanyStashKeypadDisplayState.Denied, ResultDisplaySeconds);
                return;
            }

            if (!int.TryParse(_localInput, out int code))
            {
                _localInput = string.Empty;
                ShowKeypadStatus(AccessDeniedText, CompanyStashKeypadDisplayState.Denied, ResultDisplaySeconds);
                return;
            }

            _localSubmitPending = true;
            SubmitCodeServerRpc(code);
        }

        [ServerRpc(RequireOwnership = false)]
        private void SubmitCodeServerRpc(int code, ServerRpcParams rpcParams = default)
        {
            if (!IsServer || _isUnlocked.Value)
                return;

            if (!IsSenderCloseEnough(rpcParams.Receive.SenderClientId))
            {
                SendKeypadResultClientRpc(false, AccessDeniedText, BuildTargetClientParams(rpcParams.Receive.SenderClientId));
                return;
            }

            bool granted = _assignedCode >= 0 && Mathf.Clamp(code, 0, 9999) == _assignedCode;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' keypad submit code={code:D4} assigned={(_assignedCode >= 0 ? _assignedCode.ToString("D4") : "none")} granted={granted}.");
            if (!granted)
            {
                SendKeypadResultClientRpc(false, AccessDeniedText, BuildTargetClientParams(rpcParams.Receive.SenderClientId));
                return;
            }

            _isUnlocked.Value = true;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' unlocked with code {_assignedCode:D4}.");
            SendKeypadResultClientRpc(true, AccessGrantedText, BuildTargetClientParams(rpcParams.Receive.SenderClientId));
            ApplyUnlockedStateLocal(force: true);
            BeginSpawnGoldBarLootServer();
        }

        [ServerRpc(RequireOwnership = false)]
        public void UnlockFromRemoteHackServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsServer || _isUnlocked.Value)
                return;
            if (SurveillanceBootstrap.Config != null && !SurveillanceBootstrap.Config.AllowRemoteHacking.Value)
                return;

            if (!IsSenderWithinRemoteHackRange(rpcParams.Receive.SenderClientId))
                return;

            _isUnlocked.Value = true;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' unlocked via remote hack.");
            SendKeypadResultClientRpc(true, AccessGrantedText, BuildTargetClientParams(rpcParams.Receive.SenderClientId));
            ApplyUnlockedStateLocal(force: true);
            BeginSpawnGoldBarLootServer();
        }

        [ClientRpc]
        private void SendKeypadResultClientRpc(bool granted, string message, ClientRpcParams clientRpcParams = default)
        {
            _localInput = string.Empty;
            _localSubmitPending = false;
            ShowKeypadStatus(
                string.IsNullOrWhiteSpace(message) ? (granted ? AccessGrantedText : AccessDeniedText) : message,
                granted ? CompanyStashKeypadDisplayState.Granted : CompanyStashKeypadDisplayState.Denied,
                granted ? 0f : ResultDisplaySeconds);
        }

        private static ClientRpcParams BuildTargetClientParams(ulong clientId)
        {
            return new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { clientId }
                }
            };
        }

        private void OnUnlockedChanged(bool previous, bool current)
        {
            if (current)
                ApplyUnlockedStateLocal(force: true);
        }

        private void ApplyUnlockedStateLocal(bool force)
        {
            if (!force && !IsUnlocked)
                return;

            if (IsUnlocked)
            {
                _localInput = string.Empty;
                _localSubmitPending = false;
                ShowKeypadStatus(AccessGrantedText, CompanyStashKeypadDisplayState.Granted, 0f);
                StartOpenVisualLocal();
            }
            else
            {
                UpdateKeypadEntryDisplay();
            }

            _keypad?.RefreshButtons();
            UpdateLockerScanNode();
        }

        private void UpdateKeypadEntryDisplay()
        {
            if (_keypad == null)
                return;

            char[] slots = { '_', '_', '_', '_' };
            for (int i = 0; i < Mathf.Min(_localInput.Length, slots.Length); i++)
                slots[i] = _localInput[i];

            _keypad.SetDisplayText($"{slots[0]} {slots[1]} {slots[2]} {slots[3]}", CompanyStashKeypadDisplayState.Normal);
        }

        private void ShowKeypadStatus(string text, CompanyStashKeypadDisplayState state, float seconds)
        {
            _keypad?.SetDisplayText(text, state);
            _resultDisplayUntil = seconds > 0f ? Time.time + seconds : 0f;
        }

        private void EnsureRuntimeComponents()
        {
            EnsureBodySize();

            if (_keypadVisualRoot == null)
                _keypadVisualRoot = FindKeypadVisualRoot();

            if (_keypad == null)
            {
                _keypad = GetComponentInChildren<CompanyStashKeypadInteractor>(true);
                if (_keypad == null)
                {
                    Transform host = _keypadVisualRoot != null ? _keypadVisualRoot : transform;
                    _keypad = host.gameObject.AddComponent<CompanyStashKeypadInteractor>();
                }
            }

            if (_animator == null)
                _animator = FindComponentOutsideKeypad<Animator>();
            if (_legacyAnimation == null)
                _legacyAnimation = FindComponentOutsideKeypad<Animation>();

            if (_leftDoor == null)
                _leftDoor = FindDoorTransform(rightDoor: false)
                            ?? FindChildByNameToken(transform, "door-l")
                            ?? FindChildByNameToken(transform, "door_l")
                            ?? FindChildByNameToken(transform, "leftdoor")
                            ?? FindChildByNameToken(transform, "doorleft");
            if (_rightDoor == null)
                _rightDoor = FindDoorTransform(rightDoor: true)
                             ?? FindChildByNameToken(transform, "door-r")
                             ?? FindChildByNameToken(transform, "door_r")
                             ?? FindChildByNameToken(transform, "rightdoor")
                             ?? FindChildByNameToken(transform, "doorright");
            if (_goldBarSpawnAnchor == null)
                _goldBarSpawnAnchor = FindChildByNameToken(transform, "GoldBarSpawnAnchor")
                                      ?? FindChildByNameToken(transform, "LootSpawnAnchor")
                                      ?? FindChildByNameToken(transform, "LootAnchor");

            CaptureDoorRotations();
            MountKeypadToDoor();
            EnsureScanNodes();
        }

        private void EnsureBodySize()
        {
            if (_bodySizeInitialized)
                return;

            _bodySizeInitialized = true;
            Transform cabinet = transform.Find("Cabinet");
            if (cabinet == null)
                return; // Generated fallback geometry is already sized by the spawner.

            // The shipped Cabinet is floor-pivoted and authored at 2 m with this
            // normalization scale. Assign absolutely: prefab preparation and a
            // clone (or a replacement controller) must never compound the resize.
            Vector3 scale = cabinet.localScale;
            scale.y = AuthoredCabinetScaleY * BodyHeightMultiplier;
            cabinet.localScale = scale;

            BoxCollider collider = GetComponent<BoxCollider>();
            if (collider != null)
            {
                // Preserve the root's X/Z extent and the spawner's 0.08 m padding.
                Vector3 center = collider.center;
                Vector3 size = collider.size;
                center.y = BodyHeight * 0.5f;
                size.y = BodyHeight + 0.08f;
                collider.center = center;
                collider.size = size;
            }
        }

        private void BeginSpawnGoldBarLootServer()
        {
            if (!IsServer || _lootSpawned || _lootSpawnRoutine != null)
                return;

            _lootSpawnRoutine = StartCoroutine(SpawnGoldBarLootDelayedServer());
        }

        private IEnumerator SpawnGoldBarLootDelayedServer()
        {
            yield return new WaitForSeconds(LootSpawnDelaySeconds);
            _lootSpawnRoutine = null;
            SpawnGoldBarLootServer();

            try
            {
                SpawnExtraStashLootServer();
            }
            catch (Exception exception)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Company Stash '{DisplayName}' extra stash loot rolls failed: {exception}");
            }
        }

        private void SpawnGoldBarLootServer()
        {
            if (!IsServer || _lootSpawned)
                return;

            EnsureRuntimeComponents();
            Item goldBar = ResolveGoldBarItem();
            if (goldBar?.spawnPrefab == null)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Company Stash '{DisplayName}' could not spawn loot: vanilla Gold bar item was not found.");
                return;
            }

            GameObject spawned = SpawnStashLootItem(goldBar, 0);
            if (spawned == null)
                return;

            NetworkObject networkObject = spawned.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Company Stash '{DisplayName}' spawned Gold bar loot without a NetworkObject.");
                return;
            }

            _lootSpawned = true;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' spawned Gold bar loot value={spawned.GetComponent<GrabbableObject>()?.scrapValue ?? 0}.");
        }

        private void SpawnExtraStashLootServer()
        {
            if (!IsServer || !_lootSpawned)
                return;

            int rollCount = Mathf.Max(0, CctvModuleConfig.StashLootRolls?.Value ?? 0);
            if (rollCount == 0)
                return;

            List<StashLootEntry> entries = ParseStashLootEntries(CctvModuleConfig.StashLootPool?.Value);
            if (entries.Count == 0)
            {
                CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' has no available stash loot entries; skipping {rollCount} extra roll(s).");
                return;
            }

            int lateralOffsetStep = 1;
            for (int rollIndex = 0; rollIndex < rollCount; rollIndex++)
            {
                StashLootEntry entry = PickWeightedStashLootEntry(entries);
                if (entry == null)
                    continue;

                Item item = entry.Item;

                int spawnedCount = 0;
                for (int instanceIndex = 0; instanceIndex < entry.Count; instanceIndex++)
                {
                    GameObject spawned = SpawnStashLootItem(item, lateralOffsetStep);
                    lateralOffsetStep++;
                    if (spawned?.GetComponent<NetworkObject>() != null)
                        spawnedCount++;
                }

                CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' spawned {spawnedCount}x '{item.itemName}' stash loot for roll {rollIndex + 1}/{rollCount}.");
            }
        }

        private GameObject SpawnStashLootItem(Item item, int lateralOffsetIndex)
        {
            if (item?.spawnPrefab == null)
                return null;

            Vector3 spawnPosition = ResolveLootSpawnPosition(lateralOffsetIndex);
            Quaternion spawnRotation = ResolveLootSpawnRotation();
            Transform propsContainer = StartOfRound.Instance?.propsContainer;
            GameObject spawned = Instantiate(item.spawnPrefab, spawnPosition, spawnRotation, propsContainer);
            if (spawned == null)
                return null;

            if (!spawned.activeSelf)
                spawned.SetActive(true);

            spawned.transform.SetPositionAndRotation(spawnPosition, spawnRotation);
            PrepareStashLoot(spawned, item);

            NetworkObject networkObject = spawned.GetComponent<NetworkObject>();
            if (networkObject != null && !networkObject.IsSpawned)
            {
                networkObject.Spawn();
                if (propsContainer != null)
                    networkObject.TrySetParent(propsContainer);
            }

            return spawned;
        }

        private Vector3 ResolveLootSpawnPosition(int lateralOffsetIndex)
        {
            Vector3 spawnPosition = transform.TransformPoint(GoldBarStableRootLocalPosition);
            if (lateralOffsetIndex <= 0)
                return spawnPosition;

            int offsetMagnitude = (lateralOffsetIndex + 1) / 2;
            float offsetDirection = lateralOffsetIndex % 2 == 0 ? -1f : 1f;
            return spawnPosition + transform.right * (LootLateralOffsetStep * offsetMagnitude * offsetDirection);
        }

        private Quaternion ResolveLootSpawnRotation()
        {
            return transform.rotation * Quaternion.Euler(0f, 90f, 0f);
        }

        private static void PrepareStashLoot(GameObject spawned, Item item)
        {
            if (spawned == null || item == null)
                return;

            GrabbableObject grabbable = spawned.GetComponent<GrabbableObject>();
            if (grabbable == null)
                return;

            grabbable.itemProperties = item;
            grabbable.isInFactory = true;
            grabbable.fallTime = 0f;
            grabbable.hasHitGround = true;
            grabbable.reachedFloorTarget = true;
            grabbable.startFallingPosition = spawned.transform.localPosition;
            grabbable.targetFloorPosition = spawned.transform.localPosition;
            grabbable.isHeld = false;
            grabbable.isPocketed = false;
            grabbable.heldByPlayerOnServer = false;
            grabbable.playerHeldBy = null;

            Rigidbody body = grabbable.propBody != null ? grabbable.propBody : spawned.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.useGravity = false;
                body.isKinematic = true;
                body.constraints = RigidbodyConstraints.FreezeRotation;
            }

            if (item.isScrap && (item.minValue > 0 || item.maxValue > 0))
            {
                int min = Mathf.Max(50, item.minValue);
                int max = Mathf.Max(min + 15, item.maxValue);
                int value = Mathf.RoundToInt(UnityEngine.Random.Range(min, max) * Mathf.Max(1f, RoundManager.Instance != null ? RoundManager.Instance.scrapValueMultiplier : 1f));
                grabbable.SetScrapValue(value);
                AddLootScrapValueToLevel(grabbable.scrapValue);
            }
        }

        private static void AddLootScrapValueToLevel(int value)
        {
            if (RoundManager.Instance != null)
                RoundManager.Instance.totalScrapValueInLevel += Mathf.Max(0, value);
        }

        private static Item ResolveGoldBarItem()
        {
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null || items.Count == 0)
                return null;

            Item named = null;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (!IsSpawnableScrapItem(item))
                    continue;

                if (string.Equals(item.itemName, "Gold bar", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.itemName, "GoldBar", StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }

                if (named == null && Contains(item.itemName, "gold") && Contains(item.itemName, "bar"))
                    named = item;
            }

            return named;
        }

        private List<StashLootEntry> ParseStashLootEntries(string configuredItems)
        {
            var entries = new List<StashLootEntry>();
            if (string.IsNullOrWhiteSpace(configuredItems))
                return entries;

            string[] rawEntries = configuredItems.Split(';');
            for (int i = 0; i < rawEntries.Length; i++)
            {
                string rawEntry = rawEntries[i]?.Trim();
                if (string.IsNullOrWhiteSpace(rawEntry))
                    continue;

                string[] fields = rawEntry.Split('|');
                if (fields.Length != 3
                    || string.IsNullOrWhiteSpace(fields[0])
                    || !int.TryParse(fields[1].Trim(), out int weight)
                    || weight <= 0
                    || !int.TryParse(fields[2].Trim(), out int count)
                    || count <= 0)
                {
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Company Stash '{DisplayName}' ignored malformed stash loot entry '{rawEntry}'; expected name|positive rarity|positive count.");
                    continue;
                }

                string itemName = fields[0].Trim();
                Item item = ResolveStashLootItem(itemName);
                if (item == null)
                {
                    CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' ignored unavailable stash loot item '{itemName}'.");
                    continue;
                }

                entries.Add(new StashLootEntry(itemName, weight, count, item));
            }

            return entries;
        }

        private static StashLootEntry PickWeightedStashLootEntry(List<StashLootEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return null;

            long totalWeight = 0;
            for (int i = 0; i < entries.Count; i++)
                totalWeight += entries[i].Weight;

            if (totalWeight <= 0)
                return null;

            double roll = UnityEngine.Random.value * totalWeight;
            long cursor = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                cursor += entries[i].Weight;
                if (roll < cursor)
                    return entries[i];
            }

            return entries[entries.Count - 1];
        }

        private static Item ResolveStashLootItem(string itemName)
        {
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null || items.Count == 0 || string.IsNullOrWhiteSpace(itemName))
                return null;

            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (IsSpawnableStashLootItem(item)
                    && string.Equals(item.itemName, itemName, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private static bool IsSpawnableScrapItem(Item item)
        {
            return item != null
                   && item.isScrap
                   && item.spawnPrefab != null
                   && item.spawnPrefab.GetComponent<GrabbableObject>() != null;
        }

        private static bool IsSpawnableStashLootItem(Item item)
        {
            return item != null
                   && item.spawnPrefab != null
                   && item.spawnPrefab.GetComponent<GrabbableObject>() != null
                   && item.spawnPrefab.GetComponent<NetworkObject>() != null;
        }

        private static bool Contains(string value, string fragment)
        {
            return !string.IsNullOrEmpty(value)
                   && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void EnsureScanNodes()
        {
            RemoveKeypadScanNodes();
            EnsureScanNode(transform, "CompanyStashScanNode", "Company Stash", IsUnlocked ? "access granted" : "secured", new Vector3(0f, 1.05f * BodyHeightMultiplier, 0f), 0.52f, 12);
        }

        private void RemoveKeypadScanNodes()
        {
            if (_keypadScanNodeCleanupComplete)
                return;

            _keypadScanNodeCleanupComplete = true;
            Transform legacy = FindChildByExactName(transform, "CompanyStashKeypadScanNode");
            if (legacy != null)
                Destroy(legacy.gameObject);

            ScanNodeProperties[] scanNodes = GetComponentsInChildren<ScanNodeProperties>(true);
            for (int i = 0; i < scanNodes.Length; i++)
            {
                ScanNodeProperties scanNode = scanNodes[i];
                if (scanNode == null || (legacy != null && scanNode.transform == legacy))
                    continue;
                if (!IsKeypadDescendant(scanNode.transform)
                    || !string.Equals(scanNode.headerText, "Keypad", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Transform keypadRoot = _keypadVisualRoot != null ? _keypadVisualRoot : _keypad?.transform;
                if (scanNode.transform == keypadRoot)
                    Destroy(scanNode);
                else
                    Destroy(scanNode.gameObject);
            }
        }

        private void UpdateLockerScanNode()
        {
            ScanNodeProperties[] scanNodes = GetComponentsInChildren<ScanNodeProperties>(true);
            for (int i = 0; i < scanNodes.Length; i++)
            {
                ScanNodeProperties scanNode = scanNodes[i];
                if (scanNode == null)
                    continue;
                if (string.Equals(scanNode.headerText, "Company Stash", StringComparison.OrdinalIgnoreCase))
                    scanNode.subText = IsUnlocked ? "access granted" : "secured";
            }
        }

        private static void EnsureScanNode(Transform parent, string name, string header, string subText, Vector3 localPosition, float radius, int maxRange)
        {
            if (parent == null)
                return;

            Transform existing = parent.Find(name);
            GameObject scanObject = existing != null ? existing.gameObject : new GameObject(name);
            scanObject.transform.SetParent(parent, false);
            scanObject.transform.localPosition = localPosition;
            scanObject.transform.localRotation = Quaternion.identity;
            scanObject.transform.localScale = Vector3.one;
            scanObject.layer = ResolveLayer("ScanNode", ScanNodeLayer);

            SphereCollider collider = scanObject.GetComponent<SphereCollider>();
            if (collider == null)
                collider = scanObject.AddComponent<SphereCollider>();
            collider.enabled = true;
            collider.isTrigger = true;
            collider.center = Vector3.zero;
            collider.radius = Mathf.Max(0.1f, radius);

            ScanNodeProperties scanNode = scanObject.GetComponent<ScanNodeProperties>();
            if (scanNode == null)
                scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = header;
            scanNode.subText = subText;
            scanNode.minRange = 1;
            scanNode.maxRange = Mathf.Max(1, maxRange);
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = 0;
        }

        private void StartOpenVisualLocal()
        {
            if (_openVisualStarted)
                return;

            _openVisualStarted = true;

            if (TryPlayAuthoredOpenAnimation())
                return;

            CaptureDoorRotations();
            if (_leftDoor == null && _rightDoor == null)
                return;

            _fallbackOpening = true;
            _fallbackOpenProgress = 0f;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' opening doors with fallback animation left={(_leftDoor != null ? _leftDoor.name : "none")} right={(_rightDoor != null ? _rightDoor.name : "none")}.");
        }

        private bool TryPlayAuthoredOpenAnimation()
        {
            if (_legacyAnimation != null && !IsKeypadDescendant(_legacyAnimation.transform))
            {
                _legacyAnimation.enabled = true;
                _legacyAnimation.cullingType = AnimationCullingType.AlwaysAnimate;
                AnimationState best = null;
                int bestScore = int.MinValue;
                foreach (AnimationState state in _legacyAnimation)
                {
                    if (state == null)
                        continue;

                    int score = ScoreOpenAnimationState(state);
                    if (best == null || score > bestScore)
                    {
                        best = state;
                        bestScore = score;
                    }
                }

                if (best != null)
                {
                    best.wrapMode = WrapMode.Once;
                    best.time = 0f;
                    best.speed = 1f;
                    _legacyAnimation.clip = best.clip;
                    _legacyAnimation.Play(best.name, PlayMode.StopAll);
                    return true;
                }
            }

            if (_animator != null && !IsKeypadDescendant(_animator.transform) && _animator.runtimeAnimatorController != null)
            {
                if (AnimatorHasTrigger(_animator, "Open"))
                {
                    _animator.enabled = true;
                    _animator.SetTrigger("Open");
                    return true;
                }
                if (AnimatorHasBool(_animator, "Open"))
                {
                    _animator.enabled = true;
                    _animator.SetBool("Open", true);
                    return true;
                }
            }

            return false;
        }

        private void MountKeypadToDoor()
        {
            if (_keypadMounted)
                return;

            Transform door = _rightDoor != null ? _rightDoor : _leftDoor;
            if (door == null)
                return;

            if (_keypadVisualRoot == null)
                _keypadVisualRoot = FindKeypadVisualRoot();
            Transform keypadTransform = _keypadVisualRoot;
            if (keypadTransform == null || keypadTransform == transform)
                return;

            keypadTransform.SetParent(transform, false);
            keypadTransform.localPosition = KeypadMountedRootLocalPosition;
            keypadTransform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            keypadTransform.localScale = Vector3.one * 0.72f;
            keypadTransform.SetParent(door, true);
            _keypadMounted = true;
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Company Stash '{DisplayName}' mounted keypad visual '{keypadTransform.name}' on door '{door.name}'.");
        }

        private T FindComponentOutsideKeypad<T>() where T : Component
        {
            T[] components = GetComponentsInChildren<T>(true);
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (component != null && !IsKeypadDescendant(component.transform))
                    return component;
            }

            return null;
        }

        private bool IsKeypadDescendant(Transform candidate)
        {
            Transform keypadRoot = _keypadVisualRoot != null
                ? _keypadVisualRoot
                : (_keypad != null && _keypad.transform != transform ? _keypad.transform : null);
            return keypadRoot != null
                   && candidate != null
                   && (candidate == keypadRoot || candidate.IsChildOf(keypadRoot));
        }

        private void CaptureDoorRotations()
        {
            if (_capturedDoorRotations)
                return;

            if (_leftDoor != null)
                _leftClosedRotation = _leftDoor.localRotation;
            if (_rightDoor != null)
                _rightClosedRotation = _rightDoor.localRotation;
            _capturedDoorRotations = _leftDoor != null || _rightDoor != null;
        }

        private void UpdateFallbackOpenAnimation()
        {
            if (!_fallbackOpening)
                return;

            _fallbackOpenProgress = Mathf.MoveTowards(_fallbackOpenProgress, 1f, Time.deltaTime / 1.1f);
            float eased = Mathf.SmoothStep(0f, 1f, _fallbackOpenProgress);
            if (_leftDoor != null)
                _leftDoor.localRotation = Quaternion.Slerp(_leftClosedRotation, _leftClosedRotation * Quaternion.Euler(0f, 92f, 0f), eased);
            if (_rightDoor != null)
                _rightDoor.localRotation = Quaternion.Slerp(_rightClosedRotation, _rightClosedRotation * Quaternion.Euler(0f, -92f, 0f), eased);

            if (_fallbackOpenProgress >= 0.999f)
                _fallbackOpening = false;
        }

        private bool IsSenderCloseEnough(ulong senderClientId)
        {
            PlayerControllerB player = FindPlayer(senderClientId);
            if (player == null)
                return false;

            return Vector3.Distance(player.transform.position, transform.position) <= 4.0f;
        }

        private bool IsSenderWithinRemoteHackRange(ulong senderClientId)
        {
            PlayerControllerB player = FindPlayer(senderClientId);
            if (player == null)
                return false;

            return Vector3.Distance(player.transform.position, transform.position) <= 20.0f;
        }

        private static PlayerControllerB FindPlayer(ulong senderClientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == senderClientId)
                    return player;
            }

            return null;
        }

        private static int ScoreOpenAnimationState(AnimationState state)
        {
            string text = ((state?.name ?? string.Empty) + " " + (state?.clip != null ? state.clip.name : string.Empty)).ToLowerInvariant();
            int score = 0;
            if (text.Contains("open"))
                score += 16;
            if (text.Contains("door"))
                score += 10;
            if (text.Contains("cabinet") || text.Contains("stash") || text.Contains("loop"))
                score += 3;
            return score;
        }

        private static bool AnimatorHasTrigger(Animator animator, string name)
        {
            return AnimatorHasParameter(animator, name, AnimatorControllerParameterType.Trigger);
        }

        private static bool AnimatorHasBool(Animator animator, string name)
        {
            return AnimatorHasParameter(animator, name, AnimatorControllerParameterType.Bool);
        }

        private static bool AnimatorHasParameter(Animator animator, string name, AnimatorControllerParameterType type)
        {
            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    AnimatorControllerParameter parameter = parameters[i];
                    if (parameter != null && parameter.type == type && string.Equals(parameter.name, name, StringComparison.Ordinal))
                        return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static Transform FindChildByNameToken(Transform root, string token)
        {
            if (root == null || string.IsNullOrWhiteSpace(token))
                return null;
            if (root.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildByNameToken(root.GetChild(i), token);
                if (found != null)
                    return found;
            }
            return null;
        }

        private Transform FindDoorTransform(bool rightDoor)
        {
            string[] exactNames = rightDoor
                ? new[] { "door-r", "door_r", "rightdoor", "doorright" }
                : new[] { "door-l", "door_l", "leftdoor", "doorleft" };
            Transform[] transforms = GetComponentsInChildren<Transform>(true);
            Transform best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate == transform)
                    continue;

                int nameScore = ScoreDoorName(candidate.name, exactNames);
                if (nameScore <= 0)
                    continue;

                int score = nameScore;
                string path = BuildTransformPath(candidate).Replace('\\', '/');
                if (path.IndexOf("/skeleton/", StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 80;
                if (candidate.GetComponent<Renderer>() == null)
                    score += 40;
                else
                    score -= 30;
                if (candidate.localScale.sqrMagnitude < 10f)
                    score += 10;
                else
                    score -= 20;
                if (candidate.childCount > 0)
                    score += 5;

                if (best == null || score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        private Transform FindKeypadVisualRoot()
        {
            Transform[] transforms = GetComponentsInChildren<Transform>(true);
            Transform best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate == transform)
                    continue;

                string path = BuildTransformPath(candidate);
                if (path.IndexOf("ScanNode", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("Interact", StringComparison.OrdinalIgnoreCase) >= 0
                    || path.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                int score = ScoreKeypadVisualRoot(candidate);
                if (score <= 0)
                    continue;

                if (best == null || score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        private static int ScoreKeypadVisualRoot(Transform candidate)
        {
            string name = candidate.name ?? string.Empty;
            int score = 0;
            if (string.Equals(name, "Keypad", StringComparison.OrdinalIgnoreCase))
                score += 100;
            else if (name.IndexOf("keypad", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 40;

            Renderer[] renderers = candidate.GetComponentsInChildren<Renderer>(true);
            if (renderers != null && renderers.Length > 0)
                score += 20;

            if (FindChildByExactName(candidate, "bttn9") != null)
                score += 30;
            if (FindChildByExactName(candidate, "bttnEnter") != null)
                score += 30;

            string path = BuildTransformPath(candidate);
            if (path.IndexOf("/door", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 10;

            return score;
        }

        private static Transform FindChildByExactName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;
            if (string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildByExactName(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static int ScoreDoorName(string name, string[] exactNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                return 0;

            for (int i = 0; i < exactNames.Length; i++)
            {
                if (string.Equals(name, exactNames[i], StringComparison.OrdinalIgnoreCase))
                    return 100;
            }

            string normalized = name.Replace("_", string.Empty).Replace("-", string.Empty);
            for (int i = 0; i < exactNames.Length; i++)
            {
                string exact = exactNames[i].Replace("_", string.Empty).Replace("-", string.Empty);
                if (normalized.IndexOf(exact, StringComparison.OrdinalIgnoreCase) >= 0)
                    return 35;
            }

            return 0;
        }

        private static string BuildTransformPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            Transform cursor = transform.parent;
            while (cursor != null)
            {
                path = cursor.name + "/" + path;
                cursor = cursor.parent;
            }

            return path;
        }

        private static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }
    }
}
