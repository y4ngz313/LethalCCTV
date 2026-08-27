using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// #579 — the CCTV controls sticky note, registered as a real grabbable item
    /// so it can be picked up and re-placed anywhere in the ship rather than
    /// welded to the monitor wall.
    ///
    /// Everything except the printed face is borrowed from vanilla's
    /// StickyNoteItem at runtime: its mesh already carries the peel curl and the
    /// taped top corner (14 tris), and its material is already the HDRP/Lit
    /// alpha-clip paper setup. Cloning that material rather than authoring one
    /// sidesteps the whole HDRP-shader-in-an-AssetBundle hazard documented in
    /// docs/ASSET_PIPELINE.md, and means the only asset this mod ships for the
    /// note is one embedded PNG — no bundle, no Unity round-trip.
    /// </summary>
    internal static class CCTVStickyNoteItem
    {
        internal const string PrefabName = "Y4NGZ_CCTV_StickyNote";
        private const string TextureResourceName = "Y4NGZ_CCTV_StickyNote.png";
        private const string ItemDisplayName = "CCTV controls";
        private const string VanillaItemName = "Sticky note";

        /// <summary>
        /// The vanilla note's GameObject name in the ship scene. Decompiling the
        /// shipped Assembly-CSharp shows there is no StickyNoteItem class and no
        /// StartOfRound field pointing at the note: it is a plain
        /// <see cref="GrabbableObject"/> placed in the ship scene under this name,
        /// carrying an Item asset whose itemName is <see cref="VanillaItemName"/>.
        /// </summary>
        private const string VanillaObjectName = "StickyNoteItem";

        private const string SourceScene = "the in-scene vanilla note (a GrabbableObject whose itemName is 'Sticky note')";
        private const string SourceCatalogue = "StartOfRound.allItemsList";
        private const string SourceShipHierarchy = "a ship-hierarchy object named 'StickyNoteItem'";

        /// <summary>
        /// How often the per-frame ship step is allowed to sweep the scene while
        /// no note is tracked. The sweep is a full FindObjectsByType, so running it
        /// every frame on a client whose host never spawns a note would be a
        /// permanent cost for nothing.
        /// </summary>
        private const float ExistingScanIntervalSeconds = 0.25f;

        /// <summary>
        /// How often an unresolved binding is allowed to go looking for the vanilla
        /// note. Every LateBind caller funnels through the resolver and one of them
        /// runs every LateUpdate, so without this a profile where no vanilla note
        /// exists would pay a full scene sweep every frame for the whole session.
        /// </summary>
        private const float ResolveRetryIntervalSeconds = 2f;

        /// <summary>
        /// Vanilla's own StickyNoteItem transform scale. The mesh is authored at
        /// roughly 2 units square, so without this the note spawns two metres
        /// wide. Re-read from the vanilla prefab in <see cref="LateBind"/> when
        /// it becomes reachable; this constant only has to carry the prefab from
        /// plugin load until StartOfRound exists.
        /// </summary>
        internal const float VanillaNoteScale = 0.104262345f;

        /// <summary>
        /// Vanilla's flat resting orientation for a sticky note, in ship-local
        /// space. Same value as the Item's own restingRotation.
        /// </summary>
        private static readonly Vector3 VanillaFlatRestingEuler = new Vector3(-90f, 0f, 0f);

        /// <summary>
        /// How far a restored note's rotation may sit from identity before it is
        /// taken as something a player authored rather than something vanilla's
        /// position-only save reconstructed.
        /// </summary>
        private const float RestoredIdentityRotationToleranceDeg = 1f;

        /// <summary>
        /// How close a restored note must be to the note anchor to be treated as
        /// still sitting on it.
        /// </summary>
        private const float RestoredAnchorAdoptionRadius = 0.5f;

        private static readonly Vector3 VanillaColliderSize = new Vector3(2.0155149f, 2.282906f, 0.3427938f);
        private static readonly Vector3 VanillaColliderCentre = new Vector3(-0.007757432f, 0.14145488f, 0.15767327f);

        private static bool _registrationAttempted;
        private static bool _lateBound;
        private static bool _spawnLogged;
        private static float _settleAt = -1f;
        private static float _nextExistingScanAt = -1f;
        private static float _nextResolveAttemptAt = -1f;
        private static bool _spawnPointWarned;

        /// <summary>
        /// The vanilla note object the borrowed mesh and collider shape are read
        /// from, and the label of whichever source produced it. Cleared with the
        /// binding so a scene swap re-resolves rather than holding a dead object.
        /// </summary>
        private static GameObject _vanillaSource;
        private static string _resolvedFrom = SourceScene;

        /// <summary>
        /// The note this process believes is live. Held so the per-frame spawn
        /// step is a null check rather than a scene scan, and reset whenever the
        /// StartOfRound instance changes so a second save load re-discovers the
        /// note vanilla restored for it.
        /// </summary>
        private static CCTVStickyNoteProp _trackedNote;

        /// <summary>
        /// Identity of the StartOfRound the spawn state below belongs to. Loading
        /// a second save in one session builds a new StartOfRound, and every
        /// latched value here has to start over with it.
        /// </summary>
        private static StartOfRound _spawnStateRound;

        private static Texture2D _texture;
        private static Material _material;
        private static Mesh _mesh;

        internal static GameObject Prefab { get; private set; }
        internal static Item ItemProperties { get; private set; }
        internal static bool Registered { get; private set; }

        /// <summary>
        /// #579 — set when <see cref="Register"/> throws. The shipped controls
        /// reference is the note, so the tooltip overlay is off by default; if the
        /// note never registered there would otherwise be no controls reference at
        /// all, and CCTVFocusControlsOverlay forces the panel back on for this.
        /// </summary>
        internal static bool RegistrationFailed { get; private set; }

        /// <summary>
        /// Bumped every time the borrowed mesh and material are (re-)bound. Live
        /// notes watch this so a note that ran out of retries before the vanilla
        /// note was reachable re-arms the moment a binding actually exists,
        /// instead of staying invisible for the rest of the session.
        /// </summary>
        internal static int VisualBindGeneration { get; private set; }

        /// <summary>
        /// Human-readable list of the resolution sources <see cref="FindVanillaPrefab"/>
        /// tries, for the give-up warning.
        /// </summary>
        internal static string DescribeResolutionSources()
        {
            return SourceScene + ", " + SourceCatalogue + ", and " + SourceShipHierarchy;
        }

        // ------------------------------------------------------------------
        // Registration (plugin load)
        // ------------------------------------------------------------------

        internal static void Register()
        {
            if (_registrationAttempted)
                return;

            _registrationAttempted = true;
            try
            {
                Prefab = BuildPrefab();
                ItemProperties = BuildItemProperties(Prefab);

                CCTVStickyNoteProp prop = Prefab.GetComponent<CCTVStickyNoteProp>();
                prop.itemProperties = ItemProperties;
                ItemProperties.spawnPrefab = Prefab;

                // Fully qualified: Unity.Netcode also has a NetworkPrefabs type.
                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(Prefab);
                LethalLib.Modules.Items.RegisterItem(ItemProperties);

                Registered = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] Registered the CCTV controls sticky note as a grabbable item.");
            }
            catch (Exception ex)
            {
                // The note is the only shipped controls reference, so failing here
                // would otherwise leave the player with none: the tooltip overlay
                // defaults to off now. Flagging the failure forces that panel back
                // on regardless of the config value — see CCTVFocusControlsOverlay.
                RegistrationFailed = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] CCTV sticky note registration failed; forcing the operator controls overlay on as the fallback: {ex.Message}");
                if (Prefab != null)
                    UnityEngine.Object.DestroyImmediate(Prefab);
                Prefab = null;
                ItemProperties = null;
            }
        }

        private static GameObject BuildPrefab()
        {
            GameObject root = new GameObject(PrefabName);
            root.SetActive(false);
            root.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.transform.localScale = Vector3.one * VanillaNoteScale;
            SetTagSafe(root, "PhysicsProp");
            root.layer = ResolveLayer("Props", ResolveLayer("InteractableObject", 6));

            NetworkObject networkObject = root.AddComponent<NetworkObject>();
            networkObject.SynchronizeTransform = true;
            networkObject.SceneMigrationSynchronization = true;
            networkObject.SpawnWithObservers = true;
            networkObject.DontDestroyWithOwner = true;
            networkObject.AutoObjectParentSync = true;
            CctvFixtureAssets.AssignStableNetworkHash(networkObject, "StickyNote", PrefabName);

            // Mesh and material are both left empty here: neither exists until
            // StartOfRound has loaded the vanilla item list. CCTVStickyNoteProp
            // fills them in on Start, which is also the first frame anything can
            // see the renderer.
            root.AddComponent<MeshFilter>();
            MeshRenderer renderer = root.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;

            // Vanilla GrabbableObjects do not fall under Unity physics: their body
            // is kinematic and GrabbableObject's own FallToGround/targetFloorPosition
            // lerp places them. A gravity body here would fight that machinery and
            // drift away from the position the fall state believes it is at.
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.mass = 0.35f;
            body.drag = 0.2f;
            body.angularDrag = 5f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            BoxCollider collider = root.AddComponent<BoxCollider>();
            collider.isTrigger = false;
            collider.size = VanillaColliderSize;
            collider.center = VanillaColliderCentre;

            AudioSource audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.dopplerLevel = 0.3f;
            audio.minDistance = 4f;
            audio.maxDistance = 18f;

            CCTVStickyNoteProp prop = root.AddComponent<CCTVStickyNoteProp>();
            prop.grabbable = true;
            prop.grabbableToEnemies = true;
            prop.isInFactory = false;
            prop.scrapValue = 0;
            prop.rotateObject = false;
            prop.propBody = body;
            prop.propColliders = new Collider[] { collider };
            prop.mainObjectRenderer = renderer;

            BuildScanNode(root, collider);
            return root;
        }

        private static void BuildScanNode(GameObject root, BoxCollider pickupCollider)
        {
            var scanObject = new GameObject("ScanNode");
            scanObject.transform.SetParent(root.transform, false);
            scanObject.layer = ResolveLayer("ScanNode", 22);
            scanObject.transform.localPosition = pickupCollider.center;

            BoxCollider scanCollider = scanObject.AddComponent<BoxCollider>();
            scanCollider.isTrigger = true;
            scanCollider.size = pickupCollider.size;

            ScanNodeProperties scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = ItemDisplayName;
            scanNode.subText = "operator reference";
            scanNode.maxRange = 12;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = true;
            scanNode.nodeType = 0;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = 0;
        }

        /// <summary>
        /// Mirrors vanilla's own Sticky note item entry. The hold pose values are
        /// copied verbatim so the note is carried and set down exactly like the
        /// one already in the ship — those numbers are hand-tuned art direction,
        /// not something worth re-deriving.
        /// </summary>
        private static Item BuildItemProperties(GameObject prefab)
        {
            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "Y4NGZ CCTV Sticky Note Item";
            item.itemName = ItemDisplayName;
            item.spawnPositionTypes = new System.Collections.Generic.List<ItemGroup>();
            item.twoHanded = false;
            item.twoHandedAnimation = false;
            item.canBeGrabbedBeforeGameStart = true;
            item.disallowUtilitySlot = true;
            item.weight = 1f;
            item.itemSpawnsOnGround = false;
            item.isConductiveMetal = false;
            item.isScrap = false;
            item.creditsWorth = 0;
            item.minValue = 0;
            item.maxValue = 0;
            item.spawnPrefab = prefab;
            item.requiresBattery = false;
            item.grabAnimationTime = 0f;
            item.syncGrabFunction = false;
            item.syncUseFunction = false;
            item.syncDiscardFunction = false;
            item.toolTips = new[] { "Inspect: [Z]" };
            item.verticalOffset = 0.05f;
            item.floorYOffset = 0;
            item.allowDroppingAheadOfPlayer = true;
            item.restingRotation = new Vector3(-90f, 0f, 0f);
            item.rotationOffset = new Vector3(96.65f, 15.8f, -90f);
            item.positionOffset = new Vector3(0.03f, 0.1f, -0.1f);
            item.meshOffset = false;
            item.canBeInspected = true;
            return item;
        }

        // ------------------------------------------------------------------
        // Vanilla asset resolution (needs StartOfRound)
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolves the mesh and material for a live note. Returns false until
        /// the vanilla item list exists, which is why the caller re-asks rather
        /// than caching a failure.
        ///
        /// The bound-latch is dropped if either borrowed reference has since been
        /// destroyed — a scene unload can take the vanilla mesh with it — so a
        /// dead binding re-binds on the next ask instead of failing closed for the
        /// rest of the process.
        /// </summary>
        internal static bool TryResolveVisual(out Mesh mesh, out Material material)
        {
            if (_lateBound && (_mesh == null || _material == null))
            {
                _lateBound = false;
                _vanillaSource = null;
                // A dead binding is news, not a retry: let the next ask look
                // immediately rather than waiting out the throttle.
                _nextResolveAttemptAt = -1f;
            }
            if (!_lateBound)
                LateBind();

            mesh = _mesh;
            material = _material;
            return mesh != null && material != null;
        }

        internal static bool TryResolveColliderShape(out Vector3 size, out Vector3 centre, out float scale)
        {
            size = VanillaColliderSize;
            centre = VanillaColliderCentre;
            scale = VanillaNoteScale;

            GameObject vanilla = FindVanillaPrefab(out _, out _);
            if (vanilla == null)
                return false;

            // Only a candidate carrying its own root BoxCollider is describing a
            // whole note. One matched through a child mesh would hand back a root
            // scale of 1, and that is exactly how you get a two-metre sticky note:
            // the mesh is authored around two units square and nothing but the
            // vanilla scale shrinks it. In that case the hand-measured constants
            // above are the better answer.
            BoxCollider box = vanilla.GetComponent<BoxCollider>();
            if (box == null)
                return true;

            size = box.size;
            centre = box.center;
            float vanillaScale = vanilla.transform.localScale.x;
            if (vanillaScale > 0.0001f)
                scale = vanillaScale;
            return true;
        }

        /// <summary>
        /// Resolves the vanilla note to borrow from, most specific source first,
        /// handing back the MeshFilter and MeshRenderer it matched on so the caller
        /// does not have to look them up again.
        ///
        /// The note is <em>not</em> an ordinary catalogue item: there is no
        /// StickyNoteItem class and no StartOfRound field referencing one, and
        /// because the note is scene-placed it does not have to appear in
        /// allItemsList at all. That is exactly why the old itemsList-only lookup
        /// here never resolved and the note spawned with no mesh.
        ///
        /// Order is by specificity, not by convenience: the named object under the
        /// ship is unambiguous, the catalogue entry is next in case a game update
        /// ever promotes the note to a real item, and the loose itemName sweep of
        /// every GrabbableObject in the scene goes last because it is the one that
        /// could match something a mod introduced.
        ///
        /// While nothing is resolved, attempts are rate limited: every caller of
        /// <see cref="LateBind"/> funnels through here, and one of them runs every
        /// LateUpdate, so an unthrottled miss would mean a full scene sweep per
        /// frame forever on a profile where no vanilla note exists.
        /// </summary>
        private static GameObject FindVanillaPrefab(out MeshFilter filter, out MeshRenderer renderer)
        {
            if (_vanillaSource != null && TryGetBorrowableVisual(_vanillaSource, out filter, out renderer))
                return _vanillaSource;
            _vanillaSource = null;

            filter = null;
            renderer = null;
            if (Time.realtimeSinceStartup < _nextResolveAttemptAt)
                return null;
            _nextResolveAttemptAt = Time.realtimeSinceStartup + ResolveRetryIntervalSeconds;

            GameObject candidate = FindShipNoteByObjectName();
            string source = SourceShipHierarchy;

            if (!TryGetBorrowableVisual(candidate, out filter, out renderer))
            {
                candidate = FindCatalogueNotePrefab();
                source = SourceCatalogue;
            }

            if (!TryGetBorrowableVisual(candidate, out filter, out renderer))
            {
                candidate = FindSceneNoteByItemName();
                source = SourceScene;
            }

            if (!TryGetBorrowableVisual(candidate, out filter, out renderer))
                return null;

            _vanillaSource = candidate;
            _resolvedFrom = source;
            return candidate;
        }

        /// <summary>
        /// A candidate is only usable if it can actually supply both halves of the
        /// look. Checking here rather than in each finder means a hollowed-out
        /// match never blocks the next source from being tried.
        /// </summary>
        private static bool TryGetBorrowableVisual(GameObject candidate, out MeshFilter filter, out MeshRenderer renderer)
        {
            filter = null;
            renderer = null;
            if (candidate == null)
                return false;

            filter = candidate.GetComponentInChildren<MeshFilter>(true);
            if (filter == null || filter.sharedMesh == null)
                return false;

            renderer = candidate.GetComponentInChildren<MeshRenderer>(true);
            return renderer != null && renderer.sharedMaterial != null;
        }

        /// <summary>
        /// The most specific source: the object vanilla names StickyNoteItem in the
        /// ship scene. The hierarchy walk covers a note someone has carried out of
        /// the elevator's immediate children; the path lookup afterwards is a last
        /// resort and, being GameObject.Find, only ever sees active objects — a
        /// pocketed note is disabled and invisible to it.
        /// </summary>
        private static GameObject FindShipNoteByObjectName()
        {
            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            if (ship != null)
            {
                Transform[] children = ship.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < children.Length; i++)
                {
                    Transform child = children[i];
                    if (child == null || !IsVanillaNoteName(child.gameObject.name))
                        continue;
                    return child.gameObject;
                }
            }

            return GameObject.Find("Environment/HangarShip/" + VanillaObjectName);
        }

        /// <summary>
        /// Matches the vanilla note's object name, allowing only the suffixes Unity
        /// itself appends — "(Clone)" and " (1)". A loose substring test would also
        /// accept anything a mod named, say, "FakeStickyNoteItemHolder".
        /// </summary>
        private static bool IsVanillaNoteName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.Equals(VanillaObjectName, StringComparison.OrdinalIgnoreCase))
                return true;
            return name.StartsWith(VanillaObjectName + " ", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(VanillaObjectName + "(", StringComparison.OrdinalIgnoreCase);
        }

        private static GameObject FindCatalogueNotePrefab()
        {
            var list = StartOfRound.Instance?.allItemsList?.itemsList;
            if (list == null)
                return null;

            for (int i = 0; i < list.Count; i++)
            {
                Item item = list[i];
                if (item == null || item.spawnPrefab == null)
                    continue;
                if (!string.Equals(item.itemName, VanillaItemName, StringComparison.OrdinalIgnoreCase))
                    continue;
                return item.spawnPrefab;
            }

            return null;
        }

        /// <summary>
        /// Last resort: any GrabbableObject in the scene whose itemName is the
        /// vanilla note's. Inactive objects are included because a pocketed note is
        /// disabled while it sits in an inventory slot — but a note that is being
        /// held or pocketed is only accepted if nothing settled matches, since a
        /// held item is mid-animation and its transform says nothing useful about
        /// the note's resting shape.
        /// </summary>
        private static GameObject FindSceneNoteByItemName()
        {
            GrabbableObject[] grabbables = UnityEngine.Object.FindObjectsByType<GrabbableObject>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (grabbables == null)
                return null;

            GameObject heldFallback = null;
            for (int i = 0; i < grabbables.Length; i++)
            {
                GrabbableObject candidate = grabbables[i];
                // Our own note answers to a different itemName, but excluding the
                // type outright keeps this honest if that name ever changes.
                if (candidate == null || candidate is CCTVStickyNoteProp)
                    continue;

                Item properties = candidate.itemProperties;
                if (properties == null)
                    continue;
                if (!string.Equals(properties.itemName, VanillaItemName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (candidate.isHeld || candidate.isPocketed)
                {
                    if (heldFallback == null)
                        heldFallback = candidate.gameObject;
                    continue;
                }

                return candidate.gameObject;
            }

            return heldFallback;
        }

        /// <summary>
        /// One-shot fill of everything that could not exist at plugin load: the
        /// borrowed mesh and material, and the grab/drop audio and inventory icon
        /// that make the note behave like the vanilla one in the HUD.
        /// </summary>
        private static void LateBind()
        {
            if (_lateBound)
                return;

            GameObject vanilla = FindVanillaPrefab(out MeshFilter filter, out MeshRenderer renderer);
            if (vanilla == null)
                return;

            Material rebuilt = BuildMaterial(renderer.sharedMaterial);
            if (rebuilt == null)
                return;

            // The previous clone is DontDestroyOnLoad, so nothing in the process
            // would ever free it: a re-bind after a scene swap would leak one
            // material per swap.
            if (_material != null && !ReferenceEquals(_material, rebuilt))
                UnityEngine.Object.Destroy(_material);

            _mesh = filter.sharedMesh;
            _material = rebuilt;

            CopyVanillaItemPresentation(vanilla);
            _lateBound = true;
            // Live notes watch this to re-arm their retry budget, so it has to be
            // bumped after the binding is complete, not before.
            VisualBindGeneration++;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] CCTV sticky note bound to the vanilla note mesh '{_mesh.name}' and a clone of '{renderer.sharedMaterial.name}', resolved from {_resolvedFrom}.");
        }

        private static void CopyVanillaItemPresentation(GameObject vanillaPrefab)
        {
            if (ItemProperties == null)
                return;

            GrabbableObject grabbable = vanillaPrefab.GetComponent<GrabbableObject>();
            Item source = grabbable != null ? grabbable.itemProperties : null;
            if (source == null)
                return;

            if (ItemProperties.itemIcon == null)
                ItemProperties.itemIcon = source.itemIcon;
            ItemProperties.grabSFX = ItemProperties.grabSFX != null ? ItemProperties.grabSFX : source.grabSFX;
            ItemProperties.dropSFX = ItemProperties.dropSFX != null ? ItemProperties.dropSFX : source.dropSFX;
            ItemProperties.pocketSFX = ItemProperties.pocketSFX != null ? ItemProperties.pocketSFX : source.pocketSFX;
            ItemProperties.throwSFX = ItemProperties.throwSFX != null ? ItemProperties.throwSFX : source.throwSFX;
        }

        private static Material BuildMaterial(Material vanillaMaterial)
        {
            Texture2D texture = LoadEmbeddedTexture();
            if (texture == null)
                return null;

            var material = new Material(vanillaMaterial) { name = "Y4NGZ_CCTV_StickyNoteMaterial" };
            // HDRP/Lit reads _BaseColorMap; _MainTex is kept in step so a
            // non-HDRP fallback shader (or an inspector) shows the same face.
            AssignTexture(material, "_BaseColorMap", texture);
            AssignTexture(material, "_MainTex", texture);
            // Same reason the texture is kept: a scene unload would otherwise
            // destroy this clone and leave every live note with a null material.
            UnityEngine.Object.DontDestroyOnLoad(material);
            return material;
        }

        private static void AssignTexture(Material material, string property, Texture2D texture)
        {
            if (material != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static Texture2D LoadEmbeddedTexture()
        {
            if (_texture != null)
                return _texture;

            try
            {
                Assembly assembly = typeof(CCTVStickyNoteItem).Assembly;
                string resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith("." + TextureResourceName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(resourceName))
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] Embedded sticky note texture '{TextureResourceName}' was not found in the assembly.");
                    return null;
                }

                byte[] bytes;
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        return null;
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        bytes = buffer.ToArray();
                    }
                }

                // Vanilla's own note texture is point-filtered with no mip chain;
                // its lettering is fat enough to survive that. Ours carries six
                // rows of much finer marker strokes, so it needs real filtering
                // or it aliases into mush at operating distance.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: false);
                if (!texture.LoadImage(bytes, markNonReadable: true))
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                texture.name = "Y4NGZ_CCTV_StickyNoteTex";
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 4;
                UnityEngine.Object.DontDestroyOnLoad(texture);
                _texture = texture;
                return _texture;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to load the embedded sticky note texture: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Ship spawn
        // ------------------------------------------------------------------

        /// <summary>
        /// Puts one note in the ship the first time a save has none. Called from
        /// the station's LateUpdate step, so it also self-heals if the note is
        /// destroyed. Saved notes come back through vanilla's own ship-item
        /// persistence, so this must not fire when one already exists.
        /// </summary>
        internal static void EnsureSpawnedInShip()
        {
            if (!Registered)
                return;

            StartOfRound round = StartOfRound.Instance;
            if (round == null)
                return;

            // Every latch below belongs to one StartOfRound. Loading a second save
            // in the same session builds a new one, and without this reset the
            // settle window would still be spent from the first load — the note
            // would spawn before vanilla restored the saved one, duplicating it.
            if (!ReferenceEquals(_spawnStateRound, round))
            {
                _spawnStateRound = round;
                _settleAt = -1f;
                _nextExistingScanAt = -1f;
                _nextResolveAttemptAt = -1f;
                _spawnLogged = false;
                _spawnPointWarned = false;
                _trackedNote = null;
                // The borrowed source is a scene object; a new StartOfRound means
                // a new ship scene, so the old one is gone.
                _vanillaSource = null;
            }

            LateBind();

            // Deliberately before the server gate. A note vanilla restored from the
            // save is instantiated straight from Item.spawnPrefab by
            // StartOfRound.LoadShipGrabbableItems, which never activates the clone
            // — and our registered prefab is an inactive HideAndDontSave template,
            // so the restored note arrives inactive and hidden on the host and on
            // every client alike. An inactive object never runs Start, so it cannot
            // repair itself; the repair has to come from here, on both sides.
            if (_trackedNote == null && Time.realtimeSinceStartup >= _nextExistingScanAt)
            {
                _nextExistingScanAt = Time.realtimeSinceStartup + ExistingScanIntervalSeconds;
                _trackedNote = FindExistingNote();
            }

            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || !network.IsListening)
                return;

            // Vanilla loads saved ship items during StartOfRound.Start. Spawning
            // before that finishes would put a second note in every loaded save.
            if (_settleAt < 0f)
                _settleAt = Time.realtimeSinceStartup + 3f;
            if (Time.realtimeSinceStartup < _settleAt)
                return;

            // The tracked reference is the duplicate guard, and the throttled sweep
            // above is the only thing that fills it. Re-scanning here would put a
            // full scene scan back on every frame — exactly the cost the throttle
            // exists to remove — and it cannot be throttled independently either:
            // the sweep above always consumes the shared window first, so a second
            // throttled check would block the spawn forever. The sweep runs every
            // quarter second while nothing is tracked, well inside the three-second
            // settle window, so nothing restored by vanilla can slip past it.
            if (_trackedNote != null)
                return;

            Transform anchor = CCTVOperatorStation.EnsureNoteAnchor();
            Transform elevator = round.elevatorTransform;
            if (anchor == null || elevator == null)
            {
                // Once per save load, and deliberately not fatal: the anchor lives
                // under the station root, which only exists once the CCTV system is
                // purchased and the station has resolved its pose. This step runs
                // every frame, so the note spawns the moment that happens.
                if (!_spawnPointWarned)
                {
                    _spawnPointWarned = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV] CCTV sticky note anchor unavailable (operator station root not spawned yet); "
                        + "the note will spawn as soon as the station resolves.");
                }
                return;
            }

            try
            {
                // Parented at instantiate time, mirroring StartOfRound.LoadShipGrabbableItems:
                // a ship item that is spawned unparented and re-parented afterwards has
                // already replicated a world pose that vanilla later reads as elevator-local.
                GameObject note = UnityEngine.Object.Instantiate(
                    Prefab, anchor.position, anchor.rotation, elevator);
                // The prefab is HideAndDontSave so it never shows in the hierarchy
                // or gets swept by a scene unload; a live clone must be an ordinary
                // object or nothing that enumerates the scene can see it — the
                // duplicate scan above included.
                note.hideFlags = HideFlags.None;
                note.SetActive(true);

                // Scale is authored BEFORE Spawn() on purpose: Netcode replicates
                // the transform once, in the spawn payload. A localScale written
                // after the spawn call only ever exists on the host, and every
                // client would keep the prefab's default size (#582 review).
                CCTVStickyNoteProp prop = note.GetComponent<CCTVStickyNoteProp>();
                prop?.AdoptAuthoredScale(anchor.localScale);

                note.GetComponent<NetworkObject>().Spawn();

                PlaceAtAnchor(note, anchor);
                _trackedNote = prop;

                if (!_spawnLogged)
                {
                    _spawnLogged = true;
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] Spawned the CCTV controls sticky note at the station note anchor: " +
                        $"world={anchor.position} anchorLocal={anchor.localPosition} euler={anchor.eulerAngles}.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed to spawn the CCTV sticky note: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds a note this process did not spawn — restored from a save, or
        /// replicated in. The component type is what makes a note a note, and a
        /// save-restored clone carries it just the same.
        ///
        /// Resources.FindObjectsOfTypeAll rather than FindObjectsByType on purpose:
        /// the exact object this has to catch is a clone of our HideAndDontSave
        /// template, and FindObjectsByType does not promise to return objects
        /// flagged DontSave. Missing it would be doubly bad — the restored note
        /// would never be re-activated, and the duplicate guard would be blind to
        /// it, so every save/load cycle would add another invisible note. The price
        /// is that FindObjectsOfTypeAll also returns prefabs and other non-scene
        /// objects, which the scene check below filters out.
        /// </summary>
        private static CCTVStickyNoteProp FindExistingNote()
        {
            CCTVStickyNoteProp[] found = Resources.FindObjectsOfTypeAll<CCTVStickyNoteProp>();
            if (found == null)
                return null;

            for (int i = 0; i < found.Length; i++)
            {
                CCTVStickyNoteProp candidate = found[i];
                // The disabled prefab template lives in the same object space and
                // would otherwise read as an existing note forever.
                if (candidate == null || Prefab != null && candidate.gameObject == Prefab)
                    continue;
                // Assets and prefab templates report an invalid or unloaded scene.
                // Only something actually instantiated into the world counts as an
                // existing note.
                UnityEngine.SceneManagement.Scene scene = candidate.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;
                NormalizeRestoredNote(candidate.gameObject);
                RepairRestoredNoteRotation(candidate);
                return candidate;
            }

            return null;
        }

        /// <summary>
        /// Repairs a note that vanilla restored from the save. Vanilla clones
        /// Item.spawnPrefab verbatim, so a clone of our inactive HideAndDontSave
        /// template comes back inactive and flagged not-to-save: invisible, and
        /// unable to run the Start that adopts the vanilla look.
        ///
        /// Only objects still carrying the template's hide flags are touched, so a
        /// note that is merely disabled because someone pocketed it is left alone.
        /// </summary>
        private static void NormalizeRestoredNote(GameObject note)
        {
            if (note == null || note.hideFlags == HideFlags.None)
                return;

            note.hideFlags = HideFlags.None;
            if (!note.activeSelf)
                note.SetActive(true);

            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Re-activated a save-restored CCTV sticky note that vanilla cloned from the inactive prefab template.");
        }

        /// <summary>
        /// #582 — vanilla's ship-item persistence stores a position and nothing
        /// else: LoadShipGrabbableItems re-instantiates every saved item with
        /// Quaternion.identity. A note that spawned at an authored anchor therefore
        /// comes back from a save load standing bolt upright, and the tracked-note
        /// duplicate guard stops the spawn path from ever re-placing it.
        ///
        /// One-shot at adoption, server-side. An identity rotation is the tell that
        /// vanilla — not a player — last wrote it: a note still sitting on its
        /// anchor gets the anchor's full authored rotation back, and one the crew
        /// carried elsewhere only gets the flat resting orientation the vanilla note
        /// itself uses, since its authored pose says nothing about where it now is.
        /// </summary>
        private static void RepairRestoredNoteRotation(CCTVStickyNoteProp note)
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || !network.IsListening)
                return;
            if (!CanRepositionNote(note))
                return;

            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            Quaternion localRotation = ToShipLocalRotation(ship, note.transform.rotation);
            if (Quaternion.Angle(localRotation, Quaternion.identity) > RestoredIdentityRotationToleranceDeg)
                return;

            Transform anchor = CCTVOperatorStation.NoteAnchor;
            bool onAnchor = anchor != null &&
                            (anchor.position - note.transform.position).sqrMagnitude <=
                            RestoredAnchorAdoptionRadius * RestoredAnchorAdoptionRadius;

            Quaternion repairedLocal = onAnchor
                ? ToShipLocalRotation(ship, anchor.rotation)
                : Quaternion.Euler(VanillaFlatRestingEuler);
            note.transform.rotation = ship != null ? ship.rotation * repairedLocal : repairedLocal;
            note.floorYRot = Mathf.RoundToInt(repairedLocal.eulerAngles.y);

            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Restored CCTV sticky note came back from the save with no rotation; re-applied the " +
                (onAnchor ? "authored note anchor orientation." : "vanilla flat resting orientation."));
        }

        /// <summary>
        /// #582 — publishes a freshly authored anchor pose to the live note, called
        /// by the ship-scoped placement handlers after an F9 Y4NGZDebugTools save or
        /// delete has already updated the anchor. The note carries no
        /// NetworkTransform, so a transform write would be host-local; the only
        /// replicating move is a despawn/respawn one-shot, and that is server-only.
        /// A non-host still saves the record — it is their own local placement
        /// profile — but their live note cannot move until a host round spawns from
        /// it, and the returned message says so instead of leaving them guessing.
        /// </summary>
        internal static string RespawnForAuthoring()
        {
            NetworkManager network = NetworkManager.Singleton;
            if (network == null || !network.IsServer || !network.IsListening)
                return "HOST ONLY respawn skipped - the pose applies when a host spawns the note from this profile";

            CCTVStickyNoteProp note = _trackedNote;
            if (note == null)
                return "no live note tracked yet - it will spawn at the authored pose";
            if (!CanRepositionNote(note))
                return "note is held or parented right now - the authored pose applies on its next respawn";

            RespawnNoteAtAnchor();
            return "live note is respawning at the authored pose for every client";
        }

        /// <summary>
        /// A note may only be repositioned onto the anchor while nothing else owns
        /// its pose. Held/pocketed covers the local player, isHeldByEnemy covers a
        /// hoarding bug carrying it off, and parentObject covers anything that has
        /// re-parented the prop (an enemy hand, a container).
        /// </summary>
        private static bool CanRepositionNote(CCTVStickyNoteProp note)
        {
            return note != null &&
                   !note.isHeld &&
                   !note.isPocketed &&
                   !note.isHeldByEnemy &&
                   note.parentObject == null;
        }

        /// <summary>
        /// #582 — the note carries no NetworkTransform, so a host-side transform
        /// write is invisible to every connected client. Once the authoring session
        /// ends, the only way to publish the final pose is to despawn the note and
        /// let <see cref="EnsureSpawnedInShip"/> spawn a fresh one at the anchor on
        /// the next tick: the new spawn payload replicates position, rotation and
        /// scale to everyone in one shot.
        /// </summary>
        private static void RespawnNoteAtAnchor()
        {
            CCTVStickyNoteProp note = _trackedNote;
            if (!CanRepositionNote(note))
                return;
            if (CCTVOperatorStation.NoteAnchor == null)
                return;

            try
            {
                NetworkObject networkObject = note.GetComponent<NetworkObject>();
                if (networkObject != null && networkObject.IsSpawned)
                    networkObject.Despawn(destroy: true);
                else
                    UnityEngine.Object.Destroy(note.gameObject);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Failed to despawn the CCTV sticky note for its re-place: {ex.Message}");
                return;
            }

            _trackedNote = null;
            // The settle window is a first-load duplicate guard; it has long since
            // elapsed here, and the respawn must not wait it out again.
            _settleAt = 0f;
            // Hold the existing-note sweep off for one interval so it cannot adopt
            // the note that is still being destroyed at the end of this frame.
            _nextExistingScanAt = Time.realtimeSinceStartup + ExistingScanIntervalSeconds;
            _spawnLogged = false;

            SurveillanceBootstrap.Log?.LogMessage(
                "[LethalCCTV] CCTV sticky note edit session ended; despawned the note so the next tick respawns it " +
                "at the authored anchor pose for every client.");
        }

        /// <summary>
        /// Settles a note the way vanilla settles a ship item: parented to the
        /// elevator, fall state already complete, and both floor positions
        /// expressed in the parent's space. Assigning them from an unparented
        /// transform would store world coordinates that vanilla later reinterprets
        /// as local the moment it parents the item to the elevator.
        ///
        /// The transform rotation write is what holds the authored orientation: the
        /// settled path is entered with reachedFloorTarget already true, and the
        /// per-frame euler rewrite lives in the branch that is still landing, so
        /// nothing reads itemProperties.restingRotation here. It is deliberately
        /// NOT written: restingRotation is shared by every instance of this Item,
        /// so stamping the anchor's pitch and roll into it would corrupt the
        /// orientation of every later player drop (#582 review).
        ///
        /// Server only — see <see cref="RespawnForAuthoring"/>.
        /// </summary>
        private static void PlaceAtAnchor(GameObject note, Transform anchor)
        {
            if (note == null || anchor == null)
                return;

            var grabbable = note.GetComponent<GrabbableObject>();
            if (grabbable == null)
                return;

            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            // Re-parenting is a replicated operation (AutoObjectParentSync), so it
            // is server-only, and it is skipped entirely when the note already
            // hangs off the elevator — which is the normal case, since the spawn
            // path parents at instantiate time.
            NetworkManager network = NetworkManager.Singleton;
            bool isServer = network != null && network.IsServer && network.IsListening;
            if (isServer && ship != null && note.transform.parent != ship)
                note.transform.SetParent(ship, worldPositionStays: true);

            note.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
            // The station root and the elevator are both authored at unit scale, so
            // the anchor's local scale is already the note's scale in ship space.
            // This is what makes the editor's C/V keys resize the note; routing it
            // through the prop also retires the prop's vanilla-scale normalisation.
            if (grabbable is CCTVStickyNoteProp prop)
                prop.AdoptAuthoredScale(anchor.localScale);
            else
                note.transform.localScale = anchor.localScale;

            grabbable.isInShipRoom = true;
            grabbable.isInElevator = true;
            grabbable.isInFactory = false;
            grabbable.scrapPersistedThroughRounds = true;
            grabbable.isHeld = false;
            grabbable.isPocketed = false;
            grabbable.heldByPlayerOnServer = false;
            grabbable.playerHeldBy = null;

            // Landed, not falling: the pose is authored, so there is nothing for
            // FallToGround to lerp — and nothing to raycast for either, which is
            // what used to drop the note through the floor (#582).
            grabbable.fallTime = 1f;
            grabbable.hasHitGround = true;
            grabbable.reachedFloorTarget = true;
            grabbable.targetFloorPosition = ship != null
                ? ship.InverseTransformPoint(anchor.position)
                : note.transform.localPosition;
            grabbable.startFallingPosition = grabbable.targetFloorPosition;

            // Bookkeeping only: floorYRot is what vanilla's own save/drop paths read
            // back as the item's settled yaw. The exact orientation is already on
            // the transform from the SetPositionAndRotation above, and is NOT
            // re-derived from this rounded degree value — doing so would quantise
            // the authored pose for nothing.
            grabbable.floorYRot = Mathf.RoundToInt(ToShipLocalRotation(ship, anchor.rotation).eulerAngles.y);
        }

        private static Quaternion ToShipLocalRotation(Transform ship, Quaternion worldRotation)
        {
            return ship != null ? Quaternion.Inverse(ship.rotation) * worldRotation : worldRotation;
        }

        // ------------------------------------------------------------------

        private static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
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
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Tag '{tag}' was not available for {obj.name}.");
            }
        }
    }
}
