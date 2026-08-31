using XREngine.Rendering.Occlusion;

namespace XREngine.RenderBench;

/// <summary>Delayed GPU timestamp evidence captured only after an exact production receipt completes.</summary>
public sealed record RenderBenchScenarioGpuTiming
{
    public RenderBenchScenarioGpuTimingSample Build { get; init; } = new();
    public RenderBenchScenarioGpuTimingSample Test { get; init; } = new();
    public RenderBenchScenarioGpuTimingRing Ring { get; init; } = new();
}
