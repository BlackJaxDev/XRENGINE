using XREngine.Rendering;

namespace XREngine.RenderBench;

/// <summary>Stable catalog metadata describing one deterministic profiling fixture.</summary>
public sealed record RenderBenchFixtureDefinition(
    string Name,
    string Component,
    RenderBenchFixtureKind Kind,
    RenderExecutionMode[] ExecutionModes,
    string[] Inclusions,
    string[] Exclusions,
    int DefaultChainCount = 1,
    int DefaultDrawCount = 0,
    int DefaultDescriptorCount = 0,
    int DefaultBarrierCount = 2,
    int DefaultUploadBytes = 0,
    int DefaultPassIterations = 1,
    bool SupportsOutputHash = true);
