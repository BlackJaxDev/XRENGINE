namespace XREngine.ControlPlane;

/// <summary>Durable local control-plane checkpoint. It is an internal host-agent artifact and must be protected at rest.</summary>
public sealed class DurableControlPlaneSnapshot
{
    /// <summary>Rejects incompatible or malformed checkpoints instead of partially importing them.</summary>
    public int Version { get; set; } = 1;
    public List<DurableControlPlaneInstance> Instances { get; set; } = [];
}

public sealed class DurableControlPlaneInstance
{
    public MultiplayerInstanceInfo Info { get; set; } = new();
    public bool IsManaged { get; set; }
    public string CreateFingerprint { get; set; } = string.Empty;
    public Dictionary<string, ManagedAdmissionReservation> Reservations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ManagedAdmissionGrant> Grants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ReservationIdempotency { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ReservationFingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RevokedReservationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> KickReservationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> InstalledGrantEpochs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ManagedWorkerLaunch? Launch { get; set; }
    public ManagedWorkerState RequestedState { get; set; }
    public ManagedWorkerState ObservedState { get; set; }
    public long LastWorkerReportSequence { get; set; }
    public DateTimeOffset LastWorkerReportedUtc { get; set; }
    public long DirectiveSequence { get; set; }
    public bool ProcessExitConfirmed { get; set; }
}
