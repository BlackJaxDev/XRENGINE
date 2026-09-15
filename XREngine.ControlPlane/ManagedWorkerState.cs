namespace XREngine.ControlPlane;

/// <summary>Observed lifecycle state reported by a managed dedicated-server worker.</summary>
public enum ManagedWorkerState
{
    Allocated = 0,
    Staging,
    Starting,
    Ready,
    Draining,
    Stopping,
    Stopped,
    Failed,
}
