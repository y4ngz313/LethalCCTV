using UnityEngine;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// The one yellow -> red detection ramp. Both the physical warning beam
    /// (<see cref="CctvDetectionIndicator"/>) and the screen-edge spotting arc
    /// (<see cref="CctvSpottingAlertHud"/>) read it, so "the arc is the same colour
    /// as the camera spotting you" holds by construction rather than by two copies
    /// of the same two constants staying in sync by hand.
    /// </summary>
    internal static class CctvDetectionPalette
    {
        internal static readonly Color WarnStartColor = new Color(1f, 0.86f, 0.22f);
        internal static readonly Color WarnFullColor = new Color(1f, 0.08f, 0.04f);

        /// <summary>
        /// The "this camera is live and watching" amber, for a security-active camera
        /// that has not spotted anyone yet. Shared by the lens dot, its tip light and
        /// the idle visibility cone so a hot camera reads as one colour whether you
        /// are looking at the prop, its glow, or the shaft it casts in the fog.
        /// Deliberately deeper than <see cref="WarnStartColor"/>, so escalating into
        /// detection shifts hue toward yellow as well as brightening.
        /// </summary>
        internal static readonly Color SecurityActiveColor = new Color(1f, 0.5f, 0.06f);

        /// <summary>Detection colour at <paramref name="progress01"/> through the detection window.</summary>
        internal static Color ForProgress(float progress01)
        {
            return Color.Lerp(WarnStartColor, WarnFullColor, Mathf.Clamp01(progress01));
        }
    }
}
