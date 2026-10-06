using System.Collections.Generic;
using DunGen;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// Standalone-profile fix for #569: on vanilla-only installs the game's
    /// AdjacentRoomCullingModified sets renderer.enabled=false on every tile
    /// renderer outside the culling origin's neighbourhood — and while the
    /// operator sits in the ship, StartOfRound.UpdateOcclusionCuller drags
    /// that origin to the vanilla radar target, so a CCTV camera elsewhere in
    /// the dungeon sees only non-tile objects floating in the skybox.
    ///
    /// This re-enables the camera's own tile neighbourhood for exactly the
    /// duration of that camera's render pass (beginCameraRendering /
    /// endCameraRendering), then restores, so player-camera culling is
    /// untouched. Safe against the culler because its only renderer writes
    /// happen in LateUpdate, which precedes all rendering.
    ///
    /// Inert when CullFactory is installed — its per-camera culling already
    /// keeps tiles visible for rendering cameras, and two writers of
    /// renderer.enabled must never fight.
    /// </summary>
    internal static class CctvTileCullingBypass
    {
        private sealed class CameraEntry
        {
            internal CCTVCamera Holder;
            // Tile the cached neighbourhood was built from. The F2 placement
            // editor can reassign holder.OwningTile at runtime, so the cache
            // invalidates whenever the holder's tile no longer matches.
            internal Tile CachedTile;
            internal List<Tile> Tiles;
        }

        private static readonly Dictionary<Camera, CameraEntry> _entries = new Dictionary<Camera, CameraEntry>();
        private static readonly List<Renderer> _reenabledRenderers = new List<Renderer>(256);
        private static readonly List<Light> _reenabledLights = new List<Light>(32);
        private static readonly List<Tile> _tileScratch = new List<Tile>(8);
        private static bool _hooksRegistered;
        // Depth counter + owner reference, same accepted interleave risk as
        // CCTVCameraVisual's s_physicalCameraHideDepth (sequential HDRP
        // begin/end pairs assumed; do not "fix" one without the other). The
        // owner reference keeps the pair symmetric even when the camera is
        // destroyed mid-render (Unity fake-null still reference-matches) or
        // unregistered between begin and end — an asymmetric bail would leave
        // the depth stuck and the last re-enabled tile set visible to the
        // player camera forever.
        private static int _bypassDepth;
        private static Camera _bypassOwner;
        // Frame the depth was last raised above zero. A begin whose matching end
        // never fires (a third-party begin hook throwing, HDRP bailing on a
        // released RT) would otherwise wedge the depth at 1 forever: every later
        // CCTV begin early-returns and the last re-enabled batch stays visible to
        // the player camera. Rendering never spans frames, so a stamp older than
        // the current frame with depth still up is proof of an unmatched pair.
        private static int _bypassFrame;
        private static bool _bypassLogged;
        private static bool _inertLogged;

        internal static void Register(Camera cam, CCTVCamera holder)
        {
            if (cam == null || holder == null) return;
            if (CullFactoryCompat.IsLoaded)
            {
                if (!_inertLogged)
                {
                    _inertLogged = true;
                    SurveillanceBootstrap.Log?.LogInfo(
                        "[LethalCCTV] Tile culling bypass inert: CullFactory owns per-camera culling.");
                }
                return;
            }

            EnsureHooksRegistered();
            // Tile set resolves lazily on the first render pass: at bind time
            // the culler may not be Ready yet and AdjacentTileDepth unset.
            _entries[cam] = new CameraEntry { Holder = holder, CachedTile = null, Tiles = null };
        }

        internal static void Unregister(Camera cam)
        {
            if (cam == null) return;
            _entries.Remove(cam);
        }

        /// <summary>
        /// Wholesale sweep, same contract as NightVisionBaker.ClearRegistrations:
        /// cameras and tiles are destroyed with the dungeon and replaced each
        /// round, so stale Unity-object keys would otherwise accumulate. The
        /// transient bypass state is reset with them (restoring anything still
        /// force-enabled first): a sweep that dropped the entries but left the
        /// depth raised would silently disable the bypass for the next round.
        /// </summary>
        internal static void ClearRegistrations()
        {
            _entries.Clear();
            RestoreReenabledState();
            _bypassDepth = 0;
            _bypassOwner = null;
            _bypassFrame = 0;
        }

        internal static void Shutdown()
        {
            ClearRegistrations();
            if (_hooksRegistered)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
                _hooksRegistered = false;
            }
            _bypassLogged = false;
            _inertLogged = false;
        }

        private static void EnsureHooksRegistered()
        {
            if (_hooksRegistered) return;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            _hooksRegistered = true;
        }

        // Fires for every camera in the process; misses early-return on one
        // Dictionary.TryGetValue (same discipline as NightVisionBaker).
        private static void OnBeginCameraRendering(ScriptableRenderContext _, Camera cam)
        {
            if (cam is null) return;
            if (!_entries.TryGetValue(cam, out CameraEntry entry)) return;

            int frame = Time.frameCount;
            // Self-heal an unmatched begin/end pair. Nesting is only ever
            // same-frame, so depth still raised from an EARLIER frame means an
            // end callback was skipped; without this the depth sticks up forever
            // and the last force-enabled batch leaks into the player camera
            // (see-through-walls, and the #569 regression on every feed).
            if (_bypassDepth > 0 && _bypassFrame != frame)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[TileCullingBypass] recovering from unmatched begin/end " +
                    $"(depth={_bypassDepth}, stampedFrame={_bypassFrame}, now={frame})");
                RestoreReenabledState();
                _bypassDepth = 0;
                _bypassOwner = null;
            }

            // Nested renders (a scheduled feed snapshot inside another pass, OBC) must
            // not double-enter: the outer pass owns the restore.
            _bypassDepth++;
            _bypassFrame = frame;
            if (_bypassDepth > 1) return;
            _bypassOwner = cam;

            AdjacentRoomCullingModified culler = ResolveCuller();
            if (culler == null || !culler.enabled) return;

            Tile owningTile = entry.Holder != null ? entry.Holder.OwningTile : null;
            if (entry.Tiles == null || !ReferenceEquals(owningTile, entry.CachedTile))
            {
                entry.CachedTile = owningTile;
                entry.Tiles = BuildTileNeighbourhood(owningTile, culler.AdjacentTileDepth);
            }

            _reenabledRenderers.Clear();
            _reenabledLights.Clear();
            Dictionary<Tile, List<Renderer>> tileRenderers = culler.tileRenderers;
            Dictionary<Tile, List<Light>> lightSources = culler.lightSources;
            for (int t = 0; t < entry.Tiles.Count; t++)
            {
                Tile tile = entry.Tiles[t];
                if (tile == null) continue;

                if (tileRenderers != null && tileRenderers.TryGetValue(tile, out List<Renderer> renderers))
                {
                    for (int i = 0; i < renderers.Count; i++)
                    {
                        Renderer renderer = renderers[i];
                        if (renderer == null || renderer.enabled)
                            continue;
                        // CCTVCameraVisual's begin handler (subscribed first,
                        // runs first) just disabled the physical camera
                        // meshes; re-enabling one here would film the
                        // camera's own housing.
                        if (CCTVCameraVisual.IsRegisteredPhysicalCameraRenderer(renderer))
                            continue;
                        renderer.enabled = true;
                        _reenabledRenderers.Add(renderer);
                    }
                }

                // SetTileVisibility disables the tile's lights with its
                // renderers; a renderer-only bypass would render geometry lit
                // by nothing but the night-vision fill light.
                //
                // Known limitation (watch item): the culler's per-tile light
                // list is the only signal here, so a light that some OTHER
                // system switched off — the breaker blackout, RoundManager's
                // indirect-light mode — is re-enabled on the feed too for the
                // duration of the bypassed render. If blackout-lit feeds are
                // ever reported, this loop is the site to add discrimination
                // (record the disabling owner, or skip lights the culler did
                // not itself disable this frame).
                if (lightSources != null && lightSources.TryGetValue(tile, out List<Light> lights))
                {
                    for (int i = 0; i < lights.Count; i++)
                    {
                        Light light = lights[i];
                        if (light != null && !light.enabled)
                        {
                            light.enabled = true;
                            _reenabledLights.Add(light);
                        }
                    }
                }
            }

            if (!_bypassLogged && (_reenabledRenderers.Count > 0 || _reenabledLights.Count > 0))
            {
                _bypassLogged = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Tile culling bypass active for CCTV render: tiles={entry.Tiles.Count} " +
                    $"renderers={_reenabledRenderers.Count} lights={_reenabledLights.Count} (#569 skybox-feed fix).");
            }
        }

        private static void OnEndCameraRendering(ScriptableRenderContext _, Camera cam)
        {
            // Reference checks only: a camera destroyed between begin and end
            // is Unity-fake-null but still reference-matches, and an entry
            // removed mid-pass must not leave the depth counter stuck. The
            // owner's end ALWAYS restores.
            if (cam is null) return;
            if (_bypassDepth <= 0) return;
            if (!ReferenceEquals(cam, _bypassOwner))
            {
                if (_entries.ContainsKey(cam))
                    _bypassDepth--;
                return;
            }
            _bypassDepth--;
            _bypassOwner = null;
            if (_bypassDepth > 0)
                _bypassDepth = 0;

            RestoreReenabledState();
        }

        /// <summary>
        /// Puts every renderer and light this pass force-enabled back to
        /// disabled and empties the batches. Everything in the lists was
        /// disabled when the pass began, and the culler's only writes happen in
        /// LateUpdate (before rendering), so flipping back to disabled is
        /// authoritative for this frame. Idempotent: a second call with empty
        /// lists does nothing, which is what makes it safe to share between the
        /// normal end path, the unmatched-pair recovery, and ClearRegistrations.
        /// </summary>
        private static void RestoreReenabledState()
        {
            for (int i = 0; i < _reenabledRenderers.Count; i++)
            {
                Renderer renderer = _reenabledRenderers[i];
                if (renderer != null)
                    renderer.enabled = false;
            }
            for (int i = 0; i < _reenabledLights.Count; i++)
            {
                Light light = _reenabledLights[i];
                if (light != null)
                    light.enabled = false;
            }
            _reenabledRenderers.Clear();
            _reenabledLights.Clear();
        }

        private static AdjacentRoomCullingModified ResolveCuller()
        {
            StartOfRound sor = StartOfRound.Instance;
            return sor != null ? sor.occlusionCuller : null;
        }

        private static List<Tile> BuildTileNeighbourhood(Tile owningTile, int depth)
        {
            List<Tile> result = new List<Tile>(8);
            if (owningTile == null) return result;

            depth = Mathf.Max(1, depth);
            result.Add(owningTile);
            _tileScratch.Clear();
            _tileScratch.Add(owningTile);
            int frontierStart = 0;
            for (int d = 0; d < depth; d++)
            {
                int frontierEnd = _tileScratch.Count;
                for (int f = frontierStart; f < frontierEnd; f++)
                {
                    Tile tile = _tileScratch[f];
                    if (tile == null || tile.UsedDoorways == null) continue;
                    for (int i = 0; i < tile.UsedDoorways.Count; i++)
                    {
                        Doorway doorway = tile.UsedDoorways[i];
                        Tile neighbour = doorway != null && doorway.ConnectedDoorway != null
                            ? doorway.ConnectedDoorway.Tile
                            : null;
                        if (neighbour == null || result.Contains(neighbour)) continue;
                        result.Add(neighbour);
                        _tileScratch.Add(neighbour);
                    }
                }
                frontierStart = frontierEnd;
            }
            _tileScratch.Clear();
            return result;
        }
    }
}
