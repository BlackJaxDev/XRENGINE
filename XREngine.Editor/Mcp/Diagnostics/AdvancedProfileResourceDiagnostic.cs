namespace XREngine.Editor.Mcp;

/// <summary>
/// Snapshot of one realized or declared resource relevant to the Advanced profile.
/// </summary>
internal readonly record struct AdvancedProfileResourceDiagnostic(
    string Name,
    string ResourceKind,
    string Category,
    bool Realized,
    bool CounterResource);
