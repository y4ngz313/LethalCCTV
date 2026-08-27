using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using Y4NGZCompany.Facility.Cameras;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVVanillaMonitorDisplay
    {
        private static void CreateDivider(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);
            SetLayerRecursive(go, UiRenderLayer);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = sizeDelta;
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;

            Image image = go.GetComponent<Image>();
            image.color = new Color(0f, 0.8f, 0.2f, 0.65f);
            image.raycastTarget = false;
        }

        private static void CreateBorder(RectTransform parent, Color color, float thickness)
        {
            CreateBorderEdge(parent, "BorderTop", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, thickness), color);
            CreateBorderEdge(parent, "BorderBottom", Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, thickness), color);
            CreateBorderEdge(parent, "BorderLeft", Vector2.zero, new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(thickness, 0f), color);
            CreateBorderEdge(parent, "BorderRight", new Vector2(1f, 0f), Vector2.one, new Vector2(1f, 0.5f), new Vector2(thickness, 0f), color);
        }

        private static void CreateBorderEdge(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 sizeDelta, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);
            SetLayerRecursive(go, UiRenderLayer);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = Vector2.zero;
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        private static void CopyRectTransform(RectTransform source, RectTransform destination)
        {
            if (source == null || destination == null)
                return;

            destination.anchorMin = source.anchorMin;
            destination.anchorMax = source.anchorMax;
            destination.pivot = source.pivot;
            destination.anchoredPosition = source.anchoredPosition;
            destination.sizeDelta = source.sizeDelta;
            destination.offsetMin = source.offsetMin;
            destination.offsetMax = source.offsetMax;
            destination.localRotation = source.localRotation;
            destination.localScale = source.localScale;
        }

        private static void Stretch(RectTransform rt)
        {
            if (rt == null)
                return;

            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;
        }

        private static void TryAssignHudFont(TextMeshProUGUI target)
        {
            if (target == null)
                return;

            try
            {
                HUDManager hud = HUDManager.Instance;
                TextMeshProUGUI[] tips = hud != null ? hud.controlTipLines : null;
                if (tips != null && tips.Length > 0 && tips[0] != null && tips[0].font != null)
                    target.font = tips[0].font;
            }
            catch { }
        }

        private static void ApplyTextureToMaterial(Material material, Texture texture)
        {
            if (material == null || texture == null)
                return;

            try { material.mainTexture = texture; } catch { }
            SetTextureIfExists(material, "_UnlitColorMap", texture);
            SetTextureIfExists(material, "_BaseColorMap", texture);
            SetTextureIfExists(material, "_BaseMap", texture);
            SetTextureIfExists(material, "_EmissiveColorMap", texture);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.white * 1.05f);
            if (material.HasProperty("_EmissiveColorLDR")) material.SetColor("_EmissiveColorLDR", Color.white);
            material.EnableKeyword("_EMISSION");
            material.EnableKeyword("_EMISSIVE_COLOR_MAP");
        }

        private static void SetTextureIfExists(Material material, string property, Texture texture)
        {
            try
            {
                if (material != null && material.HasProperty(property))
                    material.SetTexture(property, texture);
            }
            catch { }
        }

        private static void ReleaseRenderTexture(ref RenderTexture texture)
        {
            if (texture == null)
                return;

            if (texture.IsCreated())
                texture.Release();
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            if (root == null)
                return;

            root.layer = layer;
            for (int i = 0; i < root.transform.childCount; i++)
                SetLayerRecursive(root.transform.GetChild(i).gameObject, layer);
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            string path = transform.name;
            Transform parent = transform.parent;
            int depth = 0;
            while (parent != null && depth++ < 24)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
