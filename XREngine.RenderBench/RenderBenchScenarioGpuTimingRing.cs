namespace XREngine.RenderBench;

/// <summary>Bounded query-ring state captured alongside delayed timestamp evidence.</summary>
public sealed record RenderBenchScenarioGpuTimingRing
{
    public int Capacity { get; init; }
    public int Available { get; init; }
    public int Open { get; init; }
    public int Pending { get; init; }
    public int Quarantined { get; init; }
    public int StartReady { get; init; }
    public int EndReady { get; init; }
    public int StartAbandoned { get; init; }
    public int EndAbandoned { get; init; }
}
