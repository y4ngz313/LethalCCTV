using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Y4NGZCompany.Facility.Security
{
    public static class CctvSecurityCameraRegistry
    {
        private static readonly List<CctvSecurityCameraState> Cameras = new List<CctvSecurityCameraState>();

        public static IReadOnlyList<CctvSecurityCameraState> RegisteredCameras => Cameras;

        public static void ResetRound()
        {
            Cameras.Clear();
            CctvSecurityDirector.OnCamerasRegistered(Cameras);
        }

        public static void RegisterCameras(IEnumerable<Component> cameras)
        {
            var previous = new List<CctvSecurityCameraState>(Cameras);
            Cameras.Clear();
            if (cameras == null)
            {
                CctvSecurityDirector.OnCamerasRegistered(Cameras);
                return;
            }

            foreach (Component component in cameras)
            {
                if (component == null) continue;
                int index = ReadIntProperty(component, "CameraIndex", Cameras.Count);
                string label = ReadStringProperty(component, "ResolvedLabel", $"CAM_{index:D2}");
                var state = new CctvSecurityCameraState(index, component, component.transform, label);
                CopyRuntimeState(FindPreviousState(previous, component, index), state);
                Cameras.Add(state);
            }

            CctvSecurityDirector.OnCamerasRegistered(Cameras);
        }

        public static void RegisterSupplementaryCamera(Component camera)
        {
            if (camera == null) return;
            int index = ReadIntProperty(camera, "CameraIndex", Cameras.Count);
            for (int i = 0; i < Cameras.Count; i++)
            {
                if (Cameras[i].CameraComponent == camera || Cameras[i].CameraIndex == index)
                    return;
            }

            string label = ReadStringProperty(camera, "ResolvedLabel", $"CAM_{index:D2}");
            Cameras.Add(new CctvSecurityCameraState(index, camera, camera.transform, label));
            CctvSecurityDirector.OnCamerasRegistered(Cameras);
        }

        public static void SetCameraHasAlarmFixture(Component camera, bool hasAlarmFixture)
        {
            CctvSecurityCameraState state = Find(camera);
            if (state == null || state.HasAlarmFixture == hasAlarmFixture)
                return;

            state.HasAlarmFixture = hasAlarmFixture;
            CctvSecurityDirector.OnCameraEligibilityChanged(state);
        }

        public static void MarkCameraBroken(Component camera)
        {
            CctvSecurityCameraState state = Find(camera);
            if (state == null) return;
            CctvSecurityDirector.OnCameraBroken(state);
        }

        public static bool IsSecurityActive(Component camera)
        {
            CctvSecurityCameraState state = Find(camera);
            return state != null && state.IsSecurityActive;
        }

        // #563: the presentation-facing counterpart of IsSecurityActive. See
        // CctvSecurityCameraState.IsDetectionLive for why the two differ.
        public static bool IsDetectionLive(Component camera)
        {
            CctvSecurityCameraState state = Find(camera);
            return state != null && state.IsDetectionLive;
        }

        public static bool IsCameraBroken(Component camera)
        {
            CctvSecurityCameraState state = Find(camera);
            return state != null && state.IsBroken;
        }

        public static CctvSecurityCameraState Find(Component camera)
        {
            if (camera == null) return null;
            for (int i = 0; i < Cameras.Count; i++)
                if (Cameras[i].CameraComponent == camera)
                    return Cameras[i];
            return null;
        }

        private static CctvSecurityCameraState FindPreviousState(
            List<CctvSecurityCameraState> previous,
            Component camera,
            int cameraIndex)
        {
            if (previous == null || previous.Count == 0)
                return null;

            for (int i = 0; i < previous.Count; i++)
            {
                CctvSecurityCameraState state = previous[i];
                if (state != null && state.CameraComponent == camera)
                    return state;
            }

            for (int i = 0; i < previous.Count; i++)
            {
                CctvSecurityCameraState state = previous[i];
                if (state != null && state.CameraIndex == cameraIndex)
                    return state;
            }

            return null;
        }

        private static void CopyRuntimeState(CctvSecurityCameraState source, CctvSecurityCameraState target)
        {
            if (source == null || target == null)
                return;

            target.HasAlarmFixture = source.HasAlarmFixture;
            target.IsSecurityActive = source.IsSecurityActive;
            target.IsBroken = source.IsBroken;
            target.IsSuspicious = source.IsSuspicious;
            target.DetectionProgressSeconds = source.DetectionProgressSeconds;
            target.LastDetectionAt = source.LastDetectionAt;
            target.LastSelectedAt = source.LastSelectedAt;
            target.TrackedPlayer = source.TrackedPlayer;
            target.PresentationIsSuspicious = source.PresentationIsSuspicious;
            target.PresentationTrackedPlayer = source.PresentationTrackedPlayer;
            target.PresentationSuspicionStartedAt = source.PresentationSuspicionStartedAt;
            target.PresentationSeesLocalPlayer = source.PresentationSeesLocalPlayer;
        }

        private static int ReadIntProperty(Component component, string name, int fallback)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo prop = component.GetType().GetProperty(name, flags);
            if (prop != null && prop.GetValue(component) is int value) return value;
            FieldInfo field = component.GetType().GetField(name, flags);
            return field != null && field.GetValue(component) is int typed ? typed : fallback;
        }

        private static string ReadStringProperty(Component component, string name, string fallback)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo prop = component.GetType().GetProperty(name, flags);
            if (prop != null && prop.GetValue(component) is string value) return value;
            FieldInfo field = component.GetType().GetField(name, flags);
            return field != null && field.GetValue(component) is string typed ? typed : fallback;
        }
    }
}
