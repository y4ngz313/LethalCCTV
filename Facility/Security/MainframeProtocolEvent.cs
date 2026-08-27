namespace Y4NGZCompany.Facility.Security
{
    internal enum MainframeProtocolEvent
    {
        None = 0,
        // 1 (FireAlarmTest) and 2 (CameraCalibrationSweep) were removed; the
        // numbering gap is deliberate so nothing silently inherits their values.
        LockdownDrill = 3,
    }
}
