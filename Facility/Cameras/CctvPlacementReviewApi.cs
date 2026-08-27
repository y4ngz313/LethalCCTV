using DunGen;

namespace Y4NGZCompany.Facility.Cameras
{
    public readonly struct CctvTilePlacementHint
    {
        public readonly bool HasAnyReview;
        public readonly bool ImportantRoom;
        public readonly bool MainframeCandidate;
        public readonly bool CompanyStashCandidate;
        public readonly float ObjectiveScore;
        public readonly float CameraScoreBonus;
        public readonly int PositiveCount;
        public readonly int HardRejectCount;
        public readonly int SkyboxRejectCount;
        public readonly int FurnitureRejectCount;
        public readonly int MainframeRejectCount;
        public readonly int CompanyStashRejectCount;

        internal CctvTilePlacementHint(CameraPlacementTileHint hint)
        {
            HasAnyReview = hint.HasAnyReview;
            ImportantRoom = hint.ImportantRoom;
            MainframeCandidate = hint.MainframeCandidate;
            CompanyStashCandidate = hint.CompanyStashCandidate;
            ObjectiveScore = hint.ObjectiveScore;
            CameraScoreBonus = hint.CameraScoreBonus;
            PositiveCount = hint.PositiveCount;
            HardRejectCount = hint.HardRejectCount;
            SkyboxRejectCount = hint.SkyboxRejectCount;
            FurnitureRejectCount = hint.FurnitureRejectCount;
            MainframeRejectCount = hint.MainframeRejectCount;
            CompanyStashRejectCount = hint.CompanyStashRejectCount;
        }
    }

    public static class CctvPlacementReviewApi
    {
        public static bool TryGetTilePlacementHint(object tileObject, out CctvTilePlacementHint hint)
        {
            hint = default;
            Tile tile = tileObject as Tile;
            if (tile == null) return false;
            hint = new CctvTilePlacementHint(CameraPlacementReviewStore.GetTileHint(tile));
            return hint.HasAnyReview;
        }
    }
}
