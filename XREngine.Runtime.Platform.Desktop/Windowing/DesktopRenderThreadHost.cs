using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Owns native render threads and the collapsed native event pump.</summary>
internal sealed class DesktopRenderThreadHost(
    Action<Func<bool>> blockForTimerRendering, Action waitToRender, Action stopTimer) : IRuntimeRenderThreadHost
{
    private readonly List<XRWindow> _collapsedWindowPumpSnapshot = [];
    private readonly Action<Func<bool>> _blockForTimerRendering = blockForTimerRendering;
    private readonly Action _waitToRender = waitToRender;
    private readonly Action _stopTimer = stopTimer;
    private int _running;
    private int _currentRenderThreadId;

    public int CurrentRenderThreadId => Volatile.Read(ref _currentRenderThreadId);

    public void BlockForRendering(Func<bool> runUntilPredicate, RuntimeRenderThreadHostMode mode)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("A render loop is already running.");
        try
        {
            if (mode == RuntimeRenderThreadHostMode.CollapsedWindowRenderThread)
                RunRenderLoopOnCurrentThread(runUntilPredicate, mode);
            else
                BlockForDedicatedRenderThread(runUntilPredicate, mode);
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private void BlockForDedicatedRenderThread(Func<bool> runUntilPredicate, RuntimeRenderThreadHostMode mode)
    {
        using var started = new ManualResetEventSlim(false);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunRenderLoopOnCurrentThread(runUntilPredicate, mode, started); }
            catch (Exception error)
            {
                failure = error;
                // Signal before diagnostics or host callbacks, which can also fail.
                started.Set();
                try { Debug.LogException(error, "[RenderThreadHost] Dedicated render thread failed."); }
                catch { }
                try { _stopTimer(); }
                catch (Exception stopError) { failure = new AggregateException(error, stopError); }
            }
        })
        {
            Name = "XRE-Render",
            IsBackground = false,
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        try
        {
            started.Wait();
            Debug.Rendering(
                "[RenderThreadHost] Waiting for dedicated render thread. callerThread={0} renderThread={1} windowThread={2}.",
                Environment.CurrentManagedThreadId, RuntimeEngine.RenderThreadId, RuntimeEngine.WindowThreadId);
        }
        finally
        {
            // Keep the startup signal alive until the worker can no longer use it.
            thread.Join();
        }
        if (failure is not null)
            throw new InvalidOperationException("Dedicated render thread failed.", failure);
    }

    private void RunRenderLoopOnCurrentThread(
        Func<bool> runUntilPredicate, RuntimeRenderThreadHostMode mode, ManualResetEventSlim? started = null)
    {
        Volatile.Write(ref _currentRenderThreadId, Environment.CurrentManagedThreadId);
        RuntimeEngine.AssignRenderThread(CurrentRenderThreadId);
        Debug.Rendering("[RenderThreadHost] Entering render loop mode={0} renderThread={1} windowThread={2}.",
            mode, RuntimeEngine.RenderThreadId, RuntimeEngine.WindowThreadId);
        started?.Set();
        if (mode == RuntimeRenderThreadHostMode.CollapsedWindowRenderThread)
            BlockForCollapsedWindowRendering(runUntilPredicate);
        else
            _blockForTimerRendering(runUntilPredicate);
        Debug.Rendering("[RenderThreadHost] Exited render loop mode={0} renderThread={1} windowThread={2}.",
            mode, RuntimeEngine.RenderThreadId, RuntimeEngine.WindowThreadId);
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
