using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using Y4NGZCompany.Core;
using Y4NGZCore;

namespace LethalCCTV
{
    /// <summary>
    /// Surveillance and CCTV systems. The GUID is the pre-fold LethalCCTV identity,
    /// reused so existing configs and dependants keep resolving to this plugin.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CorePlugin.PluginGuid, "1.0.10")]
    [BepInDependency("com.y4ngz.interactions", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("evaisa.lethallib", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("LethalNetworkAPI", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.rune580.LethalCompanyInputUtils", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.sigurd.csync", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.github.teamxiaolan.dawnlib", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("Zaggy1024.OpenBodyCams", BepInDependency.DependencyFlags.SoftDependency)]
    // BetterArmory's world trace intentionally ignores trigger colliders. CCTV camera
    // hitboxes must remain triggers, so LethalCCTV adds a matching optional damage pass.
    [BepInDependency("com.y4ngz.betterarmory", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: the split Ship Systems module may own CCTV Terminal purchasing. It must
    // chainload first so the terminal entitlement bridge can resolve its public API.
    [BepInDependency(Y4NGZCore.Lifecycle.ModuleHarmonyIds.ShipSystems, BepInDependency.DependencyFlags.SoftDependency)]

    // #597 — GeneralImprovements' UseBetterMonitors replaces the whole monitor wall. Soft, and
    // one-directional like the contracts dependency below: LethalCCTV runs standalone, but when
    // GI is installed it must chainload first so its MonitorsAPI type is registered before the
    // reflection bridge in GeneralImprovementsMonitorCompat looks for it.
    [BepInDependency("ShaosilGaming.GeneralImprovements", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("imabatby.lethallevelloader", BepInDependency.DependencyFlags.SoftDependency)]
    // #601 — LethalPhonesCompat latches Chainloader.PluginInfos.ContainsKey at type-init,
    // which SoftDependencies reaches during this plugin's Awake. Without this attribute
    // LethalPhones chainloads AFTER LethalCCTV, the lookup misses, and the phone
    // suppression silently no-ops for the whole session.
    [BepInDependency(Y4NGZCompany.Core.Compat.LethalPhonesCompat.PLUGIN_GUID, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("me.loaforc.facilitymeltdown", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft, and deliberately one-directional: this plugin runs standalone, but when the
    // contracts plugin is installed it must chainload first so the reflection bridges here
    // (objective markers, HUD host, Bundy world noise) see its types already registered.
    // Y4NGZCompany must never declare the mirror dependency - that would invert load order.
    [BepInDependency("com.y4ngz.company", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft, same shape (#660): the HUD host / visibility substrate lives in the Y4NGZUI
    // plugin now, and GameplayHudHostBridge probes it first. When it is installed it must
    // chainload before this plugin so the bridge's memoized probe sees it registered.
    [BepInDependency(Y4NGZCore.Lifecycle.ModuleHarmonyIds.Ui, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class LethalCCTVPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.y4ngz.company.lethalcctv";
        public const string PluginName = "LethalCCTV";
        public const string PluginVersion = "1.2.0";

        /// <summary>
        /// This build's wire-protocol revision for the module handshake (#613 task 2.2). Covers
        /// camera shutdown, ship turret requests/results, and coded facility-device commands.
        /// Bump on a payload or authority change independently of <see cref="PluginVersion"/>.
        /// </summary>
        public const int CctvProtocolRevision = 3;

        public static LethalCCTVPlugin Instance { get; private set; }

        public static ManualLogSource Log { get; private set; }

        private Y4NGZCompany.Bootstrap.SurveillanceBootstrap _host;

        /// <summary>
        /// Name of the GameObject that owns <see cref="Y4NGZCompany.Bootstrap.SurveillanceBootstrap"/>.
        /// Every CCTV runtime system hangs off this object's lifetime, directly (LateUpdate) or
        /// through the parent chain (focus overlay, marker roots, audio proxies).
        /// </summary>
        internal const string HostObjectName = "LethalCCTV_Host";

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // #600: one readable error per hard dependency a profile resolved older than we
            // compiled against, before anything here can fail with an opaque TypeLoadException.
            DependencyVersionAudit.RunForLethalCCTV(Logger);

            // Netcode RPC registration is per-assembly: the shared bootstrap must be told
            // which assembly to scan, otherwise the CCTV RPCs here are never registered.
            NetcodeRuntimeBootstrap.Initialize(Logger, typeof(LethalCCTVPlugin).Assembly);

            // #613 task 2.2 - the cross-plugin module handshake. CCTV joins for the same reason
            // the other two do: the handshake compares the module SET of the whole install, and a
            // plugin that never announces cannot be told apart from one that is not installed.
            // Covers the module's network payloads and host authority, including coded devices.
            Y4NGZCore.Modules.Net.ModuleHandshakeService.Publish(
                Y4NGZCore.Lifecycle.ModuleIds.Cctv,
                CctvProtocolRevision,
                PluginVersion);

            // #393: the bootstrap used to be a component on BepInEx's own plugin GameObject
            // ("BepInEx_Manager"). Verified against the IL of BepInEx 5.4.23.5
            // Chainloader.Start: DontDestroyOnLoad(ManagerObject) is unconditional, and the
            // core setting Chainloader/HideManagerGameObject gates *only* the
            // hideFlags = HideAndDontSave store. So the manager object persists across scene
            // loads either way — but with the BepInEx default (false) it has no hide flags and
            // is therefore fully discoverable by GameObject.Find("BepInEx_Manager"). Any other
            // mod doing Find-based cleanup can destroy it, and every plugin component riding on
            // it goes with it.
            //
            // That is what happened in the user's Test profile: something external destroyed the
            // shared object at frame 0, SurveillanceBootstrap.OnDestroy ran with it, and down
            // went the dungeon subscription, the LateUpdate that re-subscribes it, and every
            // Harmony patch this plugin applies (OnDestroy calls Harmony.UnpatchSelf). Result:
            // no cameras, no interior support, and not a single round-time log line to say why.
            //
            // The fix is blast-radius isolation, not persistence: own the host object instead of
            // sharing one whose lifetime every other plugin (and any Find-based cleanup) also
            // touches. DontDestroyOnLoad gives it the persistence; HideAndDontSave keeps it out
            // of GameObject.Find, so nothing external can reach it by name.
            //
            // Standing convention: every Y4NGZ plugin owns its host object. Do not move
            // components back onto BepInEx_Manager.
            var hostObject = new GameObject(HostObjectName)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            Object.DontDestroyOnLoad(hostObject);

            _host = hostObject.AddComponent<Y4NGZCompany.Bootstrap.SurveillanceBootstrap>();
            _host.Initialize(Config, Logger);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
    }
}
