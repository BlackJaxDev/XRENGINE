using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>Thread-safe mutable state that backs one immutable swarm node snapshot.</summary>
internal sealed class AgentSwarmMutableNode
{
    private readonly object _gate = new();
    private AgentSwarmNodeStatus _status = AgentSwarmNodeStatus.Queued;
    private bool? _approved;
    private string _reviewSummary = string.Empty;
    private string _actualModel = string.Empty;
    private AgentTokenUsage _usage = new();
    private int _turnCount;
    private int _retryCount;
    private IReadOnlyList<AgentSwarmCodeChange> _artifacts = [];
    private readonly List<string> _failures = [];
    private readonly List<AgentProviderAttemptDiagnostic> _attempts = [];
    public AgentSwarmMutableNode(string id, string? parentId, int depth, AgentSwarmRole role, string objective, IReadOnlyList<string> paths) { Id = id; ParentId = parentId; Depth = depth; Role = role; Objective = objective; Paths = paths.ToArray(); }
    public string Id { get; }
    public string? ParentId { get; }
    public int Depth { get; }
    public AgentSwarmRole Role { get; }
    public string Objective { get; }
    public IReadOnlyList<string> Paths { get; }
    public AgentSwarmNodeStatus Status { get { lock (_gate) return _status; } set { lock (_gate) _status = value; } }
    public bool? Approved { get { lock (_gate) return _approved; } set { lock (_gate) _approved = value; } }
    public string ReviewSummary { get { lock (_gate) return _reviewSummary; } set { lock (_gate) _reviewSummary = value; } }
    public string ActualModel { get { lock (_gate) return _actualModel; } }
    public AgentTokenUsage Usage { get { lock (_gate) return _usage; } }
    public int TurnCount { get { lock (_gate) return _turnCount; } }
    public int RetryCount { get { lock (_gate) return _retryCount; } }
    public IReadOnlyList<AgentProviderAttemptDiagnostic> ProviderAttempts { get { lock (_gate) return _attempts.ToArray(); } }
    public IReadOnlyList<AgentSwarmCodeChange> Artifacts
    {
        get { lock (_gate) return _artifacts.ToArray(); }
        set { lock (_gate) _artifacts = value.ToArray(); }
    }
    public void AddFailure(string failure) { lock (_gate) _failures.Add(failure); }
    public void AddResult(AgentRunResult result) { lock (_gate) { _usage += result.Usage; _turnCount += result.TurnCount; _retryCount += result.RetryCount; _attempts.AddRange(result.ProviderAttempts); _actualModel = result.ActualModel; } }
    public AgentSwarmNodeSnapshot Snapshot() { lock (_gate) return new() { Id = Id, ParentId = ParentId, Depth = Depth, Role = Role, Objective = Objective, Status = _status, Approved = _approved, ReviewSummary = _reviewSummary, Failures = _failures.ToArray(), RequestedModel = "gpt-6-luna", ActualModel = _actualModel, Usage = _usage, ProviderAttempts = _attempts.ToArray(), Artifacts = _artifacts.ToArray() }; }
}
