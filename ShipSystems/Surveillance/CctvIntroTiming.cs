using System;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CctvIntroTiming
    {
        internal const float Duration = 1.40f;
        internal const float PressContact = 0.38f;
        internal const float PressRelease = 0.47f;
        internal const float GripArrival = 1.14f;
        internal static float Ease(float progress)
        {
            float t = Math.Max(0f, Math.Min(1f, progress));
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }
        internal static float ClipSeconds(float presentationSeconds, float clipLength)
            => Math.Max(0f, presentationSeconds) * clipLength / Duration;
        internal static float Sample(float seconds, float fps, int count)
            => Math.Max(0f, Math.Min(Math.Max(0, count - 1), seconds * fps - 1f));
    }
}
