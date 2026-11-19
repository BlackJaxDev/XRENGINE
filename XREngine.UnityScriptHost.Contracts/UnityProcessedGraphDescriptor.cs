namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Describes bounded processed output for parent validation.
/// </summary>
public sealed record UnityProcessedGraphDescriptor
{
    /// <summary>Gets the graph schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Gets the digest of the complete graph.</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>Gets the graph root record identity.</summary>
    public string RootRecordId { get; init; } = string.Empty;

    /// <summary>Gets the encoded graph size in bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Gets the typed record descriptors.</summary>
    public IReadOnlyList<UnityProcessedRecordDescriptor> Records { get; init; } = Array.Empty<UnityProcessedRecordDescriptor>();
}
