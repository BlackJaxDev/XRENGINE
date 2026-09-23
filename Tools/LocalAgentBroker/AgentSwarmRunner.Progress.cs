using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

public sealed partial class AgentSwarmRunner
{
    private async ValueTask<AgentSwarmSnapshot> PublishAsync(string runId, AgentRunStatus status, Func<AgentSwarmSnapshot, CancellationToken, ValueTask>? progress, CancellationToken cancellationToken)
    {
        await _publishGate.WaitAsync(cancellationToken);
        try
        {
            AgentSwarmSnapshot snapshot;
            lock (_stateLock)
            {
                snapshot = new AgentSwarmSnapshot
                {
                    RunId = runId,
                    Status = status,
                    Failures = _failures.ToArray(),
                    Nodes = _nodes.Values.OrderBy(static node => node.Id, StringComparer.Ordinal).Select(static node => node.Snapshot()).ToArray(),
                    Usage = _nodes.Values.Aggregate(new AgentTokenUsage(), static (usage, node) => usage + node.Usage),
                    ArtifactCount = _nodes.Values.Where(static node => node.Role == AgentSwarmRole.Leaf).Sum(static node => node.Artifacts.Count),
                    ReservedOutputTokens = Volatile.Read(ref _reservedOutputTokens),
                    ReservedArtifactCharacters = Volatile.Read(ref _reservedArtifactCharacters),
                };
                Volatile.Write(ref _snapshot, snapshot);
            }
            if (progress is not null) await progress(snapshot, cancellationToken);
            return snapshot;
        }
        finally { _publishGate.Release(); }
    }
    private AgentSwarmRunResult Failure(string runId, AgentRunRequest request, IReadOnlyList<string> failures)
    {
        _failures = failures.ToList();
        AgentSwarmSnapshot snapshot = new() { RunId = runId, Status = AgentRunStatus.Failed, Failures = failures };
        Volatile.Write(ref _snapshot, snapshot);
        return new() { Aggregate = Aggregate(runId, request, AgentRunStatus.Failed, 0, string.Join("; ", failures)), Snapshot = snapshot };
    }
    private AgentRunResult Aggregate(string runId, AgentRunRequest request, AgentRunStatus status, long elapsed, string? failure, AgentFailureCategory failureCategory = AgentFailureCategory.Validation, string finalText = "", string diagnosticDetail = "") => new()
    {
        RunId = runId,
        Status = status,
        RequestedModel = LunaModel,
        ActualModel = _nodes.Count > 0 && _nodes.Values.All(static node => node.ActualModel == LunaModel) ? LunaModel : string.Empty,
        ElapsedMilliseconds = elapsed,
        FinalText = finalText,
        Usage = _nodes.Values.Aggregate(new AgentTokenUsage(), static (usage, node) => usage + node.Usage),
        TurnCount = _nodes.Values.Sum(static node => node.TurnCount),
        RetryCount = _nodes.Values.Sum(static node => node.RetryCount),
        ProviderAttempts = _nodes.Values.SelectMany(static node => node.ProviderAttempts).ToArray(),
        Failure = failure is null ? null : new AgentFailure { Category = failureCategory, Summary = failure, DiagnosticDetail = diagnosticDetail },
    };
    private void AddFailure(string failure) { lock (_stateLock) _failures.Add(failure); }
    private void MarkUnfinishedCancelled() { foreach (AgentSwarmMutableNode node in _nodes.Values) if (node.Status is AgentSwarmNodeStatus.Queued or AgentSwarmNodeStatus.Planning or AgentSwarmNodeStatus.Running or AgentSwarmNodeStatus.Reviewing) node.Status = AgentSwarmNodeStatus.Cancelled; }
    private static AgentSwarmException Fail(AgentSwarmMutableNode node, string failure, AgentFailureCategory category = AgentFailureCategory.Validation, string diagnosticDetail = "") { node.AddFailure(failure); node.Status = AgentSwarmNodeStatus.Failed; return new AgentSwarmException(category, $"{node.Id}: {failure}", diagnosticDetail); }
    private static int CountLines(string text) => text.Length == 0 ? 0 : text.Count(static c => c == '\n') + 1;
    private static int CountOccurrences(string source, string value) { int count = 0; int index = 0; while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; } return count; }
    private static bool HasForbiddenControl(string text) => text.Any(static character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t');
    private bool TryReserveArtifactCharacters(int characters)
    {
        while (true)
        {
            long reserved = Volatile.Read(ref _reservedArtifactCharacters);
            if (characters > MaxArtifactCharacters - reserved) return false;
            if (Interlocked.CompareExchange(ref _reservedArtifactCharacters, reserved + characters, reserved) == reserved) return true;
        }
    }
}
