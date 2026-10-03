namespace XREngine.Components.VR;

/// <summary>Calibration state; degraded tracking does not discard a committed calibration.</summary>
public enum VrCalibrationState
{
    Uncalibrated,
    Calibrating,
    Calibrated,
    Failed,
}
