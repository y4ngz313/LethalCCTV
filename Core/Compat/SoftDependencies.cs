using Y4NGZCompany.Core.Compat;

namespace Y4NGZCompany.Core.Compat
{
    public sealed class SoftDependencies
    {
        public bool OpenBodyCams { get; }
        public bool LethalLevelLoader { get; }
        public bool FacilityMeltdown { get; }
        public bool Y4NGZUpgrades { get; }
        public bool GeneralImprovements { get; }
        public bool LethalPhones { get; }

        public SoftDependencies()
        {
            LethalPhones = LethalPhonesCompat.IsLoaded;
            OpenBodyCams = OpenBodyCamsCompat.IsLoaded;
            GeneralImprovements = GeneralImprovementsMonitorCompat.IsLoaded;
            LethalLevelLoader = LethalLevelLoaderCompat.IsLoaded;
            FacilityMeltdown = FacilityMeltdownCompat.IsLoaded;
            Y4NGZUpgrades = Y4NGZUpgradesCompat.IsLoaded;
        }
    }
}
