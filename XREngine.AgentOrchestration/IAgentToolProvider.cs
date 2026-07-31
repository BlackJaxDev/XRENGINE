namespace XREngine.AgentOrchestration;

/// <summary>
/// Lists and invokes the local tools made available to a model.
/// </summary>
public interface IAgentToolProvider
{
    Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken);

    Task<AgentToolResult> ExecuteAsync(AgentToolCall call, CancellationToken cancellationToken);
}
