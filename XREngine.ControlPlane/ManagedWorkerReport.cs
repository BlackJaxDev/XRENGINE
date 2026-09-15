namespace XREngine.ControlPlane;

/// <summary>Ordered worker-to-manager reconciliation snapshot.</summary>
public sealed class ManagedWorkerReport
{
    public int ContractVersion { get; set; } = 1;
    public string InstanceId { get; set; } = string.Empty;
    public Guid Generation { get; set; }
    public long Sequence { get; set; }
    public ManagedWorkerState ObservedState { get; set; }
    public ManagedWorkerFailure? Failure { get; set; }
    public XREngine.Networking.WorldAssetIdentity? LoadedWorldAsset { get; set; }
    public int BoundUdpPort { get; set; }
    public long TickProgress { get; set; }
    public List<string> InstalledReservationIds { get; set; } = [];
    /// <summary>Reservation credential epochs installed in the worker's local admission cache.</summary>
    public Dictionary<string, long> InstalledGrantEpochs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ManagedWorkerRosterEntry> Roster { get; set; } = [];
    public ManagedWorkerMetrics Metrics { get; set; } = new();
    public DateTimeOffset ReportedUtc { get; set; }
}
