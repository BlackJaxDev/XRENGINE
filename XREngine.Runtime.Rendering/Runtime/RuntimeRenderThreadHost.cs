using XREngine.Rendering;
using XREngine.Execution;

namespace XREngine;

/// <summary>Routes render-loop policy to one captured native window host.</summary>
public sealed class RuntimeRenderThreadHost
{
    private readonly Func<bool> _isExternalWindowPumpRunning;
    private readonly Action<Func<bool>> _blockForTimerRendering;
    private readonly Action _waitToRender;
    private readonly Action _stopTimer;
    private IRuntimeRenderThreadHost? _owner;
    private int _running;

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

    public int CurrentRenderThreadId => Volatile.Read(ref _owner)?.CurrentRenderThreadId ?? 0;

    public void BlockForRendering(Func<bool> runUntilPredicate)
    {
        ArgumentNullException.ThrowIfNull(runUntilPredicate);
        if (RuntimeWorkScheduler.IsCallerThread || OperatingSystem.IsBrowser())
            throw new InvalidOperationException(
                "BlockForRendering requires a threaded render host; a caller-thread host must drive StepFrame.");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("A render loop is already running.");
        try
        {
            IRuntimeWindowBackendFactory windows = RuntimeWindowBackendRegistry.RequireFactory();
            if (windows is not IRuntimeRenderThreadHostFactory factory)
                throw new NotSupportedException("RenderHost.Unavailable: the installed window backend does not provide a native render loop.");
            IRuntimeRenderThreadHost owner = factory.CreateRenderThreadHost(
                _blockForTimerRendering, _waitToRender, _stopTimer)
                ?? throw new InvalidOperationException("RenderHost.Unavailable: the window backend returned no render-loop owner.");
            Volatile.Write(ref _owner, owner);
            owner.BlockForRendering(runUntilPredicate, Mode);
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}
