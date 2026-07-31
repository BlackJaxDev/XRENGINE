namespace XREngine.Rendering.Profiling;

/// <summary>Lifecycle states for an asynchronous render profile job.</summary>
public enum RenderProfileState
{
    Created,
    Preparing,
    Stabilizing,
    Armed,
    Capturing,
    Draining,
    Completed,
    Failed,
    Cancelled,
}
