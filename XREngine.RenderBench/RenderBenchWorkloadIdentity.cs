using XREngine.Rendering;

namespace XREngine.RenderBench;

public sealed record RenderBenchWorkloadIdentity(
    int SchemaVersion,
    string Backend,
    RenderExecutionMode ExecutionMode,
    string Recipe,
    string Fixture,
    RenderTargetOutputProperties Output,
    double FixedStepSeconds,
    int RandomSeed,
    bool FrozenWorld,
    string SyntheticCamera,
    string SyntheticAnimation);
