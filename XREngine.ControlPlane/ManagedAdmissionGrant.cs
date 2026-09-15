namespace XREngine.ControlPlane;

/// <summary>A single-player admission credential. It is never included in public directory data.</summary>
public sealed class ManagedAdmissionGrant
{
    public string ReservationId { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public ManagedAdmissionGrantPurpose Purpose { get; set; }
    public long CredentialEpoch { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public Guid Generation { get; set; }
    public string WorldId { get; set; } = string.Empty;
    public string WorldRevision { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
    public DateTimeOffset ExpiresUtc { get; set; }
    public ManagedAdmissionSignature? Signature { get; set; }
}
