using System.Collections.Concurrent;
using System.Diagnostics;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Bounded in-memory registry that owns background execution, cancellation, and retention.
/// </summary>
internal sealed class AgentRunRegistry : IAsyncDisposable
{
    private readonly BrokerConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly AgentOrchestrator _orchestrator;
    private readonly EditorSessionResolver _sessionResolver;
    private readonly SessionRunLeaseManager _leaseManager = new();
    private readonly BrokerTraceWriter _traceWriter;
    private readonly SemaphoreSlim _globalConcurrency;
    private readonly ConcurrentDictionary<string, BrokerRunRecord> _runs =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _executionTasks =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    public AgentRunRegistry(BrokerConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        _orchestrator = new AgentOrchestrator(
            new OpenAiResponsesModelClient(httpClient, configuration.ReadApiKey));
        _sessionResolver = new EditorSessionResolver(configuration.RepositoryRoot);
        _traceWriter = new BrokerTraceWriter(configuration);
        _globalConcurrency = new SemaphoreSlim(
            configuration.MaximumConcurrentRuns,
            configuration.MaximumConcurrentRuns);
    }

    public string Start(AgentRunRequest request)
    {
        IReadOnlyList<string> errors = AgentRequestValidator.Validate(request);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors));
        if (!AgentModelCatalog.IsApproved(request.RequestedModel))
        {
            throw new ArgumentException(
                $"requested_model must be exactly one of: {string.Join(", ", AgentModelCatalog.Models)}");
        }
        if (!AgentModelCatalog.SupportsResponseControls(request.RequestedModel))
            throw new ArgumentException("The exact requested_model does not support broker response controls.");
        if (string.IsNullOrWhiteSpace(_configuration.ReadApiKey()))
        {
            throw new InvalidOperationException(
                $"Environment variable '{_configuration.ApiKeyEnvironmentVariable}' is not set.");
        }

        ResolvedEditorSession? session = string.IsNullOrWhiteSpace(request.EditorSession)
            ? null
            : _sessionResolver.Resolve(request.EditorSession);
        CleanupRetainedRuns();
        EnsureCapacity();

        string runId = Guid.NewGuid().ToString("N");
        var record = new BrokerRunRecord(runId, request);
        if (!_runs.TryAdd(runId, record))
            throw new InvalidOperationException("Could not allocate a unique run ID.");

        Task execution = Task.Run(() => ExecuteAsync(record, session), CancellationToken.None);
        _executionTasks[runId] = execution;
        _ = execution.ContinueWith(
            (completedTask, state) =>
            {
                var entry = ((ConcurrentDictionary<string, Task> Tasks, string RunId))state!;
                entry.Tasks.TryRemove(entry.RunId, out _);
            },
            (_executionTasks, runId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return runId;
    }

    public AgentRunSnapshot Get(string runId)
    {
        if (!_runs.TryGetValue(runId, out BrokerRunRecord? record))
            throw new KeyNotFoundException($"Agent run '{runId}' was not found.");
        return record.Snapshot();
    }

    public IReadOnlyList<AgentRunListItem> List(int limit)
    {
        CleanupRetainedRuns();
        int boundedLimit = Math.Clamp(limit, 1, 100);
        return _runs.Values
            .Select(static record => record.Snapshot())
            .OrderByDescending(static snapshot => snapshot.CreatedUtc)
            .Take(boundedLimit)
            .Select(static snapshot => new AgentRunListItem
            {
                RunId = snapshot.RunId,
                Status = snapshot.Status,
                CreatedUtc = snapshot.CreatedUtc,
                UpdatedUtc = snapshot.UpdatedUtc,
                ObservedUtc = snapshot.ObservedUtc,
                ElapsedMilliseconds = snapshot.ElapsedMilliseconds,
                ProgressMessage = snapshot.ProgressMessage,
                RequestedModel = snapshot.RequestedModel,
                ActualModel = snapshot.ActualModel,
                RequestedReasoningEffort = snapshot.RequestedReasoningEffort,
                RequestedTextVerbosity = snapshot.RequestedTextVerbosity,
                MaxOutputTokens = snapshot.MaxOutputTokens,
                EditorSession = snapshot.EditorSession,
                UseBackgroundMode = snapshot.UseBackgroundMode,
                AttemptCount = snapshot.ProviderAttempts.Count,
                RetryCount = snapshot.RetryCount,
            })
            .ToArray();
    }

    public bool Cancel(string runId)
    {
        if (!_runs.TryGetValue(runId, out BrokerRunRecord? record))
            return false;
        AgentRunStatus status = record.Snapshot().Status;
        if (status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled)
            return false;
        record.Cancellation.Cancel();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (BrokerRunRecord record in _runs.Values)
            record.Cancellation.Cancel();
        Task[] activeTasks = _executionTasks.Values.ToArray();
        if (activeTasks.Length > 0)
            await Task.WhenAll(activeTasks);
        _globalConcurrency.Dispose();
        _shutdown.Dispose();
        foreach (BrokerRunRecord record in _runs.Values)
            record.Cancellation.Dispose();
    }

    private async Task ExecuteAsync(BrokerRunRecord record, ResolvedEditorSession? session)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            record.Cancellation.Token,
            _shutdown.Token);
        CancellationToken cancellationToken = linkedCancellation.Token;
        bool enteredGlobal = false;
        try
        {
            await _globalConcurrency.WaitAsync(cancellationToken);
            enteredGlobal = true;
            record.MarkRunning();

            AgentRunResult result = session is null
                ? await RunReasoningOnlyAsync(record, cancellationToken)
                : await RunWithEditorSessionAsync(record, session, cancellationToken);
            record.SetResult(result);
            _traceWriter.Write(record, result);
        }
        catch (OperationCanceledException)
        {
            AgentRunResult result = CreateHostFailure(
                record,
                stopwatch,
                AgentRunStatus.Cancelled,
                AgentFailureCategory.Cancelled,
                "The run was cancelled.");
            record.SetResult(result);
            _traceWriter.Write(record, result);
        }
        catch (AgentToolProviderException exception)
        {
            AgentRunResult result = CreateHostFailure(
                record,
                stopwatch,
                AgentRunStatus.Failed,
                exception.Category,
                exception.Message,
                exception.DiagnosticDetail);
            record.SetResult(result);
            _traceWriter.Write(record, result);
        }
        catch (Exception exception)
        {
            AgentRunResult result = CreateHostFailure(
                record,
                stopwatch,
                AgentRunStatus.Failed,
                AgentFailureCategory.Internal,
                "The broker could not start or complete the run.",
                exception.Message);
            record.SetResult(result);
            _traceWriter.Write(record, result);
        }
        finally
        {
            if (enteredGlobal)
                _globalConcurrency.Release();
        }
    }

    private Task<AgentRunResult> RunReasoningOnlyAsync(
        BrokerRunRecord record,
        CancellationToken cancellationToken)
        => _orchestrator.RunAsync(
            record.RunId,
            record.Request,
            EmptyAgentToolProvider.Instance,
            new BrokerRunObserver(record),
            cancellationToken);

    private async Task<AgentRunResult> RunWithEditorSessionAsync(
        BrokerRunRecord record,
        ResolvedEditorSession session,
        CancellationToken cancellationToken)
    {
        await using AgentSessionLease lease = await _leaseManager.AcquireAsync(
            session.Name,
            record.Request.ToolPolicy.AllowMutation,
            cancellationToken);
        var provider = new HttpMcpToolProvider(
            _httpClient,
            session.Endpoint,
            record.Request.ToolPolicy,
            _configuration.ReadEditorAuthToken());
        await provider.PreflightAsync(session.Name, cancellationToken);

        return await _orchestrator.RunAsync(
            record.RunId,
            record.Request,
            provider,
            new BrokerRunObserver(record),
            cancellationToken);
    }

    private void CleanupRetainedRuns()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-_configuration.RetentionMinutes);
        foreach ((string runId, BrokerRunRecord record) in _runs)
        {
            AgentRunSnapshot snapshot = record.Snapshot();
            if (snapshot.Status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled
                && snapshot.UpdatedUtc < cutoff
                && _runs.TryRemove(runId, out BrokerRunRecord? removed))
            {
                removed.Cancellation.Dispose();
            }
        }
    }

    private void EnsureCapacity()
    {
        int overflow = _runs.Count - _configuration.MaximumRetainedRuns + 1;
        if (overflow <= 0)
            return;

        BrokerRunRecord[] removable = _runs.Values
            .Where(static record =>
            {
                AgentRunStatus status = record.Snapshot().Status;
                return status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled;
            })
            .OrderBy(static record => record.Snapshot().UpdatedUtc)
            .Take(overflow)
            .ToArray();
        foreach (BrokerRunRecord record in removable)
        {
            if (_runs.TryRemove(record.RunId, out BrokerRunRecord? removed))
                removed.Cancellation.Dispose();
        }

        if (_runs.Count >= _configuration.MaximumRetainedRuns)
            throw new InvalidOperationException("The agent run registry is full of active runs.");
    }

    private static AgentRunResult CreateHostFailure(
        BrokerRunRecord record,
        Stopwatch stopwatch,
        AgentRunStatus status,
        AgentFailureCategory category,
        string summary,
        string diagnosticDetail = "")
        => new()
        {
            RunId = record.RunId,
            Status = status,
            RequestedModel = record.Request.RequestedModel,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Failure = new AgentFailure
            {
                Category = category,
                Summary = summary,
                DiagnosticDetail = diagnosticDetail,
            },
        };
}
