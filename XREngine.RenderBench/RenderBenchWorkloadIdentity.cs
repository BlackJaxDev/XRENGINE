using XREngine.Rendering;
using XREngine.Rendering.Profiling;

namespace XREngine.RenderBench;

/// <summary>
/// Underlying immutable work identity. Worker count, mutation policy, instrumentation, and
/// acceptance budgets are deliberately excluded so experiments remain comparable.
/// </summary>
public sealed record RenderBenchWorkloadIdentity(
    int SchemaVersion,
    string Backend,
    RenderExecutionMode ExecutionMode,
    string Component,
    string Fixture,
    RenderTargetOutputProperties Output,
    string SceneIdentity,
    string CameraIdentity,
    string[] LightIdentities,
    string AnimationIdentity,
    double FixedTimeStepSeconds,
    int RandomSeed,
    RenderProfileMeshStrategy MeshStrategy,
    string[] RenderFeatures,
    RenderProfileStereoMode StereoMode,
    string[] OutputIdentities,
    int ChainCount,
    int DrawCount,
    int DescriptorCount,
    int BarrierCount,
    int UploadBytes,
    int PassIterations,
    IReadOnlyDictionary<string, long> TargetInputs);
