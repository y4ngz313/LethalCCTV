using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Central audio hub for the physical mainframe interaction. Owns a 2D one-shot AudioSource
    /// (menu/transition/hack cues + typing) and a 3D looping AudioSource carrying the fan hum,
    /// which runs from spawn rather than only during a session so the machine is audible as you
    /// approach it. Clips live on disk under ContractAudio/Mainframe and are loaded once,
    /// mirroring the PayloadCartAudio loader. All clips are peak-normalized on disk (~-1 dBFS);
    /// the per-cue volume scalars below shape the mix (fan hum very subtle, beeps moderate).
    /// </summary>
    internal sealed class MainframeAudio : MonoBehaviour
    {
        private const string AudioSubfolder = "Mainframe";

        /// <summary>Extensions probed for each cue, in preference order. Short input cues ship as
        /// .wav so no decoder priming delay sits between the keypress and the beep.</summary>
        private static readonly string[] AudioExtensions = { ".wav", ".ogg", ".mp3" };

        // Per-cue volume scalars applied over disk-normalized clips (all peak ~= -1 dBFS).
        private const float ScreenTransitionVolume = 0.55f;
        private const float PowerOffVolume = 0.55f;
        private const float NavigateVolume = 0.35f;
        private const float SelectVolume = 0.48f;
        private const float TypingVolume = 0.40f;
        private const float HackStartVolume = 0.55f;
        private const float HackProgressVolume = 0.45f;
        private const float HackSuccessVolume = 0.65f;
        private const float HackFailVolume = 0.60f;
        private const float FanLoopVolume = 0.09f;
        private const float IdleBeepVolume = 0.16f;

        // 3D falloff for the fan hum: full level at the console, gone a room away.
        private const float FanMinDistance = 1.5f;
        private const float FanMaxDistance = 14f;

        private const float IdleBeepMinInterval = 6.0f;
        private const float IdleBeepMaxInterval = 12.0f;

        private AudioSource _oneShot;
        private AudioSource _fanLoopSource;

        private AudioClip _screenTransition;
        private AudioClip _powerOff;
        private AudioClip _fanLoop;
        private AudioClip _idleBeep;
        private AudioClip _hackStart;
        private AudioClip _hackSuccess;
        private AudioClip _beepIncorrect;
        private readonly AudioClip[] _beeps = new AudioClip[3];
        private readonly AudioClip[] _typeClips = new AudioClip[3];
        private AudioClip _vanillaTypeClip;
        private int _typeRoundRobin;
        private int _lastBeepIndex = -1;

        private bool _idleActive;
        private float _nextIdleBeepAt;

        /// <summary>Most-recently-active hub, so the plain (non-MonoBehaviour) typing-arms class
        /// can play keystroke clicks without a back-reference. Set on Awake and re-asserted when a
        /// session starts its idle ambience.</summary>
        internal static MainframeAudio Current { get; private set; }

        /// <summary>Gets (or creates as a child of <paramref name="parent"/>) the mainframe audio hub.</summary>
        internal static MainframeAudio Ensure(Transform parent)
        {
            if (parent == null)
                return null;

            MainframeAudio existing = parent.GetComponentInChildren<MainframeAudio>(true);
            if (existing != null)
                return existing;

            var go = new GameObject("MainframeAudioHub");
            go.transform.SetParent(parent, worldPositionStays: false);
            return go.AddComponent<MainframeAudio>();
        }

        private void Awake()
        {
            Current = this;
            _oneShot = gameObject.AddComponent<AudioSource>();
            ConfigureSource(_oneShot, loop: false, spatialBlend: 0f);

            _fanLoopSource = gameObject.AddComponent<AudioSource>();
            ConfigureSource(_fanLoopSource, loop: true, spatialBlend: 1f);
            _fanLoopSource.volume = FanLoopVolume;
            _fanLoopSource.minDistance = FanMinDistance;
            _fanLoopSource.maxDistance = FanMaxDistance;

            TryResolveVanillaTypingClip();
            StartCoroutine(LoadAllClips());
        }

        private void OnEnable()
        {
            StartFanLoop();
        }

        private void OnDisable()
        {
            StopIdle();
            if (_fanLoopSource != null && _fanLoopSource.isPlaying)
                _fanLoopSource.Stop();
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(Current, this))
                Current = null;
        }

        private void Update()
        {
            if (!_idleActive || _idleBeep == null)
                return;

            if (Time.unscaledTime >= _nextIdleBeepAt)
            {
                PlayOneShot(_idleBeep, IdleBeepVolume);
                ScheduleNextIdleBeep();
            }
        }

        // ---- Cue API -------------------------------------------------------

        /// <summary>Screen load/transition sweep: boot, the jump into the hacked menu after a
        /// successful hack, and each menu view change once hacked.</summary>
        internal void PlayScreenTransition() => PlayOneShot(_screenTransition, ScreenTransitionVolume);
        internal void PlayPowerOff() => PlayOneShot(_powerOff, PowerOffVolume);
        internal void PlayNavigate() => PlayOneShot(NextBeep(), NavigateVolume);
        internal void PlaySelect() => PlayOneShot(NextBeep(), SelectVolume);
        internal void PlayHackStart() => PlayOneShot(_hackStart, HackStartVolume);
        internal void PlayHackSuccess() => PlayOneShot(_hackSuccess, HackSuccessVolume);
        internal void PlayHackFail() => PlayOneShot(_beepIncorrect, HackFailVolume);

        /// <summary>Target-linked acknowledgement. Pinned to the third beep rather than the
        /// round-robin so progress reads as one consistent ack. Safe to share with the input
        /// pool: navigate/select only fire in the hacked control menu, this only in the
        /// minigame, so the two are never heard on the same screen.</summary>
        internal void PlayHackProgress() => PlayOneShot(_beeps[2] != null ? _beeps[2] : NextBeep(), HackProgressVolume);

        /// <summary>Rejected access: the trace lockout itself, and interaction attempts made
        /// while the lockout is still counting down.</summary>
        internal void PlayLockedOut() => PlayOneShot(_beepIncorrect, HackFailVolume);

        internal void PlayTyping()
        {
            // Prefer a resolved vanilla terminal keystroke clip; otherwise round-robin ours.
            if (_vanillaTypeClip != null)
            {
                PlayOneShot(_vanillaTypeClip, TypingVolume);
                return;
            }

            AudioClip clip = null;
            for (int i = 0; i < _typeClips.Length; i++)
            {
                int index = (_typeRoundRobin + i) % _typeClips.Length;
                if (_typeClips[index] != null)
                {
                    clip = _typeClips[index];
                    _typeRoundRobin = (index + 1) % _typeClips.Length;
                    break;
                }
            }

            PlayOneShot(clip, TypingVolume);
        }

        /// <summary>Arms the periodic status blips for an active session. The fan hum is not
        /// session-scoped and keeps running either way.</summary>
        internal void StartIdle()
        {
            Current = this;
            _idleActive = true;
            ScheduleNextIdleBeep();
            StartFanLoop();
        }

        internal void StopIdle()
        {
            _idleActive = false;
        }

        // ---- Internals -----------------------------------------------------

        /// <summary>Picks a beep for input feedback, never repeating the previous one so repeated
        /// keypresses stay varied.</summary>
        private AudioClip NextBeep()
        {
            int available = 0;
            for (int i = 0; i < _beeps.Length; i++)
            {
                if (_beeps[i] != null)
                    available++;
            }

            if (available == 0)
                return null;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                int index = UnityEngine.Random.Range(0, _beeps.Length);
                if (_beeps[index] == null)
                    continue;
                if (index == _lastBeepIndex && available > 1)
                    continue;

                _lastBeepIndex = index;
                return _beeps[index];
            }

            for (int i = 0; i < _beeps.Length; i++)
            {
                if (_beeps[i] != null)
                {
                    _lastBeepIndex = i;
                    return _beeps[i];
                }
            }

            return null;
        }

        private void StartFanLoop()
        {
            if (_fanLoopSource == null || _fanLoop == null || _fanLoopSource.isPlaying)
                return;

            _fanLoopSource.clip = _fanLoop;
            _fanLoopSource.volume = FanLoopVolume;
            _fanLoopSource.Play();
        }

        private void PlayOneShot(AudioClip clip, float volumeScale)
        {
            if (clip == null || _oneShot == null)
                return;

            try
            {
                _oneShot.PlayOneShot(clip, Mathf.Clamp01(volumeScale));
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV][MainframeAudio] PlayOneShot failed for '{clip.name}': {ex.Message}");
            }
        }

        private void ScheduleNextIdleBeep()
        {
            _nextIdleBeepAt = Time.unscaledTime + UnityEngine.Random.Range(IdleBeepMinInterval, IdleBeepMaxInterval);
        }

        private static void ConfigureSource(AudioSource source, bool loop, float spatialBlend)
        {
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = spatialBlend;
            source.volume = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
        }

        private void TryResolveVanillaTypingClip()
        {
            try
            {
                Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                if (terminal == null)
                    return;

                FieldInfo[] fields = terminal.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (FieldInfo field in fields)
                {
                    string name = field.Name.ToLowerInvariant();
                    bool nameHints = name.Contains("key") || name.Contains("type") || name.Contains("click");

                    if (field.FieldType == typeof(AudioClip) && nameHints)
                    {
                        if (field.GetValue(terminal) is AudioClip clip)
                        {
                            _vanillaTypeClip = clip;
                            break;
                        }
                    }
                    else if (field.FieldType == typeof(AudioClip[]) && nameHints)
                    {
                        if (field.GetValue(terminal) is AudioClip[] clips && clips.Length > 0 && clips[0] != null)
                        {
                            _vanillaTypeClip = clips[0];
                            break;
                        }
                    }
                }

                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV][MainframeAudio] Vanilla typing clip " +
                    (_vanillaTypeClip != null ? $"resolved '{_vanillaTypeClip.name}'." : "not found; using generated keystroke clicks."));
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV][MainframeAudio] Vanilla typing clip scan failed: {ex.Message}");
            }
        }

        private IEnumerator LoadAllClips()
        {
            // Fan hum first: it is always-on, so it should come up before the cue set.
            yield return LoadClip("Idle_FanLoop", c => _fanLoop = c);
            StartFanLoop();

            yield return LoadClip("Screen_Transition", c => _screenTransition = c);
            yield return LoadClip("Menu_PowerOff", c => _powerOff = c);
            yield return LoadClip("Beep_01", c => _beeps[0] = c);
            yield return LoadClip("Beep_02", c => _beeps[1] = c);
            yield return LoadClip("Beep_03", c => _beeps[2] = c);
            yield return LoadClip("Beep_Incorrect", c => _beepIncorrect = c);
            yield return LoadClip("Idle_Beep", c => _idleBeep = c);
            yield return LoadClip("Hack_Start", c => _hackStart = c);
            yield return LoadClip("Hack_Success", c => _hackSuccess = c);
            yield return LoadClip("Type_01", c => _typeClips[0] = c);
            yield return LoadClip("Type_02", c => _typeClips[1] = c);
            yield return LoadClip("Type_03", c => _typeClips[2] = c);
        }

        private IEnumerator LoadClip(string baseName, Action<AudioClip> assign)
        {
            string path = ResolveAudioPath(baseName);
            if (string.IsNullOrEmpty(path))
                yield break;

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioTypeFor(path)))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeAudio] Failed loading '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip != null)
                {
                    clip.name = "Mainframe_" + baseName;
                    assign?.Invoke(clip);
                }
                else
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeAudio] Loaded '{path}' returned a null AudioClip.");
                }
            }
        }

        private static AudioType AudioTypeFor(string path)
        {
            string extension = Path.GetExtension(path);
            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
                return AudioType.WAV;
            if (string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase))
                return AudioType.OGGVORBIS;
            return AudioType.MPEG;
        }

        private static string ResolveAudioPath(string baseName)
        {
            string cctvAssemblyDir = Path.GetDirectoryName(typeof(MainframeAudio).Assembly.Location);
            string companyAssemblyDir = ResolveLoadedAssemblyDirectory("Y4NGZCompany")
                                        ?? Path.Combine(Paths.PluginPath, "Contracted");
            string[] roots =
            {
                Path.Combine(companyAssemblyDir, "ContractAudio", AudioSubfolder),
                Path.Combine(Paths.PluginPath, "Contracted", "ContractAudio", AudioSubfolder),
                Path.Combine(cctvAssemblyDir ?? string.Empty, "ContractAudio", AudioSubfolder),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", AudioSubfolder),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio", AudioSubfolder),
                Path.Combine(Paths.PluginPath, "ContractAudio", AudioSubfolder),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", AudioSubfolder)
            };

            for (int i = 0; i < roots.Length; i++)
            {
                for (int e = 0; e < AudioExtensions.Length; e++)
                {
                    string candidate = Path.Combine(roots[i], baseName + AudioExtensions[e]);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV][MainframeAudio] Audio missing: ContractAudio/{AudioSubfolder}/{baseName}.*");
            return null;
        }

        private static string ResolveLoadedAssemblyDirectory(string simpleName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                try
                {
                    if (!string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string location = assembly.Location;
                    if (!string.IsNullOrEmpty(location))
                        return Path.GetDirectoryName(location);
                }
                catch (Exception)
                {
                    // Dynamic assemblies may not expose a file location; keep probing.
                }
            }

            return null;
        }
    }
}
