using System;
using System.Reflection;
using UnityEngine;

namespace Y4NGZCompany.Core.Compat
{
    /// <summary>
    /// Soft bridge to Y4NGZCompany's themed gameplay HUD.
    ///
    /// The CCTV spotting-alert arcs want to live inside <c>GameplayHudHost</c>'s
    /// <c>EdgeAlertLayer</c> so they sort and fade with every other Y4NGZ edge alert, and
    /// they want to disappear when <c>GameplayUiVisibility</c> hides the HUD. Both of those
    /// types belong to the contracts plugin, which LethalCCTV must run without (#393), so
    /// they are resolved by name once and cached.
    ///
    /// With Y4NGZCompany absent the arcs still draw: the caller falls back to vanilla's own
    /// HUD canvas, and <see cref="GameplayUiVisible"/> answers true so nothing is suppressed.
    /// Losing the shared layer costs sorting niceties, not the effect.
    /// </summary>
    internal static class GameplayHudHostBridge
    {
        private const string HostTypeName =
            "Y4NGZCompany.Experience.UITheme.GameplayHudHost";

        private const string VisibilityTypeName =
            "Y4NGZCompany.Experience.UITheme.GameplayUiVisibility";

        private static bool _resolved;
        private static PropertyInfo _instanceProperty;
        private static PropertyInfo _sourceCanvasProperty;
        private static PropertyInfo _edgeAlertLayerProperty;
        private static PropertyInfo _isVisibleProperty;

        /// <summary>
        /// The themed HUD canvas when the contracts plugin is present and has built one,
        /// otherwise null so the caller can fall back.
        /// </summary>
        internal static Canvas SourceCanvas
        {
            get
            {
                object host = ResolveHost();
                if (host == null || _sourceCanvasProperty == null)
                    return null;

                try
                {
                    return _sourceCanvasProperty.GetValue(host) as Canvas;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// The shared edge-alert layer transform, or null when unavailable. Callers parent
        /// to the canvas root instead.
        /// </summary>
        internal static Transform EdgeAlertLayer
        {
            get
            {
                object host = ResolveHost();
                if (host == null || _edgeAlertLayerProperty == null)
                    return null;

                try
                {
                    return _edgeAlertLayerProperty.GetValue(host) as Transform;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Whether gameplay HUD elements should be drawn at all. Defaults to true, so an
        /// absent contracts plugin never silently hides this plugin's overlay.
        /// </summary>
        internal static bool GameplayUiVisible
        {
            get
            {
                Resolve();
                if (_isVisibleProperty == null)
                    return true;

                try
                {
                    return !(_isVisibleProperty.GetValue(null) is bool visible) || visible;
                }
                catch
                {
                    _isVisibleProperty = null;
                    return true;
                }
            }
        }

        private static object ResolveHost()
        {
            Resolve();
            if (_instanceProperty == null)
                return null;

            try
            {
                // GameplayHudHost is a MonoBehaviour, so a destroyed instance must be
                // filtered through Unity's fake-null check rather than a plain != null.
                var instance = _instanceProperty.GetValue(null) as UnityEngine.Object;
                return instance != null ? instance : null;
            }
            catch
            {
                _instanceProperty = null;
                return null;
            }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;

            const BindingFlags StaticMembers =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags InstanceMembers =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                Type hostType = CompanyAssemblyBridge.ResolveContractedType(HostTypeName);
                if (hostType != null)
                {
                    _instanceProperty = hostType.GetProperty("Instance", StaticMembers);
                    _sourceCanvasProperty = hostType.GetProperty("SourceCanvas", InstanceMembers);
                    _edgeAlertLayerProperty = hostType.GetProperty("EdgeAlertLayer", InstanceMembers);
                }

                Type visibilityType = CompanyAssemblyBridge.ResolveContractedType(VisibilityTypeName);
                if (visibilityType != null)
                    _isVisibleProperty = visibilityType.GetProperty("IsVisible", StaticMembers);
            }
            catch
            {
                _instanceProperty = null;
                _sourceCanvasProperty = null;
                _edgeAlertLayerProperty = null;
                _isVisibleProperty = null;
            }
        }
    }
}
