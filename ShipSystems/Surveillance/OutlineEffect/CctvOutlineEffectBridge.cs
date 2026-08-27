using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance.OutlineEffect
{
    internal enum CctvOutlineChannel
    {
        Hostile = 0,
        Item = 1,
        Player = 2
    }

    internal enum CctvOutlineSource
    {
        Cctv = 0
    }

    internal static class CctvOutlineEffectBridge
    {
        private static readonly Color HostileColor = new Color(1f, 0.06f, 0.04f, 1f);
        private static readonly Color ItemColor = new Color(0.10f, 1.0f, 0.28f, 1f);
        private static readonly Color PlayerColor = Color.white;

        private static Camera _camera;
        private static OutlineEffect _effect;
        private static float _nextMissingShaderLogAt;

        internal static Camera ActiveCamera => _camera;

        internal static bool Show(Camera camera, GameObject targetRoot, CctvOutlineChannel channel, float duration)
        {
            if (camera == null || targetRoot == null || duration <= 0f)
                return false;

            if (!EnsureEffect(camera))
                return false;

            CctvOutlineTarget driver = targetRoot.GetComponent<CctvOutlineTarget>();
            if (driver == null)
                driver = targetRoot.AddComponent<CctvOutlineTarget>();

            return driver.Show(camera, CctvOutlineSource.Cctv, (int)channel, duration);
        }

        internal static void Clear(GameObject targetRoot)
        {
            if (targetRoot == null)
                return;

            CctvOutlineTarget driver = targetRoot.GetComponent<CctvOutlineTarget>();
            if (driver != null)
                driver.Clear(CctvOutlineSource.Cctv);
        }

        internal static void DisableActiveEffect()
        {
            if (_effect != null && _effect.enabled)
                _effect.enabled = false;
        }

        private static bool EnsureEffect(Camera camera)
        {
            Shader outline = OutlineRuntimeAssets.LoadShader("OutlineShader");
            Shader buffer = OutlineRuntimeAssets.LoadShader("OutlineBufferShader");
            Shader overlay = OutlineRuntimeAssets.LoadShader("Y4NGZOutlineOverlayShader");
            if (outline == null || buffer == null || overlay == null)
            {
                if (Time.unscaledTime >= _nextMissingShaderLogAt)
                {
                    _nextMissingShaderLogAt = Time.unscaledTime + 10f;
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] CCTV OutlineEffect shaders are unavailable; real CCTV outlines will not render.");
                }
                return false;
            }

            if (_effect != null && _camera == camera)
            {
                ConfigureEffect(_effect, camera);
                return true;
            }

            _camera = camera;
            _effect = camera.GetComponent<OutlineEffect>();
            if (_effect == null)
                _effect = camera.gameObject.AddComponent<OutlineEffect>();

            ConfigureEffect(_effect, camera);
            return true;
        }

        private static void ConfigureEffect(OutlineEffect effect, Camera camera)
        {
            if (effect == null)
                return;

            effect.enabled = true;
            effect.sourceCamera = camera;
            effect.lineThickness = 1.35f;
            effect.lineIntensity = 2.25f;
            effect.fillAmount = 0f;
            effect.lineColor0 = HostileColor;
            effect.lineColor1 = ItemColor;
            effect.lineColor2 = PlayerColor;
            effect.additiveRendering = true;
            effect.backfaceCulling = false;
            effect.useFillColor = false;
            effect.cornerOutlines = true;
            effect.addLinesBetweenColors = false;
            effect.autoEnableOutlines = false;
            effect.UpdateMaterialsPublicProperties();
        }

        private sealed class CctvOutlineTarget : MonoBehaviour
        {
            /// <summary>
            /// Map/radar geometry. Only consulted when no local player exists to read
            /// a real culling mask from — see <see cref="IsGameplayVisibleLayer"/>.
            /// </summary>
            private const int MapRadarLayer = 14;
            private const int MapOnlyLayer = 22;

            private static int _cullingMaskFrame = -1;
            private static int _cullingMask;

            private readonly List<OutlineRecord> _records = new List<OutlineRecord>();
            private readonly Dictionary<CctvOutlineSource, OutlineRequest> _requests =
                new Dictionary<CctvOutlineSource, OutlineRequest>();
            private readonly HashSet<Renderer> _lodManagedRenderers = new HashSet<Renderer>();
            private readonly HashSet<Renderer> _activeLodRenderers = new HashSet<Renderer>();
            private int _channel;
            private Camera _camera;
            private PlayerControllerB _targetPlayer;
            private EnemyAI _targetEnemy;
            private GrabbableObject _targetItem;

            internal bool Show(Camera camera, CctvOutlineSource source, int channel, float duration)
            {
                _camera = camera;
                _requests[source] = new OutlineRequest
                {
                    Channel = channel,
                    ExpiresAt = Time.time + duration
                };

                EnsureRenderers(camera);
                ResolveActiveChannel();
                return ApplyChannel() > 0;
            }

            internal void Clear(CctvOutlineSource source)
            {
                _requests.Remove(source);
                if (_requests.Count <= 0)
                {
                    Destroy(this);
                    return;
                }

                ResolveActiveChannel();
                ApplyChannel();
            }

            private void Update()
            {
                List<CctvOutlineSource> expired = null;
                foreach (KeyValuePair<CctvOutlineSource, OutlineRequest> pair in _requests)
                {
                    if (Time.time < pair.Value.ExpiresAt)
                        continue;

                    if (expired == null)
                        expired = new List<CctvOutlineSource>();
                    expired.Add(pair.Key);
                }

                if (expired != null)
                {
                    for (int i = 0; i < expired.Count; i++)
                        _requests.Remove(expired[i]);
                }

                if (_requests.Count <= 0)
                {
                    Destroy(this);
                    return;
                }

                Camera activeCamera = ActiveCamera ?? _camera;
                EnsureRenderers(activeCamera);
                if (expired != null)
                    ResolveActiveChannel();
                ApplyChannel();
            }

            private void EnsureRenderers(Camera camera)
            {
                _targetPlayer = GetComponent<PlayerControllerB>();
                _targetEnemy = GetComponent<EnemyAI>();
                _targetItem = GetComponent<GrabbableObject>();
                RefreshActiveLodRendererSets(camera);

                Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: false);
                for (int i = 0; i < renderers.Length; i++)
                    TryTrackRenderer(renderers[i]);
            }

            private void RefreshActiveLodRendererSets(Camera camera)
            {
                _lodManagedRenderers.Clear();
                _activeLodRenderers.Clear();

                LODGroup[] groups = GetComponentsInChildren<LODGroup>(includeInactive: false);
                for (int i = 0; i < groups.Length; i++)
                {
                    LODGroup group = groups[i];
                    if (group == null || !group.enabled)
                        continue;

                    LOD[] lods = group.GetLODs();
                    int primaryLod = -1;
                    for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
                    {
                        Renderer[] lodRenderers = lods[lodIndex].renderers;
                        if (lodRenderers == null)
                            continue;

                        bool hasRenderer = false;
                        for (int rendererIndex = 0; rendererIndex < lodRenderers.Length; rendererIndex++)
                        {
                            Renderer lodRenderer = lodRenderers[rendererIndex];
                            if (lodRenderer == null)
                                continue;

                            _lodManagedRenderers.Add(lodRenderer);
                            hasRenderer = true;
                        }

                        if (primaryLod < 0 && hasRenderer)
                            primaryLod = lodIndex;
                    }

                    int activeLod = ResolveActiveLodIndex(group, lods, camera, primaryLod);
                    if (activeLod < 0 || activeLod >= lods.Length)
                        continue;

                    Renderer[] activeRenderers = lods[activeLod].renderers;
                    if (activeRenderers == null)
                        continue;

                    for (int rendererIndex = 0; rendererIndex < activeRenderers.Length; rendererIndex++)
                    {
                        Renderer activeRenderer = activeRenderers[rendererIndex];
                        if (activeRenderer != null)
                            _activeLodRenderers.Add(activeRenderer);
                    }
                }
            }

            private static int ResolveActiveLodIndex(LODGroup group, LOD[] lods, Camera camera, int fallbackLod)
            {
                if (group == null || lods == null || lods.Length == 0)
                    return -1;

                if (camera == null)
                    return fallbackLod;

                float relativeHeight = CalculateRelativeScreenHeight(group, camera);
                int best = lods.Length - 1;
                for (int i = 0; i < lods.Length; i++)
                {
                    if (relativeHeight >= lods[i].screenRelativeTransitionHeight)
                    {
                        best = i;
                        break;
                    }
                }

                return best;
            }

            private static float CalculateRelativeScreenHeight(LODGroup group, Camera camera)
            {
                if (group == null || camera == null)
                    return 1f;

                Transform groupTransform = group.transform;
                Vector3 referencePoint = groupTransform.TransformPoint(group.localReferencePoint);
                float distance = Vector3.Distance(camera.transform.position, referencePoint);
                if (distance <= 0.01f)
                    return 1f;

                float worldSize = Mathf.Max(0.01f, group.size * MaxAbsScale(groupTransform.lossyScale));
                if (camera.orthographic)
                    return worldSize / Mathf.Max(0.01f, camera.orthographicSize * 2f);

                float relativeHeight = worldSize / (2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
                return relativeHeight * Mathf.Max(0.01f, QualitySettings.lodBias);
            }

            private static float MaxAbsScale(Vector3 scale)
            {
                return Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            }

            /// <summary>
            /// True when the gameplay camera would draw something on this layer.
            /// Resolved from the live culling mask rather than a layer table so a
            /// different game build cannot silently move a layer out from under us;
            /// the hardcoded pair is only the answer while no local player exists.
            /// </summary>
            private static bool IsGameplayVisibleLayer(int layer)
            {
                if (layer < 0 || layer > 31)
                    return false;

                int mask = ResolveGameplayCullingMask();
                if (mask != 0)
                    return (mask & (1 << layer)) != 0;

                return layer != MapRadarLayer && layer != MapOnlyLayer;
            }

            /// <summary>
            /// The local player's gameplay camera, not <see cref="ActiveCamera"/>: the
            /// CCTV camera renders a different mask, and the question here is what a
            /// person standing in the world would be able to see.
            /// </summary>
            private static int ResolveGameplayCullingMask()
            {
                if (_cullingMaskFrame == Time.frameCount)
                    return _cullingMask;

                _cullingMaskFrame = Time.frameCount;
                _cullingMask = 0;

                PlayerControllerB local = StartOfRound.Instance != null
                    ? StartOfRound.Instance.localPlayerController
                    : null;
                if (local == null && GameNetworkManager.Instance != null)
                    local = GameNetworkManager.Instance.localPlayerController;

                Camera gameplay = local != null ? local.gameplayCamera : null;
                if (gameplay != null)
                    _cullingMask = gameplay.cullingMask;

                return _cullingMask;
            }

            private void TryTrackRenderer(Renderer renderer)
            {
                if (renderer == null || !renderer.enabled)
                    return;

                if (renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                    return;

                // A shadows-only renderer draws nothing the player can see, but the
                // outline pass still traces its silhouette. That is where the stray
                // duplicate outline offset from the model comes from.
                if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                    return;

                // Map/scan-only geometry lives on layers the gameplay camera never
                // renders. Outlining it puts a shape on the monitor with no model
                // under it, so the camera's own culling mask is the filter.
                if (!IsGameplayVisibleLayer(renderer.gameObject.layer))
                    return;

                if (!RendererBelongsToTarget(renderer))
                    return;

                for (int i = 0; i < _records.Count; i++)
                {
                    if (_records[i].Renderer == renderer)
                        return;
                }

                Outline outline = renderer.GetComponent<Outline>();
                bool created = outline == null;
                bool previousEnabled = false;
                int previousColor = 0;
                bool previousErase = false;

                if (outline == null)
                {
                    outline = renderer.gameObject.AddComponent<Outline>();
                }
                else
                {
                    previousEnabled = outline.enabled;
                    previousColor = outline.color;
                    previousErase = outline.eraseRenderer;
                }

                _records.Add(new OutlineRecord
                {
                    Renderer = renderer,
                    Outline = outline,
                    Created = created,
                    PreviousEnabled = previousEnabled,
                    PreviousColor = previousColor,
                    PreviousEraseRenderer = previousErase
                });
            }

            private bool RendererBelongsToTarget(Renderer renderer)
            {
                if (renderer == null)
                    return false;

                if (_targetPlayer != null)
                {
                    PlayerControllerB owner = renderer.GetComponentInParent<PlayerControllerB>();
                    if (owner != _targetPlayer)
                        return false;

                    if (renderer.GetComponentInParent<EnemyAI>() != null)
                        return false;

                    // Same defect the enemy branch was already fixed for: a player
                    // carries scan nodes and shadow proxies too, and outlining them
                    // draws a second shape beside the body on the monitor.
                    if (!IsActiveLodRenderer(renderer) || IsHelperRenderer(renderer))
                        return false;

                    GrabbableObject heldItem = renderer.GetComponentInParent<GrabbableObject>();
                    return heldItem == null;
                }

                if (_targetEnemy != null)
                {
                    EnemyAI owner = renderer.GetComponentInParent<EnemyAI>();
                    if (owner != _targetEnemy)
                        return false;

                    if (renderer.GetComponentInParent<PlayerControllerB>() != null)
                        return false;

                    if (!IsActiveLodRenderer(renderer) || IsHelperRenderer(renderer))
                        return false;

                    GrabbableObject carriedItem = renderer.GetComponentInParent<GrabbableObject>();
                    return carriedItem == null;
                }

                if (_targetItem != null)
                {
                    GrabbableObject owner = renderer.GetComponentInParent<GrabbableObject>();
                    return owner == _targetItem && IsActiveLodRenderer(renderer);
                }

                return renderer.transform.IsChildOf(transform) && IsActiveLodRenderer(renderer);
            }

            private bool IsActiveLodRenderer(Renderer renderer)
            {
                // Fail closed on a renderer we cannot inspect. The old answer here was
                // true, which let an unresolvable renderer through the player branch
                // on the strength of nothing at all.
                if (renderer == null)
                    return false;

                if (_lodManagedRenderers.Count <= 0)
                    return true;

                return !_lodManagedRenderers.Contains(renderer) || _activeLodRenderers.Contains(renderer);
            }

            /// <summary>
            /// Scan nodes, map dots and shadow proxies, on players as much as on
            /// enemies: they ride the same rig and read as a second silhouette.
            /// </summary>
            private static bool IsHelperRenderer(Renderer renderer)
            {
                if (renderer == null)
                    return true;

                string path = BuildTransformPath(renderer.transform).ToLowerInvariant();
                if (ContainsAny(path, "scannode", "scan node", "mapdot", "map dot", "radar", "minimap", "shadow"))
                    return true;

                if (HasParentComponentNamed(renderer.transform, "ScanNodeProperties"))
                    return true;

                string meshName = GetMeshName(renderer).ToLowerInvariant();
                if (ContainsAny(meshName, "scannode", "mapdot", "radar", "minimap"))
                    return true;

                string materialNames = GetMaterialNames(renderer).ToLowerInvariant();
                return ContainsAny(materialNames, "scannode", "scan node", "mapdot", "map dot", "radar", "minimap", "shadow");
            }

            private static string BuildTransformPath(Transform current)
            {
                if (current == null)
                    return string.Empty;

                string path = current.name;
                Transform parent = current.parent;
                int guard = 0;
                while (parent != null && guard++ < 48)
                {
                    path = parent.name + "/" + path;
                    parent = parent.parent;
                }

                return path;
            }

            private static bool HasParentComponentNamed(Transform transform, string componentTypeName)
            {
                Transform current = transform;
                int guard = 0;
                while (current != null && guard++ < 48)
                {
                    Component[] components = current.GetComponents<Component>();
                    for (int i = 0; i < components.Length; i++)
                    {
                        Component component = components[i];
                        if (component != null && component.GetType().Name == componentTypeName)
                            return true;
                    }

                    current = current.parent;
                }

                return false;
            }

            private static string GetMeshName(Renderer renderer)
            {
                if (renderer == null)
                    return string.Empty;

                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null && skinned.sharedMesh != null)
                    return skinned.sharedMesh.name;

                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                return meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.name : string.Empty;
            }

            private static string GetMaterialNames(Renderer renderer)
            {
                if (renderer == null)
                    return string.Empty;

                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                    return string.Empty;

                string names = string.Empty;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null)
                        continue;

                    if (names.Length > 0)
                        names += " ";
                    names += material.name;
                }

                return names;
            }

            private static bool ContainsAny(string value, params string[] needles)
            {
                if (string.IsNullOrEmpty(value))
                    return false;

                for (int i = 0; i < needles.Length; i++)
                {
                    string needle = needles[i];
                    if (!string.IsNullOrEmpty(needle) && value.Contains(needle))
                        return true;
                }

                return false;
            }

            private void ResolveActiveChannel()
            {
                int bestChannel = (int)CctvOutlineChannel.Item;
                float bestPriority = -1f;
                foreach (KeyValuePair<CctvOutlineSource, OutlineRequest> pair in _requests)
                {
                    float priority = ChannelPriority(pair.Value.Channel);
                    if (priority < bestPriority)
                        continue;

                    bestPriority = priority;
                    bestChannel = pair.Value.Channel;
                }

                _channel = bestChannel;
            }

            private static float ChannelPriority(int channel)
            {
                if (channel == (int)CctvOutlineChannel.Hostile)
                    return 3f;
                if (channel == (int)CctvOutlineChannel.Player)
                    return 2f;
                return 1f;
            }

            private int ApplyChannel()
            {
                int activeCount = 0;
                for (int i = 0; i < _records.Count; i++)
                {
                    Outline outline = _records[i].Outline;
                    if (outline == null)
                        continue;

                    bool active = RendererBelongsToTarget(_records[i].Renderer);
                    outline.color = _channel;
                    outline.eraseRenderer = false;
                    if (outline.enabled != active)
                        outline.enabled = active;
                    if (active)
                        activeCount++;
                }

                return activeCount;
            }

            private void OnDestroy()
            {
                for (int i = 0; i < _records.Count; i++)
                {
                    OutlineRecord record = _records[i];
                    if (record.Outline == null)
                        continue;

                    if (record.Created)
                    {
                        Destroy(record.Outline);
                    }
                    else
                    {
                        record.Outline.color = record.PreviousColor;
                        record.Outline.eraseRenderer = record.PreviousEraseRenderer;
                        record.Outline.enabled = record.PreviousEnabled;
                    }
                }

                _records.Clear();
            }
        }

        private struct OutlineRequest
        {
            internal int Channel;
            internal float ExpiresAt;
        }

        private struct OutlineRecord
        {
            internal Renderer Renderer;
            internal Outline Outline;
            internal bool Created;
            internal bool PreviousEnabled;
            internal int PreviousColor;
            internal bool PreviousEraseRenderer;
        }
    }
}
