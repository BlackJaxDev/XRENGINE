using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Rendering.Profiling;

/// <summary>
/// Runtime-owned executor used by the profiling control plane. Implementations must run exactly
/// one frame synchronously and may not perform RPC, logging, or result serialization while it is measured.
/// </summary>
public interface IRenderProfileExecutor
{
    /// <summary>The next engine/render frame identifier that can be armed.</summary>
    long NextFrameId => 0;

    Task<RenderProfilePreparation> PrepareAsync(RenderProfileRecipe recipe, CancellationToken cancellationToken);
    Task StabilizeAsync(RenderProfileRecipe recipe, CancellationToken cancellationToken);
    /// <summary>Performs one-time capture-thread initialization before the armed boundary is published.</summary>
    void WarmCaptureThread(RenderProfileRecipe recipe) { }
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
    RenderProfilePreparation? Preparation,
    long? ArmedFrameId = null,
    long? CaptureStartFrameId = null);

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

    public void Arm(string sessionId, long frameId) => GetRequired(sessionId).Arm(frameId);

    public Task Start(string sessionId) => GetRequired(sessionId).Start();

    public void Stop(string sessionId) => GetRequired(sessionId).Stop();

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        => GetRequired(sessionId).CancelAsync(cancellationToken);

    public Task WaitForTerminalStateAsync(string sessionId, CancellationToken cancellationToken = default)
        => GetRequired(sessionId).WaitForTerminalStateAsync(cancellationToken);

    public RenderProfileResult GetResult(string sessionId) => GetRequired(sessionId).GetResult();

    private Session GetRequired(string sessionId)
        => _sessions.TryGetValue(sessionId, out Session? session)
            ? session
            : throw new KeyNotFoundException($"Render-profile session '{sessionId}' was not found.");

    private sealed class Session
    {
        private readonly string id;
        private readonly RenderProfileRecipe recipe;
        private readonly IRenderProfileExecutor executor;
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _captureStartSignal = new(false);
        private readonly ManualResetEventSlim _captureWorkerReady = new(false);
        private RenderProfileState _state = RenderProfileState.Created;
        private RenderProfilePreparation? _preparation;
        private RenderProfileResult? _result;
        private string? _error;
        private int _capturedFrames;
        private Task? _preparationTask;
        private Task? _captureTask;
        private long? _armedFrameId;
        private long? _captureStartFrameId;
        private bool _stopRequested;
        private bool _cancelRequested;
        private long? _requestedFrameId;

        public Session(string id, RenderProfileRecipe recipe, IRenderProfileExecutor executor)
        {
            this.id = id;
            this.recipe = recipe;
            this.executor = executor;
            _cancellation.CancelAfter(TimeSpan.FromSeconds(recipe.TimeoutSeconds));
        }

        public void BeginPreparation()
        {
            lock (_sync)
            {
                Transition(RenderProfileState.Created, RenderProfileState.Preparing);
                _preparationTask = Task.Run(PrepareAsync);
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
                await TryCancelExecutorAsync().ConfigureAwait(false);
                lock (_sync)
                    SetTerminal(_cancelRequested ? RenderProfileState.Cancelled : RenderProfileState.Failed,
                        _cancelRequested ? null : $"Render-profile session '{id}' timed out after {recipe.TimeoutSeconds} seconds.");
            }
            catch (Exception ex)
            {
                await TryCancelExecutorAsync().ConfigureAwait(false);
                lock (_sync)
                    SetTerminal(RenderProfileState.Failed, ex.Message);
            }
        }

        public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task preparation = _preparationTask ?? throw new InvalidOperationException("Profile preparation did not start.");
            await preparation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            RenderProfileState state = Snapshot().State;
            if (state == RenderProfileState.Failed)
                throw new InvalidOperationException(_error ?? "Render-profile preparation failed.");
            if (state == RenderProfileState.Cancelled)
                throw new InvalidOperationException("Render-profile preparation was cancelled.");
        }

        public void Arm()
            => ArmCore(null);

        public void Arm(long frameId)
            => ArmCore(frameId);

        private void ArmCore(long? requestedFrameId)
        {
            lock (_sync)
            {
                if (_state != RenderProfileState.Created)
                    throw new InvalidOperationException($"Cannot arm render-profile session '{id}' from {_state}.");
                _requestedFrameId = requestedFrameId;
                _captureTask = Task.Factory.StartNew(
                    CaptureAsync,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
            }

            _captureWorkerReady.Wait(_cancellation.Token);
            lock (_sync)
            {
                if (_state == RenderProfileState.Failed)
                    throw new InvalidOperationException(_error ?? "The capture worker failed while arming.");
                Transition(RenderProfileState.Created, RenderProfileState.Armed);
            }
        }

        public Task Start()
        {
            lock (_sync)
            {
                Transition(RenderProfileState.Armed, RenderProfileState.Capturing);
                _captureStartFrameId = _armedFrameId;
                // The parked capture worker is released only by the post-response callback.
                _captureStartSignal.Set();
                return _terminal.Task;
            }
        }

        private async Task CaptureAsync()
        {
            try
            {
                executor.WarmCaptureThread(recipe);
                long armedFrameId = executor.NextFrameId;
                if (_requestedFrameId.HasValue && _requestedFrameId.Value != armedFrameId)
                    throw new InvalidOperationException(
                        $"Requested frame boundary {_requestedFrameId.Value} is unavailable after capture-thread warmup; next frame is {armedFrameId}.");
                _armedFrameId = armedFrameId;
                _captureWorkerReady.Set();
                _captureStartSignal.Wait(_cancellation.Token);

                for (int frame = 0; frame < recipe.TotalCaptureFrames && !Volatile.Read(ref _stopRequested); frame++)
                {
                    _cancellation.Token.ThrowIfCancellationRequested();
                    long engineFrameId = checked(_captureStartFrameId!.Value + frame);
                    executor.ExecuteMeasuredFrame(recipe, checked((int)engineFrameId));
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
                    _terminal.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                _captureWorkerReady.Set();
                await TryCancelExecutorAsync().ConfigureAwait(false);
                lock (_sync)
                    SetTerminal(_cancelRequested ? RenderProfileState.Cancelled : RenderProfileState.Failed,
                        _cancelRequested ? null : $"Render-profile session '{id}' timed out after {recipe.TimeoutSeconds} seconds.");
            }
            catch (Exception ex)
            {
                _captureWorkerReady.Set();
                await TryCancelExecutorAsync().ConfigureAwait(false);
                lock (_sync)
                    SetTerminal(RenderProfileState.Failed, ex.Message);
            }
        }

        public void Stop()
        {
            Volatile.Write(ref _stopRequested, true);
        }

        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            _cancelRequested = true;
            _cancellation.Cancel();
            _captureStartSignal.Set();
            Task? activeTask = _captureTask ?? _preparationTask;
            if (activeTask is not null)
            {
                try { await activeTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
            }
            await TryCancelExecutorAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_state is not (RenderProfileState.Completed or RenderProfileState.Failed or RenderProfileState.Cancelled))
                    SetTerminal(RenderProfileState.Cancelled, null);
            }
        }

        public Task WaitForTerminalStateAsync(CancellationToken cancellationToken)
            => _terminal.Task.WaitAsync(cancellationToken);

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
                return new(id, _state, _error, Volatile.Read(ref _capturedFrames), _preparation, _armedFrameId, _captureStartFrameId);
        }

        private void Transition(RenderProfileState expected, RenderProfileState next)
        {
            if (_state != expected)
                throw new InvalidOperationException($"Cannot transition render-profile session '{id}' from {_state} to {next}; expected {expected}.");
            _state = next;
        }

        private void SetTerminal(RenderProfileState state, string? error)
        {
            _error = error;
            _state = state;
            _terminal.TrySetResult();
        }

        private async Task TryCancelExecutorAsync(CancellationToken cancellationToken = default)
        {
            try { await executor.CancelAsync(cancellationToken).ConfigureAwait(false); }
            catch when (_cancellation.IsCancellationRequested) { }
        }
    }
}
