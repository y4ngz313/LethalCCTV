using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Reflection-only consumer of the optional Y4NGZInteractions live-body API.
    /// The CCTV assembly remains loadable when Interactions is absent; callers
    /// fall back to the existing direct-controller session in that case.
    /// </summary>
    internal static class CCTVOperatorInteractionsBridge
    {
        internal const float EnterClipLengthSeconds = 33f / 30f;
        internal const float EnterPressContactSeconds = 11f / 30f;
        internal const float ExitClipLengthSeconds = 0.87f;

        private const string PackId = "y4ngz.cctv.operator";
        private const string WorldInteractionId = "y4ngz.cctv.operator";
        private const string WorldManifestFileName = "y4ngz-cctv-operator.manifest.json";
        private const string WorldBundleFileName = "y4ngz-cctv-playeranimations.lethalbundle";
        private const string WorldBundleInternalName = "y4ngzcctvplayeranimations";
        private const string WorldControllerName = "Y4NGZ_CCTV_PlayerMetarig";
        private const string ViewmodelInteractionId = "y4ngz.cctv.operator.viewmodel";
        private const string ViewmodelManifestFileName = "y4ngz-cctv-operator-viewmodel.manifest.json";
        private const string ViewmodelBundleFileName = "y4ngz-cctv-viewmodel.animationbundle";
        private const string ViewmodelBundleInternalName = "y4ngzcctvviewmodel";
        private const string ViewmodelPrefabName = "Y4NGZ_CCTV_LocalViewmodel";
        private const string ViewmodelControllerName = "Y4NGZ_CCTV_LocalViewmodel";
        private const float RegistrationRetrySeconds = 5f;
        /// <summary>#716 C5 / MINOR 3. Ceiling for the transient-failure backoff: the delay
        /// doubles from RegistrationRetrySeconds (5, 10, 20, 40) and then holds here.</summary>
        private const float MaxRegistrationBackoffSeconds = 60f;

        private static readonly BindingFlags ApiFlags = BindingFlags.Public | BindingFlags.Static;

        private static bool _typesResolved;
        private static bool _packRegistered;
        private static bool _resolutionFailureLogged;
        private static bool _packFailureLogged;
        private static bool _transientRegistrationLogged;
        private static float _nextRegistrationRetryAt;
        /// <summary>#716 C5. Set once a registration attempt fails for a reason that cannot
        /// change while the process runs (assets absent from disk, manifest contract mismatch,
        /// the Interactions API missing outright). Before this latch existed the 5s retry kept
        /// walking every loaded assembly and stat-ing four files forever on installs without
        /// Y4NGZInteractions, which the audit measured at 6.19ms on a zero-camera LateUpdate.</summary>
        private static bool _registrationAbandoned;
        private static bool _assemblyLoadHookInstalled;
        /// <summary>#716 C5 / MINOR 3. Current transient-failure delay; 0 until the first one.</summary>
        private static float _registrationBackoffSeconds;

        private static Type _apiType;
        private static Type _packDefinitionType;
        private static Type _interactionDefinitionType;
        private static Type _presentationKindType;
        private static Type _requestType;
        private static Type _handleType;
        private static Type _stopReasonType;
        private static MethodInfo _tryRegisterInteractionPack;
        private static MethodInfo _tryStartInteraction;
        private static MethodInfo _tryBeginInteractionExit;
        private static MethodInfo _tryStopInteraction;
        private static MethodInfo _isInteractionActive;
        private static MethodInfo _trySetInteractionBool;
        private static MethodInfo _trySetInteractionInt;
        private static MethodInfo _tryFireInteractionTrigger;

        internal static bool ConfigEnabled => true;

        internal static void Initialize()
        {
            if (ConfigEnabled)
                TryRegisterPack(forceRetry: true, out _);
        }

        internal static void Tick()
        {
            if (ConfigEnabled && !_packRegistered && !_registrationAbandoned)
                TryRegisterPack(forceRetry: false, out _);
        }

        internal static void Shutdown()
        {
            _typesResolved = false;
            _packRegistered = false;
            _resolutionFailureLogged = false;
            _packFailureLogged = false;
            _transientRegistrationLogged = false;
            _nextRegistrationRetryAt = 0f;
            _registrationAbandoned = false;
            _registrationBackoffSeconds = 0f;
            _apiType = null;
            _packDefinitionType = null;
            _interactionDefinitionType = null;
            _presentationKindType = null;
            _requestType = null;
            _handleType = null;
            _stopReasonType = null;
            _tryRegisterInteractionPack = null;
            _tryStartInteraction = null;
            _tryBeginInteractionExit = null;
            _tryStopInteraction = null;
            _isInteractionActive = null;
            _trySetInteractionBool = null;
            _trySetInteractionInt = null;
            _tryFireInteractionTrigger = null;
        }

        internal static bool TryStart(
            PlayerControllerB player,
            bool useDedicatedLocalViewmodel,
            out object handle,
            out string reason)
        {
            handle = null;
            reason = string.Empty;

            if (!ConfigEnabled)
            {
                reason = "kill_switch_disabled";
                return false;
            }
            if (player == null)
            {
                reason = "player_missing";
                return false;
            }
            if (!TryRegisterPack(forceRetry: true, out reason))
                return false;

            try
            {
                object request = Activator.CreateInstance(_requestType);
                SetProperty(request, "Player", player);
                SetProperty(request, "PackId", PackId);
                SetProperty(
                    request,
                    "InteractionId",
                    useDedicatedLocalViewmodel ? ViewmodelInteractionId : WorldInteractionId);

                object[] args = { request, null, null };
                bool started = (bool)_tryStartInteraction.Invoke(null, args);
                handle = args[1];
                reason = args[2] as string ?? string.Empty;
                if (!started || handle == null)
                {
                    handle = null;
                    if (string.IsNullOrWhiteSpace(reason))
                        reason = "interaction_start_rejected";
                    return false;
                }

                if (!TrySetBool(handle, Y4NGZPlayerAnimationBridge.SeatedBool, true))
                {
                    TryStop(handle, "Interrupted");
                    handle = null;
                    reason = "seated_parameter_rejected";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                reason = "interaction_start_exception:" + Unwrap(e);
                handle = null;
                return false;
            }
        }

        internal static bool TryBeginExit(object handle, out string reason)
        {
            reason = string.Empty;
            if (handle == null || !_typesResolved || _tryBeginInteractionExit == null)
            {
                reason = "interaction_exit_api_unavailable";
                return false;
            }

            try
            {
                object[] args = { handle, null };
                bool began = (bool)_tryBeginInteractionExit.Invoke(null, args);
                reason = args[1] as string ?? string.Empty;
                return began;
            }
            catch (Exception e)
            {
                reason = "interaction_exit_exception:" + Unwrap(e);
                return false;
            }
        }

        internal static bool TryStop(object handle, string stopReasonName)
        {
            if (handle == null || !_typesResolved || _tryStopInteraction == null)
                return false;

            try
            {
                object stopReason = Enum.Parse(
                    _stopReasonType,
                    string.IsNullOrWhiteSpace(stopReasonName) ? "Interrupted" : stopReasonName);
                return (bool)_tryStopInteraction.Invoke(null, new[] { handle, stopReason });
            }
            catch (Exception e)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ApiPort] stop_failed handle={FormatHandle(handle)} reason='{Unwrap(e)}'.");
                return false;
            }
        }

        internal static bool IsActive(object handle)
        {
            if (handle == null || !_typesResolved || _isInteractionActive == null)
                return false;

            try
            {
                return (bool)_isInteractionActive.Invoke(null, new[] { handle });
            }
            catch (Exception e)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][ApiPort] active_check_failed handle={FormatHandle(handle)} reason='{Unwrap(e)}'.");
                return false;
            }
        }

        internal static bool TrySetBool(object handle, string parameterName, bool value)
        {
            return InvokeParameterMethod(
                _trySetInteractionBool,
                handle,
                parameterName,
                value);
        }

        internal static bool TrySetInt(object handle, string parameterName, int value)
        {
            return InvokeParameterMethod(
                _trySetInteractionInt,
                handle,
                parameterName,
                value);
        }

        internal static bool TryFireTrigger(object handle, string parameterName)
        {
            if (handle == null || string.IsNullOrWhiteSpace(parameterName) ||
                !_typesResolved || _tryFireInteractionTrigger == null)
            {
                return false;
            }

            try
            {
                return (bool)_tryFireInteractionTrigger.Invoke(
                    null,
                    new object[] { handle, parameterName });
            }
            catch
            {
                return false;
            }
        }

        internal static string FormatHandle(object handle)
        {
            return handle != null ? handle.ToString() : "<null>";
        }

        private static bool TryRegisterPack(bool forceRetry, out string reason)
        {
            reason = string.Empty;
            if (_packRegistered)
                return true;
            if (!ConfigEnabled)
            {
                reason = "kill_switch_disabled";
                return false;
            }
            if (!forceRetry && Time.realtimeSinceStartup < _nextRegistrationRetryAt)
            {
                reason = "registration_retry_throttled";
                return false;
            }
            if (!TryResolveApi(out reason))
            {
                // #716 C5: the only way this becomes true later is Y4NGZInteractions loading,
                // which the assembly-load hook reports for free.
                AbandonRegistration(rearmOnAssemblyLoad: true);
                return false;
            }

            string assetRootPath = GetPluginDirectory();
            string worldManifestPath = Path.Combine(assetRootPath, WorldManifestFileName);
            string worldBundlePath = Path.Combine(assetRootPath, WorldBundleFileName);
            string viewmodelManifestPath = Path.Combine(assetRootPath, ViewmodelManifestFileName);
            string viewmodelBundlePath = Path.Combine(assetRootPath, ViewmodelBundleFileName);
            string[] requiredFiles =
            {
                worldManifestPath,
                worldBundlePath,
                viewmodelManifestPath,
                viewmodelBundlePath,
            };
            for (int i = 0; i < requiredFiles.Length; i++)
            {
                if (!File.Exists(requiredFiles[i]))
                {
                    reason = "pack_asset_missing:" + requiredFiles[i];
                    LogPackFailureOnce(reason);
                    AbandonRegistration(rearmOnAssemblyLoad: false);
                    return false;
                }
            }

            try
            {
                string worldManifestJson = File.ReadAllText(worldManifestPath);
                string viewmodelManifestJson = File.ReadAllText(viewmodelManifestPath);
                if (!TryParseManifest(worldManifestJson, out CctvOperatorManifest worldManifest, out string manifestDetail))
                {
                    reason = $"manifest_parse_failed:{worldManifestPath} {manifestDetail}";
                    LogPackFailureOnce(reason);
                    AbandonRegistration(rearmOnAssemblyLoad: false);
                    return false;
                }
                if (!TryParseManifest(viewmodelManifestJson, out CctvOperatorManifest viewmodelManifest, out manifestDetail))
                {
                    reason = $"manifest_parse_failed:{viewmodelManifestPath} {manifestDetail}";
                    LogPackFailureOnce(reason);
                    AbandonRegistration(rearmOnAssemblyLoad: false);
                    return false;
                }
                if (!ValidateWorldManifest(worldManifest, out manifestDetail))
                {
                    reason = $"manifest_contract_mismatch:{worldManifestPath} {manifestDetail}";
                    LogPackFailureOnce(reason);
                    AbandonRegistration(rearmOnAssemblyLoad: false);
                    return false;
                }
                if (!ValidateViewmodelManifest(viewmodelManifest, out manifestDetail))
                {
                    reason = $"manifest_contract_mismatch:{viewmodelManifestPath} {manifestDetail}";
                    LogPackFailureOnce(reason);
                    AbandonRegistration(rearmOnAssemblyLoad: false);
                    return false;
                }

                object worldInteraction = CreateInteractionDefinition(
                    WorldInteractionId,
                    "BodyWorld",
                    worldManifestJson);
                object viewmodelInteraction = CreateInteractionDefinition(
                    ViewmodelInteractionId,
                    "DedicatedLocalViewmodel",
                    viewmodelManifestJson);

                Array interactions = Array.CreateInstance(_interactionDefinitionType, 2);
                interactions.SetValue(worldInteraction, 0);
                interactions.SetValue(viewmodelInteraction, 1);

                object pack = Activator.CreateInstance(_packDefinitionType);
                SetProperty(pack, "PackId", PackId);
                SetProperty(pack, "Version", SurveillancePluginInfo.PLUGIN_VERSION);
                SetProperty(pack, "AssetRootPath", assetRootPath);
                SetProperty(pack, "Interactions", interactions);

                object[] args = { pack, null };
                bool registered = (bool)_tryRegisterInteractionPack.Invoke(null, args);
                reason = args[1] as string ?? string.Empty;
                if (!registered && string.Equals(reason, "pack_already_registered", StringComparison.OrdinalIgnoreCase))
                    registered = true;

                if (!registered)
                {
                    if (reason.IndexOf("not_initialized", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ScheduleRegistrationRetry();
                        if (!_transientRegistrationLogged)
                        {
                            _transientRegistrationLogged = true;
                            SurveillanceBootstrap.Log?.LogInfo(
                                "[LethalCCTV][ApiPort] pack_registration_deferred reason='" + reason +
                                "'; retrying after Interactions initializes.");
                        }
                        return false;
                    }

                    LogPackFailureOnce("registration_failed:" + reason);
                    // MINOR 3: might be transient, so back off rather than latch permanently.
                    ScheduleRegistrationBackoff();
                    return false;
                }

                _packRegistered = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][ApiPort] pack_registered pack='{PackId}' " +
                    $"worldBundle='{worldBundlePath}' worldController='{WorldControllerName}' " +
                    $"viewmodelBundle='{viewmodelBundlePath}' viewmodelPrefab='{ViewmodelPrefabName}'.");
                return true;
            }
            catch (Exception e)
            {
                reason = "registration_exception:" + Unwrap(e);
                LogPackFailureOnce(reason);
                // MINOR 3: a one-off throw must be able to self-heal.
                ScheduleRegistrationBackoff();
                return false;
            }
        }

        private static object CreateInteractionDefinition(
            string interactionId,
            string presentationKind,
            string manifestJson)
        {
            object interaction = Activator.CreateInstance(_interactionDefinitionType);
            SetProperty(interaction, "InteractionId", interactionId);
            SetProperty(
                interaction,
                "PresentationKind",
                Enum.Parse(_presentationKindType, presentationKind));
            SetProperty(interaction, "ManifestJson", manifestJson);
            return interaction;
        }

        private static bool TryResolveApi(out string reason)
        {
            reason = string.Empty;
            if (_typesResolved)
                return true;

            try
            {
                _apiType = FindType("Y4NGZInteractions.InteractionAnimationApi.LCInteractionAnimationAPI");
                _packDefinitionType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationPackDefinition");
                _interactionDefinitionType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationDefinition");
                _presentationKindType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationPresentationKind");
                _requestType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationRequest");
                _handleType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationHandle");
                _stopReasonType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationStopReason");

                if (_apiType == null || _packDefinitionType == null ||
                    _interactionDefinitionType == null || _presentationKindType == null ||
                    _requestType == null || _handleType == null || _stopReasonType == null)
                {
                    reason = "required_api_types_missing";
                    LogResolutionFailureOnce(reason);
                    return false;
                }

                _tryRegisterInteractionPack = _apiType.GetMethod(
                    "TryRegisterInteractionPack", ApiFlags, null,
                    new[] { _packDefinitionType, typeof(string).MakeByRefType() }, null);
                _tryStartInteraction = _apiType.GetMethod(
                    "TryStartInteraction", ApiFlags, null,
                    new[] { _requestType, _handleType.MakeByRefType(), typeof(string).MakeByRefType() }, null);
                _tryBeginInteractionExit = _apiType.GetMethod(
                    "TryBeginInteractionExit", ApiFlags, null,
                    new[] { _handleType, typeof(string).MakeByRefType() }, null);
                _tryStopInteraction = _apiType.GetMethod(
                    "TryStopInteraction", ApiFlags, null,
                    new[] { _handleType, _stopReasonType }, null);
                _isInteractionActive = _apiType.GetMethod(
                    "IsInteractionActive", ApiFlags, null,
                    new[] { _handleType }, null);
                _trySetInteractionBool = _apiType.GetMethod(
                    "TrySetInteractionBool", ApiFlags, null,
                    new[] { _handleType, typeof(string), typeof(bool) }, null);
                _trySetInteractionInt = _apiType.GetMethod(
                    "TrySetInteractionInt", ApiFlags, null,
                    new[] { _handleType, typeof(string), typeof(int) }, null);
                _tryFireInteractionTrigger = _apiType.GetMethod(
                    "TryFireInteractionTrigger", ApiFlags, null,
                    new[] { _handleType, typeof(string) }, null);

                if (_tryRegisterInteractionPack == null || _tryStartInteraction == null ||
                    _tryBeginInteractionExit == null || _tryStopInteraction == null ||
                    _isInteractionActive == null || _trySetInteractionBool == null ||
                    _trySetInteractionInt == null || _tryFireInteractionTrigger == null)
                {
                    reason = "required_api_methods_missing";
                    LogResolutionFailureOnce(reason);
                    return false;
                }

                _typesResolved = true;
                return true;
            }
            catch (Exception e)
            {
                reason = "api_resolution_exception:" + Unwrap(e);
                LogResolutionFailureOnce(reason);
                return false;
            }
        }

        private static bool InvokeParameterMethod(
            MethodInfo method,
            object handle,
            string parameterName,
            object value)
        {
            if (handle == null || string.IsNullOrWhiteSpace(parameterName) ||
                !_typesResolved || method == null)
            {
                return false;
            }

            try
            {
                return (bool)method.Invoke(null, new[] { handle, parameterName, value });
            }
            catch
            {
                return false;
            }
        }

        private static void ScheduleRegistrationRetry()
        {
            _nextRegistrationRetryAt = Time.realtimeSinceStartup + RegistrationRetrySeconds;
        }

        /// <summary>#716 C5 / MINOR 3. Backoff for failures that MIGHT be transient - the
        /// registration call itself returning false, or throwing. A permanent latch would stop
        /// a one-off throw from ever self-healing, and the pre-patch flat 5s retry re-ran the
        /// assembly walk and four File.Exists calls forever. This doubles the delay from 5s to
        /// a 60s ceiling, so a transient fault recovers within seconds while a permanent one
        /// costs one attempt a minute instead of twelve.</summary>
        private static void ScheduleRegistrationBackoff()
        {
            _registrationBackoffSeconds = _registrationBackoffSeconds <= 0f
                ? RegistrationRetrySeconds
                : Mathf.Min(_registrationBackoffSeconds * 2f, MaxRegistrationBackoffSeconds);
            _nextRegistrationRetryAt = Time.realtimeSinceStartup + _registrationBackoffSeconds;
        }

        /// <summary>#716 C5. Stops the 5s retry for a failure that cannot resolve itself.
        /// <paramref name="rearmOnAssemblyLoad"/> is only true for "the Interactions assembly is
        /// not loaded yet": that one genuinely can change later, so a single AppDomain.AssemblyLoad
        /// subscription (installed once, never per attempt) lifts the latch instead of polling.
        /// Asset and manifest failures never re-arm - the files are read from the plugin
        /// directory at a fixed path and do not appear mid-session.</summary>
        private static void AbandonRegistration(bool rearmOnAssemblyLoad)
        {
            _registrationAbandoned = true;
            if (!rearmOnAssemblyLoad || _assemblyLoadHookInstalled)
                return;

            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoadedWhileAbandoned;
                _assemblyLoadHookInstalled = true;
            }
            catch
            {
                // Subscribing is best-effort; without it the pack simply stays unregistered
                // for the session, which is the same outcome the retry loop reached anyway.
            }
        }

        private static void OnAssemblyLoadedWhileAbandoned(object sender, AssemblyLoadEventArgs args)
        {
            if (!_registrationAbandoned || _packRegistered)
                return;

            string name = args?.LoadedAssembly?.GetName()?.Name;
            if (name == null || name.IndexOf("Y4NGZInteractions", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            _registrationAbandoned = false;
            _resolutionFailureLogged = false;
            _nextRegistrationRetryAt = 0f;
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }
            return null;
        }

        private static void SetProperty(object target, string propertyName, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite)
                throw new MissingMemberException(target?.GetType().FullName, propertyName);
            property.SetValue(target, value, null);
        }

        private static string GetPluginDirectory()
        {
            string directory = Path.GetDirectoryName(
                typeof(CCTVOperatorInteractionsBridge).Assembly.Location);
            return string.IsNullOrWhiteSpace(directory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : directory;
        }

        private static string Unwrap(Exception e)
        {
            Exception inner = e is TargetInvocationException && e.InnerException != null
                ? e.InnerException
                : e;
            return inner.GetType().Name + ": " + inner.Message;
        }

        private static void LogResolutionFailureOnce(string reason)
        {
            if (_resolutionFailureLogged)
                return;
            _resolutionFailureLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV][ApiPort] api_unavailable reason='{reason}'.");
        }

        private static void LogPackFailureOnce(string reason)
        {
            if (_packFailureLogged)
                return;
            _packFailureLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV][ApiPort] pack_unavailable reason='{reason}'.");
        }

        // #593: the manifest gate no longer goes through
        // JsonUtility.FromJson<CctvOperatorManifest>. Unity's serializer only
        // populates nested [Serializable] classes while
        // FixPluginTypesSerialization is present and healthy; where it is not,
        // `body`/`localViewmodel` came back null and the pack was rejected as
        // `manifest_contract_mismatch` against a file that matched the
        // constants exactly - taking the whole API session down to the legacy
        // fallback. The reader below is local, dependency-free, and covers only
        // the small shape these two manifests use. Migrate it to Y4NGZCore's
        // Y4NGZJson helper (#591) once that ships.
        private static bool TryParseManifest(
            string json,
            out CctvOperatorManifest manifest,
            out string detail)
        {
            manifest = null;
            if (!ManifestJson.TryParseObject(json, out Dictionary<string, object> root, out detail))
                return false;

            manifest = new CctvOperatorManifest
            {
                interactionId = ManifestJson.ReadString(root, "interactionId"),
                bundleInternalName = ManifestJson.ReadString(root, "bundleInternalName"),
            };
            if (ManifestJson.TryReadObject(root, "body", out Dictionary<string, object> body))
            {
                manifest.body = new CctvBodyManifest
                {
                    enabled = ManifestJson.ReadBool(body, "enabled"),
                    bundleFileName = ManifestJson.ReadString(body, "bundleFileName"),
                    controllerAssetName = ManifestJson.ReadString(body, "controllerAssetName"),
                };
            }
            if (ManifestJson.TryReadObject(root, "localViewmodel", out Dictionary<string, object> viewmodel))
            {
                manifest.localViewmodel = new CctvLocalViewmodelManifest
                {
                    bundleFileName = ManifestJson.ReadString(viewmodel, "bundleFileName"),
                    prefabAssetName = ManifestJson.ReadString(viewmodel, "prefabAssetName"),
                    controllerAssetName = ManifestJson.ReadString(viewmodel, "controllerAssetName"),
                };
            }

            detail = string.Empty;
            return true;
        }

        /// <summary>
        /// #593: reports the FIRST failing comparison with expected vs actual.
        /// The old all-in-one boolean said only that something mismatched,
        /// which is unactionable when the on-disk file matches the constants
        /// and the real fault is upstream of the comparison (a null section).
        /// </summary>
        private static bool ValidateWorldManifest(
            CctvOperatorManifest manifest,
            out string detail)
        {
            if (!RequireSection(manifest, "manifest", out detail))
                return false;
            if (!RequireField(manifest.interactionId, WorldInteractionId, "interactionId", ignoreCase: true, detail: out detail))
                return false;
            if (!RequireField(manifest.bundleInternalName, WorldBundleInternalName, "bundleInternalName", ignoreCase: true, detail: out detail))
                return false;
            if (!RequireSection(manifest.body, "body", out detail))
                return false;
            if (!manifest.body.enabled)
            {
                detail = "field=body.enabled expected='True' actual='False'";
                return false;
            }
            if (!RequireField(manifest.body.bundleFileName, WorldBundleFileName, "body.bundleFileName", ignoreCase: true, detail: out detail))
                return false;
            return RequireField(
                manifest.body.controllerAssetName,
                WorldControllerName,
                "body.controllerAssetName",
                ignoreCase: false,
                detail: out detail);
        }

        private static bool ValidateViewmodelManifest(
            CctvOperatorManifest manifest,
            out string detail)
        {
            if (!RequireSection(manifest, "manifest", out detail))
                return false;
            if (!RequireField(manifest.interactionId, ViewmodelInteractionId, "interactionId", ignoreCase: true, detail: out detail))
                return false;
            if (!RequireField(manifest.bundleInternalName, ViewmodelBundleInternalName, "bundleInternalName", ignoreCase: true, detail: out detail))
                return false;
            if (!RequireSection(manifest.localViewmodel, "localViewmodel", out detail))
                return false;
            if (!RequireField(manifest.localViewmodel.bundleFileName, ViewmodelBundleFileName, "localViewmodel.bundleFileName", ignoreCase: true, detail: out detail))
                return false;
            if (!RequireField(manifest.localViewmodel.prefabAssetName, ViewmodelPrefabName, "localViewmodel.prefabAssetName", ignoreCase: false, detail: out detail))
                return false;
            return RequireField(
                manifest.localViewmodel.controllerAssetName,
                ViewmodelControllerName,
                "localViewmodel.controllerAssetName",
                ignoreCase: false,
                detail: out detail);
        }

        private static bool RequireSection(object section, string field, out string detail)
        {
            if (section != null)
            {
                detail = string.Empty;
                return true;
            }

            detail = $"field={field} expected='object' actual='<null>'";
            return false;
        }

        private static bool RequireField(
            string actual,
            string expected,
            string field,
            bool ignoreCase,
            out string detail)
        {
            StringComparison comparison = ignoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(actual, expected, comparison))
            {
                detail = string.Empty;
                return true;
            }

            detail =
                $"field={field} expected='{expected}' actual='{(actual ?? "<null>")}' " +
                $"comparison={(ignoreCase ? "OrdinalIgnoreCase" : "Ordinal")}";
            return false;
        }

        private sealed class CctvOperatorManifest
        {
            public string interactionId;
            public string bundleInternalName;
            public CctvLocalViewmodelManifest localViewmodel;
            public CctvBodyManifest body;
        }

        private sealed class CctvLocalViewmodelManifest
        {
            public string bundleFileName;
            public string prefabAssetName;
            public string controllerAssetName;
        }

        private sealed class CctvBodyManifest
        {
            public bool enabled;
            public string bundleFileName;
            public string controllerAssetName;
        }

        /// <summary>
        /// Minimal JSON object reader for the two CCTV operator manifests
        /// (#593). Objects, arrays, strings, booleans, numbers and null parse;
        /// numbers are kept as text because nothing in the gate compares them.
        /// This is deliberately not a general-purpose parser - it exists so the
        /// pack gate cannot be taken down by a broken Unity serializer. Replace
        /// with Y4NGZCore's Y4NGZJson helper (#591) when it lands.
        /// </summary>
        private static class ManifestJson
        {
            internal static bool TryParseObject(
                string json,
                out Dictionary<string, object> value,
                out string error)
            {
                value = null;
                if (string.IsNullOrWhiteSpace(json))
                {
                    error = "detail='empty document'";
                    return false;
                }

                try
                {
                    int index = 0;
                    object parsed = ParseValue(json, ref index);
                    SkipWhitespace(json, ref index);
                    if (index != json.Length)
                        throw new FormatException($"trailing content at offset {index}");

                    value = parsed as Dictionary<string, object>;
                    if (value == null)
                    {
                        error = "detail='document root is not a JSON object'";
                        return false;
                    }

                    error = string.Empty;
                    return true;
                }
                catch (Exception e)
                {
                    error = $"detail='{e.GetType().Name}: {e.Message}'";
                    return false;
                }
            }

            internal static bool TryReadObject(
                Dictionary<string, object> owner,
                string key,
                out Dictionary<string, object> value)
            {
                value = owner != null && owner.TryGetValue(key, out object raw)
                    ? raw as Dictionary<string, object>
                    : null;
                return value != null;
            }

            internal static string ReadString(Dictionary<string, object> owner, string key)
            {
                return owner != null && owner.TryGetValue(key, out object raw)
                    ? raw as string
                    : null;
            }

            internal static bool ReadBool(Dictionary<string, object> owner, string key)
            {
                return owner != null && owner.TryGetValue(key, out object raw) &&
                    raw is bool parsed && parsed;
            }

            private static object ParseValue(string json, ref int index)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    throw new FormatException("unexpected end of document");

                char c = json[index];
                switch (c)
                {
                    case '{':
                        return ParseObject(json, ref index);
                    case '[':
                        return ParseArray(json, ref index);
                    case '"':
                        return ParseString(json, ref index);
                    default:
                        return ParseLiteral(json, ref index);
                }
            }

            private static Dictionary<string, object> ParseObject(string json, ref int index)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                index++; // '{'
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == '}')
                {
                    index++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace(json, ref index);
                    if (index >= json.Length || json[index] != '"')
                        throw new FormatException($"expected a member name at offset {index}");

                    string name = ParseString(json, ref index);
                    SkipWhitespace(json, ref index);
                    if (index >= json.Length || json[index] != ':')
                        throw new FormatException($"expected ':' after member '{name}'");

                    index++;
                    result[name] = ParseValue(json, ref index);
                    SkipWhitespace(json, ref index);
                    if (index >= json.Length)
                        throw new FormatException("unterminated object");
                    if (json[index] == ',')
                    {
                        index++;
                        continue;
                    }
                    if (json[index] == '}')
                    {
                        index++;
                        return result;
                    }

                    throw new FormatException($"expected ',' or '}}' at offset {index}");
                }
            }

            private static List<object> ParseArray(string json, ref int index)
            {
                var result = new List<object>();
                index++; // '['
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ']')
                {
                    index++;
                    return result;
                }

                while (true)
                {
                    result.Add(ParseValue(json, ref index));
                    SkipWhitespace(json, ref index);
                    if (index >= json.Length)
                        throw new FormatException("unterminated array");
                    if (json[index] == ',')
                    {
                        index++;
                        continue;
                    }
                    if (json[index] == ']')
                    {
                        index++;
                        return result;
                    }

                    throw new FormatException($"expected ',' or ']' at offset {index}");
                }
            }

            private static string ParseString(string json, ref int index)
            {
                index++; // opening quote
                var builder = new System.Text.StringBuilder();
                while (index < json.Length)
                {
                    char c = json[index++];
                    if (c == '"')
                        return builder.ToString();
                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (index >= json.Length)
                        break;
                    char escape = json[index++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (index + 4 > json.Length)
                                throw new FormatException($"truncated \\u escape at offset {index}");
                            builder.Append((char)ushort.Parse(
                                json.Substring(index, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            index += 4;
                            break;
                        default:
                            throw new FormatException($"unsupported escape '\\{escape}' at offset {index}");
                    }
                }

                throw new FormatException("unterminated string");
            }

            private static object ParseLiteral(string json, ref int index)
            {
                int start = index;
                while (index < json.Length && !IsLiteralTerminator(json[index]))
                    index++;

                string token = json.Substring(start, index - start).Trim();
                if (token.Length == 0)
                    throw new FormatException($"expected a value at offset {start}");
                if (string.Equals(token, "true", StringComparison.Ordinal))
                    return true;
                if (string.Equals(token, "false", StringComparison.Ordinal))
                    return false;
                if (string.Equals(token, "null", StringComparison.Ordinal))
                    return null;

                // Numbers are irrelevant to the pack gate; keep the raw text so
                // an exotic value can never fail the parse for the fields that
                // do matter.
                return token;
            }

            private static bool IsLiteralTerminator(char c)
            {
                return c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c);
            }

            private static void SkipWhitespace(string json, ref int index)
            {
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                    index++;
            }
        }
    }
}
