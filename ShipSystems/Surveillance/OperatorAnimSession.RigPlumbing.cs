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
        private void EvaluateLegacyFirstPersonArmRig()
        {
            if (Y4NGZPlayerAnimationBridge.UseManualFirstPersonArmIk)
                return;

            Component[] rigBuilders = ResolveFirstPersonRightHandRigBuilders();
            for (int i = 0; rigBuilders != null && i < rigBuilders.Length; i++)
            {
                Component rigBuilder = rigBuilders[i];
                if (rigBuilder == null)
                    continue;

                try
                {
                    Type type = rigBuilder.GetType();
                    MethodInfo sync = type.GetMethod(
                        "SyncLayers",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);
                    sync?.Invoke(rigBuilder, null);

                    MethodInfo evaluate = type.GetMethod(
                        "Evaluate",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(float) },
                        null);
                    if (evaluate != null)
                    {
                        evaluate.Invoke(rigBuilder, new object[] { 0f });
                        _handTraceRigEvaluateCount++;
                    }
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] Legacy first-person arm rig evaluate failed: {ex.Message}");
                }
            }

            // Legacy Evaluate resets the arms root to the animator pose. This
            // reassert belongs only to the manual-IK-off A/B path.
            ReassertFirstPersonArmsPresentation();
        }

        // Assigning Animator.runtimeAnimatorController rebinds the animator
        // and unbinds the RigBuilder's playable-graph output: SyncLayers and
        // Evaluate keep running without error but no longer write into the
        // animator, so the arm IK never solves. Test 10 corrected the earlier
        // swap-frame interpretation: the target starts at the captured wrist,
        // so a near-zero initial distance was not evidence of an IK solve.
        // Animation Rigging requires RigBuilder.Build() after every runtime
        // controller change; run it at every swap site, both directions, so
        // vanilla item IK also survives the exit restore.
        private void RebuildArmsRigAfterControllerSwap(string reason)
        {
            RebuildArmsRigComponents(ResolveFirstPersonRightHandRigBuilders(), reason);
        }

        private static void RebuildArmsRigComponents(Component[] rigBuilders, string reason)
        {
            if (rigBuilders == null || rigBuilders.Length == 0)
                return;

            for (int i = 0; i < rigBuilders.Length; i++)
            {
                Component rigBuilder = rigBuilders[i];
                if (rigBuilder == null)
                    continue;

                try
                {
                    MethodInfo build = rigBuilder.GetType().GetMethod(
                        "Build",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);
                    object result = build != null ? build.Invoke(rigBuilder, null) : null;
                    string graphDescription = DescribeRigGraph(
                        rigBuilder,
                        out bool? graphPlaying);
                    SurveillanceBootstrap.Log?.LogInfo(
                        $"[LethalCCTV] Rig graph rebuilt after controller swap ({reason}): " +
                        $"{rigBuilder.GetType().Name}@{GetTransformPath(rigBuilder.transform)} " +
                        $"behaviourEnabled={DescribeBehaviourEnabled(rigBuilder)} " +
                        $"build={(build == null ? "<no-method>" : result is bool ok ? ok.ToString() : "void")} " +
                        $"graph={graphDescription}.");
                    if (graphPlaying == false)
                    {
                        SurveillanceBootstrap.Log?.LogWarning(
                            $"[LethalCCTV][RIG GRAPH NOT PLAYING] Rebuild ({reason}) returned " +
                            $"graph={graphDescription} for {rigBuilder.GetType().Name}@" +
                            $"{GetTransformPath(rigBuilder.transform)}. The graph cannot produce IK writes.");
                    }
                }
                catch (Exception ex)
                {
                    SurveillanceBootstrap.Log?.LogWarning(
                        $"[LethalCCTV] Rig graph rebuild failed after controller swap ({reason}): {ex.Message}");
                }
            }
        }

        private static string DescribeBehaviourEnabled(Component component)
        {
            return component is Behaviour behaviour
                ? behaviour.enabled.ToString()
                : "<not-behaviour>";
        }

        private static string DescribeRigGraph(Component rigBuilder)
        {
            return DescribeRigGraph(rigBuilder, out _);
        }

        private static string DescribeRigGraph(Component rigBuilder, out bool? graphPlaying)
        {
            graphPlaying = null;
            try
            {
                object graph = ReadReflectedMemberNoByRef(rigBuilder, "graph");
                if (graph == null)
                    return "<none>";
                MethodInfo isValid = graph.GetType().GetMethod("IsValid", Type.EmptyTypes);
                MethodInfo isPlaying = graph.GetType().GetMethod("IsPlaying", Type.EmptyTypes);
                object valid = isValid?.Invoke(graph, null);
                object playing = isPlaying?.Invoke(graph, null);
                if (playing is bool playingValue)
                    graphPlaying = playingValue;
                string validText = valid is bool v
                    ? (v ? "valid" : "invalid")
                    : "valid:<unknown>";
                string playingText = playing is bool p
                    ? (p ? "playing" : "NOT-PLAYING")
                    : "playing:<unknown>";
                return validText + "/" + playingText;
            }
            catch
            {
                return "<error>";
            }
        }

        private Component[] ResolveFirstPersonRightHandRigBuilders()
        {
            if (_firstPersonRightHandRigBuildersResolved)
                return _firstPersonRightHandRigBuilders;

            _firstPersonRightHandRigBuildersResolved = true;
            var found = new List<Component>(4);
            try
            {
                Transform root = _animator != null ? _animator.transform : null;
                Component[] components = root != null
                    ? root.GetComponentsInChildren<Component>(true)
                    : Array.Empty<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null)
                        continue;

                    Type type = component.GetType();
                    string typeName = type != null ? type.Name : string.Empty;
                    string fullName = type != null ? type.FullName : string.Empty;
                    if (string.Equals(typeName, "RigBuilder", StringComparison.Ordinal) ||
                        string.Equals(fullName, "UnityEngine.Animations.Rigging.RigBuilder", StringComparison.Ordinal))
                    {
                        found.Add(component);
                    }
                }
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogDebug($"[LethalCCTV] First-person right-hand rig builder search failed: {ex.Message}");
            }

            _firstPersonRightHandRigBuilders = found.ToArray();
            if (_firstPersonRightHandRigBuilders.Length > 0)
            {
                string paths = string.Empty;
                for (int i = 0; i < _firstPersonRightHandRigBuilders.Length; i++)
                {
                    Component component = _firstPersonRightHandRigBuilders[i];
                    if (component == null)
                        continue;
                    if (!string.IsNullOrEmpty(paths))
                        paths += "; ";
                    paths += $"{component.GetType().Name}@{GetTransformPath(component.transform)}";
                }
                SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] First-person right-hand rig builders resolved: {paths}");
            }
            else if (!_firstPersonRightHandRigBuilderMissingLogged)
            {
                _firstPersonRightHandRigBuilderMissingLogged = true;
                SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] First-person right-hand rig builder not found; edit mode will move the target but may not visibly update the hand until the rig evaluates.");
            }

            return _firstPersonRightHandRigBuilders;
        }

        private static Transform FindChildRecursive(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;
            if (string.Equals(root.name, name, StringComparison.Ordinal))
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChildRecursive(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            string path = transform.name;
            Transform parent = transform.parent;
            int depth = 0;
            while (parent != null && depth++ < 28)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        // #593: the authored first-person arms layer follows the same gate as
        // the authored legacy controller. Weighting it off while the API
        // fallback plays the authored clip is what left the arms frozen.
        private bool ShouldShowFirstPersonArmsLayer()
        {
            return _isLocal &&
                   Y4NGZPlayerAnimationBridge.UseAuthoredLegacyOperatorPresentation &&
                   !MonitorFocus.IsStationThirdPersonDebugViewActive;
        }

        /// <summary>
        /// Presentation gate for the manual two-bone solve, which is the ONE
        /// writer both operator paths share. The old local-authored config key
        /// was a legacy-controller predicate; gating the API-mode solve on that
        /// per-profile opt-in was always wrong because it made the sanctioned
        /// final bone writer depend on a setting for the other path.
        ///
        /// This is a latent-bug fix, not a regression fix. The solve did run
        /// historically — nine archived API-mode captures log `api_first_solved`,
        /// including the user-approved Tests 39-42 (commit 8beaae3) — because
        /// the profile in use then set that legacy opt-in true alongside API
        /// mode. On a profile that left it at its `false` default the solve
        /// silently stops, the arms hang in a camera-relative rest pose, and
        /// only the shoulder cap plugs remain in frame. Accept API mode here so
        /// the writer no longer depends on profile state; the 3P debug-view
        /// escape still applies to both paths.
        ///
        /// NOTE: this restores the arm SOLVE only. It does not place the
        /// shoulders — that is ResolveApiShoulderAnchorTarget's camera-relative
        /// offset, which is separate calibration and a separate defect.
        /// </summary>
        private bool ShouldSolveFirstPersonArms()
        {
            return _isLocal &&
                   (_interactionsApiMode ||
                    Y4NGZPlayerAnimationBridge.UseAuthoredLegacyOperatorPresentation) &&
                   !MonitorFocus.IsStationThirdPersonDebugViewActive;
        }

        private void SetLayerWeight(int layerIndex, float weight)
        {
            if (_animator == null || layerIndex < 0 || layerIndex >= _animator.layerCount)
                return;

            try { _animator.SetLayerWeight(layerIndex, Mathf.Clamp01(weight)); } catch { }
        }

        private void SaveAnimatorState(Animator animator)
        {
            if (animator == null)
                return;

            AnimatorControllerParameter[] parameters = animator.parameters;
            _savedParameters = new SavedAnimatorParameter[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                _savedParameters[i] = new SavedAnimatorParameter(animator, parameter);
            }

            int layerCount = Mathf.Max(0, animator.layerCount);
            _savedStates = new SavedAnimatorState[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(i);
                _savedStates[i] = new SavedAnimatorState(state.fullPathHash, state.normalizedTime);
            }
        }

        private void RestoreAnimatorState(Animator animator)
        {
            if (animator == null)
                return;

            CacheParameters(animator);
            for (int i = 0; i < _savedParameters.Length; i++)
                _savedParameters[i].Restore(animator, _parameterTypes);

            int layerCount = Mathf.Min(animator.layerCount, _savedStates.Length);
            for (int i = 0; i < layerCount; i++)
            {
                SavedAnimatorState state = _savedStates[i];
                if (state.StateHash != 0 && animator.HasState(i, state.StateHash))
                    animator.Play(state.StateHash, i, state.NormalizedTime);
            }
            animator.Update(0f);
        }

        private static string FormatPlayerForLog(PlayerControllerB player)
        {
            if (player == null)
                return "<null>";

            string username = string.IsNullOrEmpty(player.playerUsername) ? "<unnamed>" : player.playerUsername;
            return "#" + player.playerClientId + " '" + username + "'";
        }

        private void CacheParameters(Animator animator)
        {
            _parameterTypes.Clear();
            if (animator == null)
                return;

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                _parameterTypes[parameter.nameHash] = parameter.type;
            }
        }

        private void SetBool(int hash, bool value)
        {
            if (_animator != null &&
                _parameterTypes.TryGetValue(hash, out AnimatorControllerParameterType type) &&
                type == AnimatorControllerParameterType.Bool)
            {
                _animator.SetBool(hash, value);
            }
        }

        private void SetInt(int hash, int value)
        {
            if (_animator != null &&
                _parameterTypes.TryGetValue(hash, out AnimatorControllerParameterType type) &&
                type == AnimatorControllerParameterType.Int)
            {
                _animator.SetInteger(hash, value);
            }
        }

        private void SetFloat(int hash, float value)
        {
            if (_animator != null &&
                _parameterTypes.TryGetValue(hash, out AnimatorControllerParameterType type) &&
                type == AnimatorControllerParameterType.Float)
            {
                _animator.SetFloat(hash, value);
            }
        }

        private void FireTrigger(int hash)
        {
            if (_animator != null &&
                _parameterTypes.TryGetValue(hash, out AnimatorControllerParameterType type) &&
                type == AnimatorControllerParameterType.Trigger)
            {
                _animator.ResetTrigger(hash);
                _animator.SetTrigger(hash);
            }
        }
    }
}
