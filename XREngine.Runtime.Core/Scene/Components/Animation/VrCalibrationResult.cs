namespace XREngine.Components.Animation;

/// <summary>Outcome of an atomic rig capture, including a player-facing failure reason.</summary>
public sealed record VrCalibrationResult(bool Success, string Message = "", object? Data = null)
{
    public static VrCalibrationResult Failed(string error) => new(false, error);
    public static VrCalibrationResult Failure(string message) => Failed(message);
    public static VrCalibrationResult Completed(object? data = null) => new(true, Data: data);
}
