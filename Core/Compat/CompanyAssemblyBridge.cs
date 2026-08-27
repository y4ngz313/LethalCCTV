using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace Y4NGZCompany.Core.Compat
{
    /// <summary>
    /// The one place CCTV looks up a type inside the optional Y4NGZ companion assemblies.
    ///
    /// <para>Never resolve those types through an assembly-qualified
    /// <see cref="Type.GetType(string,bool)"/>. On a standalone CCTV install
    /// the companion DLLs may be absent, and every such call makes Mono run a full
    /// assembly-load probe (~5ms) that it does not negative-cache, so a bridge that
    /// re-probes on a timer costs that much every tick forever
    /// (y4ngz313/Y4NGZCompany#592). Scanning the already-loaded assemblies asks the
    /// same question without touching the loader.</para>
    ///
    /// <para>Contracted and Ship Systems are soft BepInEx dependencies of this plugin,
    /// so the chainloader has finished with both before any CCTV code runs. Their absence
    /// from <see cref="Chainloader.PluginInfos"/> is final, and one memoized answer is
    /// enough for the whole session.</para>
    /// </summary>
    internal static class CompanyAssemblyBridge
    {
        internal const string CONTRACTED_PLUGIN_GUID = "com.y4ngz.company";
        internal const string SHIP_SYSTEMS_PLUGIN_GUID = Y4NGZCore.Lifecycle.ModuleHarmonyIds.ShipSystems;
        private const string ContractedAssemblyName = "Y4NGZCompany";
        private const string ShipSystemsAssemblyName = "Y4NGZShipSystems";

        private static readonly bool _contractedLoaded = Chainloader.PluginInfos.ContainsKey(CONTRACTED_PLUGIN_GUID);
        private static readonly bool _shipSystemsLoaded = Chainloader.PluginInfos.ContainsKey(SHIP_SYSTEMS_PLUGIN_GUID);
        private static Assembly _contractedAssembly;
        private static Assembly _shipSystemsAssembly;

        /// <summary>True when either supported companion plugin chainloaded in this session.</summary>
        internal static bool IsLoaded => _contractedLoaded || _shipSystemsLoaded;

        /// <summary>
        /// Resolves a type by its namespace-qualified name (no assembly suffix) from the
        /// split Ship Systems assembly first, then the Contracted assembly for compatibility
        /// with pre-split builds. Callers memoize the result; this never probes the loader.
        /// </summary>
        internal static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            Type resolved = ResolveType(
                typeName,
                _shipSystemsLoaded,
                ShipSystemsAssemblyName,
                ref _shipSystemsAssembly);
            return resolved ?? ResolveType(
                typeName,
                _contractedLoaded,
                ContractedAssemblyName,
                ref _contractedAssembly);
        }

        /// <summary>
        /// Resolves a type owned only by Contracted. This keeps every optional Y4NGZ lookup
        /// on the already-loaded assembly path without searching Ship Systems for types that
        /// never moved there.
        /// </summary>
        internal static Type ResolveContractedType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            return ResolveType(
                typeName,
                _contractedLoaded,
                ContractedAssemblyName,
                ref _contractedAssembly);
        }

        private static Type ResolveType(
            string typeName,
            bool pluginLoaded,
            string assemblyName,
            ref Assembly assembly)
        {
            if (!pluginLoaded)
                return null;

            assembly = ResolveAssembly(assembly, assemblyName);
            try
            {
                return assembly?.GetType(typeName, throwOnError: false);
            }
            catch
            {
                return null;
            }
        }

        private static Assembly ResolveAssembly(Assembly cached, string assemblyName)
        {
            if (cached != null)
                return cached;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (string.Equals(assemblies[i].GetName().Name, assemblyName, StringComparison.Ordinal))
                    return assemblies[i];
            }

            return null;
        }
    }
}
