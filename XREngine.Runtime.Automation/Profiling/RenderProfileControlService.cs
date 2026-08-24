using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using XREngine.Rendering.Profiling;

namespace XREngine.Runtime.Automation.Profiling;

/// <summary>
/// Runtime-owned asynchronous control plane for recipes, sessions, and bounded matrices.
/// It contains no editor, window, transport, or world dependency.
/// </summary>
public sealed class RenderProfileControlService(
    IRenderProfileExecutorFactory executorFactory,
    IEnumerable<RenderProfileTargetDefinition> targets)
{
    private const int MaxMatrixVariants = 16;
    private readonly RenderProfileSessionManager _sessions = new();
    private readonly ConcurrentDictionary<string, RenderProfileRecipeDescriptor> _recipes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MatrixJob> _matrices = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<RenderProfileTargetDefinition> _targets = targets.ToArray();
    private readonly object _activeGate = new();
    private string? _activeSessionId;
    private string? _activeMatrixId;

    /// <summary>
    /// Runs one matrix capture with its hosting transport quiesced. Hosts which expose matrix
    /// tools must bind this to their transport before accepting profiling requests.
    /// </summary>
    public Func<Func<Task>, Task>? RunWithTransportSuspendedAsync { private get; set; }

    public IReadOnlyList<RenderProfileTargetDefinition> ListTargets() => _targets;

    public RenderProfileRecipeDescriptor LoadRecipe(string json)
    {
        RenderProfileRecipe recipe = RenderProfileRecipe.Parse(json);
        ValidateTarget(recipe);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        string id = hash[..16].ToLowerInvariant();
        RenderProfileRecipeDescriptor descriptor = new(id, hash, recipe);
        _recipes[id] = descriptor;
        return descriptor;
    }

    public string Prepare(string recipeId)
    {
        RenderProfileRecipe recipe = GetRecipe(recipeId).Recipe;
        lock (_activeGate)
        {
            EnsureIdle();
            _activeSessionId = _sessions.Create(recipe, executorFactory.Create(recipe));
            return _activeSessionId;
        }
    }

    public Task<RenderProfileStatus> WaitReadyAsync(
        string sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => _sessions.WaitReadyAsync(sessionId, timeout, cancellationToken);

    public RenderProfileStatus Arm(string sessionId, long? frameId)
    {
        if (frameId.HasValue)
            _sessions.Arm(sessionId, frameId.Value);
        else
            _sessions.Arm(sessionId);
        return _sessions.GetStatus(sessionId);
    }

    public RenderProfileStartOperation CreateStartOperation(string sessionId)
    {
        Task completion = _sessions.WaitForTerminalStateAsync(sessionId);
        return new RenderProfileStartOperation(
            () =>
            {
                _ = _sessions.Start(sessionId);
                return Task.CompletedTask;
            },
            completion);
    }

    public RenderProfileStatus Stop(string sessionId)
    {
        _sessions.Stop(sessionId);
        return _sessions.GetStatus(sessionId);
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        => _sessions.CancelAsync(sessionId, cancellationToken);

    public RenderProfileStatus GetStatus(string sessionId) => _sessions.GetStatus(sessionId);

    public RenderProfileResult GetResult(string sessionId) => _sessions.GetResult(sessionId);

    public (RenderProfileMatrixStatus Status, RenderProfileStartOperation Operation) CreateMatrix(string recipeId)
    {
        RenderProfileRecipe recipe = GetRecipe(recipeId).Recipe;
        int[] workerCounts = recipe.WorkerCounts.Distinct().ToArray();
        if (workerCounts.Length > MaxMatrixVariants)
            throw new InvalidOperationException($"A render-profile matrix is limited to {MaxMatrixVariants} variants.");

        string jobId = Guid.NewGuid().ToString("N");
        MatrixJob job = new(jobId, recipe, workerCounts);
        lock (_activeGate)
        {
            EnsureIdle();
            if (!_matrices.TryAdd(jobId, job))
                throw new InvalidOperationException("Unable to allocate a unique render-profile matrix job identifier.");
            _activeMatrixId = jobId;
        }
        return (job.Snapshot(), new RenderProfileStartOperation(
            () =>
            {
                job.Start(() => RunMatrixAsync(job));
                return Task.CompletedTask;
            },
            job.Completion));
    }

    public RenderProfileMatrixStatus GetMatrixStatus(string jobId) => GetMatrix(jobId).Snapshot();

    public Task CancelMatrixAsync(string jobId) => GetMatrix(jobId).CancelAsync();

    private async Task RunMatrixAsync(MatrixJob job)
    {
        string? currentSessionId = null;
        try
        {
            job.SetState(RenderProfileState.Preparing);
            foreach (int workerCount in job.WorkerCounts)
            {
                job.CancellationToken.ThrowIfCancellationRequested();
                RenderProfileRecipe variant = job.Recipe with
                {
                    Name = $"{job.Recipe.Name}-workers-{workerCount}",
                    WorkerCounts = [workerCount],
                };
                string sessionId = _sessions.Create(variant, executorFactory.Create(variant));
                currentSessionId = sessionId;
                lock (_activeGate)
                    _activeSessionId = sessionId;
                job.AddSession(sessionId);
                await _sessions.WaitReadyAsync(
                    sessionId,
                    TimeSpan.FromSeconds(variant.TimeoutSeconds),
                    job.CancellationToken).ConfigureAwait(false);
                _sessions.Arm(sessionId);
                job.SetState(RenderProfileState.Armed);
                Func<Func<Task>, Task> suspendTransport = RunWithTransportSuspendedAsync
                    ?? throw new InvalidOperationException("The profile host did not configure measured-interval transport suspension.");
                await suspendTransport(async () =>
                {
                    job.SetState(RenderProfileState.Capturing);
                    await _sessions.Start(sessionId).WaitAsync(job.CancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
                RenderProfileStatus status = _sessions.GetStatus(sessionId);
                if (status.State != RenderProfileState.Completed)
                    throw new InvalidOperationException(status.Error ?? $"Matrix session '{sessionId}' ended in {status.State}.");
                job.CompleteVariant();
                job.SetState(RenderProfileState.Preparing);
            }
            job.Complete();
        }
        catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested)
        {
            if (currentSessionId is not null)
            {
                RenderProfileState state = _sessions.GetStatus(currentSessionId).State;
                if (state is not (RenderProfileState.Completed or RenderProfileState.Failed or RenderProfileState.Cancelled))
                    await _sessions.CancelAsync(currentSessionId).ConfigureAwait(false);
            }
            job.Cancelled();
        }
        catch (Exception exception)
        {
            job.Fail(exception.Message);
        }
    }

    private RenderProfileRecipeDescriptor GetRecipe(string recipeId)
        => _recipes.TryGetValue(recipeId, out RenderProfileRecipeDescriptor? descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Render-profile recipe '{recipeId}' was not loaded.");

    private MatrixJob GetMatrix(string jobId)
        => _matrices.TryGetValue(jobId, out MatrixJob? job)
            ? job
            : throw new KeyNotFoundException($"Render-profile matrix job '{jobId}' was not found.");

    private void ValidateTarget(RenderProfileRecipe recipe)
    {
        RenderProfileTargetDefinition? target = _targets.FirstOrDefault(candidate =>
            candidate.ExecutionMode == recipe.ExecutionMode &&
            candidate.Component.Equals(recipe.Component, StringComparison.OrdinalIgnoreCase) &&
            candidate.Fixture.Equals(recipe.Fixture, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            throw new NotSupportedException($"No profile target matches component '{recipe.Component}', fixture '{recipe.Fixture}', and mode '{recipe.ExecutionMode}'.");
        if (!target.Supported)
            throw new NotSupportedException(target.UnsupportedReason ?? $"Profile target '{target.Name}' is unsupported.");
    }

    private void EnsureIdle()
    {
        if (_activeMatrixId is not null)
        {
            RenderProfileState matrixState = GetMatrix(_activeMatrixId).Snapshot().State;
            if (matrixState is not (RenderProfileState.Completed or RenderProfileState.Failed or RenderProfileState.Cancelled))
                throw new InvalidOperationException($"Render-profile matrix '{_activeMatrixId}' is still {matrixState}.");
            _activeMatrixId = null;
        }

        if (_activeSessionId is null)
            return;
        RenderProfileState sessionState = _sessions.GetStatus(_activeSessionId).State;
        if (sessionState is not (RenderProfileState.Completed or RenderProfileState.Failed or RenderProfileState.Cancelled))
            throw new InvalidOperationException($"Render-profile session '{_activeSessionId}' is still {sessionState}.");
        _activeSessionId = null;
    }

    private sealed class MatrixJob(string id, RenderProfileRecipe recipe, int[] workerCounts)
    {
        private readonly object _gate = new();
        private readonly List<string> _sessionIds = [];
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RenderProfileState _state = RenderProfileState.Created;
        private int _completedVariants;
        private string? _error;

        public RenderProfileRecipe Recipe { get; } = recipe;
        public int[] WorkerCounts { get; } = workerCounts;
        public CancellationToken CancellationToken => _cancellation.Token;
        public Task Completion => _completion.Task;

        public void Start(Func<Task> body) => _ = Task.Run(body);
        public void SetState(RenderProfileState state) { lock (_gate) _state = state; }
        public void AddSession(string sessionId) { lock (_gate) _sessionIds.Add(sessionId); }
        public void CompleteVariant() { lock (_gate) _completedVariants++; }
        public void Complete() => SetTerminal(RenderProfileState.Completed, null);
        public void Cancelled() => SetTerminal(RenderProfileState.Cancelled, null);
        public void Fail(string error) => SetTerminal(RenderProfileState.Failed, error);

        public async Task CancelAsync()
        {
            _cancellation.Cancel();
            await _completion.Task.ConfigureAwait(false);
        }

        public RenderProfileMatrixStatus Snapshot()
        {
            lock (_gate)
                return new(id, _state, _completedVariants, WorkerCounts.Length, [.. _sessionIds], _error);
        }

        private void SetTerminal(RenderProfileState state, string? error)
        {
            lock (_gate)
            {
                _state = state;
                _error = error;
            }
            _completion.TrySetResult();
        }
    }
}
