using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.RenderBench;

public sealed record RenderBenchEffectiveConfiguration(
    int SchemaVersion,
    string Backend,
    RenderExecutionMode ExecutionMode,
    string Recipe,
    string Fixture,
    RenderTargetOutputProperties Output,
    int WarmupFrames,
    int StabilityFrames,
    int CaptureFrames,
    double FixedStepSeconds,
    int RandomSeed,
    bool FrozenWorld,
    EPixelInternalFormat ColorFormat,
    EPixelInternalFormat DepthFormat);
