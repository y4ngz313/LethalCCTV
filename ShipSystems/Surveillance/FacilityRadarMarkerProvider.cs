using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Facility.Mainframe;
using Y4NGZCompany.Facility.Stash;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    /// <summary>
    /// One throttled source of truth for permanent facility fixtures shown on either
    /// CCTV radar implementation. Positions remain live through the retained Transform;
    /// only the allocating scene discovery is limited to once per second.
    /// </summary>
    internal static class FacilityRadarMarkerProvider
    {
        private const float RefreshIntervalSeconds = 1f;

        internal enum MarkerKind
        {
            Mainframe,
            CompanyStash,
        }

        internal readonly struct MarkerSnapshot
        {
            internal readonly Transform Target;
            internal readonly MarkerKind Kind;

            internal MarkerSnapshot(Transform target, MarkerKind kind)
            {
                Target = target;
                Kind = kind;
            }

            internal Vector3 Position => Target != null ? Target.position : Vector3.zero;
        }

        private static readonly List<MarkerSnapshot> Markers = new List<MarkerSnapshot>(8);
        private static float _nextRefreshAt;
        private static bool _hasSnapshot;

        internal static IReadOnlyList<MarkerSnapshot> GetMarkers()
        {
            if (!_hasSnapshot || Time.unscaledTime >= _nextRefreshAt || ContainsDestroyedTarget())
                Refresh();
            return Markers;
        }

        internal static void Clear()
        {
            Markers.Clear();
            _nextRefreshAt = 0f;
            _hasSnapshot = false;
        }

        private static void Refresh()
        {
            Markers.Clear();

            MainframeSupport[] mainframes = Object.FindObjectsOfType<MainframeSupport>(includeInactive: false);
            for (int i = 0; i < mainframes.Length; i++)
            {
                MainframeSupport mainframe = mainframes[i];
                if (mainframe != null && mainframe.isActiveAndEnabled)
                    Markers.Add(new MarkerSnapshot(mainframe.transform, MarkerKind.Mainframe));
            }

            CompanyStashController[] stashes = Object.FindObjectsOfType<CompanyStashController>(includeInactive: false);
            for (int i = 0; i < stashes.Length; i++)
            {
                CompanyStashController stash = stashes[i];
                if (stash != null && stash.isActiveAndEnabled)
                    Markers.Add(new MarkerSnapshot(stash.transform, MarkerKind.CompanyStash));
            }

            _nextRefreshAt = Time.unscaledTime + RefreshIntervalSeconds;
            _hasSnapshot = true;
        }

        private static bool ContainsDestroyedTarget()
        {
            for (int i = 0; i < Markers.Count; i++)
            {
                if (Markers[i].Target == null)
                    return true;
            }
            return false;
        }
    }
}
