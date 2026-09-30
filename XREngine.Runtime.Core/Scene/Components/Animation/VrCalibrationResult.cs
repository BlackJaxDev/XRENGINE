namespace XREngine.Components.Animation;

/// <summary>The explicit outcome of a calibration transaction. Failure never authorizes enabling IK.</summary>
public sealed record VrCalibrationResult(bool Success, string Message, object? Data = null)
{
    public static VrCalibrationResult Failure(string message) => new(false, message);
    public static VrCalibrationResult Completed(object data) => new(true, string.Empty, data);
}
