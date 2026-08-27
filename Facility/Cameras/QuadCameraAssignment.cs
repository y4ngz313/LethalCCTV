using System.Collections.Generic;
using DunGen;
using Y4NGZCompany.ShipSystems.Surveillance;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// Camera assignment for the CCTV render targets. The current vanilla monitor station
    /// presents one visible feed at a time; legacy backing slots are retained only so
    /// older wall/overlay plumbing can continue to bind through slot 0.
    /// </summary>
    internal static class QuadCameraAssignment
    {
        private const int SLOT_COUNT = 4;

        // Per-camera render budget.
        // Runtime render rate comes from config; the fallback is the documented default.
        private const float DEFAULT_CAMERA_FPS = 24f;
        private const float DEFAULT_INACTIVE_CAMERA_FPS = 3f;
        private const float SEATED_STATION_CAMERA_FPS_CAP = 24f;
        private const float SEATED_STATION_INACTIVE_CAMERA_FPS_CAP = 1f;
        private const float MIN_INACTIVE_CAMERA_FPS = 0f;
        // #562 — unmanned idle rate for the single feed left on the wall.
        private const float DEFAULT_UNMANNED_IDLE_CAMERA_FPS = 3f;
        private const float MIN_UNMANNED_IDLE_CAMERA_FPS = 0f;
        private const float MAX_UNMANNED_IDLE_CAMERA_FPS = 10f;
        private const float MIN_CAMERA_FPS = 1f;
        private const float MAX_CAMERA_FPS = 30f;

        // Indoor-dungeon-scoped far clip. Unity's default is 1000m; nothing on a CCTV
        // feed needs to draw past the configured short indoor distance.
        private const float DEFAULT_CCTV_FAR_CLIP_M = 40f;
        private const float MIN_CCTV_FAR_CLIP_M = 4f;
        private const float MAX_CCTV_FAR_CLIP_M = 100f;
        private const float FLOOR_GROUP_TOLERANCE_M = 7.5f;

        // Phase 1.6b — derived from Phase 1.6 D1 diagnostic. Player gameplay camera's
        // cullingMask was 0x233B17FF, covering Default/Room/MiscLevelGeometry/Terrain/
        // MoldSpore/Enemies/Foliage/MapHazards and others (21 layers). We mirror that
        // onto CCTV cameras with two bits explicitly cleared: UI (bit 5) so the player
        // HUD doesn't appear on CCTV feeds, and HelmetVisor (bit 7) so the visor overlay
        // doesn't appear on CCTV feeds. Bit 3 (Player) is intentionally KEPT so other
        // players are visible on CCTV; first-person self-render exclusion is handled by
        // gameplayCamera-level config in vanilla LC, not by player-layer mask exclusion.
        private const int PLAYER_CAMERA_MASK = 0x233B17FF;
        private const int UI_LAYER = 5;
        private const int HELMET_VISOR_LAYER = 7;
        private const int CCTV_CULLING_MASK = PLAYER_CAMERA_MASK & ~((1 << UI_LAYER) | (1 << HELMET_VISOR_LAYER));
        // Result: 0x233B175F

        private static readonly CCTVCamera[] _boundCameras = new CCTVCamera[SLOT_COUNT];
        // #305 — SelectCameras idempotence. _boundCameras alone cannot distinguish
        // "empty slot" from "broken camera bound offline" (both bind the black RT
        // with different labels), so the last label target is tracked per slot.
        private static readonly CCTVCamera[] _lastLabelCameras = new CCTVCamera[SLOT_COUNT];
        private static bool _selectionCacheValid;
        private static long _selectionSkips;
        private static float _nextSelectionSkipReportAt;
        private static IReadOnlyList<CCTVCamera> _spawnedCameras;
        private static readonly List<CCTVCamera> _supplementaryCameras = new List<CCTVCamera>();
        private static readonly List<CCTVCamera> _pendingSupplementaryCameras = new List<CCTVCamera>();
        private static int _cachedCullingMask;

        // Phase 1.5a — paging state. CurrentPage is zero-indexed; the indicator displays
        // it as 1-indexed. Reset to 0 on each Assign (new dungeon = new camera ordering,
        // so prior page index has no meaningful continuity).
        internal static int CurrentPage { get; private set; }
        // Assignment order includes base dungeon cameras plus drained supplementary cameras.
        internal static IReadOnlyList<CCTVCamera> AllAssignedCameras => _spawnedCameras;
        internal static int TotalCameraCount => _spawnedCameras != null ? _spawnedCameras.Count : 0;
        // One camera per page: the vanilla monitor station shows a single feed.
        internal static int TotalPages => TotalCameraCount;

        // Read-only accessor used by FocusMouselook to resolve the active slot's
        // CCTV camera (mouselook reads its tile + applies yaw/pitch offsets through
        // the holder).
        internal static CCTVCamera GetBoundCamera(int slot) =>
            (slot >= 0 && slot < SLOT_COUNT) ? _boundCameras[slot] : null;

        internal static void Assign(IReadOnlyList<CCTVCamera> spawnedCameras)
        {
            // Clear any prior round's binds before fresh binds. Removes ordering dependency
            // on the caller — Assign is self-contained across round-to-round transitions
            // even when QuadMonitor.Spawn is idempotent (PLAN tighten #1).
            Unassign();
            _supplementaryCameras.Clear();

            // Phase 1.7b — sweep the bake registration map. Cameras are tile-parented
            // and destroyed wholesale on dungeon regen, so any keys left in the map
            // from the prior round are destroyed-Unity-object references. The handler
            // null-check prevents crashes, but the entries themselves persist until
            // Shutdown without this. Belt-and-braces alongside the per-slot Unregister
            // in TearDownSlot above (which Unassign just ran).
            NightVisionBaker.ClearRegistrations();
            CctvTileCullingBypass.ClearRegistrations();

            if (QuadMonitor.QuadRTs == null)
            {
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] QuadCameraAssignment.Assign: QuadRTs null — monitor not spawned, skipping.");
                return;
            }

            DrainPendingSupplementaryCameras();
            _spawnedCameras = BuildSpatialPageOrder(BuildCombinedCameraList(spawnedCameras));
            RadarOverlay.SetCameras(_spawnedCameras);
            VanillaRadarFeed.SetCameras(_spawnedCameras);
            _cachedCullingMask = ResolveCullingMask();
            CurrentPage = 0;

            int n = TotalCameraCount;
            if (n == 0)
            {
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] QuadCameraAssignment.Assign: no cameras spawned; binding the visible CCTV feed to empty fallback.");
            }

            // Initial page is page 0. In vanilla monitor mode only slot 0 is visible;
            // out-of-range indices map to -1 so the feed blanks cleanly.
            int[] initialIndices = ComputePageIndices(0);
            SelectCameras(initialIndices);
            QuadMonitor.UpdatePageIndicator(CurrentPage, TotalPages);

            // Freshen the RTs immediately so a dungeon transition does not leave
            // the previous round's last frame on the wall for up to 1/15s while
            // the throttle warms up. Cheap one-shot synchronous render — rare event.
            QuadMonitor.SetPhysicalDisplayBlanked(n == 0);
            // #305 — raster the radar map now (round-start load) instead of on the
            // first focus entry.
            RadarOverlay.Prewarm();
            if (MonitorFocus.IsFocused)
            {
                WakeRender();
            }
        }

        /// <summary>
        /// Load-bearing primitive for the legacy render-target backing array. Vanilla
        /// monitor mode uses only index 0 for the single visible CCTV feed.
        /// </summary>
        internal static void SelectCameras(int[] cameraIndices)
        {
            if (cameraIndices == null || cameraIndices.Length != SLOT_COUNT)
            {
                SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV] SelectCameras: expected exactly {SLOT_COUNT} indices, got {(cameraIndices != null ? cameraIndices.Length : 0)} — ignoring.");
                return;
            }
            if (QuadMonitor.QuadRTs == null || QuadMonitor.QuadMaterials == null)
            {
                SurveillanceBootstrap.Log.LogWarning("[LethalCCTV] SelectCameras: monitor not constructed — ignoring.");
                return;
            }

            // #305 — repeated same-outcome calls (OnCameraBroken re-issuing the
            // current page, zero-camera GoToPage, churn loops) previously paid a
            // full teardown/rebind per call: throttle re-attach, HDRP camera-data
            // re-add, OutlineEffect custom-pass re-registration, and four log
            // lines. Skip outright when every slot already holds the requested
            // outcome.
            if (_selectionCacheValid && SelectionMatchesCurrentBindings(cameraIndices))
            {
                _selectionSkips++;
                float nowTime = Time.unscaledTime;
                if (nowTime >= _nextSelectionSkipReportAt)
                {
                    _nextSelectionSkipReportAt = nowTime + 30f;
                    if (_selectionSkips > 1)
                        SurveillanceBootstrap.Log.LogInfo(
                            $"[LethalCCTV] SelectCameras: skipped {_selectionSkips} redundant rebind(s) in the last 30s.");
                    _selectionSkips = 0;
                }
                return;
            }

            int n = TotalCameraCount;
            for (int slot = 0; slot < SLOT_COUNT; slot++)
            {
                TearDownSlot(slot);

                int cameraIndex = cameraIndices[slot];
                if (cameraIndex < 0 || cameraIndex >= n)
                {
                    QuadMonitor.BindEmptyToSlot(slot);
                    QuadMonitor.UpdateCameraLabel(slot, null);
                    _lastLabelCameras[slot] = null;
                    continue;
                }

                CCTVCamera holder = _spawnedCameras[cameraIndex];
                if (holder == null || holder.Cam == null)
                {
                    SurveillanceBootstrap.Log.LogWarning($"[LethalCCTV] SelectCameras: camera index {cameraIndex} has null holder/Cam — slot {slot} bound empty.");
                    QuadMonitor.BindEmptyToSlot(slot);
                    QuadMonitor.UpdateCameraLabel(slot, null);
                    _lastLabelCameras[slot] = null;
                    continue;
                }

                if (holder.IsSecurityBroken)
                {
                    SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] SelectCameras: camera index {cameraIndex} is broken — slot {slot} bound offline.");
                    QuadMonitor.BindEmptyToSlot(slot);
                    QuadMonitor.UpdateCameraLabel(slot, holder);
                    _lastLabelCameras[slot] = holder;
                    continue;
                }

                BindCameraToSlot(holder, slot);
                // Spawn index, NOT slot — the CAM NN label tracks the bound camera's identity
                // (from LethalCCTVCamera_N_<tile> naming, stashed as CameraIndex at spawn).
                QuadMonitor.UpdateCameraLabel(slot, holder);
                _lastLabelCameras[slot] = holder;
            }
            _selectionCacheValid = true;

            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] SelectCameras: visible feed camera [{cameraIndices[0]}] " +
                $"(cullingMask=0x{_cachedCullingMask:X8}, farClip={ResolveInteriorFarClipPlane():F0}m).");
            RadarOverlay.UpdateHighlights();
            QuadMonitor.SetPhysicalDisplayBlanked(TotalCameraCount == 0);
        }

        /// <summary>
        /// Page jump with wrap-around. Vanilla monitor mode pages one camera per screen;
        /// legacy mode still fills the backing slot array for older display paths.
        /// </summary>
        internal static void GoToPage(int pageZeroIndexed)
        {
            int total = TotalPages;
            if (total <= 0)
            {
                // Zero-camera state — nothing meaningful to page through. Keep CurrentPage at 0
                // and re-issue an all-empty bind so the slots remain black.
                CurrentPage = 0;
                SelectCameras(new[] { -1, -1, -1, -1 });
                QuadMonitor.UpdatePageIndicator(0, 0);
                return;
            }
            int wrapped = ((pageZeroIndexed % total) + total) % total;
            CurrentPage = wrapped;
            SelectCameras(ComputePageIndices(wrapped));
            QuadMonitor.UpdatePageIndicator(CurrentPage, total);
            // Phase 1.7b — synchronous one-shot render of the newly bound page so
            // mid-focus paging never shows a previously-bound camera's last baked
            // frame on the same-slot QuadRTs while waiting up to 1/15s for the next
            // throttle tick. No double-fire with Assign's WakeRender (Assign calls
            // SelectCameras directly, not GoToPage).
            QuadMonitor.SetPhysicalDisplayBlanked(TotalCameraCount == 0);
            if (MonitorFocus.IsFocused)
            {
                WakeRender();
            }
        }

        internal static bool TrySelectCamera(CCTVCamera camera, out int slot)
        {
            slot = -1;
            if (camera != null && camera.IsSecurityBroken)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] TrySelectCamera: camera '{DescribeCamera(camera)}' is broken and cannot be selected.");
                return false;
            }

            int cameraIndex = IndexOfCamera(camera);
            if (cameraIndex < 0)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] TrySelectCamera: camera '{DescribeCamera(camera)}' was not found in the current page order.");
                return false;
            }

            int page = cameraIndex;
            slot = 0;
            GoToPage(page);
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] TrySelectCamera: selected camera='{DescribeCamera(camera)}' index={cameraIndex} page={page + 1}/{TotalPages} slot={slot}.");
            return true;
        }

        internal static void OnCameraBroken(CCTVCamera camera)
        {
            if (camera == null) return;
            for (int slot = 0; slot < SLOT_COUNT; slot++)
            {
                if (ReferenceEquals(_boundCameras[slot], camera))
                {
                    GoToPage(CurrentPage);
                    return;
                }
            }
        }

        internal static int IndexOfCamera(CCTVCamera camera)
        {
            if (camera == null || _spawnedCameras == null) return -1;
            for (int i = 0; i < _spawnedCameras.Count; i++)
            {
                CCTVCamera candidate = _spawnedCameras[i];
                if (ReferenceEquals(candidate, camera))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 0.0.8 — deferred wake for the render-loop call site. Sets the
        /// per-throttle pending flag on every bound camera. The throttle consumes
        /// those requests over a short stagger, outside the render loop, by
        /// flipping cam.enabled = true so HDRP renders each camera on its own
        /// pass. This is the SAFE-FROM-RENDER-LOOP entry point: any caller
        /// running inside the HDRP render loop must use it. Synchronous
        /// WakeRender (below) remains for the lifecycle/input-driven callers
        /// (EnterFocus, Assign, GoToPage) where Camera.Render is safe.
        /// </summary>
        internal static void RequestWakeAllSlots()
        {
            RequestWakeSlot(0, 0f);
        }

        private static void RequestWakeSlot(int slot, float delaySeconds)
        {
            if (slot < 0 || slot >= SLOT_COUNT) return;
            CCTVCamera holder = _boundCameras[slot];
            if (holder == null) return;
            Camera cam = holder.Cam;
            if (cam == null) return;
            var throttle = cam.GetComponent<CCTVCameraThrottle>();
            if (throttle != null) throttle.RequestWake(delaySeconds);
        }

        // One-shot synchronous render of every bound camera. Called from the
        // SAFE-CONTEXT wake paths: MonitorFocus.EnterFocus (input handler),
        // Assign (RoundManager lifecycle event), GoToPage (input handler).
        // NEVER call this from inside the HDRP render loop — Camera.Render
        // re-enters SRP and trips "Collection was modified" on HDRP's camera
        // List enumeration. Render-loop callers MUST use RequestWakeAllSlots
        // instead.
        internal static void WakeRender()
        {
            WakeRenderSlot(0);
        }

        private static void WakeRenderSlot(int slot)
        {
            if (slot < 0 || slot >= SLOT_COUNT) return;
            CCTVCamera holder = _boundCameras[slot];
            if (holder == null) return;
            Camera cam = holder.Cam;
            if (cam == null) return;
            if (cam.targetTexture == null) return;
            CCTVCameraVisual.HidePhysicalCameraRenderersForCctv();
            try
            {
                cam.Render();
            }
            finally
            {
                CCTVCameraVisual.RestorePhysicalCameraRenderersAfterCctv();
            }
        }

        /// <summary>
        /// #305 — true when every slot's current bound camera AND label target
        /// already equal what this cameraIndices request would produce (mirroring
        /// the branch outcomes in SelectCameras, including broken → bound-empty
        /// with an OFFLINE label). A destroyed Cam on a still-referenced holder
        /// resolves to expected-null and therefore forces the full rebind pass.
        /// </summary>
        private static bool SelectionMatchesCurrentBindings(int[] cameraIndices)
        {
            int n = TotalCameraCount;
            for (int slot = 0; slot < SLOT_COUNT; slot++)
            {
                CCTVCamera expectedBound = null;
                CCTVCamera expectedLabel = null;
                int cameraIndex = cameraIndices[slot];
                if (cameraIndex >= 0 && cameraIndex < n)
                {
                    CCTVCamera holder = _spawnedCameras[cameraIndex];
                    if (holder != null && holder.Cam != null)
                    {
                        expectedLabel = holder;
                        if (!holder.IsSecurityBroken)
                            expectedBound = holder;
                    }
                }

                if (!ReferenceEquals(_boundCameras[slot], expectedBound)) return false;
                if (!ReferenceEquals(_lastLabelCameras[slot], expectedLabel)) return false;
            }
            return true;
        }

        internal static void Unassign()
        {
            for (int slot = 0; slot < SLOT_COUNT; slot++)
            {
                TearDownSlot(slot);
                QuadMonitor.BindEmptyToSlot(slot);
                QuadMonitor.UpdateCameraLabel(slot, null);
            }
            // #305 — these binds bypassed SelectCameras; the next call must run a
            // full pass (fresh monitor spawn may also have replaced the materials).
            _selectionCacheValid = false;
            System.Array.Clear(_lastLabelCameras, 0, SLOT_COUNT);
            // Phase 1.7b — also wipe the bake map. TearDownSlot.Unregister handled
            // each currently-bound slot's entry, but a sweep here protects against
            // any edge where the dictionary outpaced _boundCameras.
            NightVisionBaker.ClearRegistrations();
            CctvTileCullingBypass.ClearRegistrations();
            _spawnedCameras = null;
            _supplementaryCameras.Clear();
            if (CCTVTerminalUnlockable.IsPurchased())
                _pendingSupplementaryCameras.RemoveAll(camera => camera == null);
            else
                _pendingSupplementaryCameras.Clear();
            CurrentPage = 0;
            RadarOverlay.ClearCameras();
            VanillaRadarFeed.ClearCameras();
            QuadMonitor.UpdatePageIndicator(0, 0);
        }

        // Post-spawn hook used by InteriorSupportCameraInjector to force a
        // camera into a tile (currently the rooms that own the global
        // support fixtures: mainframe, alarm box, mini-vaults).
        // Re-builds the spatial page order including the new camera so it
        // shows up on the wall monitor and the radar schematic.
        internal static void RegisterSupplementaryCamera(CCTVCamera camera)
        {
            if (camera == null) return;
            if (!CCTVTerminalUnlockable.IsPurchased())
            {
                _pendingSupplementaryCameras.Remove(camera);
                _supplementaryCameras.Remove(camera);
                if (camera.gameObject != null)
                    UnityEngine.Object.Destroy(camera.gameObject);
                SurveillanceBootstrap.Log?.LogInfo("[LethalCCTV] RegisterSupplementaryCamera ignored; CCTV terminal is not purchased.");
                return;
            }
            if (_spawnedCameras == null)
            {
                if (!_pendingSupplementaryCameras.Contains(camera))
                {
                    _pendingSupplementaryCameras.Add(camera);
                    SurveillanceBootstrap.Log.LogInfo(
                        $"[LethalCCTV] RegisterSupplementaryCamera queued camera='{camera.name}' until base camera assignment is ready.");
                }
                return;
            }

            if (!_supplementaryCameras.Contains(camera))
                _supplementaryCameras.Add(camera);

            Assign(BuildCombinedCameraList(_spawnedCameras));
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] RegisterSupplementaryCamera assigned camera='{DescribeCamera(camera)}' orderIndex={IndexOfCamera(camera)} total={TotalCameraCount}.");
        }

        internal static void RemoveCamera(CCTVCamera camera)
        {
            if (camera == null) return;
            _supplementaryCameras.Remove(camera);
            _pendingSupplementaryCameras.Remove(camera);
            if (_spawnedCameras == null) return;

            var remaining = new List<CCTVCamera>(_spawnedCameras.Count);
            for (int i = 0; i < _spawnedCameras.Count; i++)
            {
                CCTVCamera existing = _spawnedCameras[i];
                if (existing == null || ReferenceEquals(existing, camera)) continue;
                remaining.Add(existing);
            }

            Assign(remaining);
        }

        private static void DrainPendingSupplementaryCameras()
        {
            for (int i = _pendingSupplementaryCameras.Count - 1; i >= 0; i--)
            {
                CCTVCamera camera = _pendingSupplementaryCameras[i];
                _pendingSupplementaryCameras.RemoveAt(i);
                if (camera == null) continue;
                if (!_supplementaryCameras.Contains(camera))
                    _supplementaryCameras.Add(camera);
            }
        }

        private static List<CCTVCamera> BuildCombinedCameraList(IReadOnlyList<CCTVCamera> baseCameras)
        {
            int baseCount = baseCameras != null ? baseCameras.Count : 0;
            var combined = new List<CCTVCamera>(baseCount + _supplementaryCameras.Count);
            if (baseCameras != null)
            {
                for (int i = 0; i < baseCameras.Count; i++)
                {
                    CCTVCamera camera = baseCameras[i];
                    if (camera != null && !combined.Contains(camera))
                        combined.Add(camera);
                }
            }

            for (int i = 0; i < _supplementaryCameras.Count; i++)
            {
                CCTVCamera camera = _supplementaryCameras[i];
                if (camera != null && !combined.Contains(camera))
                    combined.Add(camera);
            }

            return combined;
        }

        private static int[] ComputePageIndices(int pageZeroIndexed)
        {
            int n = TotalCameraCount;
            int[] result = new int[SLOT_COUNT];
            result[0] = pageZeroIndexed >= 0 && pageZeroIndexed < n ? pageZeroIndexed : -1;
            for (int slot = 1; slot < SLOT_COUNT; slot++)
                result[slot] = -1;
            return result;
        }

        private static IReadOnlyList<CCTVCamera> BuildSpatialPageOrder(IReadOnlyList<CCTVCamera> source)
        {
            var supportInterior = new List<CCTVCamera>(source != null ? source.Count : 0);
            var interior = new List<CCTVCamera>(source != null ? source.Count : 0);
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    CCTVCamera camera = source[i];
                    if (camera == null) continue;
                    if (IsSupportCamera(camera)) supportInterior.Add(camera);
                    else interior.Add(camera);
                }
            }
            supportInterior.Sort(CompareCameraIdentity);

            var groups = BuildFloorGroups(interior);
            var ordered = new List<CCTVCamera>(interior.Count);
            for (int i = 0; i < groups.Count; i++)
            {
                ordered.AddRange(BuildSpatialOrderWithinFloor(groups[i].Cameras));
            }

            var combined = new List<CCTVCamera>(supportInterior.Count + ordered.Count);
            combined.AddRange(supportInterior);
            combined.AddRange(ordered);
            LogSpatialPageOrder(combined);
            return combined;
        }

        private static bool IsSupportCamera(CCTVCamera camera)
        {
            string source = camera != null ? camera.PlacementSource : null;
            if (!string.IsNullOrEmpty(source) && source.StartsWith("support", System.StringComparison.OrdinalIgnoreCase))
                return true;
            string label = camera != null ? camera.ResolvedLabel : null;
            return !string.IsNullOrEmpty(label) && label.EndsWith("S", System.StringComparison.OrdinalIgnoreCase);
        }

        internal static string BuildCameraLabel(CCTVCamera camera)
        {
            if (camera == null) return null;
            return camera.IsSecurityBroken ? camera.ResolvedLabel + " OFFLINE" : camera.ResolvedLabel;
        }

        private static List<FloorCameraGroup> BuildFloorGroups(List<CCTVCamera> cameras)
        {
            var groups = new List<FloorCameraGroup>(4);
            if (cameras == null) return groups;

            for (int i = 0; i < cameras.Count; i++)
            {
                CCTVCamera camera = cameras[i];
                if (camera == null) continue;
                float y = ResolveCameraFloorY(camera);
                int groupIndex = FindFloorGroup(groups, y);
                if (groupIndex < 0)
                {
                    groups.Add(new FloorCameraGroup(y));
                    groups.Sort((a, b) => a.FloorY.CompareTo(b.FloorY));
                    groupIndex = FindFloorGroup(groups, y);
                }
                groups[groupIndex].Cameras.Add(camera);
            }

            groups.Sort((a, b) => a.FloorY.CompareTo(b.FloorY));
            int entranceGroup = ResolveMainEntranceFloorGroupIndex(groups);
            for (int i = 0; i < groups.Count; i++)
            {
                groups[i].FloorRank = i - entranceGroup;
            }
            groups.Sort(CompareFloorCameraGroups);
            return groups;
        }

        private static int FindFloorGroup(List<FloorCameraGroup> groups, float y)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (Mathf.Abs(groups[i].FloorY - y) <= FLOOR_GROUP_TOLERANCE_M)
                    return i;
            }
            return -1;
        }

        private static int ResolveMainEntranceFloorGroupIndex(List<FloorCameraGroup> groups)
        {
            if (groups == null || groups.Count == 0) return 0;
            if (!TryResolveInteriorMainEntranceY(out float entranceY)) return 0;

            int best = 0;
            float bestDiff = Mathf.Abs(groups[0].FloorY - entranceY);
            for (int i = 1; i < groups.Count; i++)
            {
                float diff = Mathf.Abs(groups[i].FloorY - entranceY);
                if (diff >= bestDiff) continue;
                best = i;
                bestDiff = diff;
            }
            return best;
        }

        private static bool TryResolveInteriorMainEntranceY(out float y)
        {
            EntranceTeleport[] entrances = UnityEngine.Object.FindObjectsByType<EntranceTeleport>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            EntranceTeleport best = null;
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null || entrance.isEntranceToBuilding) continue;
                if (best == null || entrance.entranceId == 0)
                {
                    best = entrance;
                    if (entrance.entranceId == 0) break;
                }
            }

            if (best != null)
            {
                Transform entrancePoint = best.entrancePoint != null ? best.entrancePoint : best.transform;
                y = entrancePoint.position.y;
                return true;
            }

            y = 0f;
            return false;
        }

        private static int CompareFloorCameraGroups(FloorCameraGroup a, FloorCameraGroup b)
        {
            int ak = FloorOrderKey(a != null ? a.FloorRank : 0);
            int bk = FloorOrderKey(b != null ? b.FloorRank : 0);
            int cmp = ak.CompareTo(bk);
            if (cmp != 0) return cmp;
            float ay = a != null ? a.FloorY : 0f;
            float by = b != null ? b.FloorY : 0f;
            return ay.CompareTo(by);
        }

        private static int FloorOrderKey(int floorRank)
        {
            return floorRank >= 0 ? floorRank : 1000 + (-floorRank);
        }

        private static IReadOnlyList<CCTVCamera> BuildSpatialOrderWithinFloor(List<CCTVCamera> cameras)
        {
            var remaining = new List<CCTVCamera>(cameras != null ? cameras.Count : 0);
            if (cameras != null)
            {
                for (int i = 0; i < cameras.Count; i++)
                {
                    if (cameras[i] != null) remaining.Add(cameras[i]);
                }
            }

            if (remaining.Count <= 1) return remaining;

            Vector3 entrance = ResolveEntranceAnchor(remaining);
            remaining.Sort((a, b) => CompareCameraProgress(a, b, entrance));

            if (remaining.Count <= SLOT_COUNT)
                return remaining;

            var ordered = new List<CCTVCamera>(remaining.Count);
            while (remaining.Count > 0)
            {
                CCTVCamera seed = remaining[0];
                remaining.RemoveAt(0);

                var page = new List<CCTVCamera>(SLOT_COUNT) { seed };
                while (page.Count < SLOT_COUNT && remaining.Count > 0)
                {
                    int nearest = FindNearestPageMate(page, remaining, entrance);
                    page.Add(remaining[nearest]);
                    remaining.RemoveAt(nearest);
                }

                ordered.AddRange(page);
                remaining.Sort((a, b) => CompareCameraProgress(a, b, entrance));
            }

            return ordered;
        }

        private static int FindNearestPageMate(List<CCTVCamera> page, List<CCTVCamera> remaining, Vector3 entrance)
        {
            Vector3 center = Vector3.zero;
            float seedProgress = GetCameraProgress(page[0], entrance);
            for (int i = 0; i < page.Count; i++)
            {
                center += CameraPosition(page[i]);
            }
            center /= Mathf.Max(1, page.Count);

            int best = 0;
            float bestScore = float.PositiveInfinity;
            for (int i = 0; i < remaining.Count; i++)
            {
                CCTVCamera candidate = remaining[i];
                Vector3 pos = CameraPosition(candidate);
                float progressDelta = Mathf.Abs(GetCameraProgress(candidate, entrance) - seedProgress);
                float score =
                    (pos - center).sqrMagnitude +
                    (pos - CameraPosition(page[0])).sqrMagnitude * 0.35f +
                    progressDelta * progressDelta * 180f;

                if (score < bestScore - 0.001f ||
                    (Mathf.Abs(score - bestScore) <= 0.001f && CompareCameraIdentity(candidate, remaining[best]) < 0))
                {
                    best = i;
                    bestScore = score;
                }
            }
            return best;
        }

        private static Vector3 ResolveEntranceAnchor(List<CCTVCamera> cameras)
        {
            if (cameras == null || cameras.Count == 0) return Vector3.zero;
            CCTVCamera best = cameras[0];
            Vector3 provisionalAnchor = CameraPosition(best);
            for (int i = 1; i < cameras.Count; i++)
            {
                if (CompareCameraProgress(cameras[i], best, provisionalAnchor) < 0)
                {
                    best = cameras[i];
                    provisionalAnchor = CameraPosition(best);
                }
            }
            return CameraPosition(best);
        }

        private static int CompareCameraProgress(CCTVCamera a, CCTVCamera b, Vector3 entrance)
        {
            float ap = GetCameraProgress(a, entrance);
            float bp = GetCameraProgress(b, entrance);
            if (Mathf.Abs(ap - bp) > 0.001f) return ap.CompareTo(bp);
            return CompareCameraIdentity(a, b);
        }

        private static int CompareCameraIdentity(CCTVCamera a, CCTVCamera b)
        {
            int ai = a != null ? a.CameraIndex : int.MaxValue;
            int bi = b != null ? b.CameraIndex : int.MaxValue;
            int cmp = ai.CompareTo(bi);
            if (cmp != 0) return cmp;
            string an = a != null && a.OwningTile != null ? a.OwningTile.name : "";
            string bn = b != null && b.OwningTile != null ? b.OwningTile.name : "";
            return string.CompareOrdinal(an, bn);
        }

        private static string DescribeCamera(CCTVCamera camera)
        {
            if (camera == null) return "<null>";
            Transform transform = camera.transform;
            Vector3 pos = transform != null ? transform.position : Vector3.zero;
            return $"{camera.ResolvedLabel}#{camera.CameraIndex}@{pos.x:F1},{pos.y:F1},{pos.z:F1}";
        }

        private static float GetCameraProgress(CCTVCamera camera, Vector3 entrance)
        {
            if (camera == null) return float.PositiveInfinity;

            Tile tile = camera.OwningTile;
            float distance = Vector3.Distance(CameraPosition(camera), entrance);
            if (tile == null)
            {
                return distance * 0.05f + camera.CameraIndex * 0.001f;
            }

            TilePlacementData placement = tile.Placement;
            float pathDepth = Mathf.Max(0, placement.PathDepth);
            float branchDepth = Mathf.Max(0, placement.BranchDepth);
            float branchPenalty = placement.IsOnMainPath ? 0f : 0.35f;
            return pathDepth + branchDepth * 0.65f + branchPenalty + distance * 0.015f + camera.CameraIndex * 0.001f;
        }

        private static Vector3 CameraPosition(CCTVCamera camera)
        {
            return camera != null ? camera.transform.position : Vector3.zero;
        }

        private static float ResolveCameraFloorY(CCTVCamera camera)
        {
            if (camera == null) return 0f;
            Tile tile = camera.OwningTile;
            if (tile != null && tile.Placement != null)
            {
                Bounds local = DungeonCameraSpawner.ComputeWorldAabbLocal(tile);
                if (local.size.sqrMagnitude < 1e-6f)
                    local = tile.Placement.LocalBounds;
                if (local.size.sqrMagnitude >= 1e-6f)
                {
                    Vector3 floorPoint = new Vector3(local.center.x, local.min.y, local.center.z);
                    return (tile.Placement.Position + tile.Placement.Rotation * floorPoint).y;
                }
                return tile.Placement.Position.y;
            }
            return camera.transform.position.y;
        }

        private sealed class FloorCameraGroup
        {
            public readonly float FloorY;
            public readonly List<CCTVCamera> Cameras = new List<CCTVCamera>();
            public int FloorRank;

            public FloorCameraGroup(float floorY)
            {
                FloorY = floorY;
            }
        }

        private static void LogSpatialPageOrder(IReadOnlyList<CCTVCamera> ordered)
        {
            if (ordered == null || ordered.Count == 0)
            {
                SurveillanceBootstrap.Log.LogInfo("[LethalCCTV] Spatial page order: no cameras.");
                return;
            }

            int pages = Mathf.CeilToInt(ordered.Count / (float)SLOT_COUNT);
            for (int page = 0; page < pages; page++)
            {
                var labels = new List<string>(SLOT_COUNT);
                for (int slot = 0; slot < SLOT_COUNT; slot++)
                {
                    int index = page * SLOT_COUNT + slot;
                    if (index >= ordered.Count) break;
                    CCTVCamera camera = ordered[index];
                    labels.Add(BuildCameraLabel(camera) ?? "CAM_--");
                }
                SurveillanceBootstrap.Log.LogInfo($"[LethalCCTV] Spatial page order {page + 1}/{pages}: {string.Join(", ", labels)}");
            }
        }

        private static void TearDownSlot(int slot)
        {
            CCTVCamera holder = _boundCameras[slot];
            if (holder != null && holder.Cam != null)
            {
                // Phase 1.7b — unregister FIRST, before any state on the camera is
                // mutated, so the bake handler cannot fire on a half-torn-down camera
                // (cam.targetTexture being nulled below would otherwise sit between
                // the camera still being mapped and the dictionary entry being removed).
                // Symmetric to the BindCameraToSlot ordering: register before consumer
                // is exposed, unregister before consumer is torn down.
                NightVisionBaker.Unregister(holder.Cam);
                CctvTileCullingBypass.Unregister(holder.Cam);
                CCTVCameraVisual.UnregisterCctvFeedCamera(holder.Cam);

                var throttle = holder.Cam.GetComponent<CCTVCameraThrottle>();
                if (throttle != null)
                {
                    throttle.enabled = false;
                }
                holder.SetNightVisionFillLightActive(false);
                holder.RevertStripSet();
                holder.Cam.enabled = false;
                holder.Cam.targetTexture = null;
            }
            _boundCameras[slot] = null;
        }

        private static void BindCameraToSlot(CCTVCamera holder, int slot)
        {
            Camera cam = holder.Cam;

            // Phase 1.7b — when the night-vision bake is active, the camera writes
            // into RawRTs[slot]; the bake handler blits through the bake material
            // into QuadRTs[slot]. When the bundle is missing (RawRTs == null), the
            // camera writes directly into QuadRTs[slot] (raw passthrough).
            cam.targetTexture = (QuadMonitor.RawRTs != null)
                ? QuadMonitor.RawRTs[slot]
                : QuadMonitor.QuadRTs[slot];

            // Register the camera under its slot IMMEDIATELY after targetTexture is set
            // and BEFORE BindRTToSlot below flips the wall + overlay to QuadRTs[slot].
            // Without this ordering, a render firing between the consumer flip and the
            // dictionary insert would produce a one-frame stale display (the consumers
            // would point at QuadRTs[slot] but no bake would translate the new render).
            // Symmetric to TearDownSlot's "Unregister first" discipline.
            NightVisionBaker.Register(cam, slot);

            cam.cullingMask = _cachedCullingMask;
            CCTVCameraVisual.RegisterCctvFeedCamera(cam);
            CctvTileCullingBypass.Register(cam, holder);
            cam.farClipPlane = ResolveInteriorFarClipPlane();
            if (SurveillanceBootstrap.Config != null)
            {
                cam.fieldOfView = SurveillanceBootstrap.Config.FieldOfView.Value;
            }
            cam.clearFlags = CameraClearFlags.Color;
            cam.backgroundColor = Color.black;

            // The throttle uses the local Render Hz Per Monitor config value.
            var throttle = cam.GetComponent<CCTVCameraThrottle>();
            if (throttle == null)
            {
                throttle = cam.gameObject.AddComponent<CCTVCameraThrottle>();
            }
            throttle.enabled = true;
            throttle.Configure(cam, slot, ResolveCameraFps(), ResolveInactiveCameraFps(), ResolveUnmannedIdleCameraFps());

            // HDAdditionalCameraData is attached lazily — the spawner deliberately leaves
            // disabled cameras without it to avoid HDRP per-frame iteration leaking
            // FrameSettings into probe/volume sampling (see DungeonCameraSpawner notes).
            var hdrp = cam.GetComponent<HDAdditionalCameraData>();
            if (hdrp == null)
            {
                hdrp = cam.gameObject.AddComponent<HDAdditionalCameraData>();
            }
            hdrp.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hdrp.backgroundColorHDR = Color.black;
            holder.HdrpData = hdrp;

            // FrameSettings strip — cuts the full-default HDRP cost (post-FX, motion
            // blur, screen-space shadows/SSAO/SSR, reflections, volumetrics, decals,
            // custom passes, exposure, SSS, refraction/distortion) for
            // this camera's per-frame render. ApplyStripSet flips customRenderingSettings
            // ON and sets the per-field override mask + values; RevertStripSet (called
            // in TearDownSlot) flips customRenderingSettings OFF so the next bind
            // starts clean. Both calls are idempotent via the StripActive flag.
            holder.ApplyStripSet();

            // Phase 1.5a: re-point the slot's material to its canonical per-slot RT, in
            // case the slot was previously bound to the empty-fallback black RT.
            QuadMonitor.BindRTToSlot(slot);

            _boundCameras[slot] = holder;
        }

        private static float ResolveCameraFps()
        {
            float configured = SurveillanceBootstrap.Config?.RenderHzPerMonitor?.Value ?? DEFAULT_CAMERA_FPS;
            bool seatedStation = UseSeatedStationPerformanceCap();
            float maxFps = seatedStation ? SEATED_STATION_CAMERA_FPS_CAP : MAX_CAMERA_FPS;
            return Mathf.Clamp(configured, MIN_CAMERA_FPS, maxFps);
        }

        private static float ResolveInactiveCameraFps()
        {
            float configured = SurveillanceBootstrap.Config?.InactivePaneRenderHz?.Value ?? DEFAULT_INACTIVE_CAMERA_FPS;
            if (UseSeatedStationPerformanceCap())
                configured = Mathf.Min(configured, SEATED_STATION_INACTIVE_CAMERA_FPS_CAP);
            return Mathf.Clamp(configured, MIN_INACTIVE_CAMERA_FPS, MAX_CAMERA_FPS);
        }

        // #562 — the wall keeps showing its last feed with nobody seated. Only
        // that one camera renders, so this rate is not subject to the seated
        // multi-pane budget caps; 0 restores the old freeze-on-last-frame look.
        private static float ResolveUnmannedIdleCameraFps()
        {
            float configured = SurveillanceBootstrap.Config?.UnmannedIdleRenderHz?.Value ?? DEFAULT_UNMANNED_IDLE_CAMERA_FPS;
            return Mathf.Clamp(configured, MIN_UNMANNED_IDLE_CAMERA_FPS, MAX_UNMANNED_IDLE_CAMERA_FPS);
        }

        private static bool UseSeatedStationPerformanceCap()
        {
            return CCTVVanillaMonitorDisplay.IsCctvModeActive;
        }

        private static float ResolveInteriorFarClipPlane()
        {
            float configured = SurveillanceBootstrap.Config?.FarClipPlane?.Value ?? DEFAULT_CCTV_FAR_CLIP_M;
            return Mathf.Clamp(configured, MIN_CCTV_FAR_CLIP_M, MAX_CCTV_FAR_CLIP_M);
        }

        // Method retained for call-site stability; returns constant post-Phase 1.6b.
        private static int ResolveCullingMask()
        {
            int mask = CCTV_CULLING_MASK;
            mask |= LayerBit("Enemies");
            mask |= LayerBit("Enemy");
            mask |= LayerBit("PhysicsProp");
            mask |= LayerBit("Props");
            mask |= LayerBit("MapHazards");
            mask &= ~((1 << UI_LAYER) | (1 << HELMET_VISOR_LAYER));

            SurveillanceBootstrap.Log.LogInfo(
                $"[LethalCCTV] QuadCameraAssignment: applying Phase 1.6b CCTV cullingMask " +
                $"0x{mask:X8} = player 0x{PLAYER_CAMERA_MASK:X8} plus explicit enemy/prop layers & " +
                $"~(UI=bit{UI_LAYER} | HelmetVisor=bit{HELMET_VISOR_LAYER}).");
            return mask;
        }

        private static int LayerBit(string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            return layer >= 0 ? 1 << layer : 0;
        }
    }
}
