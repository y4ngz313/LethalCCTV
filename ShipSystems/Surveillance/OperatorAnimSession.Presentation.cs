using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal sealed partial class OperatorAnimSession
    {
        internal static void CaptureInteractionBeginPresentation(PlayerControllerB player)
        {
            AbortInteractionBeginPresentation(null, "interaction-begin-replaced");
            if (player == null)
                return;

            PlayerControllerB localPlayer = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (localPlayer != null && !ReferenceEquals(localPlayer, player))
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV] Interaction-begin presentation capture rejected for a non-local player.");
                return;
            }

            var pending = new InteractionBeginPresentationState
            {
                Player = player,
            };

            CaptureInteractionBeginRightArmPresentation(pending);
            CaptureInteractionBeginHeadPresentation(pending);
            _interactionBeginPresentation =
                pending.RightShoulderCaptured || pending.HeadCaptured
                    ? pending
                    : null;
        }

        internal static void AbortInteractionBeginPresentation(
            PlayerControllerB player,
            string reason)
        {
            InteractionBeginPresentationState pending = _interactionBeginPresentation;
            if (pending == null ||
                (player != null && !ReferenceEquals(player, pending.Player)))
            {
                return;
            }

            RestorePendingRightArmPresentation(pending, reason);
            RestorePendingHeadPresentation(pending, reason);
            _interactionBeginPresentation = null;
        }

        private static void CaptureInteractionBeginRightArmPresentation(
            InteractionBeginPresentationState pending)
        {
            Transform shoulder = ResolveLocalFirstPersonRightShoulder(pending.Player);
            if (shoulder == null)
                return;

            pending.RightShoulder = shoulder;
            pending.RightShoulderScale = shoulder.localScale;
            pending.RightShoulderCaptured = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][RightArmHide] cached local arms-only shoulder scale: " +
                $"shoulder={GetTransformPath(shoulder)} " +
                "verifiedUnderScavengerModelArmsOnly=True " +
                $"cachedLocalScale={FormatTraceVector(pending.RightShoulderScale)}.");

            if (!Y4NGZPlayerAnimationBridge.HideRightFirstPersonArmDuringEnterAndExit)
                return;

            try
            {
                shoulder.localScale = Vector3.one * PresentationHiddenScale;
                pending.RightShoulderHidden = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][RightArmHide] frame={Time.frameCount} reason=interaction-begin " +
                    $"cachedLocalScale={FormatTraceVector(pending.RightShoulderScale)} " +
                    $"appliedLocalScale={FormatTraceVector(shoulder.localScale)}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RightArmHide] interaction-begin hide failed: {ex.Message}");
            }
        }

        private static void CaptureInteractionBeginHeadPresentation(
            InteractionBeginPresentationState pending)
        {
            if (!Y4NGZPlayerAnimationBridge.UseInteractionsApiOperatorSession ||
                !Y4NGZPlayerAnimationBridge.HideLocalPlayerHeadDuringApiSession)
            {
                return;
            }

            Transform head = ResolveLocalThirdPersonHeadBone(pending.Player);
            if (head == null)
                return;

            Vector3 capturedLocalScale = head.localScale;
            if (!TryResolveHeadPresentationBaseline(
                    head,
                    capturedLocalScale,
                    "interaction-begin",
                    out Vector3 baselineLocalScale))
            {
                return;
            }

            pending.HeadBone = head;
            pending.HeadScale = baselineLocalScale;
            pending.HeadCaptured = true;
            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][HeadHide] cached local third-person head scale: " +
                $"head={GetTransformPath(head)} " +
                $"cachedLocalScale={FormatTraceVector(pending.HeadScale)}.");

            try
            {
                head.localScale = Vector3.one * PresentationHiddenScale;
                pending.HeadHidden = true;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][HeadHide] frame={Time.frameCount} reason=interaction-begin " +
                    $"cachedLocalScale={FormatTraceVector(pending.HeadScale)} " +
                    $"appliedLocalScale={FormatTraceVector(head.localScale)}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][HeadHide] interaction-begin hide failed: {ex.Message}");
            }
        }

        private static Transform ResolveLocalFirstPersonRightShoulder(
            PlayerControllerB player)
        {
            Transform armsRoot = player != null ? player.localArmsTransform : null;
            if (armsRoot == null && player?.playerBodyAnimator != null)
                armsRoot = player.playerBodyAnimator.transform.Find("ScavengerModelArmsOnly");

            Transform shoulder = armsRoot != null
                ? armsRoot.Find("metarig/spine.003/shoulder.R") ??
                  FindChildRecursive(armsRoot, "shoulder.R")
                : null;
            bool verifiedUnderArmsOnly = shoulder != null &&
                HasAncestorOrSelfNamed(shoulder, "ScavengerModelArmsOnly");
            bool verifiedName = shoulder != null &&
                string.Equals(shoulder.name, "shoulder.R", StringComparison.Ordinal);
            if (verifiedUnderArmsOnly && verifiedName)
                return shoulder;

            SurveillanceBootstrap.Log?.LogWarning(
                "[LethalCCTV][RightArmHide] shoulder.R interaction-begin cache rejected: " +
                $"shoulder={GetTransformPath(shoulder)} " +
                $"armsOnlyRoot={GetTransformPath(armsRoot)} " +
                $"verifiedUnderScavengerModelArmsOnly={verifiedUnderArmsOnly} " +
                $"verifiedName={verifiedName}.");
            return null;
        }

        private static Transform ResolveLocalThirdPersonHeadBone(PlayerControllerB player)
        {
            Transform animatorRoot = player?.playerBodyAnimator != null
                ? player.playerBodyAnimator.transform
                : null;
            Transform armsRoot = player != null ? player.localArmsTransform : null;
            Transform bodyRenderer = player?.thisPlayerModel != null
                ? player.thisPlayerModel.transform
                : null;
            Transform scavengerModel = FindAncestorOrSelfNamed(animatorRoot, "ScavengerModel") ??
                FindAncestorOrSelfNamed(armsRoot, "ScavengerModel") ??
                FindAncestorOrSelfNamed(bodyRenderer, "ScavengerModel") ??
                FindDirectChild(player != null ? player.transform : null, "ScavengerModel");
            if (scavengerModel == null ||
                HasAncestorOrSelfNamed(scavengerModel, "ScavengerModelArmsOnly"))
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV][HeadHide] local third-person ScavengerModel root was not found.");
                return null;
            }

            Transform current = scavengerModel;
            for (int i = 0; i < LocalThirdPersonHeadSpineChain.Length; i++)
            {
                string expected = LocalThirdPersonHeadSpineChain[i];
                current = FindDirectChild(current, expected);
                if (current == null)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        "[LethalCCTV][HeadHide] local third-person spine walk failed: " +
                        $"root={GetTransformPath(scavengerModel)} missingNode={expected} chainIndex={i}.");
                    return null;
                }
            }

            bool verifiedLocalThirdPerson = current.IsChildOf(scavengerModel) &&
                !HasAncestorOrSelfNamed(current, "ScavengerModelArmsOnly");
            if (!verifiedLocalThirdPerson)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    "[LethalCCTV][HeadHide] resolved head failed local third-person verification: " +
                    $"head={GetTransformPath(current)} root={GetTransformPath(scavengerModel)}.");
                return null;
            }

            SurveillanceBootstrap.Log?.LogInfo(
                "[LethalCCTV][HeadHide] resolved local third-person head bone: " +
                $"path={GetTransformPath(current)} " +
                $"matchesPlayerGlobalHead={ReferenceEquals(current, player.playerGlobalHead)}.");
            return current;
        }

        private static Transform FindDirectChild(Transform parent, string name)
        {
            if (parent == null)
                return null;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child != null && string.Equals(child.name, name, StringComparison.Ordinal))
                    return child;
            }
            return null;
        }

        private static Transform FindAncestorOrSelfNamed(Transform transform, string name)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
                    return current;
            }
            return null;
        }

        private static bool HasAncestorOrSelfNamed(Transform transform, string name)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (string.Equals(current.name, name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static void RestorePendingRightArmPresentation(
            InteractionBeginPresentationState pending,
            string reason)
        {
            if (!pending.RightShoulderCaptured || pending.RightShoulder == null)
                return;

            try
            {
                pending.RightShoulder.localScale = pending.RightShoulderScale;
                if (pending.RightShoulderHidden)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][RightArmHide] frame={Time.frameCount} reason={reason} " +
                        $"cachedLocalScale={FormatTraceVector(pending.RightShoulderScale)} " +
                        $"appliedLocalScale={FormatTraceVector(pending.RightShoulder.localScale)}.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RightArmHide] pending scale restore failed reason={reason}: {ex.Message}");
            }
        }

        private static void RestorePendingHeadPresentation(
            InteractionBeginPresentationState pending,
            string reason)
        {
            if (!pending.HeadCaptured || pending.HeadBone == null)
                return;

            try
            {
                pending.HeadBone.localScale = pending.HeadScale;
                if (pending.HeadHidden)
                {
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV][HeadHide] frame={Time.frameCount} reason={reason} " +
                        $"cachedLocalScale={FormatTraceVector(pending.HeadScale)} " +
                        $"appliedLocalScale={FormatTraceVector(pending.HeadBone.localScale)}.");
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][HeadHide] pending scale restore failed reason={reason}: {ex.Message}");
            }
        }

        private static void RestorePendingHeadPresentationForApiFallback(
            PlayerControllerB player)
        {
            RestorePendingHeadPresentationForPlayer(player, "api-fallback");
        }

        /// <summary>
        /// #593: mirror of the head restore for the right-arm hide. The
        /// interaction-begin hide collapses `shoulder.R` to ~0 before the API
        /// is even asked, and only the API branch lifts it again (the #576
        /// start-window lift, then <see cref="CaptureRightArmPresentationScale"/>
        /// adopting the cache). On the fallback branch the collapse survived
        /// into <see cref="CaptureManualArmIkChainsBeforeControllerSwap"/>,
        /// which read degenerate bone lengths and logged
        /// 'ManualIK arm=R pre-swap chain has invalid lengths'. Restoring here
        /// hands the legacy capture a real pose; the legacy path re-applies its
        /// own enter hide immediately afterwards.
        /// </summary>
        private static void RestorePendingRightArmPresentationForApiFallback(
            PlayerControllerB player)
        {
            RestorePendingRightArmPresentationForPlayer(player, "api-fallback");
        }

        private static void RestorePendingRightArmPresentationForPlayer(
            PlayerControllerB player,
            string reason)
        {
            InteractionBeginPresentationState pending = _interactionBeginPresentation;
            if (pending == null || !ReferenceEquals(pending.Player, player) ||
                !pending.RightShoulderCaptured)
            {
                return;
            }

            RestorePendingRightArmPresentation(pending, reason);
            ClearPendingRightArmPresentation(pending);
        }

        private static void ClearPendingRightArmPresentation(
            InteractionBeginPresentationState pending)
        {
            pending.RightShoulder = null;
            pending.RightShoulderScale = Vector3.one;
            pending.RightShoulderCaptured = false;
            pending.RightShoulderHidden = false;
            ClearConsumedInteractionBeginPresentation(pending);
        }

        private static void RestorePendingHeadPresentationForPlayer(
            PlayerControllerB player,
            string reason)
        {
            InteractionBeginPresentationState pending = _interactionBeginPresentation;
            if (pending == null || !ReferenceEquals(pending.Player, player) ||
                !pending.HeadCaptured)
            {
                return;
            }

            RestorePendingHeadPresentation(pending, reason);
            ClearPendingHeadPresentation(pending);
        }

        private static void ClearPendingHeadPresentation(
            InteractionBeginPresentationState pending)
        {
            pending.HeadBone = null;
            pending.HeadScale = Vector3.one;
            pending.HeadCaptured = false;
            pending.HeadHidden = false;
            ClearConsumedInteractionBeginPresentation(pending);
        }

        private bool TryAdoptInteractionBeginRightArmPresentation()
        {
            InteractionBeginPresentationState pending = _interactionBeginPresentation;
            if (!_isLocal || pending == null || !ReferenceEquals(pending.Player, Player) ||
                !pending.RightShoulderCaptured || pending.RightShoulder == null)
            {
                return false;
            }

            _rightArmPresentationShoulder = pending.RightShoulder;
            _rightArmPresentationCachedLocalScale = pending.RightShoulderScale;
            _rightArmPresentationScaleCaptured = true;
            _rightArmPresentationHidden = pending.RightShoulderHidden;
            pending.RightShoulder = null;
            pending.RightShoulderScale = Vector3.one;
            pending.RightShoulderCaptured = false;
            pending.RightShoulderHidden = false;
            ClearConsumedInteractionBeginPresentation(pending);
            return true;
        }

        private bool TryAdoptInteractionBeginHeadPresentation()
        {
            InteractionBeginPresentationState pending = _interactionBeginPresentation;
            if (!_isLocal || pending == null || !ReferenceEquals(pending.Player, Player) ||
                !pending.HeadCaptured || pending.HeadBone == null)
            {
                return false;
            }

            _headPresentationBone = pending.HeadBone;
            _headPresentationCachedLocalScale = pending.HeadScale;
            _headPresentationScaleCaptured = true;
            _headPresentationHidden = pending.HeadHidden;
            ClearPendingHeadPresentation(pending);
            return true;
        }

        private static bool TryResolveHeadPresentationBaseline(
            Transform head,
            Vector3 capturedLocalScale,
            string reason,
            out Vector3 baselineLocalScale)
        {
            baselineLocalScale = capturedLocalScale;
            if (!IsNearlyZeroPresentationScale(capturedLocalScale))
            {
                _lastKnownHeadPresentationBone = head;
                _lastKnownHeadPresentationLocalScale = capturedLocalScale;
                _lastKnownHeadPresentationScaleValid = true;
                return true;
            }

            bool canReusePrevious =
                _lastKnownHeadPresentationScaleValid &&
                ReferenceEquals(_lastKnownHeadPresentationBone, head) &&
                !IsNearlyZeroPresentationScale(_lastKnownHeadPresentationLocalScale);
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV][HeadHide] capture-suspect reason={reason} " +
                $"head={GetTransformPath(head)} " +
                $"capturedLocalScale={FormatTraceVector(capturedLocalScale)} " +
                $"previousNonZeroAvailable={canReusePrevious} " +
                $"previousLocalScale={FormatTraceVector(_lastKnownHeadPresentationLocalScale)}.");
            if (!canReusePrevious)
                return false;

            baselineLocalScale = _lastKnownHeadPresentationLocalScale;
            return true;
        }

        private static bool IsNearlyZeroPresentationScale(Vector3 scale)
        {
            return Mathf.Abs(scale.x) <= PresentationScaleSanityEpsilon &&
                   Mathf.Abs(scale.y) <= PresentationScaleSanityEpsilon &&
                   Mathf.Abs(scale.z) <= PresentationScaleSanityEpsilon;
        }

        /// <summary>
        /// Lifts the right-arm presentation hide across a Y4NGZInteractions API
        /// boundary and returns the transform that was lifted (null when nothing
        /// was collapsed). #576: the API calls RigBuilder.Build() inside both
        /// TryStart and TryStop, and Build() permanently bakes world-space IK
        /// chain link lengths / target offsets from the CURRENT pose. It also
        /// takes a scoped first-person snapshot (local position/rotation/SCALE of
        /// the whole playerModelArmsMetarig subtree) inside TryStart and replays
        /// it inside TryStop. Running either while shoulder.R is collapsed to
        /// ~0.0001 bakes a degenerate right-arm ChainIK and re-applies scale≈0 on
        /// restore, leaving the right arm riding high in vanilla sprint after
        /// exit. The lift and its re-hide happen in the same frame, so the hide
        /// stays visually continuous.
        ///
        /// The collapse can live either on the session fields or - at the START
        /// boundary, which precedes <see cref="CaptureRightArmPresentationScale"/>
        /// adopting it - on the static interaction-begin pending state, so both
        /// sources are checked.
        /// </summary>
        private Transform LiftRightArmPresentationHideForApiBoundary(string reason)
        {
            Transform shoulder = null;
            Vector3 cachedLocalScale = Vector3.one;
            if (_rightArmPresentationHidden && _rightArmPresentationShoulder != null)
            {
                shoulder = _rightArmPresentationShoulder;
                cachedLocalScale = _rightArmPresentationCachedLocalScale;
            }
            else
            {
                InteractionBeginPresentationState pending = _interactionBeginPresentation;
                if (pending != null && pending.RightShoulderHidden &&
                    pending.RightShoulder != null &&
                    (Player == null || ReferenceEquals(pending.Player, Player)))
                {
                    shoulder = pending.RightShoulder;
                    cachedLocalScale = pending.RightShoulderScale;
                }
            }

            if (shoulder == null)
                return null;

            try
            {
                shoulder.localScale = cachedLocalScale;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][RightArmHide] frame={Time.frameCount} reason={reason} " +
                    $"cachedLocalScale={FormatTraceVector(cachedLocalScale)} " +
                    $"appliedLocalScale={FormatTraceVector(shoulder.localScale)}.");
                return shoulder;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RightArmHide] api boundary lift failed reason={reason}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Re-applies the right-arm presentation hide that
        /// <see cref="LiftRightArmPresentationHideForApiBoundary"/> lifted (#576).
        /// </summary>
        private void ReapplyRightArmPresentationHideForApiBoundary(
            Transform shoulder,
            string reason)
        {
            if (shoulder == null)
                return;

            try
            {
                shoulder.localScale = Vector3.one * PresentationHiddenScale;
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV][RightArmHide] frame={Time.frameCount} reason={reason} " +
                    $"appliedLocalScale={FormatTraceVector(shoulder.localScale)}.");
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV][RightArmHide] api boundary re-hide failed reason={reason}: {ex.Message}");
            }
        }

        private static void ClearConsumedInteractionBeginPresentation(
            InteractionBeginPresentationState pending)
        {
            if (ReferenceEquals(_interactionBeginPresentation, pending) &&
                !pending.RightShoulderCaptured && !pending.HeadCaptured)
            {
                _interactionBeginPresentation = null;
            }
        }

    }
}
