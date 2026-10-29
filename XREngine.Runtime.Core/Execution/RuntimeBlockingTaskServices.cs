using XREngine.Data;

namespace XREngine.Execution;

/// <summary>Admits synchronous task completion before the operation starts.</summary>
public static class RuntimeBlockingTaskServices
{
    private static IRuntimeBlockingTaskBackend? _backend;

    /// <summary>Installs a host backend only when no backend is registered.</summary>
    public static bool TryRegisterBackend(IRuntimeBlockingTaskBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return Interlocked.CompareExchange(ref _backend, backend, null) is null;
    }

    /// <summary>Runs an operation through the installed native completion backend.</summary>
    public static T Run<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (OperatingSystem.IsBrowser() || RuntimeWorkScheduler.IsCallerThread || RuntimeAssetReadServices.IsCallerThread)
            throw new NotSupportedException("RuntimeTask.BlockingUnavailable: caller-thread hosts must await asynchronous operations.");
        IRuntimeBlockingTaskBackend backend = Volatile.Read(ref _backend)
            ?? throw new InvalidOperationException("RuntimeTask.BackendMissing: register a native completion backend before starting synchronous task work.");
        return backend.Run(operation);
    }
}
