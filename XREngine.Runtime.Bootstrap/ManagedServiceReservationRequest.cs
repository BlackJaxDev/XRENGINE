namespace XREngine.Runtime.Bootstrap;

/// <summary>Stable client reservation request sent to the local managed-instance service.</summary>
public sealed class ManagedServiceReservationRequest
{
    public string OperationId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
}
