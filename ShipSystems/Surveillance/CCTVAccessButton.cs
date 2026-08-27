using System;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Events;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// Physical CCTV access button: a runtime clone of the vanilla ship
    /// teleporter's ButtonContainer (red cap + flip glass + pedestal + cord),
    /// spawned as a child of the operator station root once the CCTV upgrade is
    /// purchased. It replaces the red monitor-bezel button as the CCTV entry
    /// interaction; the bezel button goes inert while this button is alive and
    /// remains the fallback entry if the template clone ever fails.
    ///
    /// The clone keeps the vanilla ButtonAnimContainer animator, so
    /// PlayPressAnimation can depress the cap with the same 'press' trigger the
    /// teleporter uses — MonitorFocus fires it at the enter animation's press
    /// contact so the physical cap moves in sync with the authored hand press.
    ///
    /// Placement is operator-authored via the station placement editor
    /// (target "button", entered through CCTVOperatorStation.StartDebugPlacement).
    /// </summary>
    internal static class CCTVAccessButton
    {
        private const string RootName = "LethalCCTV_AccessButton";
        // ButtonAnimContainer only — cap + glass + base box. Its parent
        // ButtonContainer also carries the multi-meter LongCord (blew the
        // station bounds up to 52m and read as a "massive pipe" in-game,
        // 2026-07-11 test) and children with large authored offsets that
        // landed the button outside the ship.
        private const string TemplateChildName = "ButtonContainer/ButtonAnimContainer";
        private const string RedButtonChildName = "RedButton";
        private const string HoverTip = "View cameras : [E]";
        private const string PressTriggerName = "press";
        private const string GlassOpenBoolName = "GlassOpen";
        private const float StateRefreshIntervalSeconds = 0.2f;
        private const float PressSfxVolume = 0.9f;
        private const string ScanNodeName = "LethalCCTV_AccessButtonScanNode";
        private const string ScanNodeHeader = "CCTV Station";
        private const string ScanNodeSubText = "View cameras";
        // Deliberately shorter than the usual ship-object nodes (ship battery
        // uses 24m): the button is inside the ship and the label only matters
        // once the player is already near the monitor cluster.
        private const int ScanNodeMaxRange = 8;
        private const int ScanNodeFallbackLayer = 22;

        private static GameObject _root;
        private static Animator _buttonAnimator;
        private static InteractTrigger _trigger;
        private static AudioSource _audio;
        private static AudioClip _pressSfx;
        private static UnityAction<PlayerControllerB> _listener;
        private static float _nextStateRefreshAt;
        private static bool _templateMissingLogged;
        private static bool _spawnLogged;

        internal static bool IsAvailable => _root != null && _trigger != null;
        internal static Transform Root => _root != null ? _root.transform : null;

        /// <summary>The red cap transform used by the entry interaction.</summary>
        internal static Transform PressContactTransform => _trigger != null ? _trigger.transform : null;

        /// <summary>
        /// Resolve the live top surface of the red cap. The access button is
        /// authored lying flat, so entry presses must approach along the cap's
        /// most-upward local axis and meet the renderer surface, not the
        /// InteractTrigger origin or renderer center.
        /// </summary>
        internal static bool TryGetPressSurface(out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.up;

            Transform press = PressContactTransform;
            if (press == null)
                return false;

            Vector3 bestAxis = press.up;
            float bestScore = Mathf.Abs(Vector3.Dot(bestAxis, Vector3.up));
            ConsiderAxis(press.right);
            ConsiderAxis(press.forward);
            if (Vector3.Dot(bestAxis, Vector3.up) < 0f)
                bestAxis = -bestAxis;
            normal = bestAxis.sqrMagnitude > 0.0001f ? bestAxis.normalized : Vector3.up;

            Renderer renderer = press.GetComponent<Renderer>();
            if (renderer == null)
                renderer = press.GetComponentInChildren<Renderer>(includeInactive: true);
            if (renderer == null)
            {
                point = press.position + normal * 0.015f;
                return true;
            }

            Bounds bounds = renderer.bounds;
            Vector3 extents = bounds.extents;
            float projectedExtent =
                Mathf.Abs(normal.x) * extents.x +
                Mathf.Abs(normal.y) * extents.y +
                Mathf.Abs(normal.z) * extents.z;
            point = bounds.center + normal * projectedExtent;
            return true;

            void ConsiderAxis(Vector3 candidate)
            {
                float score = Mathf.Abs(Vector3.Dot(candidate, Vector3.up));
                if (score <= bestScore)
                    return;
                bestAxis = candidate;
                bestScore = score;
            }
        }

        /// <summary>
        /// Idempotent spawn under the station root. Called from
        /// CCTVOperatorStation.Ensure every frame the station is alive, so a
        /// mid-day purchase (or a destroyed clone) self-heals within a frame.
        /// </summary>
        internal static void Ensure(Transform stationRoot)
        {
            if (stationRoot == null)
                return;
            if (_root != null)
                return;

            Transform template = ResolveTemplateButtonContainer(out AudioClip pressSfx);
            if (template == null)
            {
                if (!_templateMissingLogged)
                {
                    _templateMissingLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV] Access button template unavailable (teleporter unlockable prefab or ButtonContainer not found); " +
                        "red bezel button remains the CCTV entry.");
                }
                return;
            }

            GameObject clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(template.gameObject, stationRoot, false);
            }
            catch (Exception ex)
            {
                if (!_templateMissingLogged)
                {
                    _templateMissingLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Access button clone failed: {ex.Message}");
                }
                return;
            }

            clone.name = RootName;
            _root = clone;
            _pressSfx = pressSfx;
            StripClonedBehaviours(clone);

            _buttonAnimator = clone.GetComponentInChildren<Animator>(includeInactive: true);
            // The teleporter's ButtonAudio child lives outside the cloned
            // subtree; give the button its own local press-audio source.
            _audio = clone.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 1f;
            _audio.minDistance = 0.8f;
            _audio.maxDistance = 12f;

            WireInteractTrigger(clone);
            CCTVOperatorStation.ApplyAccessButtonPlacement(clone.transform);
            BuildScanNode(clone);

            // The vanilla button hides its cap behind a flip-up safety glass; the
            // CCTV entry is a single press, so the glass idles open (the press
            // clip animates both the cap and the open glass, mirroring vanilla).
            if (_buttonAnimator != null)
                _buttonAnimator.SetBool(GlassOpenBoolName, true);

            if (!_spawnLogged)
            {
                _spawnLogged = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Access button spawned from teleporter template: local={clone.transform.localPosition} " +
                    $"trigger={(_trigger != null ? "wired" : "MISSING")} animator={(_buttonAnimator != null ? _buttonAnimator.runtimeAnimatorController?.name ?? "<none>" : "MISSING")} " +
                    $"pressSfx={(_pressSfx != null ? _pressSfx.name : "<none>")}.");
            }
        }

        internal static void Shutdown()
        {
            if (_trigger != null && _listener != null && _trigger.onInteract != null)
                _trigger.onInteract.RemoveListener(_listener);
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _buttonAnimator = null;
            _trigger = null;
            _audio = null;
            _pressSfx = null;
            _listener = null;
            _nextStateRefreshAt = 0f;
            _spawnLogged = false;
        }

        /// <summary>Interactable/hover-tip refresh, same cadence contract as the bezel buttons.</summary>
        internal static void Tick()
        {
            if (_trigger == null)
                return;
            if (Time.unscaledTime < _nextStateRefreshAt)
                return;
            _nextStateRefreshAt = Time.unscaledTime + StateRefreshIntervalSeconds;

            // Reassert every refresh, not just at spawn: any disable/enable
            // cycle on the Animator (e.g. an off-screen culling governor)
            // resets its parameters to defaults, and with GlassOpen back at
            // false the next state transition CLOSES the safety glass
            // mid-session (Test 38, first sighting after ~30 clean rounds).
            // SetBool with an unchanged value is free.
            if (_buttonAnimator != null)
            {
                try { _buttonAnimator.SetBool(GlassOpenBoolName, true); } catch { }
            }

            if (CCTVMonitorFeedSync.IsEntryAvailable(out string reason))
            {
                _trigger.interactable = true;
                _trigger.hoverTip = HoverTip;
            }
            else
            {
                _trigger.interactable = false;
                _trigger.disabledHoverTip = reason ?? string.Empty;
            }
        }

        /// <summary>
        /// Depresses the physical cap (vanilla 'press' state) and plays the
        /// teleporter press SFX. Fired at the enter animation's press contact.
        /// </summary>
        internal static void PlayPressAnimation()
        {
            if (_buttonAnimator != null)
            {
                try
                {
                    // The press state's exit transition consults GlassOpen;
                    // guarantee it is true so the glass never closes as a
                    // side effect of the cap press (Test 38 glass close).
                    _buttonAnimator.SetBool(GlassOpenBoolName, true);
                    _buttonAnimator.ResetTrigger(PressTriggerName);
                    _buttonAnimator.SetTrigger(PressTriggerName);
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Access button press animation failed: {ex.Message}");
                }
            }

            if (_audio != null && _pressSfx != null)
                _audio.PlayOneShot(_pressSfx, PressSfxVolume);
        }

        /// <summary>
        /// Ship-object scan node on the access button, alive exactly as long as
        /// the button (child of the clone, so purchase gating and Shutdown
        /// teardown come from the button lifecycle). Same construction as the
        /// other ship nodes (ship battery, stash) but with a short range.
        /// </summary>
        private static void BuildScanNode(GameObject clone)
        {
            Transform anchor = _trigger != null ? _trigger.transform : clone.transform;

            GameObject scanObject = new GameObject(ScanNodeName);
            // Parent to the clone root, not the animated cap, so the node does
            // not sink with the press animation; position it at the cap.
            scanObject.transform.SetParent(clone.transform, false);
            scanObject.transform.position = anchor.position;

            int scanLayer = LayerMask.NameToLayer("ScanNode");
            scanObject.layer = scanLayer >= 0 ? scanLayer : ScanNodeFallbackLayer;
            try
            {
                scanObject.tag = "DoNotSet";
            }
            catch (UnityException)
            {
                // Some modded profiles do not expose the tag table consistently.
            }

            SphereCollider collider = scanObject.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 0.3f;

            ScanNodeProperties scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = ScanNodeHeader;
            scanNode.subText = ScanNodeSubText;
            scanNode.minRange = 0;
            scanNode.maxRange = ScanNodeMaxRange;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
            scanNode.scrapValue = 0;
            scanNode.creatureScanID = -1;
        }

        private static void OnPressed(PlayerControllerB player)
        {
            CCTVVanillaMonitorButtons.RequestEntry(player);
        }

        private static Transform ResolveTemplateButtonContainer(out AudioClip pressSfx)
        {
            pressSfx = null;
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.unlockablesList == null || sor.unlockablesList.unlockables == null)
                return null;

            for (int i = 0; i < sor.unlockablesList.unlockables.Count; i++)
            {
                UnlockableItem unlockable = sor.unlockablesList.unlockables[i];
                GameObject prefab = unlockable != null ? unlockable.prefabObject : null;
                if (prefab == null)
                    continue;

                ShipTeleporter teleporter = prefab.GetComponent<ShipTeleporter>();
                if (teleporter == null || teleporter.isInverseTeleporter)
                    continue;

                Transform container = prefab.transform.Find(TemplateChildName);
                if (container == null)
                    continue;

                pressSfx = teleporter.buttonPressSFX;
                return container;
            }

            return null;
        }

        /// <summary>
        /// The clone must carry no live vanilla logic: the glass' networked
        /// AnimatedObjectTrigger has no NetworkObject here, and the serialized
        /// onInteract calls point at the (uncloned) ShipTeleporter. Keep only
        /// the RedButton InteractTrigger, silenced, for our own listener; every
        /// other MonoBehaviour goes, as do the glass' trigger and collider (the
        /// glass idles open and must never eat the interact ray).
        /// </summary>
        private static void StripClonedBehaviours(GameObject clone)
        {
            MonoBehaviour[] behaviours = clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                    continue;
                if (behaviour is InteractTrigger &&
                    string.Equals(behaviour.gameObject.name, RedButtonChildName, StringComparison.Ordinal))
                {
                    continue;
                }
                UnityEngine.Object.Destroy(behaviour);
            }

            Transform glass = FindChildRecursive(clone.transform, "ButtonGlass");
            if (glass != null)
            {
                Collider[] glassColliders = glass.GetComponents<Collider>();
                for (int i = 0; i < glassColliders.Length; i++)
                    UnityEngine.Object.Destroy(glassColliders[i]);
            }
        }

        private static void WireInteractTrigger(GameObject clone)
        {
            Transform redButton = FindChildRecursive(clone.transform, RedButtonChildName);
            _trigger = redButton != null ? redButton.GetComponent<InteractTrigger>() : null;
            if (_trigger == null)
            {
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] Access button clone has no RedButton InteractTrigger; entry stays on the bezel button.");
                return;
            }

            // Same takeover pattern as the bezel buttons: silence the serialized
            // vanilla calls (they reference the uncloned ShipTeleporter), keep
            // the trigger alive for our runtime listener.
            if (_trigger.onInteract != null)
            {
                int count = _trigger.onInteract.GetPersistentEventCount();
                for (int i = 0; i < count; i++)
                    _trigger.onInteract.SetPersistentListenerState(i, UnityEventCallState.Off);
            }

            _listener = OnPressed;
            _trigger.onInteract.AddListener(_listener);
            _trigger.hoverTip = HoverTip;
            _trigger.interactable = true;
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            if (root == null)
                return null;
            if (string.Equals(root.name, childName, StringComparison.Ordinal))
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), childName);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
