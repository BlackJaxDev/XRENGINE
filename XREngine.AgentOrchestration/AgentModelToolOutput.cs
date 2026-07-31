namespace XREngine.AgentOrchestration;

/// <summary>
/// Correlated tool output supplied to the provider on a continuation turn.
/// </summary>
public sealed record AgentModelToolOutput
{
    public string CallId { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public string? ImageDataUri { get; init; }
}
