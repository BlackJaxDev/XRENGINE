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
    string OutputIdentity);
