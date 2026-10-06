using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DunGen;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core;
using Y4NGZCompany.Core.Compat;
using LethalCompanyInputUtils.Api;
using LethalCompanyInputUtils.BindingPathEnums;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class MonitorFocus
    {
        private static void EnsureCameraAudioProxy()
        {
            if (_cameraAudioProxyListener != null) return;

            _cameraAudioProxyRoot = new GameObject("LethalCCTV_CameraAudioProxy");
            if (SurveillanceBootstrap.Instance != null)
            {
                _cameraAudioProxyRoot.transform.SetParent(SurveillanceBootstrap.Instance.transform, worldPositionStays: false);
            }
            else
            {
                UnityEngine.Object.DontDestroyOnLoad(_cameraAudioProxyRoot);
            }
            _cameraAudioProxyListener = _cameraAudioProxyRoot.AddComponent<AudioListener>();
            _cameraAudioProxyListener.enabled = false;
        }

        private static void SyncCameraAudioProxy()
        {
            if (!IsFocused)
            {
                RestoreAudioListenerToPlayer();
                return;
            }

            CCTVCamera camera = GetActiveCamera();
            GameObject target;
            bool flip;
            if (camera != null)
            {
                target = camera.Cam != null ? camera.Cam.gameObject : camera.gameObject;
                flip = IngamePlayerSettings.Instance != null && IngamePlayerSettings.Instance.flipCamera;
            }
            else if (OpenBodyCamsCompat.TryGetFocusAudioTarget(out Transform bodycamTarget))
            {
                target = bodycamTarget != null ? bodycamTarget.gameObject : null;
                // OpenBodyCams' camera transform already owns the selected perspective.
                flip = false;
            }
            else
            {
                RestoreAudioListenerToPlayer();
                return;
            }

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null || player.activeAudioListener == null)
            {
                return;
            }

            EnsureCameraAudioProxy();
            if (target == null)
            {
                RestoreAudioListenerToPlayer();
                return;
            }

            if (_audioListenerPlayer != player)
            {
                RestoreAudioListenerToPlayer();
                _audioListenerPlayer = player;
            }

            bool bindingChanged =
                _audioProxyCamera != camera ||
                _audioProxyTarget != target ||
                _audioProxyFlip != flip ||
                !_cameraAudioProxyListener.enabled ||
                _cameraAudioProxyRoot.transform.parent != target.transform;

            if (player.activeAudioListener.enabled)
                player.activeAudioListener.enabled = false;

            if (bindingChanged)
            {
                _cameraAudioProxyRoot.transform.SetParent(target.transform, worldPositionStays: false);
                _cameraAudioProxyRoot.transform.localPosition = Vector3.zero;
                _cameraAudioProxyRoot.transform.localRotation = Quaternion.identity;
                if (flip)
                    _cameraAudioProxyRoot.transform.Rotate(0f, 180f, 0f, Space.Self);
                _cameraAudioProxyListener.enabled = true;
                _audioProxyCamera = camera;
                _audioProxyTarget = target;
                _audioProxyFlip = flip;
            }

            if (StartOfRound.Instance != null &&
                (bindingChanged || Time.unscaledTime >= _nextAudioProxyVoiceRefreshAt))
            {
                StartOfRound.Instance.audioListener = _cameraAudioProxyListener;
                StartOfRound.Instance.UpdatePlayerVoiceEffects();
                _nextAudioProxyVoiceRefreshAt = Time.unscaledTime + AUDIO_PROXY_VOICE_REFRESH_INTERVAL;
            }
        }

        private static void RestoreAudioListenerToPlayer()
        {
            PlayerControllerB player = _audioListenerPlayer != null
                ? _audioListenerPlayer
                : (GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null);
            _audioListenerPlayer = null;
            _audioProxyCamera = null;
            _audioProxyTarget = null;
            _audioProxyFlip = false;
            _nextAudioProxyVoiceRefreshAt = 0f;

            if (_cameraAudioProxyListener != null)
            {
                _cameraAudioProxyListener.enabled = false;
            }
            if (_cameraAudioProxyRoot != null && SurveillanceBootstrap.Instance != null)
            {
                _cameraAudioProxyRoot.transform.SetParent(SurveillanceBootstrap.Instance.transform, worldPositionStays: false);
                _cameraAudioProxyRoot.transform.localPosition = Vector3.zero;
                _cameraAudioProxyRoot.transform.localRotation = Quaternion.identity;
            }

            if (player != null && player.gameplayCamera != null && player.activeAudioListener != null)
            {
                player.ChangeAudioListenerToObject(player.gameplayCamera.gameObject);
                player.activeAudioListener.enabled = true;
            }
            if (StartOfRound.Instance != null)
            {
                StartOfRound.Instance.UpdatePlayerVoiceEffects();
            }
        }

        private static void EnsureFocusSfx()
        {
            _switchSfx ??= CreateSwitchClickClip();
            _scanSfx ??= CreateScanSweepClip();
            _pingSfx ??= CreateToneClip("LethalCCTV_PingBlip", 0.10f, 760f, 520f, 0.24f);
            _rotateSfx ??= CreateRotateServoClip();
        }

        private static void EnsureProvidedFocusAudioLoading()
        {
            if (_providedFocusAudioLoadStarted) return;
            if (SurveillanceBootstrap.Instance == null) return;

            _providedFocusAudioLoadStarted = true;
            SurveillanceBootstrap.Instance.StartCoroutine(LoadProvidedFocusAudio());
        }

        private static IEnumerator LoadProvidedFocusAudio()
        {
            var switchClips = new List<AudioClip>(2);
            yield return LoadProvidedFocusAudioClip(FOCUS_CLICK_2_FILE, "LethalCCTV_UI_Click_2", clip =>
            {
                if (clip != null) switchClips.Add(clip);
            });
            yield return LoadProvidedFocusAudioClip(FOCUS_CLICK_3_FILE, "LethalCCTV_UI_Click_3", clip =>
            {
                if (clip != null) switchClips.Add(clip);
            });
            if (switchClips.Count > 0)
            {
                _switchSfxPool = switchClips.ToArray();
            }

            yield return LoadProvidedFocusAudioClip(FOCUS_PAGE_FILE, "LethalCCTV_UI_PageSubmit", clip =>
            {
                if (clip != null) _pageSfx = clip;
            });
            yield return LoadProvidedFocusAudioClip(FOCUS_PING_FILE, "LethalCCTV_UI_Ping", clip =>
            {
                if (clip != null) _pingSfx = clip;
            });
            yield return LoadProvidedFocusAudioClip(FOCUS_INTERACT_FILE, "LethalCCTV_UI_Interact", clip =>
            {
                if (clip != null) _interactSfx = clip;
            });
            yield return LoadProvidedFocusAudioClip(FOCUS_BOOT_FILE, "LethalCCTV_UI_Boot", clip =>
            {
                if (clip != null) _bootSfx = clip;
            });
            yield return LoadProvidedFocusAudioClip(FOCUS_MACHINE_LOOP_FILE, "LethalCCTV_OldMachineLoop", clip =>
            {
                if (clip == null) return;
                _machineLoopSfx = clip;
                if (IsFocused) StartFocusMachineLoop();
            });

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Focus audio load complete: switchPool={(_switchSfxPool != null ? _switchSfxPool.Length : 0)} " +
                $"page={_pageSfx != null} ping={_pingSfx != null} interact={_interactSfx != null} boot={_bootSfx != null} loop={_machineLoopSfx != null}.");
        }

        private static IEnumerator LoadProvidedFocusAudioClip(string fileName, string clipName, Action<AudioClip> onLoaded)
        {
            string path = ResolveFocusAudioPath(fileName);
            if (string.IsNullOrEmpty(path))
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Focus audio missing: {fileName}");
                onLoaded?.Invoke(null);
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed loading focus audio '{path}': {request.error}");
                    onLoaded?.Invoke(null);
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Focus audio decoded null: {path}");
                    onLoaded?.Invoke(null);
                    yield break;
                }

                clip.name = clipName;
                onLoaded?.Invoke(clip);
            }
        }

        private static string ResolveFocusAudioPath(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            string pluginDir = null;
            try
            {
                pluginDir = Path.GetDirectoryName(typeof(SurveillanceBootstrap).Assembly.Location);
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Focus audio plugin-dir resolve failed: {ex.Message}");
            }

            if (string.IsNullOrEmpty(pluginDir)) return null;

            string nested = Path.Combine(pluginDir, FOCUS_AUDIO_DIR, fileName);
            if (File.Exists(nested)) return nested;
            string flat = Path.Combine(pluginDir, fileName);
            return File.Exists(flat) ? flat : null;
        }

        private static void PlaySwitchCameraSfx()
        {
            AudioClip clip = null;
            if (_switchSfxPool != null && _switchSfxPool.Length > 0)
            {
                clip = _switchSfxPool[UnityEngine.Random.Range(0, _switchSfxPool.Length)];
            }
            PlayFocusSfx(clip != null ? clip : _switchSfx, 0.78f);
        }

        private static void PlayPageSfx()
        {
            PlayFocusSfx(_pageSfx != null ? _pageSfx : _scanSfx, 0.82f);
        }

        private static void PlayPingSfx()
        {
            PlayFocusSfx(_pingSfx, 0.78f);
        }

        private static void PlayInteractSfx()
        {
            PlayFocusSfx(_interactSfx != null ? _interactSfx : _pingSfx, 0.72f);
        }

        private static void PlayBootSfx()
        {
            PlayFocusSfx(_bootSfx != null ? _bootSfx : _scanSfx, 0.78f);
        }

        private static void EnsureFocusMachineLoopSource()
        {
            if (_focusMachineLoopSource != null) return;

            _focusMachineLoopRoot = new GameObject("LethalCCTV_FocusMachineLoopAudio");
            if (SurveillanceBootstrap.Instance != null)
            {
                _focusMachineLoopRoot.transform.SetParent(SurveillanceBootstrap.Instance.transform, worldPositionStays: false);
            }
            else
            {
                UnityEngine.Object.DontDestroyOnLoad(_focusMachineLoopRoot);
            }
            _focusMachineLoopRoot.transform.localPosition = Vector3.zero;
            _focusMachineLoopRoot.transform.localRotation = Quaternion.identity;
            _focusMachineLoopRoot.transform.localScale = Vector3.one;

            _focusMachineLoopSource = _focusMachineLoopRoot.AddComponent<AudioSource>();
            _focusMachineLoopSource.playOnAwake = false;
            _focusMachineLoopSource.loop = true;
            _focusMachineLoopSource.spatialBlend = 0f;
            _focusMachineLoopSource.volume = FOCUS_MACHINE_LOOP_VOLUME;
            _focusMachineLoopSource.priority = 96;
            _focusMachineLoopSource.dopplerLevel = 0f;
            _focusMachineLoopSource.ignoreListenerPause = true;
        }

        private static void StartFocusMachineLoop()
        {
            if (_machineLoopSfx == null) return;
            EnsureFocusMachineLoopSource();
            if (_focusMachineLoopSource == null) return;

            // #716 E13: re-focusing inside the wind-down ramp has to claim the source back, or
            // the ramp still in flight would stop the loop this call just restarted.
            CctvAudioFade.CancelFade(_focusMachineLoopSource);
            _focusMachineLoopSource.clip = _machineLoopSfx;
            _focusMachineLoopSource.volume = FOCUS_MACHINE_LOOP_VOLUME;
            _focusMachineLoopSource.loop = true;
            if (!_focusMachineLoopSource.isPlaying)
            {
                _focusMachineLoopSource.Play();
            }
        }

        private static void StopFocusMachineLoop()
        {
            // #716 E13: sustained hum, so it ramps out instead of clicking off when the
            // operator leaves the monitor.
            if (_focusMachineLoopSource != null && _focusMachineLoopSource.isPlaying)
            {
                CctvAudioFade.StopLoop(_focusMachineLoopSource);
            }
        }

        private static AudioClip CreateSwitchClickClip()
        {
            const int sampleRate = 24000;
            int count = Mathf.RoundToInt(0.105f * sampleRate);
            float[] data = new float[count];
            uint seed = 0xC0FFEEu;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                seed = seed * 1664525u + 1013904223u;
                float noise = ((seed >> 9) / 8388607f) * 2f - 1f;
                float attack = Mathf.Clamp01(1f - t / 0.045f);
                float body = Mathf.Exp(-t * 34f);
                float lowClick = Mathf.Sin(2f * Mathf.PI * 155f * t) * body * 0.54f;
                float contact = noise * attack * 0.16f;
                data[i] = (lowClick + contact) * 0.58f;
            }

            AudioClip clip = AudioClip.Create("LethalCCTV_SwitchClick", count, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateScanSweepClip()
        {
            const int sampleRate = 24000;
            int count = Mathf.RoundToInt(0.34f * sampleRate);
            float[] data = new float[count];
            float phaseA = 0f;
            float phaseB = 0f;
            uint seed = 0x515CA11u;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                float env = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI);
                float hzA = Mathf.Lerp(190f, 430f, t);
                float hzB = Mathf.Lerp(320f, 680f, t);
                phaseA += hzA / sampleRate;
                phaseB += hzB / sampleRate;
                seed = seed * 1664525u + 1013904223u;
                float noise = ((seed >> 9) / 8388607f) * 2f - 1f;
                float tone = Mathf.Sin(phaseA * Mathf.PI * 2f) * 0.34f + Mathf.Sin(phaseB * Mathf.PI * 2f) * 0.18f;
                data[i] = (tone + noise * 0.035f) * env * 0.42f;
            }

            AudioClip clip = AudioClip.Create("LethalCCTV_ScanSweepSoft", count, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateRotateServoClip()
        {
            const int sampleRate = 24000;
            int count = Mathf.RoundToInt(0.12f * sampleRate);
            float[] data = new float[count];
            float phase = 0f;
            uint seed = 0xA11CEu;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                float env = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI);
                phase += Mathf.Lerp(92f, 118f, t) / sampleRate;
                seed = seed * 1664525u + 1013904223u;
                float noise = ((seed >> 9) / 8388607f) * 2f - 1f;
                data[i] = (Mathf.Sin(phase * Mathf.PI * 2f) * 0.20f + noise * 0.035f) * env * 0.28f;
            }

            AudioClip clip = AudioClip.Create("LethalCCTV_RotateServo", count, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateToneClip(string name, float duration, float startHz, float endHz, float gain)
        {
            const int sampleRate = 24000;
            int count = Mathf.Max(1, Mathf.RoundToInt(duration * sampleRate));
            float[] data = new float[count];
            float phase = 0f;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                float hz = Mathf.Lerp(startHz, endHz, t);
                phase += hz / sampleRate;
                float envelope = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI);
                data[i] = Mathf.Sin(phase * Mathf.PI * 2f) * envelope * gain;
            }

            AudioClip clip = AudioClip.Create(name, count, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static void PlayFocusSfx(AudioClip clip, float volume)
        {
            if (clip == null) return;
            HUDManager hud = HUDManager.Instance;
            if (hud != null && hud.UIAudio != null)
            {
                hud.UIAudio.PlayOneShot(clip, volume);
            }
        }

        /// <summary>
        /// Phase 1.7b — overlay-side mirror of the per-slot wall-material texture
        /// transitions. Called from QuadMonitor.BindRTToSlot / BindEmptyToSlot so
        /// the focus overlay's RawImage flips to the same texture the wall material
        /// shows on the same frame — closes the bound→empty stale-frame window
        /// that the dual-RT split would otherwise have introduced (and that the
        /// pre-dual-RT design also leaked, since the overlay was bound once at
        /// CreateOverlayQuadrant and never re-bound on empty transitions).
        /// </summary>
        internal static void NotifySlotDisplayTextureChanged(int slot, Texture tex)
        {
            if (_overlayRawImages == null) return;
            if (slot < 0 || slot >= _overlayRawImages.Length) return;
            if (_overlayRawImages[slot] == null) return;
            if (IsTurretPageActive && slot == 0)
            {
                _overlayRawImages[slot].texture = ShipTurretController.ViewTexture ?? Texture2D.blackTexture;
                return;
            }
            _overlayRawImages[slot].texture = tex;
            if (slot == 0 && IsBodycamFeedActive)
                RefreshBodycamFocusSlot();
        }
    }
}
