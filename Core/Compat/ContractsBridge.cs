using System;
using System.Reflection;

namespace Y4NGZCompany.Core.Compat
{
    /// <summary>
    /// Soft bridge to the parts of Y4NGZCompany's contract layer that CCTV security reads.
    ///
    /// Three things cross this boundary:
    /// - which contract is running, so Containment Breach can suspend hostile cameras;
    /// - the moon's risk tier, which scales how many cameras go security-active;
    /// - Contracted's shared facility lockdown gates, which the mainframe drill, CCTV alarm,
    ///   and Containment Breach contract can all hold when that complete API is available.
    ///
    /// All members are resolved by name once and cached. With Y4NGZCompany absent, the
    /// contract/risk fallbacks mean "no contract is running" and "no recognized tier". A
    /// missing complete gate API is instead the signal for CctvLockdownGateService to use
    /// LethalCCTV's standalone bundled/generated gates.
    /// </summary>
    internal static class ContractsBridge
    {
        private const string StateTypeName =
            "Y4NGZCompany.Contracts._Shared.MoonContractState";

        private const string BreachDirectorTypeName =
            "Y4NGZCompany.Contracts.ContainmentBreach.ContainmentBreachDirector";

        /// <summary>
        /// Risk tier reported when the contracts plugin is absent. CctvSecurityDirector treats
        /// any unrecognized tier as "use the flat ActiveCameraRatio/ActiveCameraMax fallback",
        /// which is exactly the intended behaviour here.
        /// </summary>
        private const char UnknownRiskTier = '?';

        private static bool _resolved;
        private static MethodInfo _getCurrentContract;
        private static MethodInfo _getCurrentRiskTier;
        private static PropertyInfo _containmentLockdownHeld;
        private static PropertyInfo _lockdownActive;
        private static MethodInfo _prepareLockdownGates;
        private static MethodInfo _beginLockdown;
        private static MethodInfo _endLockdown;
        private static MethodInfo _playLockdownSlam;

        /// <summary>True while the Containment Breach contract is the active contract.</summary>
        internal static bool IsContainmentBreachContractActive()
        {
            Resolve();
            if (_getCurrentContract == null)
                return false;

            try
            {
                // Compared by enum name rather than by value: this assembly cannot see
                // MoonContractType, and the numeric values are free to shift when a
                // contract is added upstream.
                object contract = _getCurrentContract.Invoke(null, null);
                return contract != null
                    && string.Equals(contract.ToString(), "ContainmentBreach", StringComparison.Ordinal);
            }
            catch
            {
                _getCurrentContract = null;
                return false;
            }
        }

        /// <summary>The current moon's C/B/A/S risk tier, or '?' when it cannot be resolved.</summary>
        internal static char GetCurrentRiskTier()
        {
            Resolve();
            if (_getCurrentRiskTier == null)
                return UnknownRiskTier;

            try
            {
                return _getCurrentRiskTier.Invoke(null, null) is char tier ? tier : UnknownRiskTier;
            }
            catch
            {
                _getCurrentRiskTier = null;
                return UnknownRiskTier;
            }
        }

        /// <summary>
        /// <see cref="GetCurrentRiskTier"/> behind a short-lived cache (#466).
        ///
        /// The per-risk-level security block made the tier part of
        /// <c>CctvSecurityConfig.Current</c>, which presentation code reads per camera per
        /// frame; a reflection <c>Invoke</c> at that rate is not free and the answer only
        /// changes when the crew lands on another moon. One second of staleness is
        /// invisible - the tier is fixed for the whole round.
        /// </summary>
        internal static char GetCurrentRiskTierCached()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (_riskTierCachedAt > 0f && now - _riskTierCachedAt < RiskTierCacheSeconds && now >= _riskTierCachedAt)
                return _cachedRiskTier;

            _cachedRiskTier = GetCurrentRiskTier();
            _riskTierCachedAt = now;
            return _cachedRiskTier;
        }

        private const float RiskTierCacheSeconds = 1f;
        private static char _cachedRiskTier = UnknownRiskTier;
        private static float _riskTierCachedAt;

        /// <summary>True while the Containment Breach contract itself is holding the gates shut.</summary>
        internal static bool ContainmentLockdownHeld => ReadBool(ref _containmentLockdownHeld);

        /// <summary>True while any holder has the shared facility lockdown gates shut.</summary>
        internal static bool LockdownActive => ReadBool(ref _lockdownActive);

        /// <summary>Whether Contracted exposes the complete shared-gate API.</summary>
        internal static bool HasLockdownGateApi
        {
            get
            {
                Resolve();
                return _lockdownActive != null
                    && _prepareLockdownGates != null
                    && _beginLockdown != null
                    && _endLockdown != null
                    && _playLockdownSlam != null;
            }
        }

        /// <summary>
        /// Wakes the gate objects so <see cref="BeginLockdown"/> has something to close.
        /// No-op without Contracted; CctvLockdownGateService owns the standalone path.
        /// </summary>
        internal static void PrepareLockdownGates() => Invoke(ref _prepareLockdownGates);

        internal static void BeginLockdown() => Invoke(ref _beginLockdown);

        internal static void EndLockdown() => Invoke(ref _endLockdown);

        internal static void PlayLockdownSlam() => Invoke(ref _playLockdownSlam);

        private static bool ReadBool(ref PropertyInfo slot)
        {
            Resolve();
            PropertyInfo property = slot;
            if (property == null)
                return false;

            try
            {
                return property.GetValue(null) is bool value && value;
            }
            catch
            {
                slot = null;
                return false;
            }
        }

        private static void Invoke(ref MethodInfo slot)
        {
            Resolve();
            MethodInfo method = slot;
            if (method == null)
                return;

            try
            {
                method.Invoke(null, null);
            }
            catch
            {
                // The gate machinery belongs to another plugin; a failure there must not
                // abort the mainframe protocol that asked for it.
                slot = null;
            }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;

            const BindingFlags StaticMembers =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                Type state = CompanyAssemblyBridge.ResolveContractedType(StateTypeName);
                if (state != null)
                {
                    _getCurrentContract = state.GetMethod("GetCurrentContract", StaticMembers, null, Type.EmptyTypes, null);
                    _getCurrentRiskTier = state.GetMethod("GetCurrentRiskTier", StaticMembers, null, Type.EmptyTypes, null);
                    _containmentLockdownHeld = state.GetProperty("ContainmentLockdownHeld", StaticMembers);
                }

                Type director = CompanyAssemblyBridge.ResolveContractedType(BreachDirectorTypeName);
                if (director != null)
                {
                    _lockdownActive = director.GetProperty("LockdownActive", StaticMembers);
                    _prepareLockdownGates = director.GetMethod("PrepareLockdownGates", StaticMembers, null, Type.EmptyTypes, null);
                    _beginLockdown = director.GetMethod("BeginLockdown", StaticMembers, null, Type.EmptyTypes, null);
                    _endLockdown = director.GetMethod("EndLockdown", StaticMembers, null, Type.EmptyTypes, null);
                    _playLockdownSlam = director.GetMethod("PlayLockdownSlam", StaticMembers, null, Type.EmptyTypes, null);
                }
            }
            catch
            {
                _getCurrentContract = null;
                _getCurrentRiskTier = null;
                _containmentLockdownHeld = null;
                _lockdownActive = null;
                _prepareLockdownGates = null;
                _beginLockdown = null;
                _endLockdown = null;
                _playLockdownSlam = null;
            }
        }
    }
}
