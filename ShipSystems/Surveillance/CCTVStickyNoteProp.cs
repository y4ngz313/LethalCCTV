using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// #579 — the grabbable CCTV controls note. There is deliberately no
    /// behaviour here beyond adopting the vanilla note's look: the point of the
    /// prop is that it is an ordinary item the player can pick up, carry and set
    /// down wherever they want the reference to live.
    ///
    /// The mesh and material cannot be baked into the prefab at plugin load
    /// because neither exists until StartOfRound has built the item list, so
    /// they are adopted on the first frame this instance runs. Both Start and
    /// OnNetworkSpawn call in because their order differs between the host
    /// (spawned by <see cref="CCTVStickyNoteItem.EnsureSpawnedInShip"/>) and a
    /// late-joining client (spawned by Netcode replication).
    /// </summary>
    internal sealed class CCTVStickyNoteProp : PhysicsProp
    {
        /// <summary>
        /// Frames the Update retry is allowed to keep asking per binding
        /// generation. The vanilla note is reachable within a handful of frames or
        /// it is not coming yet, and an uncapped retry would run TryResolveVisual
        /// on every note every frame for the rest of the session. Roughly twenty
        /// seconds at 60fps. The budget re-arms whenever
        /// <see cref="CCTVStickyNoteItem.VisualBindGeneration"/> moves, so running
        /// it out is a pause, not a permanent surrender.
        /// </summary>
        private const int MaxVisualAttempts = 1200;

        private bool _visualApplied;
        private int _visualAttempts;
        private bool _gaveUpLogged;

        /// <summary>
        /// #582 — set once this note's scale is known to come from the station note
        /// anchor's placement record. That is EVERY note the server places, not only
        /// one an operator resized: the record carries the vanilla size by default
        /// and whatever C/V authored otherwise. It is also set on a client whose
        /// replicated scale differs from the prefab default, which is the only
        /// evidence a client has that the host authored a size.
        ///
        /// The vanilla-scale normalisation in <see cref="ApplyVisual"/> re-runs on
        /// every visual re-bind, so without this latch it would quietly force the
        /// note back to the vanilla size.
        /// </summary>
        private bool _authoredScale;

        /// <summary>
        /// Applies the note anchor's authored scale and stops the vanilla-scale
        /// normalisation from overwriting it. <see cref="GrabbableObject.originalScale"/>
        /// is kept in step because vanilla restores it after a grab.
        /// </summary>
        internal void AdoptAuthoredScale(Vector3 scale)
        {
            if (scale.x <= 0.0001f || scale.y <= 0.0001f || scale.z <= 0.0001f)
                return;

            _authoredScale = true;
            transform.localScale = scale;
            originalScale = scale;
        }

        /// <summary>
        /// The shared binding generation this note last budgeted against. Starting
        /// below zero guarantees the first Update re-arms rather than inheriting a
        /// budget some earlier state left spent.
        /// </summary>
        private int _seenBindGeneration = -1;

        public override void Start()
        {
            base.Start();
            // GeneralImprovements' FixItemsFallingThrough postfix on
            // GrabbableObject.Start ("KEEPING <name> IN PLACE") re-flags settled
            // ship items as un-settled so vanilla snaps them onto their saved
            // spot — but vanilla's settle branch also re-poses the item via
            // itemProperties.restingRotation, which for this note is a -90°
            // pitch: the authored anchor orientation is replaced with face-up.
            // hasHitGround + fallTime>=1 with reachedFloorTarget false can only
            // be that postfix (vanilla Start leaves a non-ground-spawning item
            // fully settled, and a genuine fall runs with fallTime reset to 0),
            // so re-settle here before the first Update can rotate anything.
            if (!reachedFloorTarget && hasHitGround && fallTime >= 1f)
            {
                reachedFloorTarget = true;
                targetFloorPosition = transform.localPosition;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] CCTV sticky note re-settled after an external Start patch un-settled it "
                    + "(GeneralImprovements keep-in-place); authored orientation preserved.");
            }
            ApplyVisual();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            ApplyVisual();
        }

        public override void Update()
        {
            base.Update();

            // Deliberately ahead of the applied-check below. A note that already
            // wears the look still has to notice a re-bind: the borrowed mesh
            // belongs to the ship scene, so a scene swap can destroy it out from
            // under an applied note and leave it invisible again. Re-arming only
            // for un-applied notes would make this check unreachable in exactly
            // the case it exists for.
            int generation = CCTVStickyNoteItem.VisualBindGeneration;
            if (_seenBindGeneration != generation)
            {
                _seenBindGeneration = generation;
                _visualAttempts = 0;
                _gaveUpLogged = false;
                _visualApplied = false;
            }

            // The generation only moves when a *new* binding is made. A mesh that
            // died without one being made yet has to be caught directly, or the
            // note sits there claiming a look it no longer has.
            if (_visualApplied && !HasLiveMesh())
                _visualApplied = false;

            // The vanilla note can arrive after the first frames on a client that
            // joined mid-load. Keep asking until the look is on or the budget is
            // spent.
            if (_visualApplied)
                return;

            if (_visualAttempts >= MaxVisualAttempts)
                return;

            _visualAttempts++;
            ApplyVisual();
            if (!_visualApplied && _visualAttempts >= MaxVisualAttempts && !_gaveUpLogged)
            {
                _gaveUpLogged = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] The CCTV sticky note could not borrow the vanilla note's mesh, so it has no mesh at all "
                    + "and is completely invisible in the ship. Tried " + CCTVStickyNoteItem.DescribeResolutionSources() + ".");
            }
        }

        /// <summary>
        /// Whether the mesh this note is wearing is still alive. The borrowed mesh
        /// is owned by the ship scene, so unloading that scene can destroy it and
        /// silently empty the filter.
        /// </summary>
        private bool HasLiveMesh()
        {
            MeshFilter filter = GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null;
        }

        private void ApplyVisual()
        {
            if (_visualApplied)
                return;
            if (!CCTVStickyNoteItem.TryResolveVisual(out Mesh mesh, out Material material))
                return;

            // Both are required. Latching applied when there was nothing to dress
            // would retire the retry for a note that is still meshless.
            MeshFilter filter = GetComponent<MeshFilter>();
            MeshRenderer renderer = GetComponent<MeshRenderer>();
            if (filter == null || renderer == null)
                return;

            filter.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            mainObjectRenderer = renderer;

            // Take the collider shape and scale from the same vanilla prefab the
            // mesh came from, so a game update that re-authors the note cannot
            // leave our pickup box describing the old one.
            if (CCTVStickyNoteItem.TryResolveColliderShape(out Vector3 size, out Vector3 centre, out float scale))
            {
                BoxCollider box = GetComponent<BoxCollider>();
                if (box != null)
                {
                    box.size = size;
                    box.center = centre;
                }

                // A scale that is not the prefab template's is one the server
                // authored on the anchor and Netcode replicated in the spawn
                // payload. Nothing else can produce it, so on a client it is the
                // authored size — normalising it back to the vanilla note's would
                // make every remote player see a differently sized note than the
                // host (#582 review).
                if (!_authoredScale &&
                    !Mathf.Approximately(transform.localScale.x, CCTVStickyNoteItem.VanillaNoteScale))
                {
                    AdoptAuthoredScale(transform.localScale);
                }

                if (!_authoredScale && scale > 0.0001f && !Mathf.Approximately(transform.localScale.x, scale))
                {
                    transform.localScale = Vector3.one * scale;
                    originalScale = transform.localScale;
                }
            }

            _visualApplied = true;
        }
    }
}
