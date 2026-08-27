using BepInEx.Bootstrap;

namespace Y4NGZCompany.Core.Compat
{
    public static class LethalLevelLoaderCompat
    {
        public const string PLUGIN_GUID = "imabatby.lethallevelloader";

        private static readonly bool _isLoaded = Chainloader.PluginInfos.ContainsKey(PLUGIN_GUID);

        public static bool IsLoaded => _isLoaded;

        public static void Initialize()
        {
            if (!_isLoaded) return;
            InitializeImpl();
        }

        // Isolated soft-dep init (SKILL.md "Soft dependency pattern").
        // V1 only checks presence — LLL presence does not change behaviour. No reflection.
        private static void InitializeImpl()
        {
        }
    }
}
