using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CCTVShipSystemsBridge
    {
        private const string ApiTypeName = "Y4NGZCompany.ShipSystems.Layout.ShipSystemsApi";
        private const float UpgradeCacheSeconds = 0.5f;
        // Shorter than the upgrade cache: a battery break must darken the
        // monitors promptly, not up to half a second later.
        private const float PowerStateCacheSeconds = 0.15f;

        private static Type _apiType;
        private static bool _resolved;
        private static readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();
        private static bool _cachedHasTurretUpgrade;
        private static float _turretCacheValidUntil;
        private static bool _cachedShipPowerOnline = true;
        private static float _shipPowerCacheValidUntil;
        private static bool _cachedQuotaPresentationActive;
        private static float _quotaPresentationCacheValidUntil;

        internal static bool IsAvailable
        {
            get
            {
                Resolve();
                return _apiType != null;
            }
        }

        internal static bool CanUseRemoteOperations(out string reason)
        {
            reason = string.Empty;
            if (!IsAvailable) return true;

            if (InvokeBool("CanUseRemoteOperations", defaultValue: false))
                return true;

            reason = InvokeString("GetRemoteOperationsBlockReason", "REMOTE OPS LOCKED");
            return false;
        }

        internal static bool TrySpendRemoteOperationPower()
        {
            if (!IsAvailable) return true;
            return InvokeBool("TrySpendCctvRemotePower", defaultValue: false);
        }

        internal static bool HasTurretUpgrade()
        {
            float now = Time.unscaledTime;
            if (now < _turretCacheValidUntil)
                return _cachedHasTurretUpgrade;

            _turretCacheValidUntil = now + UpgradeCacheSeconds;
            _cachedHasTurretUpgrade = IsAvailable &&
                (InvokeBool("HasTurretUpgrade", defaultValue: false) ||
                 InvokeBool("HasUpgrade", defaultValue: false, "Turret"));
            return _cachedHasTurretUpgrade;
        }

        internal static bool TrySpendTurretShotPower()
        {
            if (!IsAvailable) return false;
            return InvokeBool("TrySpendTurretShotPower", defaultValue: false);
        }

        internal static float GetPowerFraction()
        {
            if (!IsAvailable) return 1f;
            return InvokeFloat("GetPowerFraction", 1f);
        }

        /// <summary>
        /// Ship power state for presentation gating. ShipSystemsApi already
        /// folds a broken battery into this (`ShipPowerOnline &amp;&amp; !BatteryBroken`),
        /// so a battery destroyed mid-round reads as unpowered here. Defaults to
        /// powered when ShipSystems is absent so a CCTV-only install is never
        /// dark. Cached like the upgrade probes — this is polled per frame by
        /// the monitor display.
        /// </summary>
        internal static bool IsShipPowerOnline()
        {
            float now = Time.unscaledTime;
            if (now < _shipPowerCacheValidUntil)
                return _cachedShipPowerOnline;

            _shipPowerCacheValidUntil = now + PowerStateCacheSeconds;
            _cachedShipPowerOnline = !IsAvailable ||
                InvokeBool("IsShipPowerOnline", defaultValue: true);
            return _cachedShipPowerOnline;
        }

        internal static bool IsBatteryBroken()
        {
            if (!IsAvailable) return false;
            return InvokeBool("IsBatteryBroken", defaultValue: false);
        }

        internal static bool IsPrimaryMonitorTakeoverActive()
        {
            if (!IsAvailable) return false;
            return InvokeBool("IsPrimaryMonitorTakeoverActive", defaultValue: false);
        }

        /// <summary>
        /// True while ShipSystems holds its ship-wide quota presentation (the
        /// rewards ceremony). The ceremony suspends the vanilla monitor
        /// producers itself; CCTV's recovery guards must stand down for the
        /// scope's length, same as they do for the primary takeover. Cached on
        /// the power-state cadence — the guard polls this every pass, and the
        /// suspension has to be honored promptly at scope open.
        /// </summary>
        internal static bool IsQuotaPresentationActive()
        {
            float now = Time.unscaledTime;
            if (now < _quotaPresentationCacheValidUntil)
                return _cachedQuotaPresentationActive;

            _quotaPresentationCacheValidUntil = now + PowerStateCacheSeconds;
            _cachedQuotaPresentationActive = IsAvailable &&
                InvokeBool("IsQuotaPresentationActive", defaultValue: false);
            return _cachedQuotaPresentationActive;
        }

        internal static bool IsExternalMonitorTakeoverActive()
        {
            if (!IsAvailable) return false;
            return InvokeBool("IsExternalMonitorTakeoverActive", defaultValue: false);
        }

        internal static void NoteTurretShotAccepted()
        {
            if (!IsAvailable) return;
            InvokeVoid("NoteTurretShotAccepted");
        }

        internal static string GetCurrentLayoutName()
        {
            if (!IsAvailable) return "Vanilla";
            return InvokeString("GetCurrentLayoutName", "Vanilla");
        }

        internal static bool IsPowerMonitorDisplayEnabled()
        {
            if (!IsAvailable) return false;
            return InvokeBool("IsPowerMonitorDisplayEnabled", defaultValue: false);
        }

        internal static bool TryUseSignalBeacon()
        {
            if (!IsAvailable) return false;
            return InvokeBool("TryUseSignalBeacon", defaultValue: false);
        }

        internal static bool TryUseSpeakerHijack()
        {
            if (!IsAvailable) return false;
            return InvokeBool("TryUseSpeakerHijack", defaultValue: false);
        }

        internal static void BeginExternalMonitorTakeover()
        {
            if (!IsAvailable) return;
            // Prefer the scoped takeover: CCTV only renders to the two large
            // monitors (Cube.001), so the fuel/quota/deadline overlays on the
            // small monitors must keep running. Fall back to the legacy
            // full-wall takeover on older ShipSystems builds.
            if (GetApiMethod("BeginPrimaryMonitorTakeover") != null)
                InvokeVoid("BeginPrimaryMonitorTakeover");
            else
                InvokeVoid("BeginExternalMonitorTakeover");
        }

        internal static void EndExternalMonitorTakeover()
        {
            if (!IsAvailable) return;
            if (GetApiMethod("EndPrimaryMonitorTakeover") != null)
                InvokeVoid("EndPrimaryMonitorTakeover");
            else
                InvokeVoid("EndExternalMonitorTakeover");
        }

        internal static void ForceRebindPowerDisplay()
        {
            if (!IsAvailable) return;
            InvokeVoid("ForceRebindPowerDisplay");
        }

        // One attempt, memoized for the session: split Ship Systems and Contracted are
        // soft BepInEx dependencies of this plugin, so both have already chainloaded
        // when anything here asks. Retrying on a timer bought nothing and cost a Mono
        // assembly-load probe every tick on a standalone install (#592).
        private static void Resolve()
        {
            if (_resolved) return;

            _resolved = true;
            _apiType = CompanyAssemblyBridge.ResolveType(ApiTypeName);
            if (_apiType != null)
                _methodCache.Clear();
        }

        private static MethodInfo GetApiMethod(string methodName)
        {
            if (_apiType == null || string.IsNullOrEmpty(methodName))
                return null;

            if (_methodCache.TryGetValue(methodName, out MethodInfo cached))
                return cached;

            MethodInfo method = _apiType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            _methodCache[methodName] = method;
            return method;
        }

        private static bool InvokeBool(string methodName, bool defaultValue, params object[] args)
        {
            try
            {
                MethodInfo method = GetApiMethod(methodName);
                if (method == null) return defaultValue;
                object result = method.Invoke(null, args ?? Array.Empty<object>());
                return result is bool value ? value : defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        private static string InvokeString(string methodName, string defaultValue)
        {
            try
            {
                MethodInfo method = GetApiMethod(methodName);
                if (method == null) return defaultValue;
                object result = method.Invoke(null, Array.Empty<object>());
                return result as string ?? defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        private static float InvokeFloat(string methodName, float defaultValue)
        {
            try
            {
                MethodInfo method = GetApiMethod(methodName);
                if (method == null) return defaultValue;
                object result = method.Invoke(null, Array.Empty<object>());
                if (result is float f) return f;
                if (result is double d) return (float)d;
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        private static void InvokeVoid(string methodName)
        {
            try
            {
                MethodInfo method = GetApiMethod(methodName);
                method?.Invoke(null, Array.Empty<object>());
            }
            catch
            {
            }
        }
    }
}
