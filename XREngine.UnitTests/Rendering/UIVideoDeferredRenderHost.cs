using System.Reflection;
using System.Runtime.ExceptionServices;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

internal class UIVideoDeferredRenderHost : DispatchProxy
{
    private IRuntimeRenderingHostServices _inner = null!;
    private readonly Queue<Action> _renderTasks = new();
    private readonly Queue<Action> _appTasks = new();

    public int QueuedRenderTasks => _renderTasks.Count;
    public int QueuedAppTasks => _appTasks.Count;
    public bool CaptureAppTasks { get; set; }

    public static (IRuntimeRenderingHostServices Host, UIVideoDeferredRenderHost State) CreateHost()
    {
        IRuntimeRenderingHostServices host = Create<IRuntimeRenderingHostServices, UIVideoDeferredRenderHost>();
        UIVideoDeferredRenderHost state = (UIVideoDeferredRenderHost)(object)host;
        state._inner = RuntimeRenderingHostServices.Current;
        return (host, state);
    }

    public void DrainRenderTasks()
    {
        while (_renderTasks.TryDequeue(out Action? action))
            action();
    }

    public void DrainAppTasks()
    {
        while (_appTasks.TryDequeue(out Action? action))
            action();
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IRuntimeRenderSchedulingServices.EnqueueRenderThreadTask) &&
            args is { Length: > 0 } && args[0] is Action action)
        {
            _renderTasks.Enqueue(action);
            return null;
        }
        if (CaptureAppTasks &&
            targetMethod.Name == nameof(IRuntimeRenderSchedulingServices.EnqueueAppThreadTask) &&
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
}
