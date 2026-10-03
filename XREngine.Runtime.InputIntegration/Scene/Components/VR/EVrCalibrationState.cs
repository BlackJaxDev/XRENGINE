namespace XREngine.Components.VR;

/// <summary>Calibration lifecycle. Temporary tracking loss does not erase a committed calibration.</summary>
public enum EVrCalibrationState { Uncalibrated, Calibrating, Calibrated, Failed }
