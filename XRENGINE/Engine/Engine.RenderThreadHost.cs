using XREngine.Rendering;

namespace XREngine;

internal enum EngineRenderThreadHostMode
{
    CollapsedWindowRenderThread,
    SplitWindowPumpPrototype,
}

internal sealed class EngineRenderThreadHost
{
    private readonly object _sync = new();
    private readonly List<XRWindow> _collapsedWindowPumpSnapshot = [];
    private Thread? _dedicatedThread;
    private Exception? _dedicatedThreadException;

    public EngineRenderThreadHostMode Mode
        => Engine.WindowPumpHost.IsRunning
            ? EngineRenderThreadHostMode.SplitWindowPumpPrototype
            : EngineRenderThreadHostMode.CollapsedWindowRenderThread;

    public int CurrentRenderThreadId { get; private set; }

    public void BlockForRendering(Func<bool> runUntilPredicate)
    {
        EngineRenderThreadHostMode mode = Mode;
        if (mode == EngineRenderThreadHostMode.SplitWindowPumpPrototype)
        {
            BlockForDedicatedRenderThread(runUntilPredicate, mode);
            return;
        }

        RunRenderLoopOnCurrentThread(runUntilPredicate, mode);
    }

    private void BlockForDedicatedRenderThread(
        Func<bool> runUntilPredicate,
        EngineRenderThreadHostMode mode)
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
            Engine.RenderThreadId,
            Engine.WindowThreadId);

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
        EngineRenderThreadHostMode mode,
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
            Engine.Time.Timer.Stop();
        }
    }

    private void RunRenderLoopOnCurrentThread(
        Func<bool> runUntilPredicate,
        EngineRenderThreadHostMode mode,
        ManualResetEventSlim? started = null)
    {
        CurrentRenderThreadId = Environment.CurrentManagedThreadId;
        Engine.SetRenderThreadId(CurrentRenderThreadId);

        Debug.Rendering(
            "[RenderThreadHost] Entering render loop mode={0} renderThread={1} windowThread={2}.",
            mode,
            Engine.RenderThreadId,
            Engine.WindowThreadId);

        started?.Set();
        if (mode == EngineRenderThreadHostMode.CollapsedWindowRenderThread)
            BlockForCollapsedWindowRendering(runUntilPredicate);
        else
            Engine.Time.Timer.BlockForRendering(runUntilPredicate);

        Debug.Rendering(
            "[RenderThreadHost] Exited render loop mode={0} renderThread={1} windowThread={2}.",
            mode,
            Engine.RenderThreadId,
            Engine.WindowThreadId);
    }

    /// <summary>
    /// Pumps collapsed-mode native events before entering a render dispatch.
    /// </summary>
    /// <remarks>
    /// Win32 enters its modal size/move loop from <see cref="XRWindow.PumpNativeWindowEventsFromHost"/>.
    /// Keeping that call outside <see cref="Timers.EngineTimer.DispatchRender"/> lets modal timer
    /// messages safely request complete engine frames without nesting inside an existing frame.
    /// </remarks>
    private void BlockForCollapsedWindowRendering(Func<bool> runUntilPredicate)
    {
        Debug.Out("Blocking for rendering.");
        while (runUntilPredicate())
        {
            PumpCollapsedWindowEvents();
            Engine.Time.Timer.WaitToRender();
        }
        Debug.Out("No longer blocking main thread for rendering.");
    }

    private void PumpCollapsedWindowEvents()
    {
        _collapsedWindowPumpSnapshot.Clear();
        for (int i = 0; i < Engine.Windows.Count; i++)
            _collapsedWindowPumpSnapshot.Add(Engine.Windows[i]);

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
