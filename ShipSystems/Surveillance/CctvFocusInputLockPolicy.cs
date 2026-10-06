namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Pure decision logic for the MonitorFocus player input-lock stash/restore and
    /// its lifecycle-boundary self-heal. Unity-free on purpose: every
    /// <c>PlayerControllerB</c> read stays in <c>MonitorFocus.Lifecycle</c> and only
    /// the resulting booleans come here, so the regression suite can drive the
    /// decision directly.
    /// </summary>
    internal static class CctvFocusInputLockPolicy
    {
        /// <summary>
        /// Whether a captured pre-focus flag may be replayed at exit.
        ///
        /// A captured <c>false</c> is always replayed (that is how our own lock is
        /// released). A captured <c>true</c> is only replayed while the owner that
        /// asserted it is still live. The 2026-09-16 playtest leaked
        /// <c>disableMoveInput</c> for 141 s exactly here: four colliding
        /// <c>IsInSpecialAnimationClientRpc</c> writes left a transient <c>true</c>
        /// in the stash, the owner released during the 234 s station session, and
        /// the unconditional restore re-asserted a lock nobody owned any more.
        /// </summary>
        internal static bool ShouldRestorePriorLock(bool priorValue, bool assertingContextLive)
            => priorValue && assertingContextLive;

        /// <summary>
        /// Whether the local player's input flags look like a leaked focus lock that
        /// MonitorFocus should clear.
        ///
        /// <paramref name="blockingUiContext"/> (terminal, quick menu, chat entry)
        /// always wins: those own the flags legitimately.
        ///
        /// All three flags asserted is the original #452 full-lock case and stays
        /// unconditional, including while a special animation is still set — that
        /// branch is what clears a residual CCTV seated animation.
        ///
        /// A partial lock (one or two flags) is the #1082 leak. It only counts as
        /// stale when no other owner can explain it — no special-animation context
        /// (ladder, vehicle, enemy grab, shock minigame, seated mainframe session),
        /// no in-plugin look+move capture (turret/camera placement editor), no death.
        /// That keeps a ladder climb or a teleporter beam untouched.
        /// </summary>
        internal static bool LooksLikeStaleLock(
            bool disableLook,
            bool disableMove,
            bool disableInteract,
            bool blockingUiContext,
            bool otherOwnerContext)
        {
            if (blockingUiContext) return false;
            if (disableLook && disableMove && disableInteract) return true;
            if (otherOwnerContext) return false;
            return disableLook || disableMove || disableInteract;
        }
    }
}
