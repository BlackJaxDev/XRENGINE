namespace XREngine.ControlPlane;

public sealed partial class InMemoryControlPlane
{
    private sealed class InstanceState
    {
        public bool IsManaged { get; init; }
        public string CreateFingerprint { get; init; } = string.Empty;
        public MultiplayerInstanceInfo Info { get; init; } = new();
        public Dictionary<string, MultiplayerPlayerInfo> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ManagedAdmissionGrant> Grants { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ManagedAdmissionReservation> Reservations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ReservationIdempotency { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ReservationFingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RevokedReservationIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> KickReservationIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> InstalledGrantEpochs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long LastWorkerReportSequence { get; set; } = -1;
        public DateTimeOffset LastWorkerReportedUtc { get; set; }
        public long DirectiveSequence { get; set; }
        public ManagedWorkerState RequestedState { get; set; } = ManagedWorkerState.Allocated;
        public ManagedWorkerState ObservedState { get; set; } = ManagedWorkerState.Allocated;
        public ManagedWorkerLaunch? Launch { get; set; }
        public bool ProcessExitConfirmed { get; set; }
    }
}
