namespace XREngine.RenderBench;

/// <summary>Timestamp capability, query budget, and delayed pass observations.</summary>
public sealed record RenderBenchGpuDiagnosticSnapshot(
    uint TimestampValidBits,
    double TimestampPeriodNanoseconds,
    uint QueueFamilyIndex,
    int QueriesPerFrame,
    long QueryBytes,
    long SkippedScopes,
    long BudgetOverflowScopes,
    long AbandonedQueries,
    RenderBenchGpuPassSample[] Samples)
{
    public bool CalibrationRequested { get; init; }
    public long SelectedScopes { get; init; }
    public RenderBenchGpuCalibrationSample? PreCalibration { get; init; }
    public RenderBenchGpuCalibrationSample? PostCalibration { get; init; }
}
