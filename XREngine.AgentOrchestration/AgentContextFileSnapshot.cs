namespace XREngine.AgentOrchestration;

/// <summary>
/// Immutable repository text captured at broker-run admission.
/// </summary>
public sealed record AgentContextFileSnapshot
{
    public string Path { get; init; } = string.Empty;

    public int StartLine { get; init; }

    public int EndLine { get; init; }

    public int TotalLines { get; init; }

    public long RawByteLength { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;
}
