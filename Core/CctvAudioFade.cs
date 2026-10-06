using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Core
{
    /// <summary>
    /// #716 E13: one short volume ramp for LethalCCTV's looping audio sources.
    ///
    /// Every CCTV loop — the mainframe fan, the monitor's focus hum, the camera lens hum —
    /// used to end on a bare <c>AudioSource.Stop()</c>, which cuts a sustained tone mid-cycle
    /// and reads as a pop rather than as the machine winding down. A fifth of a second of
    /// ramp is enough to remove the click without the loop sounding like it lingers.
    ///
    /// One-shots are deliberately not routed through here: a cue that is already ending, or
    /// that is being restarted on the next frame, wants the hard stop it has.
    ///
    /// The ramp runs on a private always-loaded runner rather than on the caller, so it works
    /// from a static class and from <c>OnDisable</c>, where the caller cannot start a
    /// coroutine of its own. When there is no runner to be had — quitting, or a source whose
    /// object is already going away — the call degrades to the plain stop it replaced.
    /// </summary>
    internal static class CctvAudioFade
    {
        internal const float DefaultFadeSeconds = 0.2f;

        private static readonly Dictionary<AudioSource, Coroutine> ActiveFades =
            new Dictionary<AudioSource, Coroutine>();

        private static FadeRunner _runner;
        private static bool _runnerUnavailable;

        /// <summary>
        /// Ramps <paramref name="source"/> to silence over <paramref name="fadeSeconds"/>,
        /// stops it, and restores the volume it was playing at so the next <c>Play()</c>
        /// starts at full level. Safe to call on a null, silent, or already-fading source.
        /// </summary>
        internal static void StopLoop(AudioSource source, float fadeSeconds = DefaultFadeSeconds)
        {
            if (source == null)
                return;
            if (!source.isPlaying)
            {
                CancelFade(source);
                return;
            }

            // #716 E13: a ramp already in flight owns this source; leave it alone. Callers sit
            // in per-frame gates (a camera whose detection went dark asks to stop every frame
            // while the source is still audibly winding down), and restarting the ramp on each
            // of those calls would reset its clock forever - the source would never reach the
            // stop at the end and would hum on at full volume for the rest of the round.
            if (ActiveFades.ContainsKey(source))
                return;

            FadeRunner runner = EnsureRunner();
            if (runner == null || !source.gameObject.activeInHierarchy || fadeSeconds <= 0f)
            {
                // No ramp is possible here, and none would be audible. A source outside the
                // active hierarchy has already been silenced by Unity, so the stop below is
                // bookkeeping rather than the thing the player hears - callers that want an
                // audible wind-down have to ask for it while the source is still alive, not on
                // the way out. The abrupt stop is still better than leaving the loop playing.
                CancelFade(source);
                source.Stop();
                return;
            }

            CancelFade(source);
            ActiveFades[source] = runner.StartCoroutine(FadeOut(source, fadeSeconds));
        }

        /// <summary>
        /// Drops any ramp in flight for <paramref name="source"/> without touching its
        /// playback. Call before restarting a loop that may still be fading out.
        /// </summary>
        internal static void CancelFade(AudioSource source)
        {
            if (source == null || ActiveFades.Count == 0)
                return;
            if (!ActiveFades.TryGetValue(source, out Coroutine running))
                return;

            ActiveFades.Remove(source);
            if (running != null && _runner != null)
                _runner.StopCoroutine(running);
        }

        private static IEnumerator FadeOut(AudioSource source, float fadeSeconds)
        {
            float startVolume = source.volume;
            float startedAt = Time.unscaledTime;

            while (source != null && source.isPlaying)
            {
                float t = (Time.unscaledTime - startedAt) / fadeSeconds;
                if (t >= 1f)
                    break;
                source.volume = startVolume * (1f - t);
                yield return null;
            }

            // Removed unconditionally: a source destroyed mid-ramp would otherwise leave its
            // entry behind forever.
            ActiveFades.Remove(source);
            if (source != null)
            {
                source.Stop();
                // The loop is restarted by assigning clip and volume again, but restoring the
                // level here keeps a source that is replayed without one from coming back mute.
                source.volume = startVolume;
            }
        }

        private static FadeRunner EnsureRunner()
        {
            if (_runner != null)
                return _runner;
            if (_runnerUnavailable)
                return null;

            GameObject host = new GameObject("LethalCCTV_AudioFade")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            Object.DontDestroyOnLoad(host);
            _runner = host.AddComponent<FadeRunner>();
            return _runner;
        }

        private sealed class FadeRunner : MonoBehaviour
        {
            private void OnApplicationQuit()
            {
                // Starting coroutines on the way out throws; take the plain stop instead.
                _runnerUnavailable = true;
            }

            private void OnDestroy()
            {
                if (ReferenceEquals(_runner, this))
                    _runner = null;
                ActiveFades.Clear();
            }
        }
    }
}
