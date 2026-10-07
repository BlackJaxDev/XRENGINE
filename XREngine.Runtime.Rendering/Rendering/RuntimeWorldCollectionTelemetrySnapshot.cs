namespace XREngine.Rendering;

/// <summary>Contains cumulative world collection times in Stopwatch ticks.</summary>
public readonly record struct RuntimeWorldCollectionTelemetrySnapshot(
    bool Enabled,
    long StopwatchFrequency,
    long CollectCalls,
    long CollectMatrixTicks,
    long CollectMeshTicks,
    long CollectSceneTicks,
    long SwapCalls,
    long SwapMatrixTicks,
    long SwapMeshTicks,
    long SwapSceneTicks);
