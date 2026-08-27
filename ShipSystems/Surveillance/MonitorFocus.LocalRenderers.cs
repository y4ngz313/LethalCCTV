using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DunGen;
using GameNetcodeStuff;
using Y4NGZCompany.Facility.Cameras;
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
        private static void LogLocalObstructorCallOnce(string operation, string reason)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
            string key = operation + ":" + safeReason;
            if (!_localObstructorCallLogs.Add(key))
                return;

            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV][ObstructorMask] call={operation} reason={safeReason} frame={Time.frameCount} " +
                $"hidden={_focusHiddenLocalRenderers.Count} forced={_focusForcedLocalRenderers.Count} " +
                $"visor={_focusHiddenLocalVisor != null} apiHold={_holdLocalObstructorsForInteractionsApiRestore}.");
        }

        private static bool HasActiveLocalPlayerObstructors()
        {
            return _focusHiddenLocalRenderers.Count > 0 ||
                _focusForcedLocalRenderers.Count > 0 ||
                _focusHiddenLocalVisor != null;
        }

        private static void SuppressLocalPlayerObstructors(PlayerControllerB player, string reason)
        {
            if (string.Equals(reason, "focus-view-begin", StringComparison.Ordinal) &&
                HasActiveLocalPlayerObstructors())
            {
                ReassertLocalPlayerObstructors();
                // #601: LethalPhones parents the owner's phone under the
                // arms-only rig, so every one of its renderers reads as a
                // "real first-person arm renderer" and is deliberately kept
                // visible by the sweep below. It is suppressed through the
                // mod's own local-visual setter instead.
                LethalPhonesCompat.SuppressLocalPhone(player, reason);
                LogLocalObstructorCallOnce("suppress", reason);
                return;
            }

            RestoreLocalPlayerObstructors("suppress-preclear:" + reason);
            if (player == null)
            {
                LogLocalObstructorCallOnce("suppress", reason);
                return;
            }

            // Visibility suppression is deliberately independent from arming the
            // camera-relative first-person presentation. Pose settle starts before
            // the focus camera/root state exists, so it may hide obstructors and
            // the visor but must not create a half-armed renderer state.
            bool showRealFirstPersonArms = ShouldShowRealLocalFirstPersonArms(player, out string armsDetails);
            // Ghost arm (Test 36 Issue C): during pose settle the render-pin
            // holds the rendered camera at the walk-up pose while the body
            // already stands at the station, so an enabled arms-only model is
            // seen from ~1m outside itself as a detached floating arm. Until
            // the enter camera path owns the frame, keep the real FP arms in
            // the hidden set too; ArmLocalFirstPersonArmsPresentation promotes
            // them to forced-visible at focus-view-begin (same frame the API
            // session pin and left-hand ramp-in take the bones).
            bool holdRealArmsHiddenForBeginWindow =
                showRealFirstPersonArms && !_physicalFocusViewActive;
            AddLocalObstructorRenderer(player.thisPlayerModel);
            AddLocalObstructorRenderer(player.thisPlayerModelLOD1);
            AddLocalObstructorRenderer(player.thisPlayerModelLOD2);
            if (!showRealFirstPersonArms || holdRealArmsHiddenForBeginWindow)
                AddLocalObstructorRenderer(player.thisPlayerModelArms);

            if (player.playerBodyAnimator != null)
            {
                Renderer[] renderers = player.playerBodyAnimator.GetComponentsInChildren<Renderer>(includeInactive: true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (!showRealFirstPersonArms ||
                        holdRealArmsHiddenForBeginWindow ||
                        !IsRealLocalFirstPersonArmRenderer(player, renderers[i]))
                    {
                        AddLocalObstructorRenderer(renderers[i]);
                    }
                }
            }

            LogLocalFirstPersonArmsState(
                showRealFirstPersonArms,
                holdRealArmsHiddenForBeginWindow
                    ? armsDetails + " [held hidden until the enter path owns the frame]"
                    : armsDetails);

            // Visor parity (Test 36 Issue D): the vanilla terminal keeps the
            // helmet mask through its whole animation, so ours does too — the
            // park below is now the legacy A/B path. SnapLocalVisorToCameraTarget
            // keeps the mask glued (position AND rotation) after every scripted
            // camera write so the vanilla 53/s rotation lerp can never lag a
            // fast swing (the historical reason the mask was hidden at all).
            if (player.localVisor != null && !KeepLocalVisorDuringSessionEnabled)
            {
                _focusHiddenLocalVisor = player.localVisor;
                _focusHiddenLocalVisorLocalPosition = player.localVisor.localPosition;
                _focusHiddenLocalVisorLocalRotation = player.localVisor.localRotation;
                _focusHiddenLocalVisorLocalScale = player.localVisor.localScale;

                try
                {
                    Transform hidden = StartOfRound.Instance != null ? StartOfRound.Instance.notSpawnedPosition : null;
                    _focusHiddenLocalVisorHiddenWorldPosition = hidden != null
                        ? hidden.position
                        : player.localVisor.position + Vector3.down * 5000f;
                    player.localVisor.position = _focusHiddenLocalVisorHiddenWorldPosition;
                }
                catch { }
            }

            // Soft-dep obstructor (#601): third-party viewmodels attached to the
            // arms-only rig are invisible to the sweep above by design. They are
            // registered here so the same suppress/reassert/restore lifecycle
            // owns them and every exit path restores them.
            LethalPhonesCompat.SuppressLocalPhone(player, reason);

            LogLocalObstructorCallOnce("suppress", reason);
        }

        private static void ArmLocalFirstPersonArmsPresentation(PlayerControllerB player)
        {
            if (!ShouldShowRealLocalFirstPersonArms(player, out _))
                return;

            // This runs after CaptureFirstPersonArmsCameraPose and is not folded
            // into the idempotent visibility branch above. It therefore builds
            // (or repairs) the LocalRendererState even when settle already hid
            // the body/visor before focus-view-begin.
            ForceLocalRendererVisible(player, player.thisPlayerModelArms);
            if (player.playerBodyAnimator == null)
                return;

            Renderer[] renderers = player.playerBodyAnimator.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (IsRealLocalFirstPersonArmRenderer(player, renderers[i]))
                    ForceLocalRendererVisible(player, renderers[i]);
            }
        }

        internal static void RefreshLocalOperatorFirstPersonVisibility()
        {
            if (!IsFocused || _focusedPlayer == null)
                return;

            SuppressLocalPlayerObstructors(_focusedPlayer, "visibility-refresh");
            ArmLocalFirstPersonArmsPresentation(_focusedPlayer);
        }

        private static bool ShouldShowRealLocalFirstPersonArms(PlayerControllerB player, out string details)
        {
            details = "disabled";
            if (!Y4NGZPlayerAnimationBridge.ShouldShowAuthoredFirstPersonArms)
                return false;
            if (IsStationThirdPersonDebugViewActive)
            {
                details = "third-person-debug-active";
                return false;
            }
            if (player == null)
            {
                details = "player-null";
                return false;
            }

            Renderer arms = player.thisPlayerModelArms;
            if (!IsUsableLocalFirstPersonArmsRenderer(arms, out string reason))
            {
                details = reason;
                return false;
            }

            details =
                $"renderer='{arms.name}', path='{GetTransformPath(arms.transform)}', " +
                $"mesh='{GetRendererMeshName(arms)}', materials={GetRendererMaterialCount(arms)}";
            return true;
        }

        private static bool IsUsableLocalFirstPersonArmsRenderer(Renderer renderer, out string reason)
        {
            if (renderer == null)
            {
                reason = "thisPlayerModelArms-null";
                return false;
            }
            if (renderer.gameObject == null)
            {
                reason = "thisPlayerModelArms-gameObject-null";
                return false;
            }
            if (renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0)
            {
                reason = "thisPlayerModelArms-has-no-materials";
                return false;
            }

            reason = "ok";
            return true;
        }

        private static bool IsRealLocalFirstPersonArmRenderer(PlayerControllerB player, Renderer renderer)
        {
            if (player == null || renderer == null)
                return false;
            if (renderer == player.thisPlayerModelArms)
                return true;

            string path = GetTransformPath(renderer.transform);
            return path.IndexOf("ScavengerModelArmsOnly", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void LogLocalFirstPersonArmsState(bool active, string details)
        {
            if (active)
            {
                if (_localFirstPersonArmsActiveLogged)
                    return;

                _localFirstPersonArmsActiveLogged = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] Local first-person CCTV arms active with real vanilla renderer only: " +
                    details + ".");
                return;
            }

            if (!Y4NGZPlayerAnimationBridge.UseLocalAuthoredOperatorAnimations || _localFirstPersonArmsMissingLogged)
                return;

            _localFirstPersonArmsMissingLogged = true;
            SurveillanceBootstrap.Log?.LogWarning(
                "[LethalCCTV] Local first-person CCTV arms requested, but no usable real vanilla arm renderer was found; " +
                $"arms remain hidden. Reason: {details}.");
        }

        private static void TickFirstPersonArmsOffsetHotkeys()
        {
            Keyboard keyboard = Keyboard.current;
            if (Y4NGZPlayerAnimationBridge.IsFirstPersonHandTuningHotkeyActive)
            {
                FinishFirstPersonArmsOffsetEditing();
                return;
            }
            if (keyboard == null || !CanEditFirstPersonArmsOffset())
            {
                FinishFirstPersonArmsOffsetEditing();
                return;
            }

            bool editHeld = keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
            if (!editHeld)
            {
                FinishFirstPersonArmsOffsetEditing();
                return;
            }

            EnsureFirstPersonArmsOffsetLoaded();
            _firstPersonArmsOffsetEditingLastFrame = true;

            if (keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame)
            {
                _firstPersonArmsOffset = Vector3.zero;
                _firstPersonArmsOffsetDirty = true;
                ApplyFirstPersonArmsOffsetToForcedRenderers();
                FlushFirstPersonArmsOffsetConfig();
                ShowFirstPersonArmsOffsetStatus(force: true);
                return;
            }

            Vector3 delta = Vector3.zero;
            if (keyboard.aKey.isPressed) delta.x -= 1f;
            if (keyboard.dKey.isPressed) delta.x += 1f;
            if (keyboard.fKey.isPressed || keyboard.pageDownKey.isPressed) delta.y -= 1f;
            if (keyboard.rKey.isPressed || keyboard.pageUpKey.isPressed) delta.y += 1f;
            if (keyboard.sKey.isPressed) delta.z -= 1f;
            if (keyboard.wKey.isPressed) delta.z += 1f;

            if (delta.sqrMagnitude <= 0.0001f)
                return;

            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float speed = fast ? FIRST_PERSON_ARMS_OFFSET_FAST_SPEED : FIRST_PERSON_ARMS_OFFSET_SPEED;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
            _firstPersonArmsOffset = ClampFirstPersonArmsOffset(_firstPersonArmsOffset + delta.normalized * speed * dt);
            _firstPersonArmsOffsetDirty = true;
            ApplyFirstPersonArmsOffsetToForcedRenderers();
            ShowFirstPersonArmsOffsetStatus(force: false);
        }

        private static bool CanEditFirstPersonArmsOffset()
        {
            if (!IsFocused)
                return false;
            if (!Y4NGZPlayerAnimationBridge.UseLocalAuthoredOperatorAnimations)
                return false;
            if (IsStationThirdPersonDebugViewActive || CCTVOperatorStation.IsDebugPlacementActive)
                return false;

            for (int i = 0; i < _focusForcedLocalRenderers.Count; i++)
            {
                LocalRendererState state = _focusForcedLocalRenderers[i];
                if (state != null && state.ApplyFirstPersonArmsOffset && state.OffsetTransform != null)
                    return true;
            }

            return false;
        }

        private static void FinishFirstPersonArmsOffsetEditing()
        {
            if (!_firstPersonArmsOffsetEditingLastFrame)
                return;

            _firstPersonArmsOffsetEditingLastFrame = false;
            FlushFirstPersonArmsOffsetConfig();
        }

        // #575: the three LEGACY-tagged "First Person Arms Offset" keys left the
        // config file. They are inert under the manual-IK architecture and only
        // ever persisted a developer nudge, so the offset now starts at zero
        // each session and the Alt nudge stays a live-only debug aid.
        private static void EnsureFirstPersonArmsOffsetLoaded()
        {
            if (_firstPersonArmsOffsetLoaded)
                return;

            _firstPersonArmsOffset = Vector3.zero;
            _firstPersonArmsOffsetLoaded = true;
        }

        private static Vector3 GetFirstPersonArmsOffset()
        {
            EnsureFirstPersonArmsOffsetLoaded();
            return _firstPersonArmsOffset;
        }

        private static Vector3 ClampFirstPersonArmsOffset(Vector3 value)
        {
            return new Vector3(
                Mathf.Clamp(value.x, -FIRST_PERSON_ARMS_OFFSET_MAX, FIRST_PERSON_ARMS_OFFSET_MAX),
                Mathf.Clamp(value.y, -FIRST_PERSON_ARMS_OFFSET_MAX, FIRST_PERSON_ARMS_OFFSET_MAX),
                Mathf.Clamp(value.z, -FIRST_PERSON_ARMS_OFFSET_MAX, FIRST_PERSON_ARMS_OFFSET_MAX));
        }

        private static void ApplyFirstPersonArmsOffsetToForcedRenderers()
        {
            if (!Y4NGZPlayerAnimationBridge.ShouldUseLegacyFirstPersonArmsPresentation)
                return;

            for (int i = 0; i < _focusForcedLocalRenderers.Count; i++)
                ApplyFirstPersonArmsOffset(_focusForcedLocalRenderers[i]);
        }

        private static void CaptureFirstPersonArmsCameraPose(PlayerControllerB player)
        {
            ResetFirstPersonArmsCameraPose();
            if (player == null || _physicalFocusCameraTransform == null)
                return;

            // Presentation is derived per frame from the live animator root
            // pose and the camera's offset from the vanilla head pose, so no
            // relative pose is frozen here; a raw camera-relative capture at
            // entry baked the entry look pitch and a mid-setup camera pose
            // into every subsequent frame (2026-07-14 trace).
            _firstPersonArmsCameraPoseRoot =
                ResolveFirstPersonArmsOffsetRoot(player, player.thisPlayerModelArms);
            _firstPersonArmsCameraPoseCaptured = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] First-person arms head-frame presentation armed: " +
                $"root='{(_firstPersonArmsCameraPoseRoot != null ? GetTransformPath(_firstPersonArmsCameraPoseRoot) : "<deferred to forced renderer>")}', " +
                $"headWorldPos={FormatDebugVector(_priorFocusCameraWorldPosition)}, " +
                $"headWorldEuler={FormatDebugVector(_priorFocusCameraWorldRotation.eulerAngles)}.");
        }

        private static void ResetFirstPersonArmsCameraPose()
        {
            _firstPersonArmsCameraPoseCaptured = false;
            _firstPersonArmsCameraPoseRoot = null;
            _firstPersonArmsCameraFollowLogged = false;
            _firstPersonArmsPresentationFrame = -1;
            _firstPersonArmsPresentationRoot = null;
            _firstPersonArmsPresentationAnimPosition = Vector3.zero;
            _firstPersonArmsPresentationAnimRotation = Quaternion.identity;
            _firstPersonArmsPresentationFinalPosition = Vector3.zero;
            _firstPersonArmsPresentationFinalRotation = Quaternion.identity;
        }

        // Vanilla head pose for this focus session: the seated body is
        // pose-locked at the station for the whole session, so the camera
        // world pose frozen at focus start (before the focus animation moves
        // the camera) stays the head truth. The camera's parent cannot be
        // used to re-derive it live — the parent itself moves during focus,
        // so parent-relative reconstruction drifts metres off the head.
        private static bool TryGetVanillaFocusHeadPose(out Vector3 position, out Quaternion rotation)
        {
            position = _priorFocusCameraWorldPosition;
            rotation = _priorFocusCameraWorldRotation;
            return _physicalFocusViewActive;
        }

        private static Quaternion FlattenToYaw(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = rotation * Vector3.up;
                forward.y = 0f;
            }
            if (forward.sqrMagnitude < 0.0001f)
                return Quaternion.identity;
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        /// <summary>
        /// Presentation pair for the current frame, valid only when the arms
        /// root glue ran this frame: the animator-owned root pose (what any
        /// rig/animator evaluation resets the root to) and the camera-glued
        /// pose that must be re-asserted as the final transform owner.
        /// </summary>
        internal static bool TryGetFirstPersonArmsPresentationThisFrame(
            out Transform root,
            out Vector3 animPosition,
            out Quaternion animRotation,
            out Vector3 finalPosition,
            out Quaternion finalRotation)
        {
            root = _firstPersonArmsPresentationRoot;
            animPosition = _firstPersonArmsPresentationAnimPosition;
            animRotation = _firstPersonArmsPresentationAnimRotation;
            finalPosition = _firstPersonArmsPresentationFinalPosition;
            finalRotation = _firstPersonArmsPresentationFinalRotation;
            return _firstPersonArmsPresentationFrame == Time.frameCount && root != null;
        }

        private static void ApplyFirstPersonArmsOffset(LocalRendererState state)
        {
            if (state == null || !state.ApplyFirstPersonArmsOffset || state.OffsetTransform == null)
                return;

            try
            {
                Vector3 offset = GetFirstPersonArmsOffset();
                if (_firstPersonArmsCameraPoseCaptured &&
                    _physicalFocusCameraTransform != null &&
                    (_firstPersonArmsCameraPoseRoot == null ||
                     state.OffsetTransform == _firstPersonArmsCameraPoseRoot) &&
                    TryGetVanillaFocusHeadPose(out Vector3 headPosition, out Quaternion headRotation))
                {
                    Transform camera = _physicalFocusCameraTransform;
                    Transform root = state.OffsetTransform;

                    // The animator rewrites this root every frame before
                    // LateUpdate, so the current pose is the body-anchored
                    // truth — unless the glue already ran this frame, in
                    // which case reuse the stashed animator pose instead of
                    // re-reading our own write.
                    Vector3 animPosition;
                    Quaternion animRotation;
                    if (_firstPersonArmsPresentationFrame == Time.frameCount &&
                        _firstPersonArmsPresentationRoot == root)
                    {
                        animPosition = _firstPersonArmsPresentationAnimPosition;
                        animRotation = _firstPersonArmsPresentationAnimRotation;
                    }
                    else
                    {
                        animPosition = root.position;
                        animRotation = root.rotation;
                    }

                    // Yaw-only glue: the arms translate with the camera and
                    // turn with its yaw, but stay level like the vanilla
                    // body-anchored arms when the view pitches. When the
                    // camera sits at the vanilla head pose this is exactly
                    // the animator pose.
                    Quaternion cameraYaw = FlattenToYaw(camera.rotation);
                    Quaternion yawDelta = cameraYaw * Quaternion.Inverse(FlattenToYaw(headRotation));
                    Vector3 finalPosition = camera.position +
                        yawDelta * (animPosition - headPosition) +
                        cameraYaw * offset;
                    Quaternion finalRotation = yawDelta * animRotation;

                    root.SetPositionAndRotation(finalPosition, finalRotation);
                    _firstPersonArmsPresentationFrame = Time.frameCount;
                    _firstPersonArmsPresentationRoot = root;
                    _firstPersonArmsPresentationAnimPosition = animPosition;
                    _firstPersonArmsPresentationAnimRotation = animRotation;
                    _firstPersonArmsPresentationFinalPosition = finalPosition;
                    _firstPersonArmsPresentationFinalRotation = finalRotation;

                    if (!_firstPersonArmsCameraFollowLogged)
                    {
                        _firstPersonArmsCameraFollowLogged = true;
                        SurveillanceBootstrap.Log?.LogInfo(
                            "[LethalCCTV] Camera-relative first-person arms follow active " +
                            $"(head-frame yaw glue) camera-local trim {FormatDebugVector(offset)} " +
                            $"anim={FormatDebugVector(animPosition)} final={FormatDebugVector(finalPosition)}.");
                    }
                    return;
                }

                state.OffsetTransform.localPosition = state.OffsetLocalPosition + offset;
            }
            catch { }
        }

        private static void FlushFirstPersonArmsOffsetConfig()
        {
            if (!_firstPersonArmsOffsetLoaded || !_firstPersonArmsOffsetDirty)
                return;

            Vector3 rounded = new Vector3(
                RoundFirstPersonArmsOffset(_firstPersonArmsOffset.x),
                RoundFirstPersonArmsOffset(_firstPersonArmsOffset.y),
                RoundFirstPersonArmsOffset(_firstPersonArmsOffset.z));
            _firstPersonArmsOffset = rounded;
            _firstPersonArmsOffsetDirty = false;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] First-person CCTV arms offset now {FormatDebugVector(rounded)} (session-local; #575 removed its config keys).");
        }

        private static float RoundFirstPersonArmsOffset(float value)
        {
            return Mathf.Round(value * 1000f) / 1000f;
        }

        private static void ShowFirstPersonArmsOffsetStatus(bool force)
        {
            if (!force && Time.unscaledTime < _nextFirstPersonArmsOffsetStatusAt)
                return;

            _nextFirstPersonArmsOffsetStatusAt = Time.unscaledTime + FIRST_PERSON_ARMS_OFFSET_STATUS_INTERVAL;
            Vector3 offset = GetFirstPersonArmsOffset();
            ShowTimedScanStatus(
                $"ARMS {offset.x:+0.00;-0.00;0.00} {offset.y:+0.00;-0.00;0.00} {offset.z:+0.00;-0.00;0.00}",
                OVERLAY_ACTIVE_GREEN,
                0.35f);
        }

        internal static void ShowOperatorTuningStatus(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            ShowTimedScanStatus(text, OVERLAY_ACTIVE_GREEN, 0.45f);
        }

        private static void SetStationThirdPersonPreview(bool active, bool keepPersistentDebug = false)
        {
            if (_stationThirdPersonPreviewActive == active)
            {
                if (!active && !keepPersistentDebug && !_stationThirdPersonDebugPersistent)
                    RestorePreviewForcedLocalRenderers();
                Y4NGZPlayerAnimationBridge.SetLocalThirdPersonPreviewEnabled(active || _stationThirdPersonDebugPersistent);
                return;
            }

            _stationThirdPersonPreviewActive = active;
            _previewCameraFramingLogged = false;
            if (active)
            {
                ResetStationThirdPersonOrbit();
                CCTVOperatorStation.CancelDebugPlacement();
                _stationRadarLookBlend = 0f;
                ForceLocalPlayerThirdPersonPreviewRenderers(_focusedPlayer);
            }
            else
            {
                if (!keepPersistentDebug && !_stationThirdPersonDebugPersistent)
                    RestorePreviewForcedLocalRenderers();
            }

            Y4NGZPlayerAnimationBridge.SetLocalThirdPersonPreviewEnabled(active || _stationThirdPersonDebugPersistent);
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] Station third-person operator preview {(active ? "enabled" : "disabled")}.");
        }

        private static Renderer ForceLocalPlayerThirdPersonPreviewRenderers(PlayerControllerB player)
        {
            if ((!_stationThirdPersonPreviewActive && !_stationThirdPersonDebugPersistent) || player == null)
                return null;

            Renderer selected = SelectThirdPersonPreviewRenderer(player, out string selectionDetails);
            if (selected == null)
            {
                if (!_previewRendererFailureLogged)
                {
                    _previewRendererFailureLogged = true;
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV] Station third-person operator preview has no usable body renderer. " +
                        selectionDetails);
                }
                return null;
            }

            string selectedPath = GetTransformPath(selected.transform);
            if (_previewForcedLocalRenderers.Count == 1 &&
                _previewForcedLocalRenderers[0].Renderer == selected)
            {
                ReassertPreviewRendererState(selected);
                ForcePreviewCameraCanRender(selected);
                return selected;
            }

            RestorePreviewForcedLocalRenderers();
            _previewCameraFramingLogged = false;
            ForcePreviewRendererVisible(selected);
            ForcePreviewCameraCanRender(selected);
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Station third-person operator preview renderer active: " +
                $"selected='{selected.name}', path='{selectedPath}', " +
                $"mesh='{GetRendererMeshName(selected)}', materials={GetRendererMaterialCount(selected)}, " +
                FormatPreviewRendererRuntimeState(selected) + ". " +
                selectionDetails);
            return selected;
        }

        private static void ForcePreviewRendererVisible(Renderer renderer)
        {
            if (renderer == null) return;
            if (IsRendererTracked(_previewForcedLocalRenderers, renderer)) return;

            _previewForcedLocalRenderers.Add(CaptureRendererState(renderer));

            ReassertPreviewRendererState(renderer);
        }

        private static Renderer SelectThirdPersonPreviewRenderer(PlayerControllerB player, out string details)
        {
            var diagnostics = new List<string>(3);
            Renderer selected = null;
            selected = ConsiderPreviewCandidate(player.thisPlayerModel, "thisPlayerModel", selected, diagnostics);
            selected = ConsiderPreviewCandidate(player.thisPlayerModelLOD1, "thisPlayerModelLOD1", selected, diagnostics);
            selected = ConsiderPreviewCandidate(player.thisPlayerModelLOD2, "thisPlayerModelLOD2", selected, diagnostics);

            details = "candidates=[" + string.Join("; ", diagnostics.ToArray()) + "]";
            return selected;
        }

        private static Renderer ConsiderPreviewCandidate(Renderer renderer, string label, Renderer currentSelection, List<string> diagnostics)
        {
            if (diagnostics == null)
                diagnostics = new List<string>();

            if (!IsUsableThirdPersonPreviewRenderer(renderer, out string reason))
            {
                diagnostics.Add(label + ":skip:" + reason);
                return currentSelection;
            }

            diagnostics.Add(
                label + (currentSelection == null ? ":selected:" : ":not-forced:lower-priority:") +
                $"name='{renderer.name}', path='{GetTransformPath(renderer.transform)}', " +
                $"active={IsRendererActiveInHierarchy(renderer)}, enabled={renderer.enabled}, " +
                $"forceOff={GetRendererForceRenderingOff(renderer)}, shadow={renderer.shadowCastingMode}, " +
                $"lodGroup='{GetLodGroupPath(FindRendererLodGroup(renderer))}', " +
                $"mesh='{GetRendererMeshName(renderer)}', materials={GetRendererMaterialCount(renderer)}");
            return currentSelection ?? renderer;
        }

        private static bool IsUsableThirdPersonPreviewRenderer(Renderer renderer, out string reason)
        {
            reason = null;
            if (renderer == null)
            {
                reason = "null";
                return false;
            }
            if (renderer.gameObject == null)
            {
                reason = "missing-gameobject";
                return false;
            }
            if (IsLikelyLocalArmRenderer(renderer))
            {
                reason = "local-arms";
                return false;
            }

            string path = GetTransformPath(renderer.transform);
            if (ContainsLocalOnlyPreviewBlockerToken(renderer.name) ||
                ContainsLocalOnlyPreviewBlockerToken(path))
            {
                reason = "local-only-renderer";
                return false;
            }

            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null && skinned.sharedMesh == null)
            {
                reason = "missing-skinned-mesh";
                return false;
            }

            reason = "ok";
            return true;
        }

        private static bool ContainsLocalOnlyPreviewBlockerToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf("visor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("arms", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("firstperson", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("first person", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetRendererMeshName(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh != null ? skinned.sharedMesh.name : "<none>";

            MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "<none>";
        }

        private static int GetRendererMaterialCount(Renderer renderer)
        {
            Material[] materials = renderer != null ? renderer.sharedMaterials : null;
            return materials != null ? materials.Length : 0;
        }

        private static void ReassertPreviewRendererState(Renderer renderer)
        {
            if (renderer == null) return;
            DisablePreviewLodGroupForRenderer(renderer);
            try { renderer.forceRenderingOff = false; } catch { }
            try { renderer.allowOcclusionWhenDynamic = false; } catch { }
            try { renderer.receiveShadows = true; } catch { }
            try { renderer.enabled = true; } catch { }
            try { renderer.shadowCastingMode = ShadowCastingMode.On; } catch { }

            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                try { skinned.updateWhenOffscreen = true; } catch { }
            }
        }

        private static void RestorePreviewForcedLocalRenderers()
        {
            for (int i = 0; i < _previewForcedLocalRenderers.Count; i++)
            {
                LocalRendererState state = _previewForcedLocalRenderers[i];
                RestoreRendererState(state);
            }
            _previewForcedLocalRenderers.Clear();
            RestorePreviewForcedLodGroups();
            RestorePreviewCameraCullingMask();
            _previewRendererFailureLogged = false;
        }

        private static void AddLocalObstructorRenderer(Renderer renderer)
        {
            if (renderer == null) return;
            // Dedicated interaction viewmodels are parented below the gameplay camera, which is
            // itself below playerBodyAnimator. The broad player-body renderer sweep above therefore
            // sees them as player obstructors unless they are explicitly excluded. The generic
            // presenter owns their visibility for the lifetime of the interaction.
            if (IsDedicatedLocalViewmodelRenderer(renderer)) return;
            if (IsRendererTracked(_focusForcedLocalRenderers, renderer)) return;

            if (IsRendererTracked(_focusHiddenLocalRenderers, renderer)) return;

            _focusHiddenLocalRenderers.Add(CaptureRendererState(renderer));

            try { renderer.enabled = false; } catch { }
        }

        private static bool IsDedicatedLocalViewmodelRenderer(Renderer renderer)
        {
            Transform current = renderer != null ? renderer.transform : null;
            while (current != null)
            {
                if (current.name.StartsWith("Y4NGZ_Viewmodel_", StringComparison.Ordinal))
                    return true;
                current = current.parent;
            }
            return false;
        }

        private static void ForceLocalRendererVisible(PlayerControllerB player, Renderer renderer)
        {
            if (renderer == null) return;

            LocalRendererState state = FindRendererState(_focusForcedLocalRenderers, renderer);
            LocalRendererState hiddenState = FindRendererState(_focusHiddenLocalRenderers, renderer);
            if (hiddenState != null)
            {
                // Begin-window arms hide (Test 36 Issue C): the settle put the
                // real FP arms in the hidden set; arming moves the ORIGINAL
                // captured state into the forced list so the eventual restore
                // still replays the true pre-focus renderer state. Re-capturing
                // here would record enabled=false as the restore state, and the
                // old early-return left the arms invisible for the whole
                // session (the Test-22 half-armed hazard).
                _focusHiddenLocalRenderers.Remove(hiddenState);
                if (state == null)
                {
                    state = hiddenState;
                    _focusForcedLocalRenderers.Add(state);
                }
            }
            if (state == null)
            {
                state = CaptureRendererState(renderer);
                _focusForcedLocalRenderers.Add(state);
            }

            Transform offsetRoot = ResolveFirstPersonArmsOffsetRoot(player, renderer);
            if (offsetRoot != null &&
                (!state.ApplyFirstPersonArmsOffset || state.OffsetTransform != offsetRoot) &&
                !IsFirstPersonArmsOffsetRootTracked(offsetRoot))
            {
                state.ApplyFirstPersonArmsOffset = true;
                state.OffsetTransform = offsetRoot;
                try { state.OffsetLocalPosition = offsetRoot.localPosition; } catch { }
                try { state.OffsetLocalRotation = offsetRoot.localRotation; } catch { }
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] First-person CCTV arms offset target: " +
                    $"root='{GetTransformPath(offsetRoot)}', renderer='{GetTransformPath(renderer.transform)}'.");
            }

            try { renderer.enabled = true; } catch { }
            ApplyFirstPersonArmsOffset(state);
        }

        private static LocalRendererState FindRendererState(
            List<LocalRendererState> states,
            Renderer renderer)
        {
            if (states == null || renderer == null)
                return null;
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i]?.Renderer == renderer)
                    return states[i];
            }
            return null;
        }

        private static LocalRendererState CaptureRendererState(Renderer renderer)
        {
            var state = new LocalRendererState
            {
                Renderer = renderer,
                WasEnabled = renderer != null && renderer.enabled,
                SharedMaterials = renderer != null ? renderer.sharedMaterials : null,
                ShadowCastingMode = renderer != null ? renderer.shadowCastingMode : ShadowCastingMode.Off,
                Transform = renderer != null ? renderer.transform : null,
            };

            if (renderer != null)
            {
                try { state.ReceiveShadows = renderer.receiveShadows; } catch { }
                try { state.ForceRenderingOff = renderer.forceRenderingOff; } catch { }
                try { state.AllowOcclusionWhenDynamic = renderer.allowOcclusionWhenDynamic; } catch { }
                try { state.LocalPosition = renderer.transform.localPosition; } catch { }
            }

            SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
            if (skinned != null)
            {
                state.HasUpdateWhenOffscreen = true;
                state.UpdateWhenOffscreen = skinned.updateWhenOffscreen;
            }

            return state;
        }

        private static void RestoreRendererState(LocalRendererState state)
        {
            if (state?.Renderer == null) return;

            Renderer renderer = state.Renderer;
            if (state.ApplyFirstPersonArmsOffset && state.OffsetTransform != null)
            {
                try { state.OffsetTransform.localPosition = state.OffsetLocalPosition; } catch { }
                try { state.OffsetTransform.localRotation = state.OffsetLocalRotation; } catch { }
            }
            try { renderer.enabled = state.WasEnabled; } catch { }
            try { renderer.shadowCastingMode = state.ShadowCastingMode; } catch { }
            try { renderer.receiveShadows = state.ReceiveShadows; } catch { }
            try { renderer.forceRenderingOff = state.ForceRenderingOff; } catch { }
            try { renderer.allowOcclusionWhenDynamic = state.AllowOcclusionWhenDynamic; } catch { }
            try
            {
                if (state.SharedMaterials != null)
                    renderer.sharedMaterials = state.SharedMaterials;
            }
            catch { }

            if (state.HasUpdateWhenOffscreen && renderer is SkinnedMeshRenderer skinned)
            {
                try { skinned.updateWhenOffscreen = state.UpdateWhenOffscreen; } catch { }
            }
        }

        private static bool IsRendererTracked(List<LocalRendererState> states, Renderer renderer)
        {
            if (states == null || renderer == null) return false;
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].Renderer == renderer)
                    return true;
            }
            return false;
        }

        private static Transform ResolveFirstPersonArmsOffsetRoot(PlayerControllerB player, Renderer renderer)
        {
            if (!IsRealLocalFirstPersonArmRenderer(player, renderer))
                return null;

            Transform current = renderer != null ? renderer.transform : null;
            while (current != null)
            {
                if (string.Equals(current.name, "ScavengerModelArmsOnly", StringComparison.OrdinalIgnoreCase))
                    return current;
                current = current.parent;
            }

            Renderer armsRenderer = player != null ? player.thisPlayerModelArms : null;
            current = armsRenderer != null ? armsRenderer.transform : null;
            while (current != null)
            {
                if (string.Equals(current.name, "ScavengerModelArmsOnly", StringComparison.OrdinalIgnoreCase))
                    return current;
                current = current.parent;
            }

            return renderer != null ? renderer.transform : null;
        }

        private static bool IsFirstPersonArmsOffsetRootTracked(Transform root)
        {
            if (root == null)
                return true;

            for (int i = 0; i < _focusForcedLocalRenderers.Count; i++)
            {
                LocalRendererState state = _focusForcedLocalRenderers[i];
                if (state != null && state.ApplyFirstPersonArmsOffset && state.OffsetTransform == root)
                    return true;
            }

            return false;
        }

        private static void ForcePreviewCameraCanRender(Renderer renderer)
        {
            if (_physicalFocusCamera == null || renderer == null || renderer.gameObject == null)
                return;

            int layer = renderer.gameObject.layer;
            if (layer < 0 || layer > 31)
                return;

            if (!_previewCameraCullingMaskSaved || _previewForcedCamera != _physicalFocusCamera)
            {
                if (_previewCameraCullingMaskSaved && _previewForcedCamera != null)
                    RestorePreviewCameraCullingMask();

                _previewForcedCamera = _physicalFocusCamera;
                _previewSavedCameraCullingMask = _physicalFocusCamera.cullingMask;
                _previewCameraCullingMaskSaved = true;
            }

            int layerMask = 1 << layer;
            try
            {
                if ((_physicalFocusCamera.cullingMask & layerMask) == 0)
                    _physicalFocusCamera.cullingMask |= layerMask;
            }
            catch { }
        }

        private static void RestorePreviewCameraCullingMask()
        {
            if (_previewCameraCullingMaskSaved && _previewForcedCamera != null)
            {
                try { _previewForcedCamera.cullingMask = _previewSavedCameraCullingMask; } catch { }
            }

            _previewForcedCamera = null;
            _previewSavedCameraCullingMask = 0;
            _previewCameraCullingMaskSaved = false;
        }

        private static LODGroup DisablePreviewLodGroupForRenderer(Renderer renderer)
        {
            LODGroup group = FindRendererLodGroup(renderer);
            if (group == null)
                return null;

            if (!IsLodGroupTracked(_previewForcedLodGroups, group))
            {
                bool wasEnabled = false;
                try { wasEnabled = group.enabled; } catch { }
                _previewForcedLodGroups.Add(new LocalLodGroupState
                {
                    Group = group,
                    WasEnabled = wasEnabled,
                });
            }

            try
            {
                if (group.enabled)
                    group.enabled = false;
            }
            catch { }

            return group;
        }

        private static void RestorePreviewForcedLodGroups()
        {
            for (int i = 0; i < _previewForcedLodGroups.Count; i++)
            {
                LocalLodGroupState state = _previewForcedLodGroups[i];
                if (state?.Group == null) continue;
                try { state.Group.enabled = state.WasEnabled; } catch { }
            }
            _previewForcedLodGroups.Clear();
        }

        private static bool IsLodGroupTracked(List<LocalLodGroupState> states, LODGroup group)
        {
            if (states == null || group == null) return false;
            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].Group == group)
                    return true;
            }
            return false;
        }

        private static LODGroup FindRendererLodGroup(Renderer renderer)
        {
            if (renderer == null) return null;
            try { return renderer.GetComponentInParent<LODGroup>(); } catch { return null; }
        }

        private static string GetLodGroupPath(LODGroup group)
        {
            return group != null ? GetTransformPath(group.transform) : "<none>";
        }

        private static bool IsRendererActiveInHierarchy(Renderer renderer)
        {
            try { return renderer != null && renderer.gameObject != null && renderer.gameObject.activeInHierarchy; }
            catch { return false; }
        }

        private static bool GetRendererForceRenderingOff(Renderer renderer)
        {
            try { return renderer != null && renderer.forceRenderingOff; }
            catch { return false; }
        }

        private static bool GetRendererAllowOcclusionWhenDynamic(Renderer renderer)
        {
            try { return renderer != null && renderer.allowOcclusionWhenDynamic; }
            catch { return false; }
        }

        private static bool GetRendererReceiveShadows(Renderer renderer)
        {
            try { return renderer != null && renderer.receiveShadows; }
            catch { return false; }
        }

        private static string FormatPreviewRendererRuntimeState(Renderer renderer)
        {
            LODGroup group = FindRendererLodGroup(renderer);
            string boundsText = TryGetPreviewRendererBounds(renderer, out Bounds bounds)
                ? $"boundsCenter={FormatDebugVector(bounds.center)}, boundsSize={FormatDebugVector(bounds.size)}, "
                : "bounds=<invalid>, ";
            int layer = renderer != null && renderer.gameObject != null ? renderer.gameObject.layer : -1;
            return
                $"active={IsRendererActiveInHierarchy(renderer)}, enabled={renderer != null && renderer.enabled}, " +
                $"layer={layer}, " +
                $"forceOff={GetRendererForceRenderingOff(renderer)}, " +
                $"receiveShadows={GetRendererReceiveShadows(renderer)}, " +
                $"occlusion={GetRendererAllowOcclusionWhenDynamic(renderer)}, " +
                boundsText +
                $"lodGroup='{GetLodGroupPath(group)}', lodGroupDisabledForPreview={(group != null)}";
        }

        private static bool TryGetPreviewRendererBounds(Renderer renderer, out Bounds bounds)
        {
            bounds = default;
            if (renderer == null)
                return false;

            try { bounds = renderer.bounds; }
            catch { return false; }

            return IsFinite(bounds.center) &&
                   IsFinite(bounds.size) &&
                   bounds.size.sqrMagnitude > 0.0001f &&
                   bounds.extents.sqrMagnitude > 0.000025f;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void LogStationThirdPersonPreviewFraming(
            Renderer renderer,
            bool framedRendererBounds,
            Bounds bounds,
            Vector3 target,
            Vector3 cameraPosition)
        {
            if (_previewCameraFramingLogged)
                return;

            _previewCameraFramingLogged = true;
            if (_physicalFocusCamera == null || renderer == null)
                return;

            string viewportText = "<none>";
            string frustumText = "<none>";
            try
            {
                Vector3 viewport = _physicalFocusCamera.WorldToViewportPoint(target);
                viewportText = FormatDebugVector(viewport);
                frustumText = framedRendererBounds
                    ? GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(_physicalFocusCamera), bounds).ToString()
                    : "<no-bounds>";
            }
            catch { }

            int layer = renderer.gameObject != null ? renderer.gameObject.layer : -1;
            bool cameraHasLayer = layer >= 0 && (_physicalFocusCamera.cullingMask & (1 << layer)) != 0;
            string boundsText = framedRendererBounds
                ? $"boundsCenter={FormatDebugVector(bounds.center)} boundsSize={FormatDebugVector(bounds.size)}"
                : "bounds=<invalid>";
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV] Station third-person operator preview camera framed: " +
                $"renderer='{renderer.name}', path='{GetTransformPath(renderer.transform)}', " +
                $"usingRendererBounds={framedRendererBounds}, {boundsText}, " +
                $"target={FormatDebugVector(target)}, camera={FormatDebugVector(cameraPosition)}, " +
                $"distance={Vector3.Distance(cameraPosition, target):F2}, viewport={viewportText}, " +
                $"frustumContainsBounds={frustumText}, rendererLayer={layer}, cameraHasLayer={cameraHasLayer}, " +
                $"cameraMask=0x{_physicalFocusCamera.cullingMask:X8}, fov={_physicalFocusCamera.fieldOfView:F1}, " +
                $"near={_physicalFocusCamera.nearClipPlane:F2}, far={_physicalFocusCamera.farClipPlane:F1}");
        }

        private static bool IsLikelyLocalArmRenderer(Renderer renderer)
        {
            if (renderer == null) return false;
            string name = renderer.name ?? string.Empty;
            string path = GetTransformPath(renderer.transform);
            return ContainsArmToken(name) || ContainsArmToken(path);
        }

        private static bool ContainsArmToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string lower = value.ToLowerInvariant();
            return lower.IndexOf("arm", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("hand", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("glove", StringComparison.Ordinal) >= 0 ||
                   lower.IndexOf("sleeve", StringComparison.Ordinal) >= 0;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            string path = transform.name ?? string.Empty;
            Transform current = transform.parent;
            int depth = 0;
            while (current != null && depth++ < 24)
            {
                path = (current.name ?? string.Empty) + "/" + path;
                current = current.parent;
            }
            return path;
        }

        private static string FormatDebugVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        // Bodycam mods, animation bridges, and vanilla paths re-enable player
        // renderers mid-focus, which shows up as the mask/body flickering into the
        // focus view. Re-asserting the saved states is a handful of bool compares
        // per frame — cheap insurance against every third-party writer.
        internal static void RetainLocalPlayerVisualStateForControllerRestore(PlayerControllerB player)
        {
            if (player == null)
                return;

            if (_controllerRestorePlayer == player &&
                (_controllerRestoreLocalRenderers.Count > 0 || _controllerRestoreLocalVisor != null))
            {
                return;
            }

            if (_controllerRestoreLocalRenderers.Count > 0 || _controllerRestoreLocalVisor != null)
                RestoreRetainedLocalPlayerVisualState();

            _controllerRestorePlayer = player;
            for (int i = 0; i < _focusHiddenLocalRenderers.Count; i++)
                AddControllerRestoreRendererState(_focusHiddenLocalRenderers[i]);
            for (int i = 0; i < _focusForcedLocalRenderers.Count; i++)
                AddControllerRestoreRendererState(_focusForcedLocalRenderers[i]);

            _controllerRestoreLocalVisor = _focusHiddenLocalVisor;
            _controllerRestoreLocalVisorLocalPosition = _focusHiddenLocalVisorLocalPosition;
            _controllerRestoreLocalVisorLocalRotation = _focusHiddenLocalVisorLocalRotation;
            _controllerRestoreLocalVisorLocalScale = _focusHiddenLocalVisorLocalScale;

            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][ExitVisualRestore] Retained focus-start visual state until controller restore: " +
                $"renderers={_controllerRestoreLocalRenderers.Count}, visor={_controllerRestoreLocalVisor != null}.");
        }

        internal static void RestoreStationPoseForImmediateControllerRestore(
            PlayerControllerB player,
            string reason)
        {
            if (!_stationPlayerPoseLockActive || player == null || _stationPlayerPosePlayer != player)
                return;

            RestoreStationPlayerPoseTracking("immediate-controller-restore-" + reason);
        }

        internal static void ReassertCameraStabilizerAfterImmediateControllerRestore(
            PlayerControllerB player,
            string reason)
        {
            if (player == null || player.gameplayCamera == null)
                return;

            try
            {
                CCTVLocalCameraPositionStabilizer stabilizer =
                    player.gameplayCamera.GetComponent<CCTVLocalCameraPositionStabilizer>();
                if (stabilizer == null)
                    return;

                stabilizer.ApplyNow();
                stabilizer.ReleaseAfterLateUpdates(2);
                SurveillanceBootstrap.Log?.LogInfo(
                    "[LethalCCTV] Station camera baseline guard reasserted after immediate controller restore " +
                    $"({reason}).");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Immediate controller-restore camera guard failed ({reason}): {ex.Message}");
            }
        }

        internal static void RestoreLocalPlayerVisualStateAtControllerRestore(PlayerControllerB player)
        {
            int activeHidden = _focusHiddenLocalRenderers.Count;
            int activeForced = _focusForcedLocalRenderers.Count;
            RestoreLocalPlayerObstructors("controller-restore");

            int retained = _controllerRestoreLocalRenderers.Count;
            bool retainedVisor = _controllerRestoreLocalVisor != null;
            bool playerMatches = player == null || _controllerRestorePlayer == null || _controllerRestorePlayer == player;
            RestoreRetainedLocalPlayerVisualState();

            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][ExitVisualRestore] Controller-restore visual state applied: " +
                $"activeObstructors={activeHidden}, activeForced={activeForced}, " +
                $"retainedRenderers={retained}, retainedVisor={retainedVisor}, playerMatches={playerMatches}.");
        }

        internal static void GetLocalRendererBookkeeping(
            Renderer renderer,
            out bool forcedVisible,
            out bool obstructor)
        {
            forcedVisible = IsRendererTracked(_focusForcedLocalRenderers, renderer);
            obstructor = IsRendererTracked(_focusHiddenLocalRenderers, renderer);
        }

        internal static bool TryGetFirstPersonArmsFocusStartRootPose(
            Transform root,
            out Vector3 localPosition,
            out Quaternion localRotation)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            if (root == null)
                return false;

            for (int i = 0; i < _focusForcedLocalRenderers.Count; i++)
            {
                LocalRendererState state = _focusForcedLocalRenderers[i];
                if (state == null || !state.ApplyFirstPersonArmsOffset || state.OffsetTransform != root)
                    continue;
                localPosition = state.OffsetLocalPosition;
                localRotation = state.OffsetLocalRotation;
                return true;
            }

            return false;
        }

        private static void AddControllerRestoreRendererState(LocalRendererState state)
        {
            if (state?.Renderer == null)
                return;
            for (int i = 0; i < _controllerRestoreLocalRenderers.Count; i++)
            {
                if (_controllerRestoreLocalRenderers[i]?.Renderer == state.Renderer)
                    return;
            }
            _controllerRestoreLocalRenderers.Add(state);
        }

        private static void RestoreRetainedLocalPlayerVisualState()
        {
            for (int i = 0; i < _controllerRestoreLocalRenderers.Count; i++)
                RestoreRendererState(_controllerRestoreLocalRenderers[i]);
            _controllerRestoreLocalRenderers.Clear();

            if (_controllerRestoreLocalVisor != null)
            {
                try
                {
                    _controllerRestoreLocalVisor.localPosition = _controllerRestoreLocalVisorLocalPosition;
                    _controllerRestoreLocalVisor.localRotation = _controllerRestoreLocalVisorLocalRotation;
                    _controllerRestoreLocalVisor.localScale = _controllerRestoreLocalVisorLocalScale;
                }
                catch { }
            }

            _controllerRestorePlayer = null;
            _controllerRestoreLocalVisor = null;
            _controllerRestoreLocalVisorLocalPosition = Vector3.zero;
            _controllerRestoreLocalVisorLocalRotation = Quaternion.identity;
            _controllerRestoreLocalVisorLocalScale = Vector3.one;
        }

        private static void ReassertLocalPlayerObstructors()
        {
            for (int i = 0; i < _focusHiddenLocalRenderers.Count; i++)
            {
                LocalRendererState state = _focusHiddenLocalRenderers[i];
                if (state?.Renderer == null) continue;
                try { if (state.Renderer.enabled) state.Renderer.enabled = false; } catch { }
            }

            for (int i = 0; i < _focusForcedLocalRenderers.Count; i++)
            {
                LocalRendererState state = _focusForcedLocalRenderers[i];
                if (state?.Renderer == null) continue;
                try { if (!state.Renderer.enabled) state.Renderer.enabled = true; } catch { }
                ApplyFirstPersonArmsOffset(state);
            }

            for (int i = 0; i < _previewForcedLocalRenderers.Count; i++)
            {
                LocalRendererState state = _previewForcedLocalRenderers[i];
                if (state?.Renderer == null) continue;
                ReassertPreviewRendererState(state.Renderer);
            }

            if (_focusHiddenLocalVisor != null)
            {
                try { _focusHiddenLocalVisor.position = _focusHiddenLocalVisorHiddenWorldPosition; } catch { }
            }

            // LethalPhones re-shows the phone from its own per-frame input
            // handling whenever the owner toggles it, so the hidden state has to
            // be re-asserted alongside the vanilla obstructors.
            LethalPhonesCompat.ReassertLocalPhoneSuppression();
        }

        private static void RestoreLocalPlayerObstructors(string reason)
        {
            LogLocalObstructorCallOnce("restore", reason);
            LethalPhonesCompat.RestoreLocalPhone(reason);
            FinishFirstPersonArmsOffsetEditing();
            RestorePreviewForcedLocalRenderers();

            for (int i = 0; i < _focusHiddenLocalRenderers.Count; i++)
            {
                LocalRendererState state = _focusHiddenLocalRenderers[i];
                RestoreRendererState(state);
            }
            _focusHiddenLocalRenderers.Clear();

            for (int i = 0; i < _focusForcedLocalRenderers.Count; i++)
            {
                LocalRendererState state = _focusForcedLocalRenderers[i];
                RestoreRendererState(state);
            }
            _focusForcedLocalRenderers.Clear();

            if (_focusHiddenLocalVisor != null)
            {
                try
                {
                    _focusHiddenLocalVisor.localPosition = _focusHiddenLocalVisorLocalPosition;
                    _focusHiddenLocalVisor.localRotation = _focusHiddenLocalVisorLocalRotation;
                    _focusHiddenLocalVisor.localScale = _focusHiddenLocalVisorLocalScale;
                }
                catch { }
            }

            _focusHiddenLocalVisor = null;
            _focusHiddenLocalVisorLocalPosition = Vector3.zero;
            _focusHiddenLocalVisorLocalRotation = Quaternion.identity;
            _focusHiddenLocalVisorLocalScale = Vector3.one;
            _focusHiddenLocalVisorHiddenWorldPosition = Vector3.zero;
            _holdLocalObstructorsForInteractionsApiRestore = false;
        }

        internal static void ReleaseLocalPlayerObstructorsAfterInteractionsApiRestore(
            PlayerControllerB player,
            string reason)
        {
            bool hadSuppression = _holdLocalObstructorsForInteractionsApiRestore ||
                HasActiveLocalPlayerObstructors();
            RestoreLocalPlayerObstructors("interactions-api-restored:" + reason);
            if (hadSuppression)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] obstructor suppression released reason={reason}.");
            }
        }

        // Only reached while the Interactions API restore hold is armed
        // (MonitorFocus.StationCamera). The old delayed post-focus restore path
        // was removed with its scheduler; nothing else arms this tick.
        private static void TickPendingLocalObstructorRestore()
        {
            if (_holdLocalObstructorsForInteractionsApiRestore)
                ReassertLocalPlayerObstructors();
        }
    }
}
