namespace XREngine.Rendering.Commands;

/// <summary>
/// Fail-closed fault that permanently quarantines one canonical database
/// instance after producer-private state changed but was not committed.
/// </summary>
public enum EAdvancedGpuScenePublicationFault : uint
{
    None = 0,
    SnapshotCaptureFailed = 1,
    LookupPublicationFailed = 2,
    InvariantFailure = 3,
}
