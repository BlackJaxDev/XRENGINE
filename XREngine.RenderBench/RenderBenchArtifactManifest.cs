namespace XREngine.RenderBench;

/// <summary>Paths to bounded evidence stored beneath one run root.</summary>
public sealed record RenderBenchArtifactManifest(
    string RunRoot,
    string RecipePath,
    string EffectiveConfigurationPath,
    string WorkloadIdentityPath,
    string ResultPath,
    IReadOnlyList<string> RawFrameStreams,
    IReadOnlyList<string> CpuSpans,
    IReadOnlyList<string> GpuQueries,
    IReadOnlyList<string> ValidationLogs,
    IReadOnlyList<string> OptionalTraces,
    IReadOnlyList<string> OptionalImages)
{
    /// <summary>External GPU capture files copied into this profile after drain.</summary>
    public IReadOnlyList<string> OptionalCaptures { get; init; } = [];
}
