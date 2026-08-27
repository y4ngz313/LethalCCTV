using BepInEx.Bootstrap;

namespace Y4NGZCompany.Core.Compat
{
    // Soft-dependency probe for fumiko/CullFactory.
    //
    // CullFactory replaces the vanilla AdjacentRoomCullingModified behaviour
    // with per-rendering-camera culling (beginContextRendering +
    // Camera.onPreCull), which already keeps dungeon tiles visible for our
    // CCTV cameras. When it is present, CctvTileCullingBypass must stay inert
    // so the two systems never fight over renderer.enabled.
    //
    // NOTE: CullFactory's PluginInfo.PLUGIN_GUID string constant is the bare
    // "CullFactory", but its [BepInPlugin] attribute — and therefore its
    // Chainloader.PluginInfos key — is "com.fumiko.CullFactory".
    public static class CullFactoryCompat
    {
        public const string PLUGIN_GUID = "com.fumiko.CullFactory";

        private static readonly bool _isLoaded = Chainloader.PluginInfos.ContainsKey(PLUGIN_GUID);

        public static bool IsLoaded => _isLoaded;
    }
}
