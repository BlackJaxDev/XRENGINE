using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Rendering.Profiling;

/// <summary>
/// Runtime-owned executor used by the profiling control plane. Implementations must run exactly
/// one frame synchronously and may not perform RPC, logging, or result serialization while it is measured.
/// </summary>
public interface IRenderProfileExecutor
{
    Task<RenderProfilePreparation> PrepareAsync(RenderProfileRecipe recipe, CancellationToken cancellationToken);
    Task StabilizeAsync(RenderProfileRecipe recipe, CancellationToken cancellationToken);
    void ExecuteMeasuredFrame(RenderProfileRecipe recipe, int frameIndex);
    Task<RenderProfileResult> DrainAsync(RenderProfileRecipe recipe, RenderProfilePreparation preparation, CancellationToken cancellationToken);
    Task CancelAsync(CancellationToken cancellationToken);
}

/// <summary>Immutable details captured before arming a profile.</summary>
public sealed record RenderProfilePreparation(
    string Adapter,
    string Driver,
    string WorkloadIdentity,
    IReadOnlyList<string> EnabledExtensions,
    IReadOnlyList<string>? UnsupportedRequirements = null);

/// <summary>Versioned artifact returned only after capture and delayed GPU-query drainage.</summary>
public sealed record RenderProfileResult
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string SessionId { get; init; }
    public required string RecipeName { get; init; }
    public required RenderExecutionMode ExecutionMode { get; init; }
    public required string WorkloadIdentity { get; init; }
    public int CapturedFrames { get; init; }
    public double[] FrameMilliseconds { get; init; } = [];
    public IReadOnlyDictionary<string, string> Artifacts { get; init; } = new Dictionary<string, string>();
    public bool IsIntrusive { get; init; }
}

/// <summary>Snapshot which can be returned from a control-plane status request without touching render workers.</summary>
public sealed record RenderProfileStatus(
    string SessionId,
    RenderProfileState State,
    string? Error,
    int CapturedFrames,
    RenderProfilePreparation? Preparation);

/// <summary>
/// Thread-safe state machine for deterministic render profiling. The state transition methods are
/// control-plane operations; measured frames run only inside <see cref="IRenderProfileExecutor"/>.
/// </summary>
public sealed class RenderProfileSessionManager
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public string Create(RenderProfileRecipe recipe, IRenderProfileExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(executor);
        recipe.Validate();

        string id = Guid.NewGuid().ToString("N");
        Session session = new(id, recipe, executor);
        if (!_sessions.TryAdd(id, session))
            throw new InvalidOperationException("Unable to allocate a unique render-profile session identifier.");
        session.BeginPreparation();
        return id;
    }

    public RenderProfileStatus GetStatus(string sessionId) => GetRequired(sessionId).Snapshot();

    public async Task<RenderProfileStatus> WaitReadyAsync(string sessionId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Session session = GetRequired(sessionId);
        await session.WaitReadyAsync(timeout, cancellationToken).ConfigureAwait(false);
        return session.Snapshot();
    }

    public void Arm(string sessionId) => GetRequired(sessionId).Arm();

    public void Start(string sessionId) => GetRequired(sessionId).Start();

    public void Stop(string sessionId) => GetRequired(sessionId).Stop();

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        => GetRequired(sessionId).CancelAsync(cancellationToken);

    public RenderProfileResult GetResult(string sessionId) => GetRequired(sessionId).GetResult();

    private Session GetRequired(string sessionId)
        => _sessions.TryGetValue(sessionId, out Session? session)
            ? session
            : throw new KeyNotFoundException($"Render-profile session '{sessionId}' was not found.");

    private sealed class Session(string id, RenderProfileRecipe recipe, IRenderProfileExecutor executor)
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private RenderProfileState _state = RenderProfileState.Created;
        private RenderProfilePreparation? _preparation;
        private RenderProfileResult? _result;
        private string? _error;
        private int _capturedFrames;
        private Task? _preparationTask;
        private Task? _captureTask;

        public void BeginPreparation()
        {
            lock (_sync)
            {
                Transition(RenderProfileState.Created, RenderProfileState.Preparing);
                _preparationTask = PrepareAsync();
            }
        }

        private async Task PrepareAsync()
        {
            try
            {
                RenderProfilePreparation preparation = await executor.PrepareAsync(recipe, _cancellation.Token).ConfigureAwait(false);
                if (preparation.UnsupportedRequirements is { Count: > 0 })
                    throw new NotSupportedException(string.Join("; ", preparation.UnsupportedRequirements));
                lock (_sync)
                {
                    _preparation = preparation;
                    Transition(RenderProfileState.Preparing, RenderProfileState.Stabilizing);
                }
                await executor.StabilizeAsync(recipe, _cancellation.Token).ConfigureAwait(false);
                lock (_sync)
                    Transition(RenderProfileState.Stabilizing, RenderProfileState.Created);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                lock (_sync)
                    _state = RenderProfileState.Cancelled;
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _error = ex.Message;
                    _state = RenderProfileState.Failed;
                }
            }
        }

        public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task preparation = _preparationTask ?? throw new InvalidOperationException("Profile preparation did not start.");
            await preparation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            RenderProfileState state = Snapshot().State;
            if (state == RenderProfileState.Failed)
                throw new InvalidOperationException(_error ?? "Render-profile preparation failed.");
        }

        public void Arm()
        {
            lock (_sync)
                Transition(RenderProfileState.Created, RenderProfileState.Armed);
        }

        public void Start()
        {
            lock (_sync)
            {
                Transition(RenderProfileState.Armed, RenderProfileState.Capturing);
                // The control-plane caller only arms work. The captured interval executes on
                // the profile host's worker, never while serializing an MCP response.
                _captureTask = Task.Run(CaptureAsync);
            }
        }

        private async Task CaptureAsync()
        {
            try
            {
                for (int frame = 0; frame < recipe.CaptureFrames; frame++)
                {
                    _cancellation.Token.ThrowIfCancellationRequested();
                    executor.ExecuteMeasuredFrame(recipe, frame);
                    Volatile.Write(ref _capturedFrames, frame + 1);
                }

                lock (_sync)
                    Transition(RenderProfileState.Capturing, RenderProfileState.Draining);
                RenderProfileResult result = await executor.DrainAsync(recipe, _preparation!, _cancellation.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    _result = result with
                    {
                        SessionId = id,
                        RecipeName = recipe.Name,
                        ExecutionMode = recipe.ExecutionMode,
                        WorkloadIdentity = _preparation!.WorkloadIdentity,
                        CapturedFrames = Volatile.Read(ref _capturedFrames),
                        IsIntrusive = recipe.Instrumentation.HasFlag(RenderProfileInstrumentation.TargetedCpuSpans) ||
                            recipe.Instrumentation.HasFlag(RenderProfileInstrumentation.TargetedGpuTimestamps) ||
                            recipe.Instrumentation.HasFlag(RenderProfileInstrumentation.HardwareCounters),
                    };
                    Transition(RenderProfileState.Draining, RenderProfileState.Completed);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                lock (_sync)
                    _state = RenderProfileState.Cancelled;
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _error = ex.Message;
                    _state = RenderProfileState.Failed;
                }
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (_state is RenderProfileState.Capturing or RenderProfileState.Draining)
                    _cancellation.Cancel();
            }
        }

        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            await executor.CancelAsync(cancellationToken).ConfigureAwait(false);
            Task? activeTask = _captureTask ?? _preparationTask;
            if (activeTask is not null)
                await activeTask.ConfigureAwait(false);
        }

        public RenderProfileResult GetResult()
        {
            lock (_sync)
            {
                if (_state != RenderProfileState.Completed || _result is null)
                    throw new InvalidOperationException($"Render-profile result is unavailable while session '{id}' is {_state}.");
                return _result;
            }
        }

        public RenderProfileStatus Snapshot()
        {
            lock (_sync)
                return new(id, _state, _error, Volatile.Read(ref _capturedFrames), _preparation);
        }

        private void Transition(RenderProfileState expected, RenderProfileState next)
        {
            if (_state != expected)
                throw new InvalidOperationException($"Cannot transition render-profile session '{id}' from {_state} to {next}; expected {expected}.");
            _state = next;
        }
    }
}
