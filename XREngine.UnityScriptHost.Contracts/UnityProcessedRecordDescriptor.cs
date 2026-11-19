namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Identifies one typed, versioned record in processed output.
/// </summary>
public sealed record UnityProcessedRecordDescriptor
{
    /// <summary>Gets the record identity within the graph.</summary>
    public string RecordId { get; init; } = string.Empty;

    /// <summary>Gets the allowed record type identifier.</summary>
    public string TypeId { get; init; } = string.Empty;

    /// <summary>Gets the record schema version.</summary>
    public int Version { get; init; }

    /// <summary>Gets the digest of the encoded record data.</summary>
    public string DataDigest { get; init; } = string.Empty;

    /// <summary>Gets the encoded record size in bytes.</summary>
    public long DataBytes { get; init; }
}
