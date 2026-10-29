using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Gives focused tests a deterministic application-thread owner and queue.</summary>
internal class UITestAppThreadHostScope : DispatchProxy, IDisposable
{
    private IRuntimeRenderingHostServices _inner = null!;
    private IDisposable? _installation;
    private readonly ConcurrentQueue<Action> _appTasks = new();
    private int _ownerThreadId;

    public static UITestAppThreadHostScope Install()
    {
        IRuntimeRenderingHostServices host = Create<IRuntimeRenderingHostServices, UITestAppThreadHostScope>();
        UITestAppThreadHostScope state = (UITestAppThreadHostScope)(object)host;
        state._inner = RuntimeRenderingHostServices.Current;
        state._ownerThreadId = Environment.CurrentManagedThreadId;
        state._installation = RuntimeRenderingHostServices.Install(host);
        return state;
    }

    public int QueuedAppTasks => _appTasks.Count;

    public void DrainAppTasks()
    {
        EnsureOwnerThread();
        while (_appTasks.TryDequeue(out Action? action))
            action();
    }

    public void Dispose()
    {
        try { DrainAppTasks(); }
        finally { Interlocked.Exchange(ref _installation, null)?.Dispose(); }
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == "get_IsAppThread")
            return Environment.CurrentManagedThreadId == _ownerThreadId;
        if (targetMethod.Name is nameof(IRuntimeRenderSchedulingServices.EnqueueAppThreadTask) &&
            args is { Length: > 0 } && args[0] is Action appAction)
        {
            _appTasks.Enqueue(appAction);
            return null;
        }

        try { return targetMethod.Invoke(_inner, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Application-thread test work must be drained by its captured owner thread.");
    }
}
