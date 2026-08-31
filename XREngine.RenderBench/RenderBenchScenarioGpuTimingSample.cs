using XREngine.Rendering.Occlusion;

namespace XREngine.RenderBench;

/// <summary>One source-frame-attributed Hi-Z timestamp observation; this is diagnostic evidence, not promotion data.</summary>
public sealed record RenderBenchScenarioGpuTimingSample
{
    public EOcclusionGpuElapsedAvailability Availability { get; init; }
    public ulong ElapsedNanoseconds { get; init; }
    public ulong SourceFrameId { get; init; }
    public ulong AgeFrames { get; init; }
    public ulong Sequence { get; init; }
}
