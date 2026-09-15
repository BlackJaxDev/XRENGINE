namespace XREngine.ControlPlane;

/// <summary>Manager-to-worker response to a report. Grants are a complete authoritative snapshot.</summary>
public sealed class ManagedWorkerDirective
{
    public int ContractVersion { get; set; } = 1;
    public string InstanceId { get; set; } = string.Empty;
    public Guid Generation { get; set; }
    public long DirectiveSequence { get; set; }
    public bool Draining { get; set; }
    public bool Stop { get; set; }
    public List<ManagedAdmissionGrant> AdmissionGrants { get; set; } = [];
    public List<string> RevokedReservationIds { get; set; } = [];
    public List<string> KickReservationIds { get; set; } = [];
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset FreshUntilUtc { get; set; }
}
