namespace Y4NGZCompany.ShipSystems.Surveillance
{
    public static class ShipTurretDebugApi
    {
        public static void StartPlacement()
        {
            ShipTurretController.StartDebugPlacement();
        }

        public static void StartPlacement(string layoutName)
        {
            ShipTurretController.StartDebugPlacement(layoutName);
        }

        public static void ShowPreview()
        {
            ShipTurretController.ShowDebugPreview();
        }

        public static void ClearPreview()
        {
            ShipTurretController.ClearDebugPreview();
        }

        public static string GetStatus()
        {
            return ShipTurretController.GetDebugStatus();
        }
    }
}
