namespace XREngine.AgentOrchestration;

/// <summary>
/// Adapts host-owned tool listing and execution delegates to the reusable provider boundary.
/// </summary>
public sealed class DelegateAgentToolProvider(
    Func<CancellationToken, Task<IReadOnlyList<AgentToolDefinition>>> listTools,
    Func<AgentToolCall, CancellationToken, Task<AgentToolResult>> executeTool) : IAgentToolProvider
{
    public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
        => listTools(cancellationToken);

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolCall call,
        CancellationToken cancellationToken)
        => executeTool(call, cancellationToken);
}
