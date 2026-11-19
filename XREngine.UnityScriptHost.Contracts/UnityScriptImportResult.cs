namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Reports the parent decision to convert and publish worker output.
/// </summary>
public sealed record UnityScriptImportResult
{
    /// <summary>Gets the matching request identity.</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Gets the parent conversion and publication status.</summary>
    public UnityScriptImportStatus Status { get; init; }

    /// <summary>Gets the published native generation identity, when published.</summary>
    public string? PublishedGenerationId { get; init; }

    /// <summary>Gets the parent diagnostics.</summary>
    public IReadOnlyList<UnityBuildDiagnostic> Diagnostics { get; init; } = Array.Empty<UnityBuildDiagnostic>();

    /// <summary>Gets partial per-feature native conversion evidence, separate from publication status.</summary>
    public UnityConversionCoverageReport ConversionCoverage { get; init; } = new();
}
