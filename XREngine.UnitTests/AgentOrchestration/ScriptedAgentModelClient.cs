using XREngine.AgentOrchestration;

namespace XREngine.UnitTests.AgentOrchestration;

internal sealed class ScriptedAgentModelClient : IAgentModelClient
{
    private readonly Queue<Func<AgentModelTurnRequest, CancellationToken, Task<AgentModelTurnResult>>> _turns = new();

    public void Enqueue(AgentModelTurnResult result)
        => _turns.Enqueue((_, _) => Task.FromResult(result));

    public void Enqueue(
        Func<AgentModelTurnRequest, CancellationToken, Task<AgentModelTurnResult>> turn)
        => _turns.Enqueue(turn);

    public List<AgentModelTurnRequest> Requests { get; } = [];

    public Task<AgentModelTurnResult> CreateResponseAsync(
        AgentModelTurnRequest request,
        IAgentRunObserver observer,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_turns.Count == 0)
            throw new InvalidOperationException("No scripted model turn remains.");
        return _turns.Dequeue()(request, cancellationToken);
    }
}
