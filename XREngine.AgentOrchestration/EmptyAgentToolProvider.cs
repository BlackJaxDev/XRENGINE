namespace XREngine.AgentOrchestration;

/// <summary>
/// Exposes no local tools for a reasoning-only agent run.
/// </summary>
public sealed class EmptyAgentToolProvider : IAgentToolProvider
{
    public static EmptyAgentToolProvider Instance { get; } = new();

    private EmptyAgentToolProvider()
    {
    }

    public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AgentToolDefinition>>([]);

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolCall call,
        CancellationToken cancellationToken)
        => Task.FromException<AgentToolResult>(
            new InvalidOperationException("Reasoning-only agent runs do not expose local tools."));
}
