namespace XREngine.Components.Lights;

/// <summary>Progress of one capture step, including source-epoch invalidation.</summary>
public enum ECaptureStepResult
{
    Pending,
    Completed,
    RestartRequired,
    Cancelled,
}
