namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Result codes returned by <c>MotionBricksBridge.Native.dll</c>.
/// </summary>
public enum EMotionBricksBridgeResult
{
    Success = 0,
    NoData = 1,
    InvalidArgument = 2,
    BackendUnavailable = 3,
    ContractMismatch = 4,
    EngineBuildFailed = 5,
    InferenceFailed = 6,
    InternalError = 7,
}
