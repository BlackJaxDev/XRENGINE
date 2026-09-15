namespace XREngine.ControlPlane;

public sealed class ManagedWorkerMetrics
{
    public int ConnectedPlayers { get; set; }
    public int SynchronizedPlayers { get; set; }
    public double TickRate { get; set; }
    public long WorkingSetBytes { get; set; }
    public double CpuPercent { get; set; }
    public double LastSimulationTickMilliseconds { get; set; }
    public double ConfiguredFixedTickMilliseconds { get; set; }
    public double LastTickBudgetHeadroomMilliseconds { get; set; }
    public long LastSimulationTickAllocatedBytes { get; set; }
    public long TotalSimulationTickCount { get; set; }
    public int SimulationMetricWindowTickCount { get; set; }
    public double SimulationMetricWindowAverageTickMilliseconds { get; set; }
    public double SimulationMetricWindowPeakTickMilliseconds { get; set; }
    public long SimulationMetricWindowAllocatedBytes { get; set; }
    public int InputQueueDepth { get; set; }
    public double OldestInputAgeMilliseconds { get; set; }
    public long SimulatedInputCount { get; set; }
    public long RejectedInputCount { get; set; }
    public long AuthoritativeTransformBytes { get; set; }
    /// <summary>Encrypted ingress counters are cumulative for this worker generation, excluding TLS framing overhead.</summary>
    public bool EncryptedIngressListening { get; set; }
    public int EncryptedConnections { get; set; }
    public long RejectedEncryptedConnections { get; set; }
    public long EncryptedReceivedBytes { get; set; }
    public long EncryptedSentBytes { get; set; }
}
