namespace Y4NGZCompany.Bootstrap
{
    // Version reconciled with LethalCCTVPlugin's BepInPlugin attribute in #393; the two
    // must agree because this constant is what the log lines and Harmony instance id use.
    internal static class SurveillancePluginInfo
    {
        public const string PLUGIN_GUID = "com.y4ngz.company.lethalcctv";
        public const string PLUGIN_NAME = "Y4NGZ CCTV";
        public const string PLUGIN_VERSION = "1.1.0";
    }
}
