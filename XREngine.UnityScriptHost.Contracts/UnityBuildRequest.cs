namespace XREngine.UnityScriptHost.Contracts;

/// <summary>
/// Identifies one bounded build from an immutable source snapshot.
/// </summary>
public sealed record UnityBuildRequest
{
    /// <summary>Gets the protocol version.</summary>
    public int ProtocolVersion { get; init; } = UnityBuildProtocol.Version;

    /// <summary>Gets the request identity.</summary>
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Gets the digest of the complete admitted source snapshot.</summary>
    public string SourceDigest { get; init; } = string.Empty;

    /// <summary>Gets the digest of the exact package lock.</summary>
    public string PackageLockDigest { get; init; } = string.Empty;

    /// <summary>Gets the selected scene or prefab identity.</summary>
    public string SelectedSourceId { get; init; } = string.Empty;

    /// <summary>Gets the selected root identity.</summary>
    public string SelectedRootId { get; init; } = string.Empty;

    /// <summary>Gets the selected build profile.</summary>
    public UnityBuildProfile Profile { get; init; } = new();

    /// <summary>Gets the target platform identifier.</summary>
    public string TargetPlatform { get; init; } = string.Empty;

    /// <summary>Gets the declared build context identifier.</summary>
    public string BuildContext { get; init; } = string.Empty;

    /// <summary>Gets the ordered entry point identifiers.</summary>
    public IReadOnlyList<string> EntryPoints { get; init; } = Array.Empty<string>();

    /// <summary>Gets the resource and output limits.</summary>
    public UnityBuildLimits Limits { get; init; } = new();
}
