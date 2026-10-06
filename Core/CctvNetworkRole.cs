using Unity.Netcode;

namespace Y4NGZCompany.Core
{
    /// <summary>
    /// One answer to "is this instance the server?" for every LethalCCTV system.
    ///
    /// #716 G5: the role check used to be copy-pasted per system, and the copy in
    /// <c>CctvSecurityDirector</c> returned <c>true</c> when <see cref="StartOfRound"/> was
    /// also missing. That made a client with no netcode singletons yet run server-only
    /// security work (alarm expiry, hostile-camera escalation) for the frames before the
    /// singletons resolve. Absent every authority source the honest answer is "not the
    /// server", so the fallback is <c>false</c>.
    /// </summary>
    internal static class CctvNetworkRole
    {
        /// <summary>
        /// True only when this instance can be shown to be the server. Sources are checked
        /// in order of authority: the netcode singleton, then the two vanilla managers that
        /// carry the same flag, and finally <c>false</c> when none of them exist.
        /// </summary>
        internal static bool IsServer()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null)
                return manager.IsServer;

            RoundManager round = RoundManager.Instance;
            if (round != null)
                return round.IsServer;

            StartOfRound start = StartOfRound.Instance;
            if (start != null)
                return start.IsServer;

            return false;
        }
    }
}
