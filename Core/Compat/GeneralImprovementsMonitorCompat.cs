using System;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Core.Compat
{
    /// <summary>
    /// Soft-dependency bridge for Shaosil's GeneralImprovements, specifically its
    /// <c>UseBetterMonitors</c> replacement monitor wall (#597).
    ///
    /// With better monitors on, GI instantiates a <c>MonitorGroup(Clone)</c> as a sibling of
    /// the vanilla <c>Cube.001</c> under <c>MonitorWall</c> (a <c>StartOfRound.Start</c>
    /// prefix at <see cref="HarmonyLib.HarmonyPriority.High"/>) and then hides the vanilla
    /// monitors in a <c>PlayerControllerB.ConnectClientToPlayerObject</c> finalizer — the old
    /// meshes are not destroyed, just <c>MeshRenderer.enabled = false</c> plus
    /// <c>Collider.enabled = false</c>. Every material slot CCTV used to take on
    /// <c>Cube.001</c> is therefore still writable and still completely invisible.
    ///
    /// The sanctioned way to put a foreign feed on the new wall is to write
    /// <c>MonitorInfo.MeshRenderer.sharedMaterial</c>. GI notices the unexpected material on
    /// its next power toggle or monitor rebuild, stores it as <c>OverwrittenMaterial</c> and
    /// keeps re-applying it ("Found an unexpected material on ship monitor N ..."), and its
    /// text pipeline no-ops on any screen whose <c>sharedMaterial != AssignedMaterial</c>.
    ///
    /// Access is reflection-only, matching the existing GI probes in the sibling ShipSystems
    /// assembly (<c>MonitorWallFaceMap.GeneralImprovementsUsesReplacementMesh</c>) and the
    /// OpenBodyCams bridge next door. Nothing here references a GI type at compile time, so
    /// no JIT of a GI type can happen when GI is absent and LethalCCTV.csproj needs no
    /// package reference.
    /// </summary>
    internal static class GeneralImprovementsMonitorCompat
    {
        public const string PLUGIN_GUID = "ShaosilGaming.GeneralImprovements";

        /// <summary>Config sentinel: let this bridge choose the screen.</summary>
        internal const int AutoScreenIndex = -1;

        /// <summary>
        /// Config sentinel AND resolved value for GI's MAP screen — the <c>MScreen</c> child
        /// mesh under <c>MonitorGroup(Clone)/Monitors/BigMiddle</c>.
        ///
        /// This screen is deliberately NOT one of <c>MonitorsAPI.AllMonitors</c>' 14 indices:
        /// GI keeps it as the private <c>Monitors._mapRenderer</c> and drives it only from its
        /// <c>ManualCameraRendererPatch.SwitchScreenOn</c> prefix
        /// (<c>Monitors.UpdateMapMaterial</c>). It is the surface the operator chair faces and
        /// the direct analogue of the vanilla map monitor CCTV takes over on <c>Cube.001</c>,
        /// so it is what "automatic" means for the CCTV feed under better monitors (#600).
        /// </summary>
        internal const int MapScreenIndex = -2;

        /// <summary>The <c>BigMiddle</c> child that actually carries the map material.</summary>
        private const string MapScreenChildName = "MScreen";
        private const string MapScreenGroupRelativePath = "Monitors/BigMiddle/MScreen";
        private const string MonitorGroupNamePrefix = "MonitorGroup";

        /// <summary>
        /// GI's screen ordering from <c>Monitors.Initialize</c>. With
        /// <c>AddMoreBetterMonitors</c> there are 14 entries
        /// (Screen1,2, Screen3-6, Screen7,8, Screen9-12, BigLeft/LScreen, BigRight/RScreen);
        /// without it there are 9 (Screen3-6, Screen9-12, BigRight/RScreen) and the whole
        /// TopGroupL + BigLeft cluster is inactive. Index 13/8 is therefore "the big right
        /// screen" in either layout.
        /// </summary>
        internal const int MaxScreenIndex = 13;

        private const int BigLeftScreenIndexWithExtras = 12;
        private const int BigRightScreenIndexWithExtras = 13;
        private const int BigRightScreenIndexWithoutExtras = 8;

        private static readonly bool _isLoaded = Chainloader.PluginInfos.ContainsKey(PLUGIN_GUID);

        private static bool _resolved;
        private static MethodInfo _getMonitorAtIndex;
        private static PropertyInfo _numMonitorsActiveProperty;
        private static PropertyInfo _poweredOnProperty;
        private static PropertyInfo _meshRendererProperty;
        private static PropertyInfo _screenMaterialIndexProperty;
        private static PropertyInfo _assignmentProperty;
        private static bool _memberResolveFailureLogged;

        private static bool _activationLogged;

        internal static bool IsLoaded => _isLoaded;

        /// <summary>
        /// True while GI's replacement monitor wall is live and usable.
        ///
        /// Deliberately probes <c>GetMonitorAtIndex(0)?.MeshRenderer</c> rather than reading
        /// <c>MonitorsAPI.NewMonitorMeshActive</c>: that flag reported true whenever
        /// <c>AddMoreBetterMonitors</c> was set even with <c>UseBetterMonitors</c> off until
        /// GI 1.5.4 fixed it, and it also stays true across the window where GI has torn the
        /// group down and not yet rebuilt it. A live MeshRenderer at index 0 is the only
        /// statement that answers the question we actually need answered.
        /// </summary>
        internal static bool AreBetterMonitorsActive()
        {
            if (!_isLoaded)
                return false;

            return TryGetScreen(0, out MeshRenderer renderer, out _) && renderer != null;
        }

        /// <summary>Number of screens GI currently exposes (14 or 9 in stock configurations).</summary>
        internal static int ActiveMonitorCount()
        {
            if (!_isLoaded)
                return 0;

            Resolve();
            try
            {
                object value = _numMonitorsActiveProperty?.GetValue(null);
                return value is int count && count > 0 ? count : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// GI's own monitor power state, driven by the map-screen power button through
        /// <c>Monitors.TogglePower</c>. While it is false GI has written its blank-screen
        /// material over EVERY screen; re-applying our material there would fight the power
        /// toggle every reassert pass and light one screen in an otherwise dark wall.
        /// </summary>
        internal static bool MonitorsPoweredOn()
        {
            if (!_isLoaded)
                return true;

            Resolve();
            try
            {
                object value = _poweredOnProperty?.GetValue(null);
                return !(value is bool powered) || powered;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Resolves one GI screen to the renderer + material slot a
        /// <c>CCTVVanillaMonitorDisplay.ScreenBinding</c> can take over.
        ///
        /// Never cache the result: GI destroys and rebuilds the whole MonitorGroup on every
        /// <c>StartOfRound.Start</c> (so, every lobby reload) and again on client join when
        /// <c>SyncMonitorsFromOtherHost</c> is on. Callers re-resolve on each bind/reassert
        /// pass instead.
        /// </summary>
        internal static bool TryGetScreen(int index, out MeshRenderer renderer, out int materialIndex)
        {
            renderer = null;
            materialIndex = -1;
            if (!_isLoaded || index < 0)
                return false;

            Resolve();
            if (_getMonitorAtIndex == null || _meshRendererProperty == null)
                return false;

            try
            {
                object info = _getMonitorAtIndex.Invoke(null, new object[] { index });
                if (info == null)
                    return false;

                renderer = _meshRendererProperty.GetValue(info) as MeshRenderer;
                if (renderer == null)
                    return false;

                // GI documents ScreenMaterialIndex as always 0 ("our screen meshes are
                // separate from the surrounding meshes and always only have one material"),
                // but read it rather than assume, and clamp so a future multi-slot screen
                // cannot walk off the end of sharedMaterials.
                object slot = _screenMaterialIndexProperty?.GetValue(info);
                materialIndex = slot is int value ? value : 0;
                if (materialIndex < 0)
                    materialIndex = 0;

                int slotCount = renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;
                if (slotCount <= 0)
                {
                    renderer = null;
                    materialIndex = -1;
                    return false;
                }
                if (materialIndex >= slotCount)
                    materialIndex = 0;

                return true;
            }
            catch (Exception ex)
            {
                LogMemberResolveFailureOnce($"GetMonitorAtIndex({index}) failed: {ex.GetType().Name}: {ex.Message}");
                renderer = null;
                materialIndex = -1;
                return false;
            }
        }

        /// <summary>True for any value this bridge can hand to <see cref="TryGetResolvedScreen"/>.</summary>
        internal static bool IsResolvedScreenIndex(int index)
        {
            return index >= 0 || index == MapScreenIndex;
        }

        internal static bool IsMapScreenIndex(int index)
        {
            return index == MapScreenIndex;
        }

        /// <summary>
        /// <see cref="TryGetScreen"/> plus <see cref="MapScreenIndex"/> support, so callers can
        /// treat "GI screen N" and "GI's map screen" uniformly.
        /// </summary>
        internal static bool TryGetResolvedScreen(int index, out MeshRenderer renderer, out int materialIndex)
        {
            if (index == MapScreenIndex)
                return TryGetMapScreen(out renderer, out materialIndex);

            return TryGetScreen(index, out renderer, out materialIndex);
        }

        /// <summary>
        /// Resolves GI's map screen mesh (<c>BigMiddle/MScreen</c>).
        ///
        /// Two independent paths, in order of directness:
        /// 1. <c>StartOfRound.mapScreen.mesh</c> — GI repoints that at its own
        ///    <c>BigMiddle</c> frame in <c>MonitorsHelper</c>, and <c>MScreen</c> is that
        ///    frame's child. This is the same statement GI itself makes about the map.
        /// 2. Failing that, walk up from any <c>MonitorsAPI</c> screen renderer to the
        ///    <c>MonitorGroup(Clone)</c> root and take <c>Monitors/BigMiddle/MScreen</c>.
        ///
        /// Both results are verified to live under a <c>MonitorGroup*</c> ancestor so a
        /// vanilla <c>mapScreen.mesh</c> (Cube.001) can never be mistaken for it.
        ///
        /// Never cached — like <see cref="TryGetScreen"/>, this must be re-resolved on every
        /// bind/reassert pass because GI rebuilds the whole group on <c>StartOfRound.Start</c>.
        /// </summary>
        internal static bool TryGetMapScreen(out MeshRenderer renderer, out int materialIndex)
        {
            renderer = null;
            materialIndex = -1;
            if (!_isLoaded)
                return false;

            Transform screen = null;

            ManualCameraRenderer mapScreen = StartOfRound.Instance != null ? StartOfRound.Instance.mapScreen : null;
            MeshRenderer frame = mapScreen != null ? mapScreen.mesh : null;
            if (frame != null)
                screen = frame.transform.Find(MapScreenChildName);

            if (!IsUnderMonitorGroup(screen))
                screen = FindMapScreenFromGroupRoot();

            if (!IsUnderMonitorGroup(screen))
                return false;

            MeshRenderer candidate = screen.GetComponent<MeshRenderer>();
            if (candidate == null)
                return false;

            int slotCount;
            try { slotCount = candidate.sharedMaterials != null ? candidate.sharedMaterials.Length : 0; }
            catch { return false; }
            if (slotCount <= 0)
                return false;

            // GI builds MScreen as a single-material mesh and writes it through
            // `sharedMaterial`, which is slot 0 by definition.
            renderer = candidate;
            materialIndex = 0;
            return true;
        }

        private static Transform FindMapScreenFromGroupRoot()
        {
            if (!TryGetScreen(0, out MeshRenderer any, out _) || any == null)
                return null;

            for (Transform t = any.transform; t != null; t = t.parent)
            {
                if (t.name != null && t.name.StartsWith(MonitorGroupNamePrefix, StringComparison.Ordinal))
                    return t.Find(MapScreenGroupRelativePath);
            }

            return null;
        }

        private static bool IsUnderMonitorGroup(Transform transform)
        {
            for (Transform t = transform; t != null; t = t.parent)
            {
                if (t.name != null && t.name.StartsWith(MonitorGroupNamePrefix, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when GI has nothing of its own assigned to this screen — the screens we
        /// prefer to take, because taking one leaves every GI readout intact.
        /// </summary>
        internal static bool IsScreenUnassigned(int index)
        {
            return ReadAssignment(index) == GiAssignment.None;
        }

        /// <summary>
        /// The vanilla <see cref="ManualCameraRenderer"/> GI points at this screen, if any.
        ///
        /// GI repoints the driver's <c>mesh</c> field at the new screen mesh for its
        /// <c>ExternalCam</c> / <c>InternalCam</c> assignments, so the existing mesh-identity
        /// sweep in ScreenBinding.DisableDrivers already finds it. This resolves the same
        /// driver by scene identity as well, so suppression does not depend on winning a race
        /// with GI's own mesh assignment (or on GI keeping that implementation detail).
        /// </summary>
        internal static ManualCameraRenderer ResolveScreenDriver(int index)
        {
            // The map screen's driver is StartOfRound.mapScreen itself. It cannot be found by
            // the mesh-identity sweep in ScreenBinding.DisableDrivers, because GI points
            // mapScreen.mesh at the BigMiddle FRAME while the surface CCTV owns is that
            // frame's MScreen child. Resolving it here is what puts the map camera to sleep
            // for the session, exactly as the vanilla path does.
            if (index == MapScreenIndex)
                return StartOfRound.Instance != null ? StartOfRound.Instance.mapScreen : null;

            GiAssignment assignment = ReadAssignment(index);
            string path = assignment switch
            {
                GiAssignment.ExternalCam => "Cameras/FrontDoorSecurityCam/SecurityCamera",
                GiAssignment.InternalCam => "Cameras/ShipCamera",
                _ => null,
            };
            if (path == null)
                return null;

            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            Transform camera = ship != null ? ship.Find(path) : null;
            return camera != null ? camera.GetComponent<ManualCameraRenderer>() : null;
        }

        /// <summary>
        /// Turns the two configured screen indices into concrete ones.
        ///
        /// A configured value is honoured whenever it names a screen that currently exists;
        /// <see cref="AutoScreenIndex"/> falls through to the defaults below.
        ///
        /// #600 — automatic FEED is GI's map screen (<see cref="MapScreenIndex"/>). That is
        /// vanilla parity: on the vanilla wall CCTV takes over the map monitor's slot on
        /// Cube.001, and under better monitors the map lives on BigMiddle/MScreen — the big
        /// middle screen the operator chair actually faces. The previous default (the big LEFT
        /// screen) put the feed on a monitor nobody at the station was looking at.
        ///
        /// Automatic RADAR claims the big RIGHT screen first (index 13 with extras, index 8
        /// without them), even when GI assigned its ExternalCam there. The station's Space
        /// glance is authored toward that physical screen, so the visible radar and the
        /// operator's viewpoint must agree. If that screen is unavailable, the remaining
        /// preference order starts with big LEFT and then walks the top rows, preferring an
        /// unassigned screen for the fallback only.
        ///
        /// An explicit index that does not resolve — out of range for this client's GI layout,
        /// or the same screen as the other feed — leaves that feed unbound rather than quietly
        /// landing somewhere the player did not ask for.
        /// </summary>
        internal static bool TryResolveConfiguredScreens(
            int configuredFeed,
            int configuredRadar,
            out int feedIndex,
            out int radarIndex)
        {
            feedIndex = -1;
            radarIndex = -1;

            int count = ActiveMonitorCount();
            if (count <= 0)
                return false;

            int[] preference = BuildPreferenceOrder(count);
            bool mapAvailable = TryGetMapScreen(out _, out _);

            bool autoFeed = configuredFeed == AutoScreenIndex;
            bool autoRadar = configuredRadar == AutoScreenIndex;

            if (autoFeed || configuredFeed == MapScreenIndex)
                feedIndex = mapAvailable ? MapScreenIndex : -1;
            else
                feedIndex = NormalizeConfigured(configuredFeed, count, -1);

            if (configuredRadar == MapScreenIndex)
                radarIndex = mapAvailable && feedIndex != MapScreenIndex ? MapScreenIndex : -1;
            else if (!autoRadar)
                radarIndex = NormalizeConfigured(configuredRadar, count, feedIndex);

            // Auto feed with no map screen (GI mid-rebuild, or a future layout without one)
            // still deserves a screen; fall through to the same preference order the radar
            // uses rather than leaving the feed dark.
            if (autoFeed && feedIndex == -1)
                feedIndex = PickAuto(preference, count, radarIndex, preferUnassigned: true);
            if (autoRadar)
            {
                radarIndex = PickAutomaticRadar(count, feedIndex);
                if (radarIndex == -1)
                    radarIndex = PickAuto(preference, count, feedIndex, preferUnassigned: true);
            }

            return IsResolvedScreenIndex(feedIndex) || IsResolvedScreenIndex(radarIndex);
        }

        /// <summary>Big screens first, then every top-row screen in index order.</summary>
        private static int[] BuildPreferenceOrder(int count)
        {
            var order = new System.Collections.Generic.List<int>(count);
            if (count > BigLeftScreenIndexWithExtras)
                order.Add(BigLeftScreenIndexWithExtras);
            if (count > BigRightScreenIndexWithExtras)
                order.Add(BigRightScreenIndexWithExtras);
            else if (count > BigRightScreenIndexWithoutExtras)
                order.Add(BigRightScreenIndexWithoutExtras);

            for (int i = 0; i < count; i++)
            {
                if (!order.Contains(i))
                    order.Add(i);
            }

            return order.ToArray();
        }

        private static int NormalizeConfigured(int configured, int count, int taken)
        {
            if (configured < 0 || configured >= count || configured == taken)
                return -1;
            return TryGetScreen(configured, out _, out _) ? configured : -1;
        }

        private static int PickAutomaticRadar(int count, int taken)
        {
            int bigRight = count > BigRightScreenIndexWithExtras
                ? BigRightScreenIndexWithExtras
                : count > BigRightScreenIndexWithoutExtras
                    ? BigRightScreenIndexWithoutExtras
                    : -1;
            return NormalizeConfigured(bigRight, count, taken);
        }

        private static int PickAuto(int[] preference, int count, int taken, bool preferUnassigned)
        {
            for (int pass = preferUnassigned ? 0 : 1; pass < 2; pass++)
            {
                for (int i = 0; i < preference.Length; i++)
                {
                    int index = preference[i];
                    if (index < 0 || index >= count || index == taken)
                        continue;
                    if (pass == 0 && !IsScreenUnassigned(index))
                        continue;
                    if (!TryGetScreen(index, out _, out _))
                        continue;
                    return index;
                }
            }

            return -1;
        }

        /// <summary>
        /// One line, once per activation, naming exactly which GI screens CCTV took. Re-arms
        /// when the wall goes away so a lobby reload that lands on different screens says so.
        /// </summary>
        internal static void LogBindingOnce(int leftIndex, int rightIndex)
        {
            if (_activationLogged)
                return;

            _activationLogged = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] GeneralImprovements better monitors detected " +
                $"({ActiveMonitorCount()} screens); CCTV feed -> screen {Describe(leftIndex)}, " +
                $"radar -> screen {Describe(rightIndex)}. Vanilla Cube.001 is hidden by GI and is not bound.");
        }

        private static string Describe(int index)
        {
            if (index == MapScreenIndex)
                return "-2 (GI map screen, Monitors/BigMiddle/MScreen)";
            if (index < 0)
                return "<none>";

            int count = ActiveMonitorCount();
            string role =
                index == BigLeftScreenIndexWithExtras && count > BigLeftScreenIndexWithExtras ? " (big left)"
                : index == BigRightScreenIndexWithExtras && count > BigRightScreenIndexWithExtras ? " (big right)"
                : index == BigRightScreenIndexWithoutExtras && count == BigRightScreenIndexWithoutExtras + 1 ? " (big right)"
                : string.Empty;

            // GI numbers its own log lines and config keys from 1 (ShipMonitor1..14) while the
            // API indexes from 0; print both so a config change is unambiguous.
            return $"{index}{role} [GI ShipMonitor{index + 1}]";
        }

        internal static void ResetDiagnostics()
        {
            _activationLogged = false;
        }

        /// <summary>Mirrors the subset of GI's <c>eMonitorNames</c> this bridge cares about.</summary>
        private enum GiAssignment
        {
            Unknown = -1,
            None = 0,
            Text = 1,
            ExternalCam = 2,
            InternalCam = 3,
        }

        private static GiAssignment ReadAssignment(int index)
        {
            if (!_isLoaded || index < 0)
                return GiAssignment.Unknown;

            Resolve();
            if (_getMonitorAtIndex == null || _assignmentProperty == null)
                return GiAssignment.Unknown;

            try
            {
                object info = _getMonitorAtIndex.Invoke(null, new object[] { index });
                object assignment = info != null ? _assignmentProperty.GetValue(info) : null;
                if (assignment == null)
                    return GiAssignment.Unknown;

                // Compared by name rather than by numeric value: GI has added members to
                // eMonitorNames between releases, so every ordinal except None's is unstable.
                string name = assignment.ToString();
                if (string.Equals(name, "None", StringComparison.Ordinal))
                    return GiAssignment.None;
                if (string.Equals(name, "ExternalCam", StringComparison.Ordinal))
                    return GiAssignment.ExternalCam;
                if (string.Equals(name, "InternalCam", StringComparison.Ordinal))
                    return GiAssignment.InternalCam;
                return GiAssignment.Text;
            }
            catch (Exception ex)
            {
                LogMemberResolveFailureOnce($"MonitorInfo.Assignment read failed: {ex.GetType().Name}: {ex.Message}");
                return GiAssignment.Unknown;
            }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;
            if (!_isLoaded)
                return;

            try
            {
                Type api = Type.GetType("GeneralImprovements.API.MonitorsAPI, GeneralImprovements", throwOnError: false);
                if (api == null)
                {
                    LogMemberResolveFailureOnce("GeneralImprovements.API.MonitorsAPI was not found");
                    return;
                }

                BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static;
                BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.Instance;

                _getMonitorAtIndex = api.GetMethod(
                    "GetMonitorAtIndex",
                    staticFlags,
                    binder: null,
                    types: new[] { typeof(int) },
                    modifiers: null);
                _numMonitorsActiveProperty = api.GetProperty("NumMonitorsActive", staticFlags);
                _poweredOnProperty = api.GetProperty("PoweredOn", staticFlags);

                Type monitorInfo = api.GetNestedType("MonitorInfo", BindingFlags.Public)
                    ?? _getMonitorAtIndex?.ReturnType;
                if (monitorInfo != null)
                {
                    _meshRendererProperty = monitorInfo.GetProperty("MeshRenderer", instanceFlags);
                    _screenMaterialIndexProperty = monitorInfo.GetProperty("ScreenMaterialIndex", instanceFlags);
                    _assignmentProperty = monitorInfo.GetProperty("Assignment", instanceFlags);
                }

                if (_getMonitorAtIndex == null || _meshRendererProperty == null)
                {
                    LogMemberResolveFailureOnce(
                        _getMonitorAtIndex == null
                            ? "MonitorsAPI.GetMonitorAtIndex(int) was not found"
                            : "MonitorInfo.MeshRenderer was not found");
                }
            }
            catch (Exception ex)
            {
                LogMemberResolveFailureOnce($"reflection setup failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void LogMemberResolveFailureOnce(string reason)
        {
            if (_memberResolveFailureLogged)
                return;
            _memberResolveFailureLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                "[LethalCCTV] GeneralImprovements monitor API unavailable; CCTV will keep using the " +
                $"vanilla monitor wall: {reason}.");
        }
    }
}
