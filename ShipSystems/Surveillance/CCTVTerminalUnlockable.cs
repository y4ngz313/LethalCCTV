using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CSync.Lib;
using HarmonyLib;
using LethalLib.Modules;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CCTVTerminalUnlockable
    {
        internal const string UnlockableName = "CCTV Terminal";

        // #561: no longer furniture in any visible sense - an invisible upgrade marker
        // that keeps the vanilla ship-upgrade spawn plumbing intact. The name is retained
        // so the NetworkObject hash (derived from it) stays stable across the change.
        private const string PrefabName = "Y4NGZ_CCTVTerminal_Furniture";
        private const int PlaceableObjectLayer = 26;
        private static readonly Vector3 PlacementColliderSize = new Vector3(1.12f, 0.82f, 0.36f);

        private const int DefaultVanillaStorePrice = 200;

        private static bool _registrationAttempted;
        private static GameObject _prefab;
        private static UnlockableItem _unlockable;
        private static MethodInfo _spawnUnlockableMethod;
        private static bool _cachedPurchased;
        private static float _purchasedCacheValidUntil;
        private static MethodInfo _shipSystemsHasUpgradeMethod;
        private static bool _shipSystemsMethodResolved;
        private static bool? _shipSystemsPresent;
        // Set only on the standalone path, once LethalLib has accepted a real vanilla
        // store registration. Everything that changes behaviour for a Contracted-less
        // install is guarded on this, so a profile that also carries Contracted - or one
        // where the store registration threw - keeps the pre-#393 behaviour untouched.
        private static bool _soldInVanillaStore;
        // Pivot-to-base distance of the built furniture, used for the placeable's
        // yOffset. Captured in BuildPrefab from the same bounds the placement collider
        // is sized from.
        private static float _defaultPlaceableYOffset;
        private static bool _vanillaStoreSpawnTipShown;
        private static StartOfRound _ownershipSession;
        private static ConfigSyncBehaviour _hostConfigSync;
        private static bool _ownershipReady;
        private static bool _freeFurnitureEnsured;

        /// <summary>
        /// True unless both Ship Systems and the terminal shop provide the seller.
        /// This is independent of the host's free-terminal setting so peers register
        /// the same unlockable and store nodes before config synchronization.
        /// </summary>
        internal static bool SoldInVanillaStore => _soldInVanillaStore;

        // CSync 5.0.1 exposes LocalValue until its NetworkList has been applied.
        // Require its completion event AND a live override on clients, including after
        // disconnect (OnDestroy disables overrides and restores the local default).
        internal static bool ShipStartsWithTerminal
        {
            get
            {
                if (!HasActiveOwnershipSession())
                    return false;

                SyncedEntry<bool> setting = SurveillanceBootstrap.Config?.ShipStartsWithCctvTerminal;
                if (setting == null)
                    return false;
                if (CctvNetworkRole.IsServer())
                    return setting.LocalValue;

                return _hostConfigSync != null && _hostConfigSync.IsSpawned &&
                       setting.ValueOverridden && setting.Value;
            }
        }

        internal static void InitializeOwnership(LethalCCTVConfig config)
        {
            config.InitialSyncCompleted += OnInitialConfigSync;
            config.ShipStartsWithCctvTerminal.Changed += OnFreeSettingChanged;
        }

        private static bool HasActiveOwnershipSession()
        {
            NetworkManager network = NetworkManager.Singleton;
            GameNetworkManager game = GameNetworkManager.Instance;
            return _ownershipSession != null && StartOfRound.Instance == _ownershipSession &&
                   network != null && network.IsListening &&
                   (game == null || !game.isDisconnecting);
        }

        private static void OnInitialConfigSync(object sender, EventArgs args)
        {
            if (!HasActiveOwnershipSession() || !(sender is ConfigSyncBehaviour sync) || !sync.IsSpawned)
                return;

            _hostConfigSync = sync;
            InvalidatePurchaseCache();
            ApplyFreeEntitlement();
        }

        private static void OnFreeSettingChanged(object sender, SyncedSettingChangedEventArgs<bool> args)
        {
            InvalidatePurchaseCache();
            if (ShipStartsWithTerminal)
            {
                ApplyFreeEntitlement();
            }
            else if (TryFindUnlockable(out _, out UnlockableItem item))
            {
                // Removing the default must not revoke a grant/purchase already earned
                // in this save. Vanilla persists hasBeenUnlockedByPlayer independently.
                item.alreadyUnlocked = false;
            }
        }

        internal static void BeginOwnershipSession(StartOfRound start)
        {
            _ownershipSession = start;
            _hostConfigSync = null;
            _ownershipReady = false;
            _freeFurnitureEnsured = false;
            _vanillaStoreSpawnTipShown = false;
            ResetRuntimeOwnership();
        }

        internal static void BeforeLoadUnlockables()
        {
            _ownershipReady = false;
            _freeFurnitureEnsured = false;
            // Clear only memory, BEFORE vanilla restores this save's purchased/storage
            // data. alreadyUnlocked is not cleared by vanilla's own list reset.
            ResetRuntimeOwnership();
        }

        private static void ResetRuntimeOwnership()
        {
            ResetRuntimeOwnership(_unlockable);
            if (TryFindUnlockable(out _, out UnlockableItem item) && !ReferenceEquals(item, _unlockable))
                ResetRuntimeOwnership(item);
            _cachedPurchased = false;
            InvalidatePurchaseCache();
        }

        private static void ResetRuntimeOwnership(UnlockableItem item)
        {
            if (item == null)
                return;
            item.alreadyUnlocked = false;
            item.hasBeenUnlockedByPlayer = false;
            item.inStorage = false;
            item.hasBeenMoved = false;
            item.placedPosition = Vector3.zero;
            item.placedRotation = Vector3.zero;
        }

        internal static void AfterLoadUnlockables()
        {
            _ownershipReady = true;
            InvalidatePurchaseCache();
            ApplyFreeEntitlement();
        }

        internal static void AfterSyncUnlockables()
        {
            // The vanilla ClientRpc has now applied the host's paid ownership flags.
            if (CctvNetworkRole.IsServer())
                return;
            _ownershipReady = true;
            InvalidatePurchaseCache();
            ApplyFreeEntitlement();
        }

        internal static void AfterResetFurniture()
        {
            _freeFurnitureEnsured = false;
            InvalidatePurchaseCache();
            ApplyFreeEntitlement();
        }

        private static void ApplyFreeEntitlement()
        {
            if (!ShipStartsWithTerminal)
                return;

            bool server = CctvNetworkRole.IsServer();
            if (server && !_ownershipReady)
                return;
            if (!TryFindUnlockable(out int id, out UnlockableItem item))
                return;

            // SaveGameValues includes hasBeenUnlockedByPlayer even for alreadyUnlocked
            // items in UnlockedShipObjects. SyncShipUnlockables sends that same flag.
            // On clients the synchronized free setting also arms vanilla's owned-item
            // store guard before its later furniture sync; no purchase RPC is necessary.
            item.alreadyUnlocked = true;
            item.hasBeenUnlockedByPlayer = true;
            InvalidatePurchaseCache();
            if (!server || _freeFurnitureEnsured)
                return;

            // LoadUnlockables runs after all scene Awakes, at vanilla's furniture-spawn
            // readiness boundary. Existing/stored paid furniture is never duplicated or
            // pulled from storage. No per-frame scan or spawn retry follows success.
            _freeFurnitureEnsured = item.inStorage || EnsureFurnitureSpawned(id, item);
        }

        internal static void Register()
        {
            if (_registrationAttempted)
                return;

            _registrationAttempted = true;
            try
            {
                BuildPrefab();
                if (_unlockable == null)
                    return;

                // #393. The Y4NGZ terminal shop is the seller when installed. Without a
                // companion seller LethalCCTV registers a genuine vanilla ship upgrade and
                // owns that purchase state itself.
                // Its store selection node is the one DawnLib warns about when missing.
                // #767: since the #666 split the shop is its own plugin. Ship Systems alone
                // answers the purchase state but sells nothing, so a profile carrying it
                // without the shop registered an item no store offered. The hidden path
                // now needs the seller itself to be loaded.
                bool shipSystems = IsShipSystemsPresent();
                bool terminalShop = IsTerminalShopPresent();
                if (shipSystems && terminalShop)
                {
                    Unlockables.RegisterUnlockable(_unlockable, StoreType.None);
                    SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Registered CCTV Terminal as a hidden ship unlockable (invisible upgrade marker; no console furniture). Seller: Y4NGZ terminal shop.");
                }
                else
                {
                    int price = ResolveVanillaStorePrice();
                    // Vanilla ship upgrades never rotate out of the store; without this the
                    // only seller in a standalone install would be an intermittent one.
                    _unlockable.alwaysInStock = true;
                    Unlockables.RegisterUnlockable(_unlockable, StoreType.ShipUpgrade, price: price);
                    _soldInVanillaStore = true;
                    string why = shipSystems
                        ? "Ship Systems present but the Y4NGZ terminal shop is absent"
                        : terminalShop
                            ? "Y4NGZ terminal shop present but Ship Systems is absent"
                            : "Ship Systems and the Y4NGZ terminal shop are absent";
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] Registered CCTV Terminal as a vanilla store ship upgrade for {price} credits ({why}).");
                }

                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(_prefab);
                WarnIfLethalLibCannotDeliverTheStoreItem();
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] CCTV terminal unlockable registration failed: {ex.Message}");
                WarnIfLethalLibCannotDeliverTheStoreItem();
            }
        }

        /// <summary>
        /// #600. The registration calls above cannot fail loudly when LethalLib is the wrong
        /// version: LethalLib 0.16.2 throws a TypeLoadException on DunGen.TileSet inside its OWN
        /// Awake against the current game, so its terminal patches never install. Our call still
        /// returns normally, the unlockable is queued into a list nothing ever reads, and the
        /// player just finds no "CCTV Terminal" in the store with nothing in the log to explain
        /// it. Two signals catch that: an outdated version from the Awake audit, and a plugin
        /// registered with no live instance (BepInEx keeps the entry when the constructor throws).
        /// </summary>
        private static void WarnIfLethalLibCannotDeliverTheStoreItem()
        {
            string versionNote = DependencyVersionAudit.DescribeOutdated(DependencyVersionAudit.LethalLibGuid);
            bool loadedButDead = DependencyVersionAudit.IsLoadedButDead(DependencyVersionAudit.LethalLibGuid);
            if (versionNote == null && !loadedButDead)
                return;

            string cause = versionNote
                           ?? "LethalLib is installed but failed to initialise, so its terminal and store patches never ran";

            SurveillanceBootstrap.Log?.LogError(
                $"[LethalCCTV] The CCTV Terminal will NOT appear in the ship store: {cause}. " +
                $"{DependencyVersionAudit.UpdateAdvice}");
        }

        private static int ResolveVanillaStorePrice()
        {
            try
            {
                LethalCCTVConfig config = SurveillanceBootstrap.Config;
                if (config?.CctvTerminalPrice != null)
                    return Mathf.Max(0, config.CctvTerminalPrice.Value);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Falling back to the default CCTV terminal price: {ex.Message}");
            }

            return DefaultVanillaStorePrice;
        }

        /// <summary>
        /// Vanilla's <c>StartOfRound.SpawnUnlockable</c> instantiates
        /// <see cref="UnlockableItem.prefabObject"/> as-is, and this prefab is kept
        /// inactive so the template never renders in the ship. Spawning a disabled
        /// NetworkObject drops its NetworkBehaviours, so every vanilla spawn wakes
        /// the template for the duration of that call, including saved hidden upgrades.
        /// <see cref="EnsureFurnitureSpawned"/> uses the same wake/restore contract.
        /// Returns true when the caller must hand the prefab back to
        /// <see cref="EndVanillaFurnitureSpawn"/>.
        /// </summary>
        internal static bool BeginVanillaFurnitureSpawn(int unlockableIndex)
        {
            if (_prefab == null)
                return false;

            try
            {
                if (!TryFindUnlockable(out int id, out UnlockableItem item) || id != unlockableIndex || item == null)
                    return false;

                StampUnlockableId(_prefab, id);
                if (ShouldUseDefaultPose(item))
                    ApplyDefaultFurniturePose(_prefab);

                if (_prefab.activeSelf)
                    return false;

                _prefab.SetActive(true);
                return true;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to prepare the CCTV terminal template for a vanilla store spawn: {ex.Message}");
                return false;
            }
        }

        internal static void EndVanillaFurnitureSpawn(int unlockableIndex, bool restorePrefabInactive)
        {
            try
            {
                if (restorePrefabInactive && _prefab != null)
                    _prefab.SetActive(false);

                InvalidatePurchaseCache();

                if (TryFindUnlockable(out int id, out _) &&
                    id == unlockableIndex &&
                    TryGetSpawnedFurnitureRoot(out GameObject root))
                {
                    PrepareSpawnedFurnitureRoot(root, id);
                    if (_soldInVanillaStore && !ShipStartsWithTerminal)
                    {
                        // #561: the marker is invisible; the access button is the affordance.
                        SurveillanceBootstrap.Log?.LogInfo(
                            $"[LethalCCTV] CCTV terminal owned in the vanilla store; no console furniture is spawned " +
                            $"(invisible upgrade marker at world={root.transform.position} shipLocal={root.transform.localPosition}).");
                        AnnounceVanillaStoreInstall();
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to finish a vanilla store CCTV terminal spawn: {ex.Message}");
            }
        }

        /// <summary>
        /// #561. Standalone (Contracted-less) profiles buy the terminal from the vanilla
        /// ship store, which gives no feedback of its own beyond the credit deduction.
        /// Since the purchase spawns no furniture at all, the only thing it adds is the
        /// access button in front of the monitor wall - behind the player, who is stood
        /// at the terminal. So the install says so once, in words.
        /// </summary>
        private static void AnnounceVanillaStoreInstall()
        {
            if (_vanillaStoreSpawnTipShown)
                return;

            _vanillaStoreSpawnTipShown = true;
            try
            {
                HUDManager.Instance?.DisplayTip(
                    "CCTV Terminal installed",
                    "Press the red access button in front of the ship's monitor wall to view the cameras.",
                    isWarning: false);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] CCTV terminal install tip failed: {ex.Message}");
            }
        }

        // The single ownership read. Every gate in this plugin - camera-to-pane binding,
        // the persistent monitor, the vanilla monitor display and its feed sync, the
        // operator station, the supplementary-camera injector and the whole purchased
        // half of the LateUpdate pass - asks this and nothing else.
        //
        // Hot path: called from several per-frame Update/LateUpdate loops. The
        // uncached computation walks the unlockables list and invokes a reflection
        // bridge, so the result is cached on a short TTL. Purchase paths call
        // InvalidatePurchaseCache so the unlock is picked up immediately.
        internal static bool IsPurchased()
        {
            if (!HasActiveOwnershipSession())
                return false;
            if (ShipStartsWithTerminal)
                return true;
            if (!_ownershipReady)
                return false;

            if (Time.unscaledTime < _purchasedCacheValidUntil)
                return _cachedPurchased;

            _purchasedCacheValidUntil = Time.unscaledTime + 0.5f;
            _cachedPurchased = ComputeIsPurchased();
            return _cachedPurchased;
        }

        internal static void InvalidatePurchaseCache()
        {
            _purchasedCacheValidUntil = 0f;
        }

        private static bool ComputeIsPurchased()
        {
            bool ownedByUnlockable = TryFindUnlockable(out _, out UnlockableItem item) &&
                                     item != null &&
                                     (item.hasBeenUnlockedByPlayer || item.alreadyUnlocked);

            if (TryReadShipSystemsPurchaseState(out bool ownedByShipSystems))
                return ownedByShipSystems || ownedByUnlockable;

            // #393 standalone path: the vanilla store sells it, so the vanilla unlockable
            // is the authority. This replaces the old blanket always-on fallback, which
            // handed a standalone install the whole module for free and left the
            // hidden unlockable unbuyable.
            if (_soldInVanillaStore)
                return ownedByUnlockable;

            // If neither seller registered successfully, no purchase occurred. Stay locked
            // and rely on registration's targeted error instead of granting CCTV for free.
            return ownedByUnlockable;
        }

        private static bool TryGetSpawnedFurnitureRoot(out GameObject root)
        {
            root = null;

            if (!TryFindUnlockable(out int id, out _))
                return false;

            StartOfRound start = StartOfRound.Instance;
            if (start != null &&
                start.SpawnedShipUnlockables != null &&
                start.SpawnedShipUnlockables.TryGetValue(id, out GameObject spawned) &&
                spawned != null)
            {
                if (IsPrefabTemplateRoot(spawned))
                {
                    start.SpawnedShipUnlockables.Remove(id);
                    return false;
                }

                root = spawned;
                PrepareSpawnedFurnitureRoot(root, id);
                return true;
            }

            PlaceableShipObject[] placeables = UnityEngine.Object.FindObjectsByType<PlaceableShipObject>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < placeables.Length; i++)
            {
                PlaceableShipObject placeable = placeables[i];
                if (placeable == null || placeable.unlockableID != id)
                    continue;

                GameObject candidateRoot = placeable.parentObject != null
                    ? placeable.parentObject.gameObject
                    : placeable.gameObject;
                if (IsPrefabTemplateRoot(candidateRoot))
                    continue;

                root = candidateRoot;
                PrepareSpawnedFurnitureRoot(root, id);
                return root != null;
            }

            return false;
        }

        internal static bool UnlockFromShipUpgrade(Terminal terminal)
        {
            Register();

            if (!TryFindUnlockable(out int id, out UnlockableItem item) || item == null)
            {
                // #716 F4: this is the normal first pass of a retry path, not a fault.
                SurveillanceBootstrap.Log?.LogDebug("[LethalCCTV] CCTV terminal unlockable was not present in StartOfRound.unlockablesList yet.");
                return false;
            }

            StampUnlockableId(_prefab, id);

            StartOfRound start = StartOfRound.Instance;
            if (start == null)
                return false;

            bool alreadyOwned = item.hasBeenUnlockedByPlayer || item.alreadyUnlocked;
            if (!alreadyOwned)
            {
                try
                {
                    int credits = terminal != null ? terminal.groupCredits : FindObjectCredits();
                    start.BuyShipUnlockableServerRpc(id, credits);
                    SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] CCTV terminal unlock requested through StartOfRound.");
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] BuyShipUnlockableServerRpc failed for CCTV terminal: {ex.Message}");
                    return false;
                }
            }

            InvalidatePurchaseCache();
            return EnsureFurnitureSpawned(id, item);
        }

        private static void BuildPrefab()
        {
            GameObject root = new GameObject(PrefabName);
            root.SetActive(false);
            root.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(root);

            NetworkObject networkObject = root.AddComponent<NetworkObject>();
            networkObject.SynchronizeTransform = true;
            networkObject.SceneMigrationSynchronization = true;
            networkObject.SpawnWithObservers = true;
            networkObject.DontDestroyWithOwner = true;
            networkObject.AutoObjectParentSync = true;
            AssignStableNetworkHash(networkObject);

            AutoParentToShip parent = root.AddComponent<AutoParentToShip>();
            parent.overrideOffset = true;
            parent.positionOffset = ResolveDefaultLocalPosition();
            parent.rotationOffset = ResolveDefaultEuler();
            parent.disableObject = false;

            // #561. The purchase deliberately spawns NO visible furniture. The authored
            // CRT+keyboard console (lethalcctv-terminal.meshbundle) has been out of the
            // design for months - it was only ever "invisible" because the pose bug put
            // it outside the hull, and once the pose was corrected it reappeared on top
            // of the vanilla monitor wall. What the purchase actually delivers is the
            // armed CCTVAccessButton in front of the monitor wall, exactly as a
            // Contracted-present profile behaves.
            //
            // The root is still a real spawned prefab so that nothing in the vanilla
            // ship-upgrade plumbing changes shape (StartOfRound.SpawnUnlockable still
            // instantiates + Spawns a NetworkObject, SpawnedShipUnlockables still gets
            // its entry, and the store-spawn postfix still fires). It just carries an
            // invisible placement proxy instead of a mesh.
            MeshFilter placementMesh = CreatePlacementMeshProxy(root.transform);

            Bounds placementBounds = CalculateLocalBounds(root.transform, placementMesh.transform);
            _defaultPlaceableYOffset = Mathf.Max(0f, -(placementBounds.center.y - placementBounds.extents.y));

            GameObject handle = new GameObject("CCTVTerminal_PlaceableHandle");
            handle.transform.SetParent(root.transform, worldPositionStays: false);
            handle.transform.localPosition = placementBounds.center;
            handle.transform.localRotation = Quaternion.identity;
            handle.transform.localScale = Vector3.one;
            handle.layer = PlaceableObjectLayer;
            SetTagSafe(handle, "PlaceableObject");

            BoxCollider collider = handle.AddComponent<BoxCollider>();
            collider.size = EnsureMinimumColliderSize(placementBounds.size);
            collider.center = Vector3.zero;
            // #561. There is nothing to see and nothing to move, so build mode must not
            // be able to grab it. ShipBuildModeManager selects objects by raycasting the
            // PlaceableShipObjects layer; a disabled collider is never hit. The reference
            // stays assigned on the PlaceableShipObject below so vanilla's placement code
            // never dereferences a null collider.
            collider.enabled = false;

            AudioSource audio = handle.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 1f;
            audio.maxDistance = 16f;

            PlaceableShipObject placeable = handle.AddComponent<PlaceableShipObject>();
            placeable.parentObject = parent;
            placeable.mainMesh = placementMesh;
            placeable.mainTransform = placementMesh.transform;
            placeable.placeObjectCollider = collider;
            placeable.yOffset = ResolveDefaultHeight();
            placeable.overrideWallOffset = true;
            placeable.wallOffset = 0.18f;
            placeable.AllowPlacementOnWalls = true;
            placeable.AllowPlacementOnCounters = false;
            placeable.doCollisionPointCheck = false;

            _unlockable = new UnlockableItem
            {
                unlockableName = UnlockableName,
                prefabObject = root,
                unlockableType = 1,
                // IsPlaceable stays true: vanilla's SpawnUnlockable reads the spawned
                // PlaceableShipObject through this flag, and the prefab still carries one.
                // #561 keeps the marker un-storable - an invisible object in the storage
                // closet is a trap, and storing it would silently drop the upgrade root.
                IsPlaceable = true,
                canBeStored = false,
                maxNumber = 1,
                spawnPrefab = true,
                alwaysInStock = false,
                alreadyUnlocked = false,
                hasBeenUnlockedByPlayer = false,
            };
            _prefab = root;
        }

        private static void StampUnlockableId(GameObject root, int id)
        {
            if (root == null || id < 0)
                return;

            try
            {
                AutoParentToShip parent = root.GetComponent<AutoParentToShip>();
                if (parent != null)
                    parent.unlockableID = id;

                PlaceableShipObject[] placeables = root.GetComponentsInChildren<PlaceableShipObject>(includeInactive: true);
                for (int i = 0; i < placeables.Length; i++)
                {
                    if (placeables[i] != null)
                        placeables[i].unlockableID = id;
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to stamp CCTV terminal unlockable id {id}: {ex.Message}");
            }
        }

        /// <summary>
        /// The invisible stand-in the upgrade root carries in place of a console mesh
        /// (#561). Vanilla's <c>PlaceableShipObject</c> requires a non-null
        /// <c>mainMesh</c>/<c>mainTransform</c>; this satisfies that contract while
        /// rendering nothing.
        /// </summary>
        private static MeshFilter CreatePlacementMeshProxy(Transform parent)
        {
            GameObject meshProxy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            meshProxy.name = "CCTVTerminal_PlacementMeshProxy";
            meshProxy.transform.SetParent(parent, worldPositionStays: false);
            meshProxy.transform.localPosition = Vector3.zero;
            meshProxy.transform.localRotation = Quaternion.identity;
            meshProxy.transform.localScale = PlacementColliderSize;
            UnityEngine.Object.Destroy(meshProxy.GetComponent<Collider>());
            MeshRenderer proxyRenderer = meshProxy.GetComponent<MeshRenderer>();
            if (proxyRenderer != null)
                proxyRenderer.enabled = false;
            return meshProxy.GetComponent<MeshFilter>();
        }

        private static Bounds CalculateLocalBounds(Transform root, Transform source)
        {
            if (root == null || source == null)
                return new Bounds(Vector3.zero, PlacementColliderSize);

            Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool initialized = false;
            MeshFilter[] meshes = source.GetComponentsInChildren<MeshFilter>(includeInactive: true);
            for (int i = 0; i < meshes.Length; i++)
            {
                MeshFilter meshFilter = meshes[i];
                Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                if (mesh == null)
                    continue;

                Bounds meshBounds = mesh.bounds;
                EncapsulateLocalPoint(root, meshFilter.transform.TransformPoint(meshBounds.center + new Vector3(meshBounds.extents.x, meshBounds.extents.y, meshBounds.extents.z)), ref bounds, ref initialized);
                EncapsulateLocalPoint(root, meshFilter.transform.TransformPoint(meshBounds.center + new Vector3(meshBounds.extents.x, meshBounds.extents.y, -meshBounds.extents.z)), ref bounds, ref initialized);
                EncapsulateLocalPoint(root, meshFilter.transform.TransformPoint(meshBounds.center + new Vector3(meshBounds.extents.x, -meshBounds.extents.y, meshBounds.extents.z)), ref bounds, ref initialized);
                EncapsulateLocalPoint(root, meshFilter.transform.TransformPoint(meshBounds.center + new Vector3(meshBounds.extents.x, -meshBounds.extents.y, -meshBounds.extents.z)), ref bounds, ref initialized);
                EncapsulateLocalPoint(root, meshFilter.transform.TransformPoint(meshBounds.center + new Vector3(-meshBounds.extents.x, meshBounds.extents.y, meshBounds.extents.z)), ref bounds, ref initialized);
                EncapsulateLocalPoint(root, meshFilter.transform.TransformPoint(meshBounds.center + new Vector3(-meshBounds.extents.x, meshBounds.extents.y, -meshBounds.extents.z)), ref bounds, ref initialized);
                EncapsulateLocalPoint(root, meshFilter.transform.TransformPoint(meshBounds.center + new Vector3(-meshBounds.extents.x, -meshBounds.extents.y, meshBounds.extents.z)), ref bounds, ref initialized);
                EncapsulateLocalPoint(root, meshFilter.transform.TransformPoint(meshBounds.center - meshBounds.extents), ref bounds, ref initialized);
            }

            return initialized ? bounds : new Bounds(Vector3.zero, PlacementColliderSize);
        }

        private static void EncapsulateLocalPoint(Transform root, Vector3 worldPoint, ref Bounds bounds, ref bool initialized)
        {
            Vector3 local = root.InverseTransformPoint(worldPoint);
            if (!initialized)
            {
                bounds = new Bounds(local, Vector3.zero);
                initialized = true;
                return;
            }

            bounds.Encapsulate(local);
        }

        private static Vector3 EnsureMinimumColliderSize(Vector3 size)
        {
            return new Vector3(
                Mathf.Max(PlacementColliderSize.x, Mathf.Abs(size.x)),
                Mathf.Max(PlacementColliderSize.y, Mathf.Abs(size.y)),
                Mathf.Max(PlacementColliderSize.z, Mathf.Abs(size.z)));
        }

        private static void ApplyDefaultFurniturePose(GameObject root)
        {
            if (root == null)
                return;

            AutoParentToShip parent = root.GetComponent<AutoParentToShip>();
            if (parent != null)
            {
                parent.overrideOffset = true;
                parent.positionOffset = ResolveDefaultLocalPosition();
                parent.rotationOffset = ResolveDefaultEuler();
                parent.startingPosition = parent.positionOffset;
                parent.startingRotation = parent.rotationOffset;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] CCTV terminal upgrade-marker default pose (no visible furniture): " +
                    $"hangarLocal={ResolveConfiguredHangarLocalPosition()} shipLocal={parent.positionOffset} " +
                    $"world={ResolveConfiguredWorldPosition()} rotOffset={parent.rotationOffset}.");
            }

            PlaceableShipObject[] placeables = root.GetComponentsInChildren<PlaceableShipObject>(includeInactive: true);
            for (int i = 0; i < placeables.Length; i++)
            {
                if (placeables[i] != null)
                    placeables[i].yOffset = ResolveDefaultHeight();
            }
        }

        private static bool ShouldUseDefaultPose(UnlockableItem item)
        {
            return item != null &&
                   !item.hasBeenMoved &&
                   item.placedPosition == Vector3.zero &&
                   item.placedRotation == Vector3.zero;
        }

        private static void PrepareSpawnedFurnitureRoot(GameObject root, int id, UnlockableItem item = null, bool forceDefaultPose = false)
        {
            if (root == null)
                return;

            StampUnlockableId(root, id);

            try
            {
                if (!root.activeSelf)
                    root.SetActive(true);

                AutoParentToShip parent = root.GetComponent<AutoParentToShip>();
                if (parent != null)
                {
                    if (forceDefaultPose)
                        ApplyDefaultFurniturePose(root);

                    parent.disableObject = false;
                    parent.overrideOffset = true;
                    if (parent.positionOffset == Vector3.zero)
                        parent.positionOffset = ResolveDefaultLocalPosition();
                    if (parent.rotationOffset == Vector3.zero)
                        parent.rotationOffset = ResolveDefaultEuler();
                    parent.MoveToOffset();
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed preparing spawned CCTV terminal furniture root: {ex.Message}");
            }
        }

        private static bool IsPrefabTemplateRoot(GameObject root)
        {
            if (root == null || _prefab == null)
                return false;

            if (ReferenceEquals(root, _prefab))
                return true;

            Transform current = root.transform;
            while (current != null)
            {
                if (ReferenceEquals(current.gameObject, _prefab))
                    return true;

                current = current.parent;
            }

            return false;
        }

        private static bool EnsureFurnitureSpawned(int id, UnlockableItem item)
        {
            if (id < 0 || item == null)
                return false;

            item.unlockableType = 1;
            item.IsPlaceable = true;
            item.spawnPrefab = true;
            item.prefabObject = _prefab;

            if (TryGetSpawnedFurnitureRoot(out GameObject existing))
            {
                PrepareSpawnedFurnitureRoot(existing, id, item);
                return true;
            }

            if (item.inStorage)
            {
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] CCTV terminal furniture is unlocked but currently stored; not forcing a spawn.");
                return false;
            }

            StartOfRound start = StartOfRound.Instance;
            if (start == null)
                return false;
            if (!start.IsServer)
                return false;

            try
            {
                if (ShouldUseDefaultPose(item))
                    ApplyDefaultFurniturePose(_prefab);

                if (start.SpawnedShipUnlockables != null &&
                    start.SpawnedShipUnlockables.TryGetValue(id, out GameObject stale) &&
                    stale == null)
                {
                    start.SpawnedShipUnlockables.Remove(id);
                }

                item.hasBeenUnlockedByPlayer = true;
                _spawnUnlockableMethod ??= typeof(StartOfRound).GetMethod(
                    "SpawnUnlockable",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (_spawnUnlockableMethod == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Could not resolve StartOfRound.SpawnUnlockable for CCTV terminal furniture.");
                    return false;
                }

                bool restorePrefabInactive = _prefab != null && !_prefab.activeSelf;
                if (restorePrefabInactive)
                    _prefab.SetActive(true);

                try
                {
                    _spawnUnlockableMethod.Invoke(start, new object[] { id, true });
                }
                finally
                {
                    if (restorePrefabInactive && _prefab != null)
                        _prefab.SetActive(false);
                }

                if (TryGetSpawnedFurnitureRoot(out GameObject spawned))
                {
                    PrepareSpawnedFurnitureRoot(spawned, id, item);
                    SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] Ensured the owned CCTV terminal upgrade marker is spawned (no visible furniture).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to ensure CCTV terminal furniture spawn: {ex.Message}");
            }

            return false;
        }

        private static int FindObjectCredits()
        {
            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                return terminal != null ? terminal.groupCredits : 0;
            }
            catch
            {
                return 0;
            }
        }

        // #561. The Ship Monitor position/rotation calibration is a WORLD-space
        // pose, not a hangar-local one: QuadMonitor.Spawn consumes the raw value as
        // worldPos and converts it into the ship frame with InverseTransformPoint
        // (QuadMonitor.Lifecycle.cs). This class used to TransformPoint the same value as
        // if it were already hangar-local, which applied HangarShip's world offset a
        // second time and dropped the console furniture a full ship-offset behind the
        // hull - outside the interior, so a purchase spawned nothing the player could
        // see. Fresh-profile evidence: QuadMonitor world (10.86, 3.00, -13.00) versus
        // furniture world (12.13, 3.28, -20.50), a delta exactly equal to
        // HangarShip.position. Both now resolve to the same point, which is the intent -
        // the console body carries the monitor.
        private static Vector3 ResolveConfiguredWorldPosition()
        {
            Quaternion rotation = ResolveConfiguredWorldRotation();
            Vector3 position = SurveillanceBootstrap.Config != null
                ? SurveillanceBootstrap.Config.MonitorPosition
                : new Vector3(11f, 3f, -13f);
            // The same 0.14 m step behind the screen plane QuadMonitor applies, so the
            // console sits directly under/behind the monitor rather than through it.
            return position + rotation * new Vector3(0f, 0f, -0.14f);
        }

        private static Quaternion ResolveConfiguredWorldRotation()
        {
            return SurveillanceBootstrap.Config != null
                ? SurveillanceBootstrap.Config.MonitorRotation
                : Quaternion.Euler(0f, 90f, 0f);
        }

        /// <summary>Diagnostics only: the configured world pose expressed in the hangar frame.</summary>
        private static Vector3 ResolveConfiguredHangarLocalPosition()
        {
            Vector3 worldPosition = ResolveConfiguredWorldPosition();
            Transform hangar = ResolveHangarShipTransform();
            return hangar != null ? hangar.InverseTransformPoint(worldPosition) : worldPosition;
        }

        private static Transform ResolveHangarShipTransform()
        {
            GameObject hangar = GameObject.Find("Environment/HangarShip");
            return hangar != null ? hangar.transform : null;
        }

        private static Vector3 ResolveDefaultLocalPosition()
        {
            Vector3 worldPosition = ResolveConfiguredWorldPosition();
            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            return ship != null ? ship.InverseTransformPoint(worldPosition) : worldPosition;
        }

        private static Vector3 ResolveDefaultEuler()
        {
            Quaternion worldRotation = ResolveConfiguredWorldRotation();
            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            Quaternion localRotation = ship != null ? Quaternion.Inverse(ship.rotation) * worldRotation : worldRotation;
            return localRotation.eulerAngles;
        }

        // #561. Vanilla's ShipBuildModeManager reads PlaceableShipObject.yOffset as the
        // height of the object's pivot above the surface it is being placed on. This
        // used to return the monitor's WORLD Y (~3.3 m), a value that has nothing to do
        // with this prefab's pivot and would have launched the console into the ceiling
        // the first time a player picked it up in build mode. The pivot-to-base distance
        // of the actual furniture mesh is the correct answer.
        private static float ResolveDefaultHeight()
        {
            return _defaultPlaceableYOffset;
        }

        private static bool TryFindUnlockable(out int id, out UnlockableItem item)
        {
            id = -1;
            item = null;

            var unlockables = StartOfRound.Instance?.unlockablesList?.unlockables;
            if (unlockables == null)
                return false;

            for (int i = 0; i < unlockables.Count; i++)
            {
                UnlockableItem candidate = unlockables[i];
                if (candidate == null)
                    continue;

                if (ReferenceEquals(candidate, _unlockable) ||
                    string.Equals(candidate.unlockableName, UnlockableName, StringComparison.OrdinalIgnoreCase))
                {
                    id = i;
                    item = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadShipSystemsPurchaseState(out bool purchased)
        {
            purchased = false;

            try
            {
                if (!_shipSystemsMethodResolved)
                {
                    _shipSystemsMethodResolved = true;
                    Type api = ResolveShipSystemsApiType();
                    _shipSystemsHasUpgradeMethod = api?.GetMethod(
                        "HasCctvTerminalUpgrade",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                if (_shipSystemsHasUpgradeMethod == null)
                    return false;

                object value = _shipSystemsHasUpgradeMethod.Invoke(null, null);
                if (value is bool hasUpgrade)
                {
                    purchased = hasUpgrade;
                    return true;
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to read ship-system CCTV terminal state: {ex.Message}");
            }

            return false;
        }

        private static Type ResolveShipSystemsApiType()
        {
            return Y4NGZCompany.Core.Compat.CompanyAssemblyBridge.ResolveType("Y4NGZCompany.ShipSystems.Layout.ShipSystemsApi")
                   ?? Type.GetType("LGUShipSystems.ShipSystemsApi, LGUShipSystems", throwOnError: false);
        }

        // Resolves the API type rather than merely asking whether an assembly of that
        // name is loaded: the purchase state this gate defers to lives on that type, and
        // a build that carries either assembly without the API can no more answer
        // HasCctvTerminalUpgrade than a profile that omits it entirely. Both companion
        // plugins are soft BepInEx dependencies, so they have already chainloaded by the
        // time Register() asks, which is why one memoized answer is enough.
        private static bool IsShipSystemsPresent()
        {
            if (_shipSystemsPresent.HasValue)
                return _shipSystemsPresent.Value;

            _shipSystemsPresent = ResolveShipSystemsApiType() != null;
            return _shipSystemsPresent.Value;
        }

        // #767. The seller is a plugin, not a type: the shop's catalog reflects into
        // ShipSystems at runtime, so the only reliable "will something sell this" signal
        // is the shop's own BepInEx entry. The GUID is Y4NGZCore's
        // ModuleHarmonyIds.TerminalUpgrades, kept as a literal here so a standalone
        // LethalCCTV never touches a Core type it may not have loaded.
        private const string TerminalShopPluginGuid = "com.y4ngz.company.terminalupgrades";

        private static bool IsTerminalShopPresent()
        {
            try
            {
                return BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(TerminalShopPluginGuid);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Could not query the plugin list for the terminal shop: {ex.Message}");
                return false;
            }
        }

        private static void AssignStableNetworkHash(NetworkObject networkObject)
        {
            if (networkObject == null)
                return;

            try
            {
                uint hash;
                using (MD5 md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(SurveillancePluginInfo.PLUGIN_GUID + "." + PrefabName));
                    hash = BitConverter.ToUInt32(bytes, 0);
                }

                Type type = typeof(NetworkObject);
                PropertyInfo property = type.GetProperty("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(networkObject, hash);
                    return;
                }

                FieldInfo field = type.GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? type.GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                field?.SetValue(networkObject, hash);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to assign CCTV terminal NetworkObject hash: {ex.Message}");
            }
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            if (obj == null)
                return;

            try
            {
                obj.tag = tag;
            }
            catch
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Tag '{tag}' was unavailable for CCTV terminal furniture.");
            }
        }

    }

    [HarmonyPatch]
    internal static class CctvTerminalOwnershipLifecyclePatch
    {
        [HarmonyPatch(typeof(StartOfRound), "Awake")]
        [HarmonyPostfix]
        private static void OnSessionAwake(StartOfRound __instance)
        {
            CCTVTerminalUnlockable.BeginOwnershipSession(__instance);
            SurveillanceBootstrap bootstrap = SurveillanceBootstrap.Instance;
            if (bootstrap != null)
                bootstrap.ApplyTerminalCompanionOwnershipPatchOnce();
        }

        [HarmonyPatch(typeof(StartOfRound), "LoadUnlockables")]
        [HarmonyPrefix]
        private static void BeforeLoadUnlockables()
        {
            CCTVTerminalUnlockable.BeforeLoadUnlockables();
        }

        [HarmonyPatch(typeof(StartOfRound), "LoadUnlockables")]
        [HarmonyPostfix]
        private static void AfterLoadUnlockables()
        {
            CCTVTerminalUnlockable.AfterLoadUnlockables();
        }

        [HarmonyPatch(typeof(StartOfRound), "SyncShipUnlockablesClientRpc")]
        [HarmonyPostfix]
        private static void AfterSyncUnlockables()
        {
            CCTVTerminalUnlockable.AfterSyncUnlockables();
        }

        [HarmonyPatch(typeof(StartOfRound), "ResetShipFurniture")]
        [HarmonyPostfix]
        private static void AfterResetFurniture()
        {
            CCTVTerminalUnlockable.AfterResetFurniture();
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void AfterDisconnect()
        {
            // Disconnect calls SaveGame synchronously; clear runtime state only AFTER
            // that call so leaving the lobby cannot erase the saved ownership flag.
            CCTVTerminalUnlockable.BeginOwnershipSession(null);
        }
    }
}
