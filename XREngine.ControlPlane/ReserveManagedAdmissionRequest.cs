namespace XREngine.ControlPlane;

public sealed class ReserveManagedAdmissionRequest
{
    public string InstanceId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public int? LifetimeSeconds { get; set; }
}
