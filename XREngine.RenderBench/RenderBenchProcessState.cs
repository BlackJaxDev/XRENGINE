namespace XREngine.RenderBench;

public sealed class RenderBenchProcessState(RenderBenchOptions options)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _startRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RenderBenchPhase _phase = RenderBenchPhase.Starting;
    private string? _resultPath;
    private string? _failure;

    public Task StartRequested => _startRequested.Task;
    public Task ShutdownRequested => _shutdownRequested.Task;
    public bool IsShutdownRequested => _shutdownRequested.Task.IsCompleted;

    public void RequestStart() => _startRequested.TrySetResult();
    public void RequestShutdown() => _shutdownRequested.TrySetResult();

    public void SetPhase(RenderBenchPhase phase)
    {
        lock (_gate)
            _phase = phase;
    }

    public void Complete(string resultPath)
    {
        lock (_gate)
        {
            _resultPath = resultPath;
            _phase = RenderBenchPhase.Completed;
        }
    }

    public void Fail(Exception exception)
    {
        lock (_gate)
        {
            _failure = exception.ToString();
            _phase = RenderBenchPhase.Failed;
        }
    }

    public RenderBenchStatus Snapshot()
    {
        lock (_gate)
        {
            return new RenderBenchStatus(
                _phase,
                Environment.ProcessId,
                options.SessionName,
                options.Backend,
                options.ExecutionMode.ToString(),
                options.Recipe,
                options.OutputDirectory,
                _resultPath,
                _failure);
        }
    }
}
