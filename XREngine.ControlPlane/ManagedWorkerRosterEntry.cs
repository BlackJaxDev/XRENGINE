namespace XREngine.ControlPlane;

/// <summary>Authoritative worker roster entry. A report contains the complete current roster.</summary>
public sealed class ManagedWorkerRosterEntry
{
    public string ReservationId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public ManagedAdmissionGrantPurpose CredentialPurpose { get; set; }
    public long CredentialEpoch { get; set; }
    public ManagedPlayerConnectionState State { get; set; }
    public DateTimeOffset ConnectedUtc { get; set; }
    public DateTimeOffset? SynchronizedUtc { get; set; }
}
