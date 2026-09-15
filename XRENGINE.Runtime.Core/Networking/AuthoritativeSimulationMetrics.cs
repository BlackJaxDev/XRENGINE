namespace XREngine.Networking;

/// <summary>Bounded counters sampled by host diagnostics for authoritative fixed-tick simulation.</summary>
public readonly record struct AuthoritativeSimulationMetrics(
    long ServerTickId,
    double ConfiguredFixedTickMilliseconds,
    double LastTickDurationMilliseconds,
    double LastTickBudgetHeadroomMilliseconds,
    long LastTickAllocatedBytes,
    long TotalSimulationTickCount,
    int SampleWindowTickCount,
    double SampleWindowAverageTickMilliseconds,
    double SampleWindowPeakTickMilliseconds,
    long SampleWindowAllocatedBytes,
    int InputQueueDepth,
    double OldestInputAgeMilliseconds,
    long SimulatedInputCount,
    long RejectedInputCount,
    long AuthoritativeTransformBytes);
