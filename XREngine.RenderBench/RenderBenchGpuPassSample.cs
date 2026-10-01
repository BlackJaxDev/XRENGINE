namespace XREngine.RenderBench;

/// <summary>One valid queue-local GPU pass interval from a completed frame slot.</summary>
public readonly record struct RenderBenchGpuPassSample(
    string Target,
    int PassIndex,
    ulong SourceFrameId,
    uint QueueFamilyIndex,
    ulong BeginTicks,
    ulong EndTicks,
    double ElapsedNanoseconds,
    ulong ReadbackLatencyFrames);
