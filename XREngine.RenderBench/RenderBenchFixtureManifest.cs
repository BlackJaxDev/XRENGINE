namespace XREngine.RenderBench;

public sealed record RenderBenchFixtureManifest(
    int SchemaVersion,
    string Name,
    string Component,
    RenderBenchFixtureKind Kind,
    string[] Inclusions,
    string[] Exclusions,
    int ChainCount,
    int DrawCount,
    int DescriptorCount,
    int BarrierCount,
    int UploadBytes,
    int PassIterations,
    int WorkerCount,
    string MutationPolicy,
    string OutputIdentity)
{
    /// <summary>The measured scope; production full frames must be distinguished from pass proxies.</summary>
    public string EvidenceScope { get; init; } = "component";

    /// <summary>Work counters actually observed by this fixture; omitted counters are unavailable.</summary>
    public string[] ObservableWorkCounters { get; init; } =
        ["Draws", "Dispatches", "Submissions", "CommandBuffers", "Descriptors", "Barriers", "UploadBytes", "PassIterations", "CommandBufferDecisions"];
}
