namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>Every LethalCCTV manual-render producer, in same-frame eligibility tie-break order.</summary>
    internal enum CctvRenderClient
    {
        FeedSnapshot = 0,
        Turret = 1,
        LeftCompositor = 2,
        RightCompositor = 3,
    }
}
