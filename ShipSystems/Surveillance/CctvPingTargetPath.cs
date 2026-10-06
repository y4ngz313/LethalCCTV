using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CctvPingTargetPath
    {
        internal static string Capture(Transform target, Transform ancestor)
        {
            var parts = new List<string>();
            for (Transform current = target; current != null && current != ancestor; current = current.parent)
            {
                int ordinal = 0;
                foreach (Transform sibling in Children(current.parent, current.gameObject.scene))
                {
                    if (sibling == current) break;
                    if (sibling.name == current.name) ordinal++;
                }
                parts.Add(Uri.EscapeDataString(current.name) + "#" + ordinal);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        internal static Transform Resolve(string path, Transform ancestor, Vector3 expectedPosition)
        {
            if (string.IsNullOrEmpty(path)) return ancestor;
            string[] parts = path.Split('/');
            if (ancestor != null) return Descend(parts, ancestor, ancestor.gameObject.scene);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                Transform result = Descend(parts, null, scene);
                if (result != null && Vector3.Distance(result.position, expectedPosition) <= 0.5f) return result;
            }
            return null;
        }

        private static Transform Descend(string[] parts, Transform parent, Scene scene)
        {
            foreach (string part in parts)
            {
                int separator = part.LastIndexOf('#');
                if (separator <= 0 || !int.TryParse(part.Substring(separator + 1), out int ordinal) || ordinal < 0) return null;
                string name = Uri.UnescapeDataString(part.Substring(0, separator));
                Transform found = null;
                foreach (Transform child in Children(parent, scene))
                {
                    if (child.name != name) continue;
                    if (ordinal-- == 0) { found = child; break; }
                }
                if (found == null) return null;
                parent = found;
            }
            return parent;
        }

        private static IEnumerable<Transform> Children(Transform parent, Scene scene)
        {
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++) yield return parent.GetChild(i);
            }
            else
            {
                foreach (GameObject root in scene.GetRootGameObjects()) yield return root.transform;
            }
        }
    }
}
