using XREngine.Rendering;

namespace XREngine;

public enum RuntimeRenderThreadHostMode
{
    CollapsedWindowRenderThread,
    SplitWindowPumpPrototype,
}

/// <summary>
/// Owns render-thread creation and collapsed-mode native-event pumping while delegating
/// application timer policy through narrow callbacks.
/// </summary>
public sealed class RuntimeRenderThreadHost
{
    private readonly object _sync = new();
    private readonly List<XRWindow> _collapsedWindowPumpSnapshot = [];
    private readonly Func<bool> _isExternalWindowPumpRunning;
    private readonly Action<Func<bool>> _blockForTimerRendering;
    private readonly Action _waitToRender;
    private readonly Action _stopTimer;
    private Thread? _dedicatedThread;
    private Exception? _dedicatedThreadException;

    public RuntimeRenderThreadHost(
        Func<bool> isExternalWindowPumpRunning,
        Action<Func<bool>> blockForTimerRendering,
        Action waitToRender,
        Action stopTimer)
    {
        _isExternalWindowPumpRunning = isExternalWindowPumpRunning;
        _blockForTimerRendering = blockForTimerRendering;
        _waitToRender = waitToRender;
        _stopTimer = stopTimer;
    }

    public RuntimeRenderThreadHostMode Mode
        => _isExternalWindowPumpRunning()
            ? RuntimeRenderThreadHostMode.SplitWindowPumpPrototype
            : RuntimeRenderThreadHostMode.CollapsedWindowRenderThread;

    public int CurrentRenderThreadId { get; private set; }

    public void BlockForRendering(Func<bool> runUntilPredicate)
    {
        RuntimeRenderThreadHostMode mode = Mode;
        if (mode == RuntimeRenderThreadHostMode.SplitWindowPumpPrototype)
        {
            BlockForDedicatedRenderThread(runUntilPredicate, mode);
            return;
        }

        RunRenderLoopOnCurrentThread(runUntilPredicate, mode);
    }

    private void BlockForDedicatedRenderThread(
        Func<bool> runUntilPredicate,
        RuntimeRenderThreadHostMode mode)
    {
        lock (_sync)
        {
            if (_dedicatedThread is { IsAlive: true })
                throw new InvalidOperationException("Dedicated render thread is already running.");
        }

        using var started = new ManualResetEventSlim(false);
        _dedicatedThreadException = null;
        var renderThread = new Thread(() => RunDedicatedRenderThread(runUntilPredicate, mode, started))
        {
            Name = "XRE-Render",
            IsBackground = false,
            Priority = ThreadPriority.AboveNormal,
        };

        lock (_sync)
            _dedicatedThread = renderThread;

        renderThread.Start();
        started.Wait();

        Debug.Rendering(
            "[RenderThreadHost] Waiting for dedicated render thread. callerThread={0} renderThread={1} windowThread={2}.",
            Environment.CurrentManagedThreadId,
            RuntimeEngine.RenderThreadId,
            RuntimeEngine.WindowThreadId);

        renderThread.Join();

        lock (_sync)
        {
            if (ReferenceEquals(_dedicatedThread, renderThread))
                _dedicatedThread = null;
        }

        if (_dedicatedThreadException is not null)
            throw new InvalidOperationException("Dedicated render thread failed.", _dedicatedThreadException);
    }

    private void RunDedicatedRenderThread(
        Func<bool> runUntilPredicate,
        RuntimeRenderThreadHostMode mode,
        ManualResetEventSlim started)
    {
        try
        {
            RunRenderLoopOnCurrentThread(runUntilPredicate, mode, started);
        }
        catch (Exception ex)
        {
            _dedicatedThreadException = ex;
            Debug.LogException(ex, "[RenderThreadHost] Dedicated render thread failed.");
            started.Set();
            _stopTimer();
        }
    }

    private void RunRenderLoopOnCurrentThread(
        Func<bool> runUntilPredicate,
        RuntimeRenderThreadHostMode mode,
        ManualResetEventSlim? started = null)
    {
        CurrentRenderThreadId = Environment.CurrentManagedThreadId;
        RuntimeEngine.AssignRenderThread(CurrentRenderThreadId);

        Debug.Rendering(
            "[RenderThreadHost] Entering render loop mode={0} renderThread={1} windowThread={2}.",
            mode,
            RuntimeEngine.RenderThreadId,
            RuntimeEngine.WindowThreadId);

        started?.Set();
        if (mode == RuntimeRenderThreadHostMode.CollapsedWindowRenderThread)
            BlockForCollapsedWindowRendering(runUntilPredicate);
        else
            _blockForTimerRendering(runUntilPredicate);

        Debug.Rendering(
            "[RenderThreadHost] Exited render loop mode={0} renderThread={1} windowThread={2}.",
            mode,
            RuntimeEngine.RenderThreadId,
            RuntimeEngine.WindowThreadId);
    }

    /// <summary>
    /// Pumps collapsed-mode native events before entering a render dispatch.
    /// </summary>
    /// <remarks>
    /// Win32 enters its modal size/move loop from <see cref="XRWindow.PumpNativeWindowEventsFromHost"/>.
    /// Keeping that call outside the timer's render dispatch lets modal timer
    /// messages safely request complete engine frames without nesting inside an existing frame.
    /// </remarks>
    private void BlockForCollapsedWindowRendering(Func<bool> runUntilPredicate)
    {
        Debug.Out("Blocking for rendering.");
        while (runUntilPredicate())
        {
            PumpCollapsedWindowEvents();
            _waitToRender();
        }
        Debug.Out("No longer blocking main thread for rendering.");
    }

    private void PumpCollapsedWindowEvents()
    {
        _collapsedWindowPumpSnapshot.Clear();
        for (int i = 0; i < RuntimeEngine.Windows.Count; i++)
            _collapsedWindowPumpSnapshot.Add(RuntimeEngine.Windows[i]);

        try
        {
            for (int i = 0; i < _collapsedWindowPumpSnapshot.Count; i++)
            {
                XRWindow window = _collapsedWindowPumpSnapshot[i];
                if (!window.IsDisposed && !window.IsNativeEventPumpExternallyOwned)
                    window.PumpNativeWindowEventsFromHost();
            }
        }
        finally
        {
            _collapsedWindowPumpSnapshot.Clear();
        }
    }
}
