namespace XREngine.Editor.Mcp;

/// <summary>
/// Describes the lifecycle of a viewport sequence capture session.
/// </summary>
internal enum ViewportSequenceCaptureState
{
    Created,
    Capturing,
    Stopping,
    Finalizing,
    Completed,
    Canceled,
    Failed,
}
