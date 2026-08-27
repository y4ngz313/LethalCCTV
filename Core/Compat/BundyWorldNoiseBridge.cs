using System;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Core.Compat
{
    /// <summary>
    /// Soft bridge to Y4NGZCompany's Bundy world-noise bus.
    ///
    /// The CCTV operator station and the ship turret are audible events, and before #393 they
    /// called <c>BundyWorldNoisePatchSupport.Report</c> directly. That is a hard reference from
    /// this plugin into the contracts plugin, which LethalCCTV must run without. Everything is
    /// resolved by name once and cached; when Y4NGZCompany is absent - or its internals move -
    /// every report is a silent no-op, exactly matching the old behaviour of a bus that
    /// discards reports nobody owns.
    ///
    /// The meaning is passed as its integer value and boxed back into the real enum here, so
    /// this file never needs the enum type at compile time. The values mirror
    /// <c>Y4NGZCompany.Escalations.Bundy.AI.BundyWorldNoise</c>; they are explicit ordinals
    /// upstream, so they are stable to reorder-refactors on the other side.
    /// </summary>
    internal static class BundyWorldNoiseBridge
    {
        /// <summary>Mirror of <c>BundyWorldNoise.CctvStationUse</c>.</summary>
        internal const int CctvStationUse = 8;

        /// <summary>Mirror of <c>BundyWorldNoise.ShipTurretFire</c>.</summary>
        internal const int ShipTurretFire = 9;

        private const string SupportTypeName =
            "Y4NGZCompany.Escalations.Bundy.BundyWorldNoisePatchSupport";

        private const string NoiseEnumTypeName =
            "Y4NGZCompany.Escalations.Bundy.AI.BundyWorldNoise";

        private static bool _resolved;
        private static MethodInfo _report;
        private static Type _noiseEnumType;

        /// <summary>
        /// Reports one world noise. Never throws: a bus failure must not take down the
        /// interaction that produced the sound, which was the contract on the direct call too.
        /// </summary>
        internal static void Report(Vector3 position, int meaning, PlayerControllerB causedBy)
        {
            Resolve();
            if (_report == null || _noiseEnumType == null)
                return;

            try
            {
                _report.Invoke(null, new object[]
                {
                    position,
                    Enum.ToObject(_noiseEnumType, meaning),
                    causedBy,
                });
            }
            catch
            {
                // Matches the upstream guarantee: reporting is best-effort.
            }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;

            try
            {
                Type support = CompanyAssemblyBridge.ResolveContractedType(SupportTypeName);
                _noiseEnumType = CompanyAssemblyBridge.ResolveContractedType(NoiseEnumTypeName);
                if (support == null || _noiseEnumType == null)
                    return;

                // Internal on the other side, hence NonPublic; the optional `causedBy`
                // parameter is not optional through reflection, so all three are passed.
                _report = support.GetMethod(
                    "Report",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(Vector3), _noiseEnumType, typeof(PlayerControllerB) },
                    modifiers: null);
            }
            catch
            {
                _report = null;
                _noiseEnumType = null;
            }
        }
    }
}
