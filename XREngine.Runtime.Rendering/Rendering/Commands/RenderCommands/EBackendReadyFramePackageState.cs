namespace XREngine.Rendering.Commands;

/// <summary>
/// Describes the ownership state of a backend-ready frame package.
/// </summary>
public enum EBackendReadyFramePackageState
{
    Empty = 0,
    Prepared = 1,
    Published = 2,
    Cancelled = 3,
}
