namespace XREngine.Benchmarks;

/// <summary>
/// Nearest-rank distribution for one numeric frame metric.
/// </summary>
public sealed class VulkanPerformanceMetricStatistics
{
    public int SampleCount { get; init; }
    public double P50 { get; init; }
    public double P90 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Maximum { get; init; }
    public int MissedFiveMillisecondCount { get; init; }
    public int MissedEightPointThreeThreeMillisecondCount { get; init; }
}
