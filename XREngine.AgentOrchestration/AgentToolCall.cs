namespace XREngine.AgentOrchestration;

/// <summary>
/// A function call emitted by a model, preserving its provider correlation ID.
/// </summary>
public sealed record AgentToolCall
{
    public string CallId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string ArgumentsJson { get; init; } = "{}";
}
