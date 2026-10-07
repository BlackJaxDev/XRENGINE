using System.Threading;

namespace XREngine;

/// <summary>Stores the native log backend selected by application composition.</summary>
/// <remarks>
/// A native log operation requires Current at entry and captures that backend
/// for any queued work. Replacing or removing Current does not close a session
/// or writer that Debug already holds. New operations fail when Current is null.
/// </remarks>
public static class RuntimeDebugLogBackendServices
{
    private static IRuntimeDebugLogBackend? _current;

    public static IRuntimeDebugLogBackend? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IRuntimeDebugLogBackend RequireCurrent()
        => Current ?? throw new InvalidOperationException(
            "Native Debug logging requires a registered IRuntimeDebugLogBackend. Register a native logging backend before logging.");

    /// <summary>Installs a built-in backend only when no provider is installed.</summary>
    public static void RegisterIfAbsent(IRuntimeDebugLogBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        Interlocked.CompareExchange(ref _current, backend, null);
    }
}
