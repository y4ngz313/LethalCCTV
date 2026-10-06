using UnityEngine;

namespace Y4NGZCompany.Facility.Security
{
    internal static class AlarmSurfacePatch
    {
        private const float PlaneToleranceM = 0.08f;
        private const float NormalMinDot = 0.95f;

        // Mirrors SurfaceMount.IsStructuralPatch (S1, #1313); replace with that call once S1 lands.
        internal static bool IsStructuralPatch(in RaycastHit hit, Vector2 halfExtents, int mask, out string reason)
        {
            reason = "accepted";
            Vector3 normal = hit.normal;
            if (Mathf.Abs(normal.y) >= 0.4f)
            {
                reason = "structural-slope";
                return false;
            }
            if (InteriorAlarmSpawner.IsForbiddenMountSurface(hit.collider))
            {
                reason = "structural-forbidden-surface";
                return false;
            }

            Vector3 tangent = Vector3.Cross(Vector3.up, normal);
            if (tangent.sqrMagnitude < 0.001f)
            {
                reason = "structural-invalid-tangent";
                return false;
            }
            tangent.Normalize();
            Bounds bounds = hit.collider.bounds;
            float tangentWidth = 2f * (Mathf.Abs(tangent.x) * bounds.extents.x + Mathf.Abs(tangent.z) * bounds.extents.z);
            if (tangentWidth < 1f && bounds.size.y < 1f)
            {
                reason = "structural-small-surface";
                return false;
            }

            for (int axis = 0; axis < 2; axis++)
            {
                Vector3 offset = axis == 0 ? tangent * halfExtents.x : Vector3.up * halfExtents.y;
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 origin = hit.point + normal * 0.5f + offset * sign;
                    if (!Physics.Raycast(origin, -normal, out RaycastHit sample, 1f, mask, QueryTriggerInteraction.Ignore))
                    {
                        reason = "structural-missing-patch";
                        return false;
                    }
                    if (Vector3.Dot(sample.normal, normal) < NormalMinDot
                        || Mathf.Abs(Vector3.Dot(sample.point - hit.point, normal)) > PlaneToleranceM)
                    {
                        reason = "structural-uneven-patch";
                        return false;
                    }
                    if (InteriorAlarmSpawner.IsForbiddenMountSurface(sample.collider))
                    {
                        reason = "structural-forbidden-patch";
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
