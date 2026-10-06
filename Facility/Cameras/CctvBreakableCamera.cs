using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Security;
using Y4NGZCompany.Facility.Shared;
namespace Y4NGZCompany.Facility.Cameras
{
    internal sealed class CctvBreakableCamera : MonoBehaviour, IHittable
    {
        private const string HitAudioFileName = "blunt_hit_wood_ufx_1.ogg";
        private const string StaticAudioFileName = "radio_static_mx_1_loop.wav";
        // Vanilla melee (Shovel/KnifeItem) resolves IHittable from the transform the
        // collider itself sits on, and its cast mask (1084754248 = bits 3/6/8/11/19/21/
        // 23/30) excludes Default while including Props (6). The hittable therefore
        // lives on a dedicated Props-layer child that owns the only collider.
        //
        // That collider is a TRIGGER. Both melee casts run with
        // QueryTriggerInteraction.Collide (Shovel.cs:162, KnifeItem.cs:77), so a trigger
        // is hit exactly like a solid box, and the shovel's occlusion Linecast uses
        // .Ignore so the hitbox can never shadow itself. A solid Props-layer box would
        // instead snag players walking past and, because every camera-placement overlap
        // in this module runs with QueryTriggerInteraction.Ignore (SurfaceMount,
        // PlacementProbe, InteriorSupportSpawner, CameraPlacementReviewStore), would be
        // seen by later mount passes as blocking geometry. A trigger is invisible to all
        // of them, so no name/component exclusion is needed anywhere.
        private const string HitboxChildName = "LethalCCTV_CameraHitbox";
        private const float MinHitboxSizeM = 0.30f;
        // A hand-scale camera prop must not grow a room-scale box; this caps each axis
        // in WORLD metres (see the lossyScale conversion in ResolveHitboxPlacement).
        private const float MaxHitboxSizeM = 0.60f;
        // Renderer bounds resolved to a centre this far from the prop origin mean the
        // union swallowed something bogus; fall back to the fixed box instead.
        private const float MaxHitboxCenterOffsetM = 1f;
        // No replicated break within this window means the request was refused or lost.
        // Works identically on host and client (a client's request is fire-and-forget).
        private const float BreakLatchTimeoutSeconds = 4f;
        private const float ShutdownPitchDegrees = 38f;
        private const float ShutdownDurationSeconds = 2f;
        private const float HitVolume = 0.7f;
        private const float StaticVolume = 0.2f;

        private static readonly Vector3 DefaultHitboxSize = new Vector3(0.45f, 0.35f, 0.35f);

        private static readonly Dictionary<int, CctvBreakableCamera> BreakablesByIndex =
            new Dictionary<int, CctvBreakableCamera>();

        private static readonly HashSet<int> LatchTimeoutLoggedIndices = new HashSet<int>();

        private static int _propsLayer = -1;
        private static bool _propsLayerResolved;
        private static bool _propsLayerWarned;

        // #563: damage feedback. A wounded camera smokes from half health onward, which
        // reuses the same code-built emitter the shutdown sequence already spawns rather
        // than introducing a new effect asset.
        private const float DamagedSmokeThreshold01 = 0.5f;
        private static AudioClip _fallbackHitClip;
        private static bool _fallbackHitClipResolved;
        private static bool _silentHitWarned;

        private CCTVCamera _camera;
        private Collider _hitboxCollider;
        private float _health;
        private float _maxHealth = 1f;
        private bool _firstHitLogged;
        private CCTVCameraLensBlinker _lensBlinker;
        private bool _lensBlinkerResolved;
        private CCTVCameraVisual _visual;
        private bool _visualResolved;
        private ParticleSystem _damageSmoke;
        private bool _breakRequested;
        private float _breakRequestedAt;
        private bool _broken;
        private bool _shutdownComplete;
        private AudioClip _hitClip;
        private AudioClip _staticClip;
        private AudioSource _hitSource;
        private AudioSource _staticSource;
        private ParticleSystem _smoke;

        // `target` must be the transform that actually carries the head geometry (the
        // visual's AimPivot). The head is re-posed every LateUpdate as the camera
        // sweeps, so a hitbox parented anywhere above it would drift off the visible
        // camera within seconds.
        internal static void AttachTo(CCTVCamera camera, GameObject target)
        {
            if (camera == null || target == null || target.GetComponentInChildren<CctvBreakableCamera>(true) != null)
                return;

            if (!TryResolvePropsLayer(out int propsLayer))
                return;

            // The visual build removes every prefab/primitive collider (immediately since
            // #1271), so this child owns the prop's only collider. It is created
            // unconditionally instead of probing for an existing collider.
            var hitbox = new GameObject(HitboxChildName).transform;
            hitbox.SetParent(target.transform, worldPositionStays: false);
            hitbox.localRotation = Quaternion.identity;
            hitbox.localScale = Vector3.one;
            hitbox.gameObject.layer = propsLayer;

            ResolveHitboxPlacement(target, out Vector3 localCenter, out Vector3 localSize);
            hitbox.localPosition = localCenter;

            BoxCollider collider = hitbox.gameObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.center = Vector3.zero;
            collider.size = localSize;

            // #563: melee and gunfire both resolve their target through
            // RaycastHit.transform, and Unity retargets that to the attached Rigidbody's
            // transform when the collider has no body of its own - which, for a hitbox
            // parented into a dungeon tile, can be a body several levels up that carries
            // no IHittable. Its own kinematic body makes the hitbox the attached body, so
            // RaycastHit.transform always lands here. Kinematic and gravity-free so it
            // stays a pure query anchor and never simulates.
            Rigidbody body = hitbox.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var breakable = hitbox.gameObject.AddComponent<CctvBreakableCamera>();
            breakable._camera = camera;
            breakable._health = CctvSecurityConfig.Current.CameraHealth;
            breakable._maxHealth = Mathf.Max(1f, breakable._health);
            BreakablesByIndex[camera.CameraIndex] = breakable;
            breakable.EnsureHitSource();
            FacilityAudioClipLoader.Request(breakable, HitAudioFileName, clip => breakable._hitClip = clip);
            FacilityAudioClipLoader.Request(breakable, StaticAudioFileName, breakable.OnStaticClipLoaded);

            if (ShouldLogVerbose())
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Breakable camera attached cam={camera.CameraIndex:D2} layer={hitbox.gameObject.layer} " +
                    $"trigger=true local={hitbox.localPosition} size={collider.size} health={breakable._health:F0}.");
            }
        }

        // Same two-step resolution FuelRuntimeUtility.PropsLayer uses (that helper lives
        // in the Y4NGZCompany assembly, which this one does not reference). Resolution is
        // by NAME so a moon that renames layers degrades gracefully; when neither name
        // exists we skip attaching entirely rather than guessing an index, matching the
        // documented skip-on-missing convention in PlacementMask.
        private static bool TryResolvePropsLayer(out int layer)
        {
            if (!_propsLayerResolved)
            {
                _propsLayer = LayerMask.NameToLayer("Props");
                if (_propsLayer < 0)
                    _propsLayer = LayerMask.NameToLayer("InteractableObject");
                _propsLayerResolved = true;
            }

            layer = _propsLayer;
            if (layer >= 0)
                return true;

            if (!_propsLayerWarned)
            {
                _propsLayerWarned = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] Neither 'Props' nor 'InteractableObject' layer resolves; " +
                    "breakable camera hitboxes are skipped this session.");
            }
            return false;
        }

        // Sizes the hitbox from the head subtree's renderer bounds, folded into the
        // head's local space. Falls back to the fixed box whenever the result is
        // untrustworthy, because a bogus centre would push an unhittable box far away
        // from the visible prop.
        private static void ResolveHitboxPlacement(GameObject target, out Vector3 localCenter, out Vector3 localSize)
        {
            localCenter = Vector3.zero;
            localSize = DefaultHitboxSize;

            if (!CCTVCameraVisual.TryComputeLocalRendererBounds(target, target.transform, out Bounds local))
                return;

            // Local units are only world-meaningful after the parent's scale, so the
            // min/max limits are applied in world metres and converted back.
            Vector3 lossy = target.transform.lossyScale;
            float sx = SafeScale(lossy.x);
            float sy = SafeScale(lossy.y);
            float sz = SafeScale(lossy.z);

            Vector3 worldOffset = new Vector3(local.center.x * sx, local.center.y * sy, local.center.z * sz);
            if (worldOffset.magnitude > MaxHitboxCenterOffsetM)
                return;

            localCenter = local.center;
            localSize = new Vector3(
                ClampWorldAxis(local.size.x, sx),
                ClampWorldAxis(local.size.y, sy),
                ClampWorldAxis(local.size.z, sz));
        }

        private static float SafeScale(float scale)
        {
            float magnitude = Mathf.Abs(scale);
            return magnitude > 1e-4f ? magnitude : 1f;
        }

        private static float ClampWorldAxis(float localSize, float scale)
        {
            return Mathf.Clamp(localSize * scale, MinHitboxSizeM, MaxHitboxSizeM) / scale;
        }

        private static bool ShouldLogVerbose()
        {
            return CCTVCameraVisual.ShouldLogVerbose();
        }

        /// <summary>
        /// #563: snapshot of every live (attached, unbroken) breakable for the shotgun
        /// pass, which iterates this registry directly instead of physics-sweeping the
        /// Props layer (an unordered NonAlloc buffer can truncate the camera away in a
        /// heavily looted corridor). The snapshot also keeps the caller safe against
        /// registry mutation while it swings through the list.
        /// </summary>
        internal static void CollectLive(List<CctvBreakableCamera> into)
        {
            into.Clear();
            foreach (CctvBreakableCamera breakable in BreakablesByIndex.Values)
            {
                if (breakable != null && !breakable._broken)
                    into.Add(breakable);
            }
        }

        /// <summary>World-space centre of the hitbox volume; the point gunfire aims for.</summary>
        internal Vector3 WorldHitPoint
        {
            get
            {
                if (_hitboxCollider == null)
                    _hitboxCollider = GetComponent<Collider>();
                return _hitboxCollider != null ? _hitboxCollider.bounds.center : transform.position;
            }
        }

        /// <summary>
        /// Directly traces the trigger hitboxes without adding Props to a weapon's world mask.
        /// BetterArmory must ignore triggers for ordinary world impacts, while CCTV must use one
        /// so the hitbox never blocks a player or a later placement pass. Selecting the nearest
        /// live hit here preserves both contracts.
        /// </summary>
        internal static bool TryRaycastNearest(
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            out CctvBreakableCamera nearest,
            out float distance)
        {
            nearest = null;
            distance = Mathf.Max(0f, maxDistance);
            if (direction.sqrMagnitude < 1e-6f || distance <= 0f)
                return false;

            Ray ray = new Ray(origin, direction.normalized);
            int nearestIndex = int.MaxValue;
            foreach (KeyValuePair<int, CctvBreakableCamera> entry in BreakablesByIndex)
            {
                CctvBreakableCamera candidate = entry.Value;
                if (candidate == null || candidate._broken)
                    continue;

                Collider collider = candidate._hitboxCollider;
                if (collider == null)
                    collider = candidate.GetComponent<Collider>();
                candidate._hitboxCollider = collider;
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                if (!collider.Raycast(ray, out RaycastHit hit, distance))
                    continue;
                if (hit.distance < 0.001f)
                    continue;
                if (nearest != null && hit.distance >= distance - 0.0001f && entry.Key >= nearestIndex)
                    continue;

                nearest = candidate;
                distance = hit.distance;
                nearestIndex = entry.Key;
            }

            return nearest != null;
        }

        // Called at round teardown; the per-round hitboxes and their indices do not
        // survive the interior.
        internal static void ResetRound()
        {
            BreakablesByIndex.Clear();
            LatchTimeoutLoggedIndices.Clear();
        }

        internal static bool ApplyReplicatedBreak(CCTVCamera camera)
        {
            if (camera == null
                || !BreakablesByIndex.TryGetValue(camera.CameraIndex, out CctvBreakableCamera breakable)
                || breakable == null)
            {
                return false;
            }

            breakable.ApplyBreakLocal();
            return true;
        }

        private void OnDestroy()
        {
            if (_camera != null
                && BreakablesByIndex.TryGetValue(_camera.CameraIndex, out CctvBreakableCamera current)
                && current == this)
            {
                BreakablesByIndex.Remove(_camera.CameraIndex);
            }
        }

        public bool Hit(int force, Vector3 hitDirection, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            if (_broken || _camera == null)
                return false;
            if (!CctvSecurityConfig.Current.BreakableCamerasEnabled)
                return false;

            if (_breakRequested)
            {
                // A client's RequestPhysicalBreak is fire-and-forget (it cannot report a
                // server-side refusal), so the latch is released on a TIMEOUT rather than
                // on a return value: identical behaviour on host and client, and it also
                // covers a dropped/ignored request. One restored hit point means the very
                // next swing re-requests.
                if (Time.unscaledTime < _breakRequestedAt + BreakLatchTimeoutSeconds)
                    return false;

                _breakRequested = false;
                _health = 1f;
                if (LatchTimeoutLoggedIndices.Add(_camera.CameraIndex))
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] No replicated break arrived within {BreakLatchTimeoutSeconds:F0}s " +
                        $"cam={_camera.CameraIndex:D2}; camera stays hittable.");
                }
            }

            int damage = Mathf.Max(1, force);
            _health -= damage;
            PlayHitSfx();
            PlayHitFeedback(hitDirection);

            // #563: the first hit on each camera always logs, verbose or not. "I swung and
            // nothing happened" is indistinguishable in a log from "the hitbox was never
            // reached", and that ambiguity is exactly what this line exists to remove; the
            // once-per-camera gate keeps a sustained beating from flooding the file.
            if (!_firstHitLogged || ShouldLogVerbose())
            {
                _firstHitLogged = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Camera hit cam={_camera.CameraIndex:D2} force={force} damage={damage} "
                    + $"health={_health:F0}/{_maxHealth:F0}.");
            }

            if (_health <= 0f)
            {
                _breakRequested = true;
                _breakRequestedAt = Time.unscaledTime;
                CctvCameraShutdownSync.RequestPhysicalBreak(_camera.CameraIndex);
            }
            return true;
        }

        private void ApplyBreakLocal()
        {
            if (_broken || _camera == null)
                return;

            _breakRequested = true;
            _broken = true;
            _camera.MarkSecurityBroken();
            CctvSecurityCameraRegistry.MarkCameraBroken(_camera);
            QuadCameraAssignment.OnCameraBroken(_camera);
            StartCoroutine(RunShutdownSequence());
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] Camera broken cam={_camera.CameraIndex:D2}; shutdown sequence started.");
        }

        private IEnumerator RunShutdownSequence()
        {
            Transform head = CCTVCameraVisual.TryGetRotatingHead(_camera);
            if (head != null)
            {
                Quaternion startRotation = head.localRotation;
                Quaternion downRotation = startRotation * Quaternion.Euler(ShutdownPitchDegrees, 0f, 0f);
                float elapsed = 0f;
                while (elapsed < ShutdownDurationSeconds && head != null)
                {
                    elapsed += Time.unscaledDeltaTime;
                    float linear = Mathf.Clamp01(elapsed / ShutdownDurationSeconds);
                    float smooth = linear * linear * (3f - 2f * linear);
                    head.localRotation = Quaternion.SlerpUnclamped(startRotation, downRotation, smooth);
                    yield return null;
                }
                if (head != null)
                    head.localRotation = downRotation;
            }

            Transform smokeParent = transform;
            _smoke = FacilitySmokeEffectBuilder.BuildSmoke(smokeParent, "CameraShutdownSmoke", 0.6f);
            if (_smoke != null)
            {
                Vector3 emitterPosition = head != null ? head.position : transform.position;
                _smoke.transform.SetPositionAndRotation(emitterPosition + Vector3.up * 0.03f, Quaternion.identity);
                _smoke.Play();
            }

            _shutdownComplete = true;
            StartStaticLoopIfReady(head);
        }

        private void EnsureHitSource()
        {
            if (_hitSource != null)
                return;
            _hitSource = gameObject.AddComponent<AudioSource>();
            ConfigureSpatialSource(_hitSource, HitVolume, 0.75f, 12f, loop: false);
        }

        // #563: a hit must never be silent. The bundled wood-hit clip loads asynchronously
        // and can fail outright, and the old body simply skipped the one-shot when it was
        // missing - so a camera that had lost its clip absorbed swings with no sound at
        // all. Vanilla's own surface hit SFX is the fallback; if even that is unreachable
        // the failure is logged once instead of disappearing.
        private void PlayHitSfx()
        {
            EnsureHitSource();
            // Unity's overloaded != , not ??: a destroyed clip is fake-null and the
            // null-coalescing operator would hand it straight to PlayOneShot.
            AudioClip clip = _hitClip != null ? _hitClip : ResolveFallbackHitClip();
            if (clip != null)
            {
                _hitSource.PlayOneShot(clip, 1f);
                return;
            }

            if (!_silentHitWarned)
            {
                _silentHitWarned = true;
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Camera hit SFX unavailable ('{HitAudioFileName}' not loaded and no vanilla "
                    + "footstep surface clip resolved); camera hits are silent this session.");
            }
        }

        // StartOfRound.footstepSurfaces is the same table Shovel.HitShovel plays its
        // surface impact from, so this is a clip vanilla itself uses for melee contact and
        // is guaranteed loaded whenever a round is running.
        private static AudioClip ResolveFallbackHitClip()
        {
            if (_fallbackHitClipResolved)
                return _fallbackHitClip;

            StartOfRound round = StartOfRound.Instance;
            FootstepSurface[] surfaces = round != null ? round.footstepSurfaces : null;
            if (surfaces != null)
            {
                for (int i = 0; i < surfaces.Length; i++)
                {
                    if (surfaces[i] != null && surfaces[i].hitSurfaceSFX != null)
                    {
                        _fallbackHitClip = surfaces[i].hitSurfaceSFX;
                        break;
                    }
                }
            }

            // Only latch once a round exists; before that the table is legitimately null
            // and a permanent cache of "nothing" would outlive the reason for it.
            _fallbackHitClipResolved = round != null;
            return _fallbackHitClip;
        }

        // The three non-audio halves of "that swing landed": the lens glitches, the head
        // physically flinches, and past half damage the housing starts smoking. All local
        // to the client that registered the hit, which is the same client vanilla runs
        // Shovel.HitShovel and ShotgunItem's damage pass on.
        private void PlayHitFeedback(Vector3 hitDirection)
        {
            float damage01 = Mathf.Clamp01(1f - Mathf.Max(0f, _health) / Mathf.Max(1f, _maxHealth));

            CCTVCameraLensBlinker blinker = ResolveLensBlinker();
            if (blinker != null)
                blinker.FlashDamage(damage01);

            CCTVCameraVisual visual = ResolveVisual();
            if (visual != null)
                visual.ApplyDamageKick(hitDirection);

            if (damage01 >= DamagedSmokeThreshold01)
                EnsureDamagedSmoke();
        }

        private CCTVCameraLensBlinker ResolveLensBlinker()
        {
            if (_lensBlinkerResolved)
                return _lensBlinker;

            // The hitbox is a child of the visual's AimPivot and the lens dot is another
            // child of the same subtree, so the parent is the nearest common root. Resolved
            // lazily because the lens can finish building after the hitbox is attached.
            Transform pivot = transform.parent;
            _lensBlinker = pivot != null ? pivot.GetComponentInChildren<CCTVCameraLensBlinker>(true) : null;
            _lensBlinkerResolved = _lensBlinker != null;
            return _lensBlinker;
        }

        private CCTVCameraVisual ResolveVisual()
        {
            if (_visualResolved)
                return _visual;

            _visual = _camera != null ? _camera.GetComponent<CCTVCameraVisual>() : null;
            _visualResolved = _visual != null;
            return _visual;
        }

        private void EnsureDamagedSmoke()
        {
            if (_damageSmoke != null)
                return;

            _damageSmoke = FacilitySmokeEffectBuilder.BuildSmoke(transform, "CameraDamageSmoke", 0.35f);
            if (_damageSmoke == null)
                return;

            Transform head = CCTVCameraVisual.TryGetRotatingHead(_camera);
            Vector3 emitterPosition = head != null ? head.position : transform.position;
            _damageSmoke.transform.SetPositionAndRotation(emitterPosition + Vector3.up * 0.03f, Quaternion.identity);
            _damageSmoke.Play();
        }

        private void OnStaticClipLoaded(AudioClip clip)
        {
            _staticClip = clip;
            if (_shutdownComplete)
                StartStaticLoopIfReady(CCTVCameraVisual.TryGetRotatingHead(_camera));
        }

        private void StartStaticLoopIfReady(Transform head)
        {
            if (_staticClip == null)
                return;

            if (_staticSource == null)
            {
                GameObject staticObject = new GameObject("CameraBrokenRadioStatic");
                staticObject.transform.SetParent(transform, worldPositionStays: false);
                if (head != null)
                    staticObject.transform.position = head.position;
                _staticSource = staticObject.AddComponent<AudioSource>();
                ConfigureSpatialSource(_staticSource, StaticVolume, 1f, 7f, loop: true);
            }

            _staticSource.clip = _staticClip;
            if (!_staticSource.isPlaying)
                _staticSource.Play();
        }

        private static void ConfigureSpatialSource(
            AudioSource source,
            float volume,
            float minDistance,
            float maxDistance,
            bool loop)
        {
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 1f;
            source.volume = volume;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.dopplerLevel = 0f;
        }
    }
}
