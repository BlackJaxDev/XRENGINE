namespace XREngine.Execution;

/// <summary>Runs host-owned profiler cycles on one native background thread.</summary>
internal sealed class ThreadedProfilerStatsWorker : IProfilerStatsWorker
{
    private readonly Func<int> _runCycle;
    private readonly Action<Exception> _reportException;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _thread;

    internal ThreadedProfilerStatsWorker(Func<int> runCycle, Action<Exception> reportException)
    {
        _runCycle = runCycle ?? throw new ArgumentNullException(nameof(runCycle));
        _reportException = reportException ?? throw new ArgumentNullException(nameof(reportException));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "XREngine.ProfilerStats",
            Priority = ThreadPriority.BelowNormal
        };
    }

    public bool IsAlive => _thread.IsAlive;

    public void Start() => _thread.Start();

    public void RequestStop() => _cancellation.Cancel();

    public void WaitForExit() => _thread.Join();

    /// <summary>Releases cancellation state after the owner has stopped and joined the worker.</summary>
    public void Dispose() => _cancellation.Dispose();

    private void Run()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                int delayMs = _runCycle();
                if (delayMs > 0)
                    Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                _reportException(ex);
            }
        }
    }
}
