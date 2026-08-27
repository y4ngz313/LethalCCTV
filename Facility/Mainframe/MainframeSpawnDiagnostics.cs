using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Facility.Mainframe
{
    internal static class MainframeSpawnDiagnostics
    {
        private const string MainframeAssetName = "CCTVMainframeSupport";
        private const string BundlePrefix = "[MoonContracts.MainframeDiag.Bundle]";
        private const string HierarchyPrefix = "[MoonContracts.MainframeDiag.Hierarchy]";
        private const string LivePrefix = "[MoonContracts.MainframeDiag.Live]";
        private const float LiveDumpDelaySeconds = 2f;
        private const int MaxHierarchyNodes = 260;
        private const int MaxComponentsPerNode = 12;
        private const int MaxMaterialsPerRenderer = 8;

        private static readonly HashSet<string> DumpedHierarchyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<PendingLiveDump> PendingLiveDumps = new List<PendingLiveDump>();

        /// <summary>Every dump in this class is dev-only spelunking output. Gate it behind the
        /// CCTV module's recon-logging switch so a normal session stays quiet; when the switch is
        /// off nothing here runs, not even the queueing.</summary>
        private static bool DiagnosticsEnabled =>
            SurveillanceBootstrap.Config?.ReconLoggingEnabled?.Value == true;

        internal static void ResetRunState()
        {
            PendingLiveDumps.Clear();
        }

        internal static void UpdateRuntime()
        {
            if (PendingLiveDumps.Count == 0)
                return;

            float now = Time.unscaledTime;
            for (int i = PendingLiveDumps.Count - 1; i >= 0; i--)
            {
                PendingLiveDump pending = PendingLiveDumps[i];
                if (now < pending.DueTime)
                    continue;

                PendingLiveDumps.RemoveAt(i);
                if (pending.Root == null)
                {
                    CctvModuleConfig.Log?.LogWarning($"{LivePrefix} skipped state=live-instance context={pending.Context} reason=root-destroyed elapsedSeconds={now - pending.QueuedAt:0.00}.");
                    continue;
                }

                DumpHierarchy(LivePrefix, "live-instance", pending.Root, pending.Context);
            }
        }

        internal static void LogBundleMetadata(string path)
        {
            if (!DiagnosticsEnabled)
                return;

            if (string.IsNullOrWhiteSpace(path))
            {
                CctvModuleConfig.Log?.LogWarning($"{BundlePrefix} path=<empty> exists=false.");
                return;
            }

            try
            {
                FileInfo file = new FileInfo(path);
                CctvModuleConfig.Log?.LogInfo(
                    $"{BundlePrefix} path='{file.FullName}' exists={file.Exists} sizeBytes={(file.Exists ? file.Length : 0)} " +
                    $"lastWriteUtc={(file.Exists ? file.LastWriteTimeUtc.ToString("O") : "n/a")}.");
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogWarning($"{BundlePrefix} path='{path}' metadata failed: {ex.Message}");
            }
        }

        internal static void LogRawBundlePrefab(string assetName, GameObject prefab, string context)
        {
            if (!DiagnosticsEnabled)
                return;

            if (!IsMainframeAsset(assetName, prefab))
                return;

            DumpHierarchyOnce("raw-bundle-prefab", prefab, $"assetName={assetName} context={context}", "raw-bundle-prefab:" + context);
        }

        internal static void LogPreparedPrefab(string assetName, GameObject prefab, string context)
        {
            if (!DiagnosticsEnabled)
                return;

            if (!IsMainframeAsset(assetName, prefab))
                return;

            DumpHierarchyOnce("prepared-prefab", prefab, $"assetName={assetName} context={context}", "prepared-prefab:" + context);
        }

        internal static void ScheduleLiveInstanceDump(GameObject root, string context)
        {
            if (!DiagnosticsEnabled)
                return;

            if (root == null)
            {
                CctvModuleConfig.Log?.LogWarning($"{LivePrefix} queue skipped context={context} reason=root-null.");
                return;
            }

            float now = Time.unscaledTime;
            PendingLiveDumps.Add(new PendingLiveDump(root, string.IsNullOrWhiteSpace(context) ? "unspecified" : context, now, now + LiveDumpDelaySeconds));
            CctvModuleConfig.Log?.LogDebug(
                $"{LivePrefix} queued state=live-instance context={context} root='{root.name}' delaySeconds={LiveDumpDelaySeconds:0.00} " +
                $"activeSelf={root.activeSelf} activeInHierarchy={root.activeInHierarchy} pos={FormatVector(root.transform.position)} yaw={root.transform.eulerAngles.y:0.0}.");
        }

        private static void DumpHierarchyOnce(string state, GameObject root, string context, string key)
        {
            if (!DumpedHierarchyKeys.Add(key))
                return;

            DumpHierarchy(HierarchyPrefix, state, root, context);
        }

        private static void DumpHierarchy(string prefix, string state, GameObject root, string context)
        {
            if (root == null)
            {
                CctvModuleConfig.Log?.LogWarning($"{prefix} state={state} context={context} root=<null>.");
                return;
            }

            Transform rootTransform = root.transform;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            NetworkObject networkObject = root.GetComponent<NetworkObject>();
            Bounds? rendererBounds = TryBuildRendererBounds(rootTransform, renderers);
            Bounds? colliderBounds = TryBuildColliderBounds(rootTransform, colliders);

            CctvModuleConfig.Log?.LogInfo(
                $"{prefix} BEGIN state={state} context={context} root='{root.name}' scene='{FormatScene(root)}' " +
                $"activeSelf={root.activeSelf} activeInHierarchy={root.activeInHierarchy} layer={root.layer} tag='{SafeTag(root)}' " +
                $"worldPos={FormatVector(rootTransform.position)} worldRot={FormatVector(rootTransform.eulerAngles)} worldScale={FormatVector(rootTransform.lossyScale)} " +
                $"renderers={renderers.Length} colliders={colliders.Length} rootComponents={root.GetComponents<Component>().Length} " +
                $"networkObject={FormatNetworkObject(networkObject)} rendererBoundsLocalToRoot={FormatBounds(rendererBounds)} colliderBoundsLocalToRoot={FormatBounds(colliderBounds)}.");

            int visited = 0;
            DumpTransform(prefix, state, rootTransform, 0, ref visited);
            if (visited >= MaxHierarchyNodes)
                CctvModuleConfig.Log?.LogWarning($"{prefix} state={state} context={context} hierarchy truncated after {MaxHierarchyNodes} node(s).");

            CctvModuleConfig.Log?.LogInfo($"{prefix} END state={state} context={context} nodes={visited}.");
        }

        private static void DumpTransform(string prefix, string state, Transform transform, int depth, ref int visited)
        {
            if (transform == null || visited >= MaxHierarchyNodes)
                return;

            visited++;
            GameObject go = transform.gameObject;
            Component[] components = go.GetComponents<Component>();
            Renderer renderer = go.GetComponent<Renderer>();
            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            Collider collider = go.GetComponent<Collider>();
            NetworkObject networkObject = go.GetComponent<NetworkObject>();
            string indent = new string(' ', Mathf.Min(depth, 24) * 2);

            CctvModuleConfig.Log?.LogInfo(
                $"{prefix} state={state} node={visited:000} depth={depth} path='{GetHierarchyPath(transform)}' " +
                $"{indent}activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy} layer={go.layer} tag='{SafeTag(go)}' " +
                $"localPos={FormatVector(transform.localPosition)} localRot={FormatVector(transform.localEulerAngles)} localScale={FormatVector(transform.localScale)} " +
                $"worldPos={FormatVector(transform.position)} worldRot={FormatVector(transform.eulerAngles)} worldScale={FormatVector(transform.lossyScale)} childCount={transform.childCount} " +
                $"components={FormatComponents(components)} renderer={FormatRenderer(renderer, transform, meshFilter)} collider={FormatCollider(collider, transform)} networkObject={FormatNetworkObject(networkObject)}.");

            for (int i = 0; i < transform.childCount && visited < MaxHierarchyNodes; i++)
                DumpTransform(prefix, state, transform.GetChild(i), depth + 1, ref visited);
        }

        private static bool IsMainframeAsset(string assetName, GameObject prefab)
        {
            if (string.Equals(assetName, MainframeAssetName, StringComparison.OrdinalIgnoreCase))
                return true;

            return prefab != null && string.Equals(prefab.name, MainframeAssetName, StringComparison.OrdinalIgnoreCase);
        }

        private static Bounds? TryBuildRendererBounds(Transform root, Renderer[] renderers)
        {
            Bounds? bounds = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                try
                {
                    Bounds local = ConvertWorldBoundsToLocal(root, renderer.bounds);
                    bounds = bounds.HasValue ? Encapsulate(bounds.Value, local) : local;
                }
                catch
                {
                }
            }

            return bounds;
        }

        private static Bounds? TryBuildColliderBounds(Transform root, Collider[] colliders)
        {
            Bounds? bounds = null;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null)
                    continue;

                try
                {
                    Bounds local = ConvertWorldBoundsToLocal(root, collider.bounds);
                    bounds = bounds.HasValue ? Encapsulate(bounds.Value, local) : local;
                }
                catch
                {
                }
            }

            return bounds;
        }

        /// <summary>Pulls a world-space AABB into <paramref name="root"/>'s local space by
        /// transforming each extent axis separately, so a rotated surface keeps a correct extent
        /// instead of the skewed one a single InverseTransformVector would produce.</summary>
        internal static Bounds ConvertWorldBoundsToLocal(Transform root, Bounds worldBounds)
        {
            if (root == null)
                return worldBounds;

            Vector3 center = root.InverseTransformPoint(worldBounds.center);
            Vector3 extents = worldBounds.extents;
            Vector3 axisX = root.InverseTransformVector(new Vector3(extents.x, 0f, 0f));
            Vector3 axisY = root.InverseTransformVector(new Vector3(0f, extents.y, 0f));
            Vector3 axisZ = root.InverseTransformVector(new Vector3(0f, 0f, extents.z));
            extents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, extents * 2f);
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            a.Encapsulate(b.min);
            a.Encapsulate(b.max);
            return a;
        }

        private static string FormatComponents(Component[] components)
        {
            if (components == null || components.Length == 0)
                return "none";

            StringBuilder builder = new StringBuilder();
            int count = Mathf.Min(components.Length, MaxComponentsPerNode);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    builder.Append(",");
                builder.Append(components[i] != null ? components[i].GetType().Name : "missing");
            }

            if (components.Length > count)
                builder.Append(",+").Append(components.Length - count);
            return builder.ToString();
        }

        private static string FormatRenderer(Renderer renderer, Transform node, MeshFilter meshFilter)
        {
            if (renderer == null)
                return "none";

            string worldBounds = "unavailable";
            string localBounds = "unavailable";
            try
            {
                Bounds bounds = renderer.bounds;
                worldBounds = FormatBounds(bounds);
                localBounds = FormatBounds(ConvertWorldBoundsToLocal(node, bounds));
            }
            catch
            {
            }

            return $"type={renderer.GetType().Name},enabled={renderer.enabled},forceOff={renderer.forceRenderingOff},mesh={FormatMesh(renderer, meshFilter)},boundsWorld={worldBounds},boundsLocal={localBounds},materials={FormatMaterials(renderer)}";
        }

        private static string FormatMesh(Renderer renderer, MeshFilter meshFilter)
        {
            try
            {
                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null && skinned.sharedMesh != null)
                    return $"{skinned.sharedMesh.name}(vertices={skinned.sharedMesh.vertexCount})";

                if (meshFilter != null && meshFilter.sharedMesh != null)
                    return $"{meshFilter.sharedMesh.name}(vertices={meshFilter.sharedMesh.vertexCount})";
            }
            catch
            {
                return "unavailable";
            }

            return "none";
        }

        private static string FormatMaterials(Renderer renderer)
        {
            if (renderer == null)
                return "none";

            try
            {
                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                    return "none";

                string[] formatted = materials
                    .Take(MaxMaterialsPerRenderer)
                    .Select(material =>
                    {
                        if (material == null)
                            return "null|shader=null";
                        string shaderName = material.shader != null ? material.shader.name : "null";
                        return material.name + "|shader=" + shaderName;
                    })
                    .ToArray();

                string suffix = materials.Length > MaxMaterialsPerRenderer
                    ? ",+" + (materials.Length - MaxMaterialsPerRenderer)
                    : string.Empty;
                return string.Join(",", formatted) + suffix;
            }
            catch
            {
                return "unavailable";
            }
        }

        private static string FormatCollider(Collider collider, Transform node)
        {
            if (collider == null)
                return "none";

            string worldBounds = "unavailable";
            string localBounds = "unavailable";
            try
            {
                Bounds bounds = collider.bounds;
                worldBounds = FormatBounds(bounds);
                localBounds = FormatBounds(ConvertWorldBoundsToLocal(node, bounds));
            }
            catch
            {
            }

            return $"type={collider.GetType().Name},enabled={collider.enabled},trigger={collider.isTrigger},boundsWorld={worldBounds},boundsLocal={localBounds}";
        }

        private static string FormatNetworkObject(NetworkObject networkObject)
        {
            if (networkObject == null)
                return "none";

            return $"present,spawned={networkObject.IsSpawned},id={networkObject.NetworkObjectId},hash={ReadNetworkObjectHash(networkObject)},syncTransform={networkObject.SynchronizeTransform}";
        }

        private static string ReadNetworkObjectHash(NetworkObject networkObject)
        {
            if (networkObject == null)
                return "none";

            try
            {
                Type type = typeof(NetworkObject);
                PropertyInfo prop = type.GetProperty("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanRead)
                {
                    object value = prop.GetValue(networkObject, null);
                    return value != null ? value.ToString() : "null";
                }

                FieldInfo field = type.GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? type.GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    object value = field.GetValue(networkObject);
                    return value != null ? value.ToString() : "null";
                }
            }
            catch
            {
            }

            return "unavailable";
        }

        private static string FormatBounds(Bounds? bounds)
        {
            return bounds.HasValue ? FormatBounds(bounds.Value) : "none";
        }

        private static string FormatBounds(Bounds bounds)
        {
            return $"center={FormatVector(bounds.center)},size={FormatVector(bounds.size)}";
        }

        private static string FormatVector(Vector3 vector)
        {
            return $"({vector.x:0.###},{vector.y:0.###},{vector.z:0.###})";
        }

        private static string FormatScene(GameObject root)
        {
            if (root == null)
                return "<null>";

            try
            {
                return root.scene.IsValid() ? root.scene.name : "<invalid>";
            }
            catch
            {
                return "<unavailable>";
            }
        }

        private static string SafeTag(GameObject go)
        {
            if (go == null)
                return "null";

            try
            {
                return go.tag;
            }
            catch
            {
                return "unavailable";
            }
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            Stack<string> names = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names.ToArray());
        }

        private readonly struct PendingLiveDump
        {
            internal readonly GameObject Root;
            internal readonly string Context;
            internal readonly float QueuedAt;
            internal readonly float DueTime;

            internal PendingLiveDump(GameObject root, string context, float queuedAt, float dueTime)
            {
                Root = root;
                Context = context;
                QueuedAt = queuedAt;
                DueTime = dueTime;
            }
        }
    }
}
