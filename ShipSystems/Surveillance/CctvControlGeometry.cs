using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static class CctvControlGeometry
    {
        // Centers of the colored top faces in vanilla ControlPanelWTexture's
        // mesh coordinates, measured from its triangles and ControlPanelB UVs.
        internal static readonly Vector3 BlueButton = new Vector3(2.14224f, -3.48693f, 0.82560f);
        internal static readonly Vector3 GreenButton = new Vector3(5.93751f, 4.87375f, -1.69817f);
        internal static readonly Vector3 RedButton = new Vector3(0.27866f, 4.76938f, 3.21640f);
        internal static readonly Vector3 ButtonNormal = new Vector3(0.5209f, 0.005f, 0.8536f).normalized;

        // The vanilla glove's fingers extend along +Y and curl toward -Z.
        // Therefore +Z is the back of the hand and must face away from the grip.
        internal static Quaternion PalmDown(Vector3 normal, Vector3 fingersForward)
            => Quaternion.LookRotation(normal, Vector3.ProjectOnPlane(fingersForward, normal).normalized);

        internal static bool TryLeverHandleTop(Transform transform, Mesh mesh, out Vector3 top)
        {
            top = default;
            if (mesh == null || mesh.subMeshCount < 2) return false;
            Bounds local = mesh.GetSubMesh(1).bounds;
            if (local.size.sqrMagnitude < 0.000001f) return false;
            if (mesh.isReadable)
            {
                Vector3[] vertices = mesh.vertices;
                int[] indices = mesh.GetIndices(1);
                if (indices.Length == 0) return false;
                Bounds world = new Bounds(transform.TransformPoint(vertices[indices[0]]), Vector3.zero);
                foreach (int index in indices) world.Encapsulate(transform.TransformPoint(vertices[index]));
                top = new Vector3(world.center.x, world.max.y, world.center.z);
            }
            else
            {
                // The handle is round. Use its transformed ellipsoid height
                // above the center, not inflated rotated bounding-box corners.
                Vector3 x = transform.TransformVector(Vector3.right * local.extents.x);
                Vector3 y = transform.TransformVector(Vector3.up * local.extents.y);
                Vector3 z = transform.TransformVector(Vector3.forward * local.extents.z);
                float radius = Mathf.Sqrt(x.y * x.y + y.y * y.y + z.y * z.y);
                if (radius < 0.0001f) return false;
                top = transform.TransformPoint(local.center) + Vector3.up * radius;
            }
            return true;
        }

        internal static Vector3 FitMonitorEye(Vector3 eye, Quaternion rotation, Vector3 center,
            Vector3 right, Vector3 up, float halfWidth, float halfHeight, float fov, float aspect)
        {
            float tanY = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            float tanX = tanY * Mathf.Max(0.25f, aspect);
            float pullback = 0f;
            Quaternion inverse = Quaternion.Inverse(rotation);
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            {
                Vector3 corner = center + right * (halfWidth * x) + up * (halfHeight * y);
                Vector3 local = inverse * (corner - eye);
                pullback = Mathf.Max(pullback, Mathf.Abs(local.x) / (tanX * 0.84f) - local.z);
                pullback = Mathf.Max(pullback, Mathf.Abs(local.y) / (tanY * 0.84f) - local.z);
            }
            return eye - rotation * Vector3.forward * Mathf.Max(0f, pullback);
        }

        internal static bool TryKeepElbowBehindWrist(Vector3 center, Vector3 aim, float radius,
            Vector3 proposed, Vector3 wrist, Vector3 forward, out Vector3 elbow)
        {
            elbow = proposed;
            if (Vector3.Dot(proposed - wrist, forward) <= 0f) return true;
            Vector3 projected = Vector3.ProjectOnPlane(forward, aim);
            float span = projected.magnitude;
            if (radius < 0.000001f || span < 0.000001f) return false;
            float limit = Vector3.Dot(wrist - center, forward) / (radius * span);
            if (limit < -1f) return false; // No valid point on this reach circle.
            Vector3 axis = projected / span;
            Vector3 side = Vector3.Cross(aim, axis).normalized;
            if (Vector3.Dot(proposed - center, side) < 0f) side = -side;
            float component = Mathf.Clamp(limit, -1f, 1f);
            elbow = center + radius * (axis * component + side * Mathf.Sqrt(1f - component * component));
            return true;
        }

        internal static Vector3 PressPosition(float seconds, Vector3 start, Vector3 rest,
            Vector3 contact, Vector3 normal)
        {
            Vector3 hover = contact + normal * 0.070f;
            if (seconds < 0.30f) return Vector3.Lerp(start, hover, Ease(seconds / 0.30f));
            if (seconds < 0.44f) return Vector3.Lerp(hover, contact, Ease((seconds - 0.30f) / 0.14f));
            if (seconds < 0.51f) return contact;
            if (seconds < 0.66f) return Vector3.Lerp(contact, hover, Ease((seconds - 0.51f) / 0.15f));
            return Vector3.Lerp(hover, rest, Ease((seconds - 0.66f) / 0.34f));
        }

        internal static Vector3 EntryHandPosition(float seconds, Vector3 rest, Vector3 contact,
            Vector3 grip, Vector3 normal)
        {
            Vector3 hover = contact + normal * 0.055f;
            if (seconds < 0.27f) return Vector3.Lerp(rest, hover, Ease(seconds / 0.27f));
            if (seconds < CctvIntroTiming.PressContact)
                return Vector3.Lerp(hover, contact, Ease((seconds - 0.27f) / (CctvIntroTiming.PressContact - 0.27f)));
            if (seconds < CctvIntroTiming.PressRelease) return contact;
            if (seconds < CctvIntroTiming.PressRelease + 0.12f)
                return Vector3.Lerp(contact, hover, Ease((seconds - CctvIntroTiming.PressRelease) / 0.12f));
            float transfer = Ease((seconds - CctvIntroTiming.PressRelease - 0.12f) /
                (CctvIntroTiming.GripArrival - CctvIntroTiming.PressRelease - 0.12f));
            return Vector3.Lerp(hover, grip, transfer) + normal * (0.025f * Mathf.Sin(transfer * Mathf.PI));
        }

        internal static float Ease(float t) => CctvIntroTiming.Ease(Mathf.Clamp01(t));
    }
}
