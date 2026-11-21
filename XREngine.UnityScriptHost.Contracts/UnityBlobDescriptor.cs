namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Identifies one content-addressed output blob.
/// </summary>
public sealed record UnityBlobDescriptor
{
    /// <summary>Gets the content digest.</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>Gets the blob size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Gets the declared content type.</summary>
    public string ContentType { get; init; } = string.Empty;
}
