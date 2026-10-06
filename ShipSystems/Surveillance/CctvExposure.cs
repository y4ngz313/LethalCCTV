using System;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    // Linear-light metering. Separate mode thresholds prevent a lamp crossing
    // the edge of the image from repeatedly switching night vision on and off.
    internal sealed class CctvExposure
    {
        internal bool LowLight { get; private set; }
        internal float Meter { get; private set; } = 0.18f;
        internal float Gain { get; private set; } = 1f;
        internal float NightBlend { get; private set; }

        internal void Measure(float[] luminance)
        {
            if (luminance == null || luminance.Length == 0) return;
            Array.Sort(luminance);
            // Ignore the darkest 5% and brightest 10%; meter the room rather
            // than a small emissive bulb. Work in log light so dark detail counts.
            int first = luminance.Length / 20;
            int end = Math.Max(first + 1, luminance.Length * 9 / 10);
            double sum = 0;
            for (int i = first; i < end; i++)
                sum += Math.Log(Math.Max(0.0001, luminance[i]));
            Meter = (float)Math.Exp(sum / (end - first));
            if (Meter < 0.045f) LowLight = true;
            else if (Meter > 0.085f) LowLight = false;
        }

        internal void Tick(float seconds, float ceiling, bool enabled, bool automaticMode, bool autoGain)
        {
            float dt = Math.Max(0, Math.Min(seconds, 0.25f));
            ceiling = Math.Max(0.5f, Math.Min(8f, ceiling));
            float targetBlend = enabled && (!automaticMode || LowLight) ? 1f : 0f;
            NightBlend = Ease(NightBlend, targetBlend, dt, targetBlend > NightBlend ? 0.35f : 0.65f);
            float targetGain = !enabled ? 1f : autoGain
                ? Math.Max(0.12f, Math.Min(ceiling, 0.18f / Meter)) : ceiling;
            Gain = Ease(Gain, targetGain, dt, 0.5f);
        }

        private static float Ease(float from, float to, float dt, float timeConstant)
            => from + (to - from) * (1f - (float)Math.Exp(-dt / timeConstant));
    }
}
