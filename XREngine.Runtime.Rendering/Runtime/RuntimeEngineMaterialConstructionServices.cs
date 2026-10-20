namespace XREngine.Rendering;

/// <summary>
/// Application-owned target selection for built-in material construction. Browser composition
/// must install its cooked target before deserializing materials; desktop retains its GLSL path.
/// </summary>
public static class RuntimeEngineMaterialConstructionServices
{
    private static Installation? _current;
    [ThreadStatic]
    private static ThreadInstallation? _currentThread;

    /// <summary>
    /// Gets the calling thread's scoped target, then the application target, or the
    /// legacy desktop GLSL target. Browser construction without either installation
    /// fails before shader asset IO.
    /// </summary>
    public static EngineMaterialConstructionTarget Target
        => _currentThread?.Target ?? Volatile.Read(ref _current)?.Target ?? (OperatingSystem.IsBrowser()
            ? throw new InvalidOperationException(
                "WebGPU.MaterialConstruction.TargetRequired: install the browser material target before asset deserialization.")
            : EngineMaterialConstructionTarget.DesktopGlsl);

    /// <summary>Installs a material target until the returned lease is disposed.</summary>
    public static IDisposable Install(EngineMaterialConstructionTarget target)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        if (OperatingSystem.IsBrowser() && target != EngineMaterialConstructionTarget.WebGpuCooked)
            throw new InvalidOperationException(
                "WebGPU.MaterialConstruction.TargetMismatch: browser materials require the cooked WebGPU target.");

        Installation installation = new(target);
        if (Interlocked.CompareExchange(ref _current, installation, null) is not null)
            throw new InvalidOperationException(
                "MaterialConstruction.TargetConflict: dispose the active installation before replacing its target.");
        return installation;
    }

    /// <summary>
    /// Temporarily selects material construction on the calling thread only. Synchronous
    /// editor cooking can inspect another target without changing live renderer threads.
    /// Nested scopes restore the preceding target in stack order.
    /// </summary>
    public static IDisposable InstallForCurrentThread(EngineMaterialConstructionTarget target)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        if (OperatingSystem.IsBrowser() && target != EngineMaterialConstructionTarget.WebGpuCooked)
            throw new InvalidOperationException(
                "WebGPU.MaterialConstruction.TargetMismatch: browser materials require the cooked WebGPU target.");

        ThreadInstallation installation = new(target, _currentThread);
        _currentThread = installation;
        return installation;
    }

    private sealed class ThreadInstallation(
        EngineMaterialConstructionTarget target,
        ThreadInstallation? previous) : IDisposable
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private bool _disposed;

        internal EngineMaterialConstructionTarget Target { get; } = target;

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException(
                    "MaterialConstruction.ThreadTargetThreadMismatch: dispose the scoped target on its creating thread.");
            if (_disposed)
                return;
            if (!ReferenceEquals(_currentThread, this))
                throw new InvalidOperationException(
                    "MaterialConstruction.ThreadTargetOutOfOrder: dispose scoped targets in stack order.");

            _currentThread = previous;
            _disposed = true;
        }
    }

    private sealed class Installation(EngineMaterialConstructionTarget target) : IDisposable
    {
        private int _disposed;

        internal EngineMaterialConstructionTarget Target { get; } = target;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.CompareExchange(ref _current, null, this);
        }
    }
}
