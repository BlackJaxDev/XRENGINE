namespace XREngine.AgentOrchestration;

/// <summary>
/// Bounded, provider-neutral text or image output returned by a model.
/// </summary>
public sealed record AgentOutputItem
{
    public AgentOutputItemKind Kind { get; init; }

    public string Text { get; init; } = string.Empty;

    public string? DataUri { get; init; }

    public string? FilePath { get; init; }
}
