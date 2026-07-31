using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

internal sealed class RecordingAgentToolProvider : IAgentToolProvider
{
    private readonly IReadOnlyList<AgentToolDefinition> _tools;
    private readonly Func<AgentToolCall, AgentToolResult> _execute;

    public RecordingAgentToolProvider(
        IReadOnlyList<AgentToolDefinition>? tools = null,
        Func<AgentToolCall, AgentToolResult>? execute = null)
    {
        _tools = tools ?? [];
        _execute = execute ?? (_ => new AgentToolResult { Content = "ok" });
    }

    public List<AgentToolCall> Calls { get; } = [];

    public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
        => Task.FromResult(_tools);

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolCall call,
        CancellationToken cancellationToken)
    {
        Calls.Add(call);
        return Task.FromResult(_execute(call));
    }
}
