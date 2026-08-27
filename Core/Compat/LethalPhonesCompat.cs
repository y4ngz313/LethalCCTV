using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Core.Compat
{
    // Soft-dependency bridge for CassCoffey/Scoops "LethalPhones".
    //
    // Why this exists (#601): the CCTV obstructor mask hides the local player's
    // body and arm renderers for the duration of an operator focus session, but
    // LethalPhones draws the owner's phone from its own prefab clone parented
    // under the arms-only rig:
    //
    //   localArmsTransform/.../hand.L/LocalPhoneModel(Clone)/LocalPhoneModel
    //
    // Every renderer below that path resolves to a transform path containing
    // "ScavengerModelArmsOnly", which MonitorFocus classifies as a *real
    // first-person arm renderer* and therefore deliberately keeps visible (and
    // even force-enables) while the authored operator arms are presented. The
    // phone consequently renders over the station console for the whole
    // session, on the healthy Interactions-API path as well as the legacy one.
    //
    // LethalPhones exposes exactly the local-visual toggles we need:
    //   PlayerPhone.SetPhoneLocalModelActive(bool)   — renderer/canvas only
    //   PlayerPhone.SetPhoneServerModelActive(bool)  — renderer/canvas only
    // Neither sends an RPC or writes a NetworkVariable, so both are safe to
    // drive locally. The networked ToggleActive/ToggleServerPhoneModelServerRpc
    // path is deliberately never touched.
    //
    // Only the LOCAL player's PlayerPhone instance is ever addressed; remote
    // crew phones are untouched, and everything no-ops when the plugin is
    // absent.
    internal static class LethalPhonesCompat
    {
        public const string PLUGIN_GUID = "LethalPhones";

        private const string PlayerPhoneTypeName = "Scoops.misc.PlayerPhone, LethalPhones";
        private const string PhoneNetworkHandlerTypeName = "Scoops.service.PhoneNetworkHandler, LethalPhones";
        private const string PhonePrefabCloneName = "PhonePrefab(Clone)";

        private static readonly bool _isLoaded = Chainloader.PluginInfos.ContainsKey(PLUGIN_GUID);
        private static readonly HashSet<string> _callLogs = new HashSet<string>();

        private static bool _resolved;
        private static Type _playerPhoneType;
        private static MethodInfo _setLocalModelActive;
        private static MethodInfo _setServerModelActive;
        private static FieldInfo _phonePlayerField;
        private static FieldInfo _phoneToggledField;
        private static FieldInfo _localPhoneModelField;
        private static FieldInfo _serverPhoneModelField;
        private static PropertyInfo _handlerInstanceProperty;
        private static FieldInfo _handlerInstanceField;
        private static FieldInfo _handlerLocalPhoneField;
        private static string _resolveFailure;

        private static Component _suppressedPhone;
        private static bool _suppressionActive;
        private static bool _priorLocalModelVisible;
        private static bool _priorServerModelVisible;
        private static readonly List<Renderer> _suppressedRenderers = new List<Renderer>();
        private static readonly List<Canvas> _suppressedCanvases = new List<Canvas>();
        private static readonly List<GameObject> _suppressedCharms = new List<GameObject>();

        public static bool IsLoaded => _isLoaded;

        /// <summary>
        /// Hides the local player's phone for the duration of a CCTV focus
        /// session. Idempotent: a repeat call for the same phone only
        /// re-asserts the hidden state.
        /// </summary>
        internal static void SuppressLocalPhone(PlayerControllerB player, string reason)
        {
            if (!_isLoaded || player == null)
                return;

            Component phone = ResolveLocalPhone(player);
            if (phone == null)
            {
                // Nothing to hide: the phone prefab spawns asynchronously and a
                // dedicated-visual session can legitimately start before it.
                if (_suppressionActive)
                    RestoreLocalPhone("suppress-lost-phone:" + reason);
                return;
            }

            if (_suppressionActive && ReferenceEquals(_suppressedPhone, phone))
            {
                ReassertLocalPhoneSuppression();
                return;
            }

            if (_suppressionActive)
                RestoreLocalPhone("suppress-preclear:" + reason);

            try
            {
                GameObject localModel = ReadGameObject(_localPhoneModelField, phone);
                GameObject serverModel = ReadGameObject(_serverPhoneModelField, phone);

                _priorLocalModelVisible = ReadModelVisible(localModel, "LocalPhoneModel");
                _priorServerModelVisible = ReadModelVisible(serverModel, "ServerPhoneModel");

                CacheModelVisuals(localModel, "LocalPhoneModel");
                CacheModelVisuals(serverModel, "ServerPhoneModel");

                InvokeSetModelActive(_setLocalModelActive, phone, false);
                InvokeSetModelActive(_setServerModelActive, phone, false);

                _suppressedPhone = phone;
                _suppressionActive = true;
                ReassertLocalPhoneSuppression();
                LogOnce(
                    "suppress",
                    reason,
                    $"priorLocal={_priorLocalModelVisible} priorServer={_priorServerModelVisible} " +
                    $"renderers={_suppressedRenderers.Count} canvases={_suppressedCanvases.Count} " +
                    $"charms={_suppressedCharms.Count}");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][LethalPhones] Local phone suppression failed ({reason}): {ex.GetType().Name}: {ex.Message}");
                RestoreLocalPhone("suppress-failed:" + reason);
            }
        }

        /// <summary>
        /// Cheap per-frame re-assert, mirroring ReassertLocalPlayerObstructors:
        /// LethalPhones re-shows the model from its own ManageInputs/ToggleActive
        /// path whenever the owner presses the toggle key mid-session.
        /// </summary>
        internal static void ReassertLocalPhoneSuppression()
        {
            if (!_isLoaded || !_suppressionActive)
                return;

            for (int i = 0; i < _suppressedRenderers.Count; i++)
            {
                Renderer renderer = _suppressedRenderers[i];
                if (renderer == null) continue;
                try { if (renderer.enabled) renderer.enabled = false; } catch { }
            }

            for (int i = 0; i < _suppressedCanvases.Count; i++)
            {
                Canvas canvas = _suppressedCanvases[i];
                if (canvas == null) continue;
                try { if (canvas.enabled) canvas.enabled = false; } catch { }
            }

            for (int i = 0; i < _suppressedCharms.Count; i++)
            {
                GameObject charm = _suppressedCharms[i];
                if (charm == null) continue;
                try { if (charm.activeSelf) charm.SetActive(false); } catch { }
            }
        }

        /// <summary>
        /// Restores the phone through the mod's own local-visual setters. The
        /// first-person model follows LethalPhones' own <c>toggled</c> intent so
        /// a toggle pressed during focus survives the session; the third-person
        /// model is replayed at exactly the state captured at suppression.
        /// </summary>
        internal static void RestoreLocalPhone(string reason)
        {
            if (!_suppressionActive)
            {
                _suppressedPhone = null;
                ClearCaches();
                return;
            }

            Component phone = _suppressedPhone;
            bool localTarget = _priorLocalModelVisible;
            bool phoneAlive = phone != null;

            try
            {
                if (phoneAlive && _phoneToggledField != null)
                {
                    object toggled = _phoneToggledField.GetValue(phone);
                    if (toggled is bool toggledValue)
                        localTarget = toggledValue;
                }

                if (phoneAlive)
                {
                    InvokeSetModelActive(_setLocalModelActive, phone, localTarget);
                    InvokeSetModelActive(_setServerModelActive, phone, _priorServerModelVisible);
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][LethalPhones] Local phone restore failed ({reason}): {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _suppressionActive = false;
                _suppressedPhone = null;
                ClearCaches();
            }

            LogOnce("restore", reason, $"phoneAlive={phoneAlive} local={localTarget} server={_priorServerModelVisible}");
        }

        internal static void Shutdown()
        {
            RestoreLocalPhone("shutdown");
            _callLogs.Clear();
        }

        private static Component ResolveLocalPhone(PlayerControllerB player)
        {
            Resolve();
            if (_playerPhoneType == null)
                return null;

            Component phone = ResolveLocalPhoneFromHandler();
            if (IsPhoneOwnedBy(phone, player))
                return phone;

            // Fallback: LethalPhones re-parents the spawned prefab onto the
            // player, and every one of its own patches looks it up this way.
            try
            {
                Transform clone = player.transform != null ? player.transform.Find(PhonePrefabCloneName) : null;
                Component fallback = clone != null ? clone.GetComponent(_playerPhoneType) : null;
                if (IsPhoneOwnedBy(fallback, player))
                    return fallback;
            }
            catch { }

            return null;
        }

        private static Component ResolveLocalPhoneFromHandler()
        {
            if (_handlerLocalPhoneField == null)
                return null;

            try
            {
                object handler = _handlerInstanceProperty != null
                    ? _handlerInstanceProperty.GetValue(null)
                    : _handlerInstanceField?.GetValue(null);
                if (handler == null)
                    return null;

                return _handlerLocalPhoneField.GetValue(handler) as Component;
            }
            catch
            {
                return null;
            }
        }

        // Remote crew phones must never be touched: a candidate only counts when
        // its own PlayerPhone.player is the local controller we were handed.
        private static bool IsPhoneOwnedBy(Component phone, PlayerControllerB player)
        {
            if (phone == null || player == null)
                return false;
            if (_phonePlayerField == null)
                return true;

            try
            {
                return ReferenceEquals(_phonePlayerField.GetValue(phone), player);
            }
            catch
            {
                return false;
            }
        }

        private static GameObject ReadGameObject(FieldInfo field, object instance)
        {
            if (field == null || instance == null)
                return null;
            try
            {
                return field.GetValue(instance) as GameObject;
            }
            catch
            {
                return null;
            }
        }

        private static bool ReadModelVisible(GameObject model, string innerName)
        {
            Renderer main = FindRenderer(model, innerName);
            try { return main != null && main.enabled; }
            catch { return false; }
        }

        // Cache exactly the objects SetPhone*ModelActive drives, so the restore
        // call re-shows everything the per-frame re-assert touched. A broad
        // GetComponentsInChildren sweep would strand charm renderers disabled.
        private static void CacheModelVisuals(GameObject model, string innerName)
        {
            if (model == null)
                return;

            AddRenderer(FindRenderer(model, innerName));
            AddRenderer(FindRenderer(model, innerName + "/PhoneAntenna"));
            AddRenderer(FindRenderer(model, innerName + "/PhoneTop"));
            AddRenderer(FindRenderer(model, innerName + "/PhoneDial"));

            Transform canvas = FindChild(model, innerName + "/PhoneTop/PhoneCanvas");
            Canvas canvasComponent = canvas != null ? canvas.GetComponent<Canvas>() : null;
            if (canvasComponent != null && !_suppressedCanvases.Contains(canvasComponent))
                _suppressedCanvases.Add(canvasComponent);

            Transform charm = FindChild(model, innerName + "/CharmAttach");
            GameObject charmObject = charm != null ? charm.gameObject : null;
            if (charmObject != null && !_suppressedCharms.Contains(charmObject))
                _suppressedCharms.Add(charmObject);
        }

        private static void AddRenderer(Renderer renderer)
        {
            if (renderer != null && !_suppressedRenderers.Contains(renderer))
                _suppressedRenderers.Add(renderer);
        }

        private static Renderer FindRenderer(GameObject model, string path)
        {
            Transform child = FindChild(model, path);
            try { return child != null ? child.GetComponent<Renderer>() : null; }
            catch { return null; }
        }

        private static Transform FindChild(GameObject model, string path)
        {
            if (model == null)
                return null;
            try { return model.transform.Find(path); }
            catch { return null; }
        }

        private static void InvokeSetModelActive(MethodInfo method, object phone, bool active)
        {
            if (method == null || phone == null)
                return;
            method.Invoke(phone, new object[] { active });
        }

        private static void ClearCaches()
        {
            _suppressedRenderers.Clear();
            _suppressedCanvases.Clear();
            _suppressedCharms.Clear();
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
                _playerPhoneType = Type.GetType(PlayerPhoneTypeName, throwOnError: false);
                if (_playerPhoneType == null)
                {
                    LogResolveFailureOnce("Scoops.misc.PlayerPhone was not found");
                    return;
                }

                BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                _setLocalModelActive = _playerPhoneType.GetMethod(
                    "SetPhoneLocalModelActive", instanceFlags, binder: null, types: new[] { typeof(bool) }, modifiers: null);
                _setServerModelActive = _playerPhoneType.GetMethod(
                    "SetPhoneServerModelActive", instanceFlags, binder: null, types: new[] { typeof(bool) }, modifiers: null);
                _phonePlayerField = _playerPhoneType.GetField("player", instanceFlags);
                _phoneToggledField = _playerPhoneType.GetField("toggled", instanceFlags);
                _localPhoneModelField = _playerPhoneType.GetField("localPhoneModel", instanceFlags);
                _serverPhoneModelField = _playerPhoneType.GetField("serverPhoneModel", instanceFlags);

                Type handlerType = Type.GetType(PhoneNetworkHandlerTypeName, throwOnError: false);
                if (handlerType != null)
                {
                    _handlerInstanceProperty = handlerType.GetProperty("Instance", staticFlags);
                    _handlerInstanceField = handlerType.GetField("Instance", staticFlags);
                    _handlerLocalPhoneField = handlerType.GetField("localPhone", instanceFlags);
                }

                if (_setLocalModelActive == null)
                {
                    LogResolveFailureOnce("PlayerPhone.SetPhoneLocalModelActive(bool) was not found");
                    return;
                }

                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][LethalPhones] Local phone suppression armed: " +
                    $"localSetter={_setLocalModelActive != null} serverSetter={_setServerModelActive != null} " +
                    $"handlerLocalPhone={_handlerLocalPhoneField != null} toggledField={_phoneToggledField != null}.");
            }
            catch (Exception ex)
            {
                LogResolveFailureOnce($"reflection failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void LogResolveFailureOnce(string reason)
        {
            if (_resolveFailure != null)
                return;
            _resolveFailure = reason;
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV][LethalPhones] Phone suppression unavailable; no-op: {reason}.");
        }

        private static void LogOnce(string operation, string reason, string details)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
            if (!_callLogs.Add(operation + ":" + safeReason))
                return;

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][ObstructorMask] call={operation}-phone reason={safeReason} " +
                $"frame={Time.frameCount} {details}.");
        }
    }
}
