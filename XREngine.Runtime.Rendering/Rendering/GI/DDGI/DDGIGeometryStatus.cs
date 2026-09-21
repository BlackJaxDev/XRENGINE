namespace XREngine.Rendering.GI.DDGI;

/// <summary>Readiness state for the aggregate GPU DDGI geometry service.</summary>
public enum DDGIGeometryStatus
{
    Unprepared,
    PendingRenderThread,
    PendingGpuProgramLink,
    PendingSkinning,
    Ready,
    Failed,
    Disposed,
}
