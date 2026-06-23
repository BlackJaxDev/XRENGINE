namespace XREngine;

internal enum EngineRenderThreadHostMode
{
    CollapsedWindowRenderThread,
    SplitWindowPumpPrototype,
}

internal sealed class EngineRenderThreadHost
{
    private readonly object _sync = new();
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
        Engine.Time.Timer.BlockForRendering(runUntilPredicate);

        Debug.Rendering(
            "[RenderThreadHost] Exited render loop mode={0} renderThread={1} windowThread={2}.",
            mode,
            Engine.RenderThreadId,
            Engine.WindowThreadId);
    }
}
