namespace XREngine.AgentOrchestration;

/// <summary>
/// A safe, structured run failure suitable for logs and MCP output.
/// </summary>
public sealed record AgentFailure
{
    public AgentFailureCategory Category { get; init; }

    public string Summary { get; init; } = string.Empty;

    public bool Retryable { get; init; }

    public int? ProviderStatus { get; init; }

    public string DiagnosticDetail { get; init; } = string.Empty;
}
