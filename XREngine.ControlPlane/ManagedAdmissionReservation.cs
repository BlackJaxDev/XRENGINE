namespace XREngine.ControlPlane;

public sealed class ManagedAdmissionReservation
{
    public string ReservationId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public ManagedPlayerConnectionState State { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? ResumeUntilUtc { get; set; }
}
