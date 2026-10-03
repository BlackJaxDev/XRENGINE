using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Owns the immutable cooked catalog used by shared native material publication.</summary>
public static class RuntimeEngineMaterialArtifactServices
{
    private static Installation? _current;
    [ThreadStatic] private static ThreadInstallation? _currentThread;

    public static IShaderProgramArtifactResolver? Resolver
        => _currentThread is { } thread ? thread.Resolver : Volatile.Read(ref _current)?.Resolver;

    /// <summary>Installs one application catalog until all its scene publications have retired.</summary>
    public static IDisposable Install(IShaderProgramArtifactResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Installation installation = new(resolver);
        if (Interlocked.CompareExchange(ref _current, installation, null) is not null)
            throw new InvalidOperationException("MaterialArtifacts.CatalogConflict: retire the active catalog before replacing it.");
        return installation;
    }

    /// <summary>Overrides the catalog for a synchronous cold audit, including an explicitly absent catalog.</summary>
    public static IDisposable InstallForCurrentThread(IShaderProgramArtifactResolver? resolver)
    {
        ThreadInstallation installation = new(resolver, _currentThread);
        _currentThread = installation;
        return installation;
    }

    private sealed class Installation(IShaderProgramArtifactResolver resolver) : IDisposable
    {
        internal IShaderProgramArtifactResolver Resolver { get; } = resolver;
        public void Dispose() => Interlocked.CompareExchange(ref _current, null, this);
    }

    private sealed class ThreadInstallation(IShaderProgramArtifactResolver? resolver, ThreadInstallation? previous) : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _disposed;
        internal IShaderProgramArtifactResolver? Resolver { get; } = resolver;

        public void Dispose()
        {
            if (_thread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("MaterialArtifacts.ThreadMismatch: dispose the catalog scope on its owning thread.");
            if (_disposed) return;
            if (!ReferenceEquals(_currentThread, this))
                throw new InvalidOperationException("MaterialArtifacts.ScopeOrder: dispose catalog scopes in stack order.");
            _currentThread = previous;
            _disposed = true;
        }
    }
}
