using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Owns one secondary worker generation and retains resources through a bounded stop.</summary>
internal sealed class DesktopSecondaryGpuContext(IDesktopSecondaryGpuContextResources resources) : IRuntimeSecondaryGpuContext
{
    private readonly object _gate = new();
    private readonly Queue<Action<AbstractRenderer>> _jobs = new();
    private readonly AutoResetEvent _jobSignal = new(false);
    private readonly CancellationTokenSource _cancellation = new();
    private Thread? _thread;
    private bool _started;
    private bool _stopping;
    private bool _exited;
    private int _releasePending;
    private int _released;

    public bool IsRunning { get { lock (_gate) return _started && !_exited; } }
    public bool HasRenderer => !IsReleased && resources.HasRenderer;
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    internal void Start()
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (_started || _exited)
                throw new InvalidOperationException("The secondary graphics owner can start only once.");
            _started = true;
            try
            {
                _thread = new Thread(RunContext)
                {
                    IsBackground = true,
                    Name = "XR Secondary Render Context",
                };
                _thread.Start();
                return;
            }
            catch (Exception error)
            {
                _thread = null;
                FinishWorkerUnderLock();
                failure = error;
            }
        }
        if (failure is not null) ReportFailure("Secondary GPU worker creation failed", failure);
        ReleaseStoppedResources();
    }

    public bool TryEnqueue(Action<AbstractRenderer> job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            if (!_started || _stopping || _exited) return false;
            _jobs.Enqueue(job);
            _jobSignal.Set();
            return true;
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            if (!_exited)
            {
                _stopping = true;
                _cancellation.Cancel();
                _jobs.Clear();
                _jobSignal.Set();
                if (!_started) FinishWorkerUnderLock();
            }
        }
        // A job can request its own stop. Its finally path owns cleanup in that case.
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try
            {
                if (!thread.Join(TimeSpan.FromSeconds(1))) return;
            }
            catch (Exception error)
            {
                ReportFailure("Secondary GPU worker join failed", error);
                if (thread.IsAlive) return;
            }
        }
        lock (_gate)
        {
            if (!_exited) return;
        }
        ReleaseStoppedResources();
    }

    private void RunContext()
    {
        try
        {
            resources.Initialize();
            while (true)
            {
                Action<AbstractRenderer>? job;
                lock (_gate)
                {
                    if (_stopping || _cancellation.IsCancellationRequested) break;
                    _jobs.TryDequeue(out job);
                }
                if (job is null)
                {
                    _jobSignal.WaitOne(TimeSpan.FromMilliseconds(2));
                    continue;
                }
                resources.Execute(job);
            }
        }
        catch (Exception error)
        {
            // Close admission before diagnostics or cleanup can block the failed worker.
            lock (_gate) _stopping = true;
            ReportFailure("Secondary render context terminated", error);
        }
        finally
        {
            try { resources.CleanUp(); }
            catch (Exception error) { ReportFailure("Secondary GPU context teardown failed", error); }
            lock (_gate) FinishWorkerUnderLock();
            ReleaseStoppedResources();
        }
    }

    private void FinishWorkerUnderLock()
    {
        _stopping = true;
        _exited = true;
        _jobs.Clear();
        _cancellation.Dispose();
        _jobSignal.Dispose();
    }

    private void ReleaseStoppedResources()
    {
        if (IsReleased || Interlocked.CompareExchange(ref _releasePending, 1, 0) != 0) return;
        try
        {
            resources.Release(success =>
            {
                if (success) Volatile.Write(ref _released, 1);
                else Volatile.Write(ref _releasePending, 0);
            });
        }
        catch (Exception error)
        {
            Volatile.Write(ref _releasePending, 0);
            ReportFailure("Secondary GPU native window disposal could not be scheduled", error);
        }
    }

    private static void ReportFailure(string operation, Exception error)
    {
        // Diagnostics must not prevent owner cleanup or terminate a native worker.
        try { Debug.RenderingWarning($"{operation}: {error.Message}"); }
        catch { }
    }
}
