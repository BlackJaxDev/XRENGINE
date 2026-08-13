using XREngine.Rendering;

namespace XREngine.RenderBench;

public sealed record RenderBenchResult
{
    public int SchemaVersion { get; init; } = 1;
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
    public required double FixedStepSeconds { get; init; }
    public required int RandomSeed { get; init; }
    public required bool FrozenWorld { get; init; }
    public required RenderBenchInputManifest DeterministicInputs { get; init; }
    public required long[] CpuFrameNanoseconds { get; init; }
    public required double[] GpuFrameNanoseconds { get; init; }
    public required long AllocatedBytesOnCaptureThread { get; init; }
    public string? OutputSha256 { get; init; }
    public required IReadOnlyList<RenderBenchGateResult> StabilityGates { get; init; }
}
