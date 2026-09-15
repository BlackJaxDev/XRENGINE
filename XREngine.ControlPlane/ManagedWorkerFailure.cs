namespace XREngine.ControlPlane;

public sealed class ManagedWorkerFailure
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool Retryable { get; set; }
}
