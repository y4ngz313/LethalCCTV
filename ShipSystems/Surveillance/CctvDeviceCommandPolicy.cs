using System;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal enum CctvDeviceAction { Invalid, OpenDoor, AlreadyOpen, Unpowered, DisableTurret, Cooldown }

    internal static class CctvDeviceCommandPolicy
    {
        internal static CctvDeviceAction Decide(string input, bool door, bool open, bool powered, bool cooling, bool turretActive)
        {
            string command = (input ?? string.Empty).Trim();
            if (door)
            {
                if (!command.Equals("unlock", StringComparison.OrdinalIgnoreCase) && !command.Equals("open", StringComparison.OrdinalIgnoreCase))
                    return CctvDeviceAction.Invalid;
                if (!powered) return CctvDeviceAction.Unpowered;
                return open ? CctvDeviceAction.AlreadyOpen : CctvDeviceAction.OpenDoor;
            }
            if (!command.Equals("disable", StringComparison.OrdinalIgnoreCase)) return CctvDeviceAction.Invalid;
            return cooling || !turretActive ? CctvDeviceAction.Cooldown : CctvDeviceAction.DisableTurret;
        }
    }
}
