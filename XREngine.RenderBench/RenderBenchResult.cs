using XREngine.Rendering;

namespace XREngine.RenderBench;

public sealed record RenderBenchResult
{
    public int SchemaVersion { get; init; } = 2;
    public required string RunId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required string Backend { get; init; }
    public required RenderExecutionMode ExecutionMode { get; init; }
    public required string Recipe { get; init; }
    public required string Fixture { get; init; }
    public required string ExecutablePath { get; init; }
    public required string ExecutableSha256 { get; init; }
    public required string EffectiveConfigurationSha256 { get; init; }
    public required string WorkloadSha256 { get; init; }
    public required string EffectiveConfigurationPath { get; init; }
    public required string WorkloadIdentityPath { get; init; }
    public required string AdapterName { get; init; }
    public required uint DriverVersion { get; init; }
    public required uint VendorId { get; init; }
    public required uint DeviceId { get; init; }
    public required string PresentationDescription { get; init; }
    public required RenderTargetOutputProperties Output { get; init; }
    public required int ProcessId { get; init; }
    public required int WarmupFrames { get; init; }
    public required int StabilityFrames { get; init; }
    public required int CaptureFrames { get; init; }
    public required int Repetitions { get; init; }
    public required double FixedStepSeconds { get; init; }
    public required int RandomSeed { get; init; }
    public required bool FrozenWorld { get; init; }
    /// <summary>Specialized or benchmark-only GENERAL image-layout policy used by the GPU fixture.</summary>
    public required string LayoutPolicy { get; init; }
    public required RenderBenchInputManifest DeterministicInputs { get; init; }
    public required RenderBenchFixtureManifest FixtureManifest { get; init; }
    public required RenderBenchWorkCounters WorkCounters { get; init; }
    public required long[] CpuFrameNanoseconds { get; init; }
    public required double[] GpuFrameNanoseconds { get; init; }
    public required long AllocatedBytesOnCaptureThread { get; init; }
    public required long? AllocatedBytesOnFixtureWorkers { get; init; }
    public string? OutputSha256 { get; init; }
    public string? OutputImagePath { get; init; }
    public required IReadOnlyList<RenderBenchGateResult> StabilityGates { get; init; }
    /// <summary>Exact source and process identity. Missing values make clean comparison ineligible.</summary>
    public RenderBenchSourceIdentity? Source { get; init; }
    public RenderBenchEnvironment? Environment { get; init; }
    public RenderBenchPhaseIntervals? Intervals { get; init; }
    public RenderBenchMetricStatistics? CpuFrameStatistics { get; init; }
    public RenderBenchMetricStatistics? GpuFrameStatistics { get; init; }
    public RenderBenchArtifactManifest? ArtifactManifest { get; init; }
    public RenderBenchPromotionEvidence? PromotionEvidence { get; init; }
    public string? RecipeSha256 { get; init; }
    public bool IsIntrusive { get; init; }
    public RenderBenchTargetManifest? TargetManifest { get; init; }
    public double? OperationsPerSecond { get; init; }
    public RenderBenchCommandBufferActivity? CommandBufferActivity { get; init; }
}
