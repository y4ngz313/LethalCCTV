using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// The 24-hour ship clock rendered as CCTV screen furniture — on the live
    /// vanilla lower-left monitor (CCTVVanillaMonitorDisplay) and in the legacy
    /// fullscreen focus overlay header (MonitorFocus.Overlay). Both read from here
    /// so the two can never disagree.
    ///
    /// The arithmetic is vanilla <c>HUDManager.SetClock</c>'s, verbatim:
    /// <code>
    ///     int num  = (int)(timeNormalized * (60f * numberOfHours)) + 360;
    ///     int num2 = (int)Mathf.Floor(num / 60);   // hour, 24h
    ///     int num3 = num % 60;                     // minute
    /// </code>
    /// The <c>+ 360</c> is a 06:00 origin, and <c>TimeOfDay.Update</c> feeds it
    /// <c>normalizedTimeOfDay</c> and <c>numberOfHours</c> — the same two fields read
    /// below. An older in-repo comment (src/Y4NGZAtmosphere/CompanyAudioManager.cs) calls
    /// the vanilla day "08:00 to midnight"; that is wrong about the origin. Decompiling
    /// Assembly-CSharp settles it: 06:00 plus <c>numberOfHours</c> game hours.
    ///
    /// Vanilla renders that as 12-hour + AM/PM; the CCTV feeds render 24-hour HH:MM,
    /// which is what a security DVR timestamp looks like. <see cref="LogComparisonOnce"/>
    /// prints one line the first time a real clock is produced so the two can be
    /// eyeballed against each other in a single test run.
    /// </summary>
    internal static class CctvScreenClock
    {
        /// <summary>Shown before landing, after leaving, and whenever TimeOfDay is unavailable.</summary>
        internal const string UnavailableText = ShipClock24h.UnavailableText;

        private static bool _loggedComparison;

        /// <summary>
        /// Current ship time as 24-hour <c>HH:MM</c>, or <see cref="UnavailableText"/>
        /// when there is no clock to show. The formula moved to Y4NGZCore in #393 so the
        /// ship monitor row (Y4NGZCompany) and these feeds cannot drift apart.
        /// </summary>
        internal static string GetShipTime24h() => ShipClock24h.GetShipTime24h();

        /// <summary>
        /// One-shot diagnostic: prints our string next to vanilla's HUD clock text and
        /// the two inputs both are derived from. Fires at most once per game session,
        /// and only once both clocks actually have a value, so it cannot spam.
        /// </summary>
        internal static void LogComparisonOnce(string ours)
        {
            if (_loggedComparison)
                return;
            if (string.IsNullOrEmpty(ours) || ours == UnavailableText)
                return;

            TimeOfDay time = TimeOfDay.Instance;
            if (time == null)
                return;

            HUDManager hud = HUDManager.Instance;
            string vanilla = hud != null && hud.clockNumber != null ? hud.clockNumber.text : null;
            if (string.IsNullOrEmpty(vanilla))
                return;

            _loggedComparison = true;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][Clock] cctv='{ours}' vanillaHud='{vanilla.Replace("\n", " ")}' " +
                $"(normalizedTimeOfDay={time.normalizedTimeOfDay:F4}, numberOfHours={time.numberOfHours}). " +
                "Both use HUDManager.SetClock's (int)(t*60*hours)+360 minutes-since-midnight; " +
                "ours renders 24h HH:MM, vanilla renders 12h + AM/PM.");
        }

        /// <summary>Reset for a fresh session. Called from the CCTV display teardown.</summary>
        internal static void ResetDiagnostics()
        {
            _loggedComparison = false;
        }
    }
}
