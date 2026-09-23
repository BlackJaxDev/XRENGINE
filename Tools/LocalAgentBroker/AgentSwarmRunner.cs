using System.Collections.Concurrent;
using System.Diagnostics;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>Runs a bounded, read-only hierarchy that produces reviewed code proposals.</summary>
public sealed partial class AgentSwarmRunner(AgentOrchestrator orchestrator, SemaphoreSlim providerSlots)
{
    private const string LunaModel = "gpt-6-luna";
    private const string LunaEffort = "max";
    private const int MaxObjectiveCharacters = 8_000;
    private const int MaxArtifactCharacters = 131_072;
    private readonly AgentOrchestrator _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    private readonly SemaphoreSlim _providerSlots = providerSlots ?? throw new ArgumentNullException(nameof(providerSlots));
    private readonly ConcurrentDictionary<string, AgentSwarmMutableNode> _nodes = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private SemaphoreSlim? _swarmSlots;
    private AgentSwarmSnapshot _snapshot = new();
    private int _admittedNodes;
    private long _reservedOutputTokens;
    private long _reservedArtifactCharacters;
    private List<string> _failures = [];
    private int _started;

    /// <summary>Most recently published immutable progress snapshot.</summary>
    public AgentSwarmSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async Task<AgentSwarmRunResult> RunAsync(string runId, AgentRunRequest rootRequest, IReadOnlyList<AgentContextFileSnapshot> snapshots, Func<AgentSwarmSnapshot, CancellationToken, ValueTask>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("AgentSwarmRunner instances are single-use.");
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(rootRequest);
        ArgumentNullException.ThrowIfNull(snapshots);
        AgentSwarmOptions options = GetEffectiveOptions(rootRequest);
        IReadOnlyList<string> validation = Validate(options, snapshots);
        if (validation.Count > 0)
        {
            _publishGate.Dispose();
            return Failure(runId, rootRequest, validation);
        }
        _nodes.Clear(); _failures = []; _admittedNodes = 1; _reservedOutputTokens = 0; _reservedArtifactCharacters = 0;
        _swarmSlots = new SemaphoreSlim(options.MaxParallelAgents, options.MaxParallelAgents);
        var files = snapshots.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.MaxElapsedSeconds));
        Stopwatch stopwatch = Stopwatch.StartNew();
        var root = new AgentSwarmMutableNode("root", null, 0, AgentSwarmRole.Orchestrator, rootRequest.Objective, options.AllowedPaths);
        _nodes[root.Id] = root;
        try
        {
            await PublishAsync(runId, AgentRunStatus.Running, progress, deadline.Token);
            IReadOnlyList<AgentSwarmCodeChange> changes = await RunNodeAsync(runId, rootRequest, options, files, root, progress, deadline.Token);
            AgentSwarmSnapshot snapshot = await PublishAsync(runId, AgentRunStatus.Completed, progress, CancellationToken.None);
            return new() { Aggregate = Aggregate(runId, rootRequest, AgentRunStatus.Completed, stopwatch.ElapsedMilliseconds, null, finalText: $"Approved {changes.Count} read-only code change proposal(s)."), Snapshot = snapshot, Changes = changes };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkUnfinishedCancelled();
            return new() { Aggregate = Aggregate(runId, rootRequest, AgentRunStatus.Cancelled, stopwatch.ElapsedMilliseconds, "The swarm was cancelled.", AgentFailureCategory.Cancelled), Snapshot = await PublishAsync(runId, AgentRunStatus.Cancelled, progress, CancellationToken.None) };
        }
        catch (OperationCanceledException)
        {
            AddFailure("The swarm exceeded its elapsed-time budget."); MarkUnfinishedCancelled();
            return new() { Aggregate = Aggregate(runId, rootRequest, AgentRunStatus.Failed, stopwatch.ElapsedMilliseconds, "The swarm exceeded its elapsed-time budget.", AgentFailureCategory.BudgetExceeded), Snapshot = await PublishAsync(runId, AgentRunStatus.Failed, progress, CancellationToken.None) };
        }
        catch (Exception exception)
        {
            AddFailure(exception.Message); MarkUnfinishedCancelled();
            AgentFailureCategory category = exception is AgentSwarmException swarmException ? swarmException.Category : AgentFailureCategory.Internal;
            string detail = exception is AgentSwarmException swarmFailure ? swarmFailure.DiagnosticDetail : string.Empty;
            return new() { Aggregate = Aggregate(runId, rootRequest, AgentRunStatus.Failed, stopwatch.ElapsedMilliseconds, exception.Message, category, diagnosticDetail: detail), Snapshot = await PublishAsync(runId, AgentRunStatus.Failed, progress, CancellationToken.None) };
        }
        finally
        {
            _swarmSlots?.Dispose();
            _swarmSlots = null;
            _publishGate.Dispose();
        }
    }

    private static AgentSwarmOptions GetEffectiveOptions(AgentRunRequest request)
    {
        AgentSwarmOptions options = request.Swarm ?? throw new ArgumentException("A swarm request requires swarm options.", nameof(request));
        return options with { MaxOutputTokens = request.Budget.MaxOutputTokens > 0 ? Math.Min(options.MaxOutputTokens, request.Budget.MaxOutputTokens) : options.MaxOutputTokens, MaxPhaseOutputTokens = request.Budget.MaxOutputTokens > 0 ? Math.Min(options.MaxPhaseOutputTokens, request.Budget.MaxOutputTokens) : options.MaxPhaseOutputTokens, MaxElapsedSeconds = request.Budget.MaxElapsedSeconds > 0 ? Math.Min(options.MaxElapsedSeconds, request.Budget.MaxElapsedSeconds) : options.MaxElapsedSeconds };
    }

    private static IReadOnlyList<string> Validate(AgentSwarmOptions options, IReadOnlyList<AgentContextFileSnapshot> snapshots)
    {
        HashSet<string> captured = snapshots.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
        return options.AllowedPaths.All(captured.Contains) ? [] : ["Every allowed path requires an immutable admission snapshot."];
    }
}
