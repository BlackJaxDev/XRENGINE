namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Reports worker compilation, execution, and export without parent acceptance.
/// </summary>
public sealed record UnityBuildResult
{
    /// <summary>Gets the protocol version.</summary>
    public int ProtocolVersion { get; init; } = UnityBuildProtocol.Version;

    /// <summary>Gets the matching request identity.</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Gets the admitted source digest.</summary>
    public string SourceDigest { get; init; } = string.Empty;

    /// <summary>Gets the profile used by the worker.</summary>
    public UnityBuildProfile Profile { get; init; } = new();

    /// <summary>Gets the terminal worker status.</summary>
    public UnityBuildTerminalStatus Status { get; init; }

    /// <summary>Gets the compilation status.</summary>
    public UnityBuildStageStatus Compilation { get; init; }

    /// <summary>Gets the execution status.</summary>
    public UnityBuildStageStatus Execution { get; init; }

    /// <summary>Gets the export status.</summary>
    public UnityBuildStageStatus Export { get; init; }

    /// <summary>Gets the completed callback identifiers in execution order.</summary>
    public IReadOnlyList<string> CompletedCallbacks { get; init; } = Array.Empty<string>();

    /// <summary>Gets the observed dependency identifiers.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>Gets the bounded worker diagnostics.</summary>
    public IReadOnlyList<UnityBuildDiagnostic> Diagnostics { get; init; } = Array.Empty<UnityBuildDiagnostic>();

    /// <summary>Gets authoring evidence without treating it as native conversion support.</summary>
    public UnityConversionCoverageReport ConversionCoverage { get; init; } = new();

    /// <summary>Gets the processed graph descriptor, when export completed.</summary>
    public UnityProcessedGraphDescriptor? Graph { get; init; }

    /// <summary>Gets the content-addressed blob descriptors.</summary>
    public IReadOnlyList<UnityBlobDescriptor> Blobs { get; init; } = Array.Empty<UnityBlobDescriptor>();
}
