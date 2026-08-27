using UnityEngine;

using Y4NGZCompany.Facility.Cameras;
namespace Y4NGZCompany.Facility.Security
{
    internal sealed class CctvSupportTickDriver : MonoBehaviour
    {
        private void Update()
        {
            CctvSupportState.EnsureInitialized();
            Y4NGZCompany.Facility.Security.CctvSecurityDirector.Tick();
            Y4NGZCompany.Facility.Security.CctvCameraShutdownSync.Tick();
            Y4NGZCompany.Facility.Security.MainframeProtocolDirector.Tick();
            CctvAlarmSystem.Tick();
            // Local presentation, built once the HUD canvas exists; it draws itself in
            // LateUpdate so it reads the detection sweep above in the same frame.
            CctvSpottingAlertHud.Ensure();
        }
    }
}
