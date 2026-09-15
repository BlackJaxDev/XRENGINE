namespace XREngine.ControlPlane.Service;

/// <summary>Idempotent reservation request made on behalf of the authenticated API account.</summary>
public sealed class JoinLocalInstanceRequest
{
    public string OperationId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
}
