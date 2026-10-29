using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>Owns dedicated native threads for the three critical engine loops.</summary>
internal sealed class EngineLoopDomain : IEngineLoopDomain
{
    private readonly object _startGate = new();
    private Thread? _updateThread;
    private Thread? _collectVisibleThread;
    private Thread? _fixedUpdateThread;
    private bool _started;

    internal EngineLoopDomain(Action update, Action collectVisible, Action fixedUpdate)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(collectVisible);
        ArgumentNullException.ThrowIfNull(fixedUpdate);
        _updateThread = CreateThread(update, "XRE-Update");
        _collectVisibleThread = CreateThread(collectVisible, "XRE-CollectVisible");
        _fixedUpdateThread = CreateThread(fixedUpdate, "XRE-FixedUpdate");
    }

    public bool HasLiveThreads
        => Volatile.Read(ref _updateThread) is { IsAlive: true } ||
           Volatile.Read(ref _collectVisibleThread) is { IsAlive: true } ||
           Volatile.Read(ref _fixedUpdateThread) is { IsAlive: true };

    private static Thread CreateThread(Action loop, string name)
        => new(loop.Invoke)
        {
            Name = name,
            // Orderly shutdown joins the workers. Background ownership also lets
            // the process exit when a callback cannot finish.
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };

    public void Start()
    {
        lock (_startGate)
        {
            if (_started)
                throw new InvalidOperationException("The engine loop domain was already started or released.");
            _started = true;
            // Keep every handle if Start throws. The host must signal its loop
            // callbacks to stop before it asks this domain to join them.
            _updateThread!.Start();
            _collectVisibleThread!.Start();
            _fixedUpdateThread!.Start();
        }
    }

    public EngineLoopExitStatus WaitForExit(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The shutdown timeout must be non-negative.");

        Thread? update;
        Thread? collectVisible;
        Thread? fixedUpdate;
        long started = Stopwatch.GetTimestamp();
        TimeSpan maximum = TimeSpan.FromMilliseconds(int.MaxValue);
        if (!Monitor.TryEnter(_startGate, timeout > maximum ? maximum : timeout))
            return new(false, false, false);
        try
        {
            // A completed idle owner cannot later start workers that its caller
            // has already released.
            _started = true;
            update = _updateThread;
            collectVisible = _collectVisibleThread;
            fixedUpdate = _fixedUpdateThread;
        }
        finally
        {
            Monitor.Exit(_startGate);
        }
        bool updateStopped = JoinWithinTimeout(update, started, timeout);
        bool collectVisibleStopped = JoinWithinTimeout(collectVisible, started, timeout);
        bool fixedUpdateStopped = JoinWithinTimeout(fixedUpdate, started, timeout);
        if (updateStopped)
            Interlocked.CompareExchange(ref _updateThread, null, update);
        if (collectVisibleStopped)
            Interlocked.CompareExchange(ref _collectVisibleThread, null, collectVisible);
        if (fixedUpdateStopped)
            Interlocked.CompareExchange(ref _fixedUpdateThread, null, fixedUpdate);
        return new(updateStopped, collectVisibleStopped, fixedUpdateStopped);
    }

    private static bool JoinWithinTimeout(Thread? thread, long started, TimeSpan timeout)
    {
        if (thread is null || !thread.IsAlive)
            return true;
        if (thread.ManagedThreadId == Environment.CurrentManagedThreadId)
            return false;
        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
            return !thread.IsAlive;
        TimeSpan maximum = TimeSpan.FromMilliseconds(int.MaxValue);
        if (remaining > maximum)
            remaining = maximum;
        return thread.Join(remaining);
    }
}
