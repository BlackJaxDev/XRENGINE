using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using XREngine.Rendering;

namespace XREngine.Rendering;

public enum RuntimeWindowPumpHostMode
{
    Disabled,
    SdlPrototype,
}

public sealed class RuntimeWindowPumpHost : IDisposable
{
    private const string EnvironmentVariableName = XREngineEnvironmentVariables.WindowPumpHost;
    private readonly object _windowsSync = new();
    private readonly List<XRWindow> _windows = [];
    private readonly List<XRWindow> _pumpSnapshot = [];
    private readonly ManualResetEventSlim _started = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private BlockingCollection<WindowPumpWorkItem>? _queue;
    private Thread? _thread;
    private int _threadId;
    private volatile bool _disposed;
    private long _enqueuedCount;
    private long _completedCount;
    private long _failedCount;
    private long _inlineExecutionCount;
    private long _wrongThreadBypassCount;
    private long _blockingWaitCount;
    private long _flushCount;
    private long _flushTimeoutCount;
    private long _shutdownDrainCount;
    private long _totalQueueWaitTicks;
    private long _lastQueueWaitTicks;
    private int _currentDepth;
    private int _maxDepth;
    private int _isStopping;

    public RuntimeWindowPumpHostMode Mode { get; private set; } = RuntimeWindowPumpHostMode.Disabled;
    public bool IsRunning => _thread is { IsAlive: true } && !_disposed;
    public int ThreadId => Volatile.Read(ref _threadId);
    public WindowMailboxDiagnostics Diagnostics
    {
        get
        {
            long enqueued = Volatile.Read(ref _enqueuedCount);
            long totalWaitTicks = Volatile.Read(ref _totalQueueWaitTicks);
            double averageWaitMs = enqueued <= 0
                ? 0.0
                : totalWaitTicks * 1000.0 / Stopwatch.Frequency / enqueued;

            return new WindowMailboxDiagnostics(
                enqueued,
                Volatile.Read(ref _completedCount),
                Volatile.Read(ref _failedCount),
                Volatile.Read(ref _inlineExecutionCount),
                Volatile.Read(ref _wrongThreadBypassCount),
                Volatile.Read(ref _blockingWaitCount),
                Volatile.Read(ref _flushCount),
                Volatile.Read(ref _flushTimeoutCount),
                Volatile.Read(ref _shutdownDrainCount),
                Volatile.Read(ref _currentDepth),
                Volatile.Read(ref _maxDepth),
                averageWaitMs,
                Volatile.Read(ref _lastQueueWaitTicks),
                ThreadId,
                Volatile.Read(ref _isStopping) != 0);
        }
    }

    public XRWindow CreateWindow(Func<XRWindow> factory, string reason)
    {
        if (ThreadId == Environment.CurrentManagedThreadId)
            return AttachWindow(factory());

        return Enqueue(
            () => AttachWindow(factory()),
            reason);
    }

    public void EnqueueWindowTask(IRuntimeRenderWindowHost window, Action task, string reason)
    {
        if (!ShouldRouteToWindowThread(window))
        {
            RecordInlineWindowTask(window);
            task();
            return;
        }

        Post(task, reason);
    }

    public T InvokeWindowTask<T>(IRuntimeRenderWindowHost window, Func<T> task, string reason)
    {
        if (!ShouldRouteToWindowThread(window))
        {
            RecordInlineWindowTask(window);
            return task();
        }

        return Enqueue(task, reason);
    }

    public void UnregisterWindow(XRWindow window)
    {
        lock (_windowsSync)
            _windows.Remove(window);
    }

    public void Stop()
    {
        if (_disposed)
            return;

        Interlocked.Exchange(ref _isStopping, 1);
        if (IsRunning)
            Flush(TimeSpan.FromSeconds(2), "WindowPumpHost.Stop");

        _shutdown.Cancel();
        _queue?.CompleteAdding();

        Thread? thread = _thread;
        if (thread is not null && thread.IsAlive && thread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                RuntimeRenderingHostServices.Diagnostics.LogWarning(
                    $"[WindowPumpHost] Timed out waiting for pump thread shutdown. thread={ThreadId} mode={Mode}.");
            }
        }

        _thread = null;
        Mode = RuntimeWindowPumpHostMode.Disabled;
    }

    public bool Flush(TimeSpan timeout, string reason)
    {
        if (!IsRunning)
            return true;

        Interlocked.Increment(ref _flushCount);

        if (ThreadId == Environment.CurrentManagedThreadId)
        {
            DrainQueue();
            return Volatile.Read(ref _currentDepth) == 0;
        }

        using var completion = new ManualResetEventSlim(false);
        try
        {
            Post(
                () =>
                {
                    Interlocked.Increment(ref _shutdownDrainCount);
                    completion.Set();
                },
                reason);
        }
        catch (InvalidOperationException)
        {
            return !IsRunning;
        }

        if (completion.Wait(timeout))
            return true;

        Interlocked.Increment(ref _flushTimeoutCount);
        RuntimeRenderingHostServices.Diagnostics.LogWarning(
            $"[WindowPumpHost] Timed out flushing window-thread mailbox. reason={reason} " +
            $"timeoutMs={timeout.TotalMilliseconds:F0} depth={Volatile.Read(ref _currentDepth)} owner={ThreadId}.");
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
        _shutdown.Dispose();
        _started.Dispose();
        _queue?.Dispose();
    }

    public void Start(RuntimeWindowPumpHostMode mode)
    {
        if (IsRunning)
            return;

        Mode = mode;
        Interlocked.Exchange(ref _isStopping, 0);
        _queue = new BlockingCollection<WindowPumpWorkItem>();
        _started.Reset();

        _thread = new Thread(ThreadMain)
        {
            Name = "XRE-WindowPump",
            // Stop() performs a bounded join. Background ownership ensures a wedged native
            // event callback cannot keep the process alive after shutdown has been abandoned.
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };

        if (OperatingSystem.IsWindows())
            _thread.SetApartmentState(ApartmentState.STA);

        _thread.Start();
        _started.Wait();

        RuntimeRenderingHostServices.Diagnostics.LogOutput(
            $"[WindowPumpHost] Started mode={Mode} thread={ThreadId}.");
    }

    private XRWindow AttachWindow(XRWindow window)
    {
        window.AttachExternalNativeEventPump(ThreadId, Mode.ToString());
        lock (_windowsSync)
            _windows.Add(window);

        return window;
    }

    private T Enqueue<T>(Func<T> action, string reason)
    {
        BlockingCollection<WindowPumpWorkItem> queue = _queue
            ?? throw new InvalidOperationException("Window pump host is not running.");

        long queuedAtTimestamp = Stopwatch.GetTimestamp();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _enqueuedCount);
        int depth = Interlocked.Increment(ref _currentDepth);
        UpdateMaxDepth(depth);
        Interlocked.Increment(ref _blockingWaitCount);
        try
        {
            queue.Add(new WindowPumpWorkItem(
                reason,
                queuedAtTimestamp,
                () =>
                {
                    try
                    {
                        completion.TrySetResult(action());
                        Interlocked.Increment(ref _completedCount);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _failedCount);
                        completion.TrySetException(ex);
                    }
                }));
        }
        catch
        {
            Interlocked.Decrement(ref _currentDepth);
            Interlocked.Increment(ref _failedCount);
            throw;
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    private void Post(Action action, string reason)
    {
        BlockingCollection<WindowPumpWorkItem> queue = _queue
            ?? throw new InvalidOperationException("Window pump host is not running.");

        long queuedAtTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _enqueuedCount);
        int depth = Interlocked.Increment(ref _currentDepth);
        UpdateMaxDepth(depth);
        try
        {
            queue.Add(new WindowPumpWorkItem(
                reason,
                queuedAtTimestamp,
                () =>
                {
                    try
                    {
                        action();
                        Interlocked.Increment(ref _completedCount);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _failedCount);
                        RuntimeRenderingHostServices.Diagnostics.LogException(
                            ex,
                            $"[WindowPumpHost] Posted work item failed. Reason={reason}");
                    }
                }));
        }
        catch
        {
            Interlocked.Decrement(ref _currentDepth);
            Interlocked.Increment(ref _failedCount);
            throw;
        }
    }

    private void RecordInlineWindowTask(IRuntimeRenderWindowHost window)
    {
        Interlocked.Increment(ref _inlineExecutionCount);
        if (IsRunning &&
            ThreadId != Environment.CurrentManagedThreadId &&
            window is XRWindow xrWindow &&
            xrWindow.NativeWindowThreadId != ThreadId)
        {
            Interlocked.Increment(ref _wrongThreadBypassCount);
        }
    }

    private bool ShouldRouteToWindowThread(IRuntimeRenderWindowHost window)
        => IsRunning &&
           ThreadId != Environment.CurrentManagedThreadId &&
           window is XRWindow xrWindow &&
           xrWindow.NativeWindowThreadId == ThreadId;

    private void UpdateMaxDepth(int depth)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxDepth);
            if (depth <= observed)
                return;
        }
        while (Interlocked.CompareExchange(ref _maxDepth, depth, observed) != observed);
    }

    private void ThreadMain()
    {
        int threadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _threadId, threadId);
        RuntimeEngine.AssignWindowThread(threadId);
        _started.Set();

        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                bool didWork = DrainQueue();
                didWork |= PumpWindows();

                if (!didWork)
                    Thread.Sleep(1);
                else
                    Thread.Yield();
            }
        }
        catch (Exception ex)
        {
            RuntimeRenderingHostServices.Diagnostics.LogException(ex, "[WindowPumpHost] Pump thread failed.");
        }
    }

    private bool DrainQueue()
    {
        BlockingCollection<WindowPumpWorkItem>? queue = _queue;
        if (queue is null)
            return false;

        bool didWork = false;
        while (queue.TryTake(out WindowPumpWorkItem? item))
        {
            didWork = true;
            Interlocked.Decrement(ref _currentDepth);
            long queueWaitTicks = Math.Max(0L, Stopwatch.GetTimestamp() - item.QueuedAtTimestamp);
            Volatile.Write(ref _lastQueueWaitTicks, queueWaitTicks);
            Interlocked.Add(ref _totalQueueWaitTicks, queueWaitTicks);
            try
            {
                item.Execute();
            }
            catch (Exception ex)
            {
                RuntimeRenderingHostServices.Diagnostics.LogException(
                    ex,
                    $"[WindowPumpHost] Work item failed. Reason={item.Reason}");
            }
        }

        return didWork;
    }

    private bool PumpWindows()
    {
        bool didWork = false;
        lock (_windowsSync)
        {
            for (int i = _windows.Count - 1; i >= 0; i--)
            {
                XRWindow window = _windows[i];
                if (window.IsDisposed)
                {
                    _windows.RemoveAt(i);
                    continue;
                }

                _pumpSnapshot.Add(window);
            }
        }

        for (int i = 0; i < _pumpSnapshot.Count; i++)
        {
            XRWindow window = _pumpSnapshot[i];
            window.PumpNativeWindowEventsFromHost();
            didWork = true;
        }

        _pumpSnapshot.Clear();

        return didWork;
    }

    private sealed record WindowPumpWorkItem(string Reason, long QueuedAtTimestamp, Action Execute);
}
