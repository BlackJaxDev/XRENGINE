namespace XREngine.UnityScriptHost.Execution;

/// <summary>
/// Maps exact member identifiers to declared execution capability.
/// </summary>
public sealed class UnityCompatibilityCatalog
{
    private readonly Dictionary<string, UnityCapabilityStatus> _members;

    /// <summary>
    /// Creates a catalog and rejects empty or duplicate member identifiers.
    /// </summary>
    public UnityCompatibilityCatalog(IEnumerable<KeyValuePair<string, UnityCapabilityStatus>> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        _members = new Dictionary<string, UnityCapabilityStatus>(StringComparer.Ordinal);
        foreach (var (identifier, status) in members)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                throw new ArgumentException("Member identifiers must not be empty.", nameof(members));
            if (!Enum.IsDefined(status))
                throw new ArgumentException("Capability status is invalid.", nameof(members));
            if (!_members.TryAdd(identifier, status))
                throw new ArgumentException($"Duplicate member identifier: {identifier}", nameof(members));
        }
    }

    /// <summary>
    /// Gets the declared capability. Unknown identifiers are unsupported.
    /// </summary>
    public UnityCapabilityStatus GetStatus(string memberIdentifier)
    {
        return TryGetStatus(memberIdentifier, out var status)
            ? status
            : UnityCapabilityStatus.Unsupported;
    }

    /// <summary>Distinguishes an explicit catalog declaration from an unclassified member.</summary>
    public bool TryGetStatus(string memberIdentifier, out UnityCapabilityStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberIdentifier);
        return _members.TryGetValue(memberIdentifier, out status);
    }

    /// <summary>
    /// Latches an unsupported call before it throws. This checks executed calls only.
    /// </summary>
    public void RequireImplemented(string memberIdentifier, UnityBuildExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (GetStatus(memberIdentifier) == UnityCapabilityStatus.Implemented)
            return;

        state.FailUnsupportedOperation(memberIdentifier);
        throw new NotSupportedException("Unity member has no executable implementation.");
    }
}
