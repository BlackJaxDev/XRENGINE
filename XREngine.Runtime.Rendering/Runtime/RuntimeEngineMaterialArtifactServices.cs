using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

/// <summary>Owns the immutable cooked catalog used by shared native material publication.</summary>
public static class RuntimeEngineMaterialArtifactServices
{
    private static Installation? _current;
    [ThreadStatic] private static ThreadInstallation? _currentThread;

    public static IShaderProgramArtifactResolver? Resolver
        => _currentThread is { } thread ? thread.Resolver : Volatile.Read(ref _current)?.Resolver;

    public static IEngineMaterialVariantResolver? Variants
        => _currentThread is { } thread ? thread.Variants : Volatile.Read(ref _current)?.Variants;

    /// <summary>Captures the paired identity and variant views of one installed catalog.</summary>
    public static void GetCurrent(out IShaderProgramArtifactResolver? resolver,
        out IEngineMaterialVariantResolver? variants)
    {
        if (_currentThread is { } thread)
        {
            resolver = thread.Resolver;
            variants = thread.Variants;
            return;
        }
        Installation? installation = Volatile.Read(ref _current);
        resolver = installation?.Resolver;
        variants = installation?.Variants;
    }

    /// <summary>Installs one application catalog until all its scene publications have retired.</summary>
    public static IDisposable Install(IShaderProgramArtifactResolver resolver,
        IEngineMaterialVariantResolver? variants = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Installation installation = new(resolver, variants);
        if (Interlocked.CompareExchange(ref _current, installation, null) is not null)
            throw new InvalidOperationException("MaterialArtifacts.CatalogConflict: retire the active catalog before replacing it.");
        return installation;
    }

    /// <summary>Overrides the catalog for a synchronous cold audit, including an explicitly absent catalog.</summary>
    public static IDisposable InstallForCurrentThread(IShaderProgramArtifactResolver? resolver,
        IEngineMaterialVariantResolver? variants = null)
    {
        ThreadInstallation installation = new(resolver, variants, _currentThread);
        _currentThread = installation;
        return installation;
    }

    private sealed class Installation(IShaderProgramArtifactResolver resolver,
        IEngineMaterialVariantResolver? variants) : IDisposable
    {
        internal IShaderProgramArtifactResolver Resolver { get; } = resolver;
        internal IEngineMaterialVariantResolver? Variants { get; } = variants;
        public void Dispose() => Interlocked.CompareExchange(ref _current, null, this);
    }

    private sealed class ThreadInstallation(IShaderProgramArtifactResolver? resolver,
        IEngineMaterialVariantResolver? variants, ThreadInstallation? previous) : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private bool _disposed;
        internal IShaderProgramArtifactResolver? Resolver { get; } = resolver;
        internal IEngineMaterialVariantResolver? Variants { get; } = variants;

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
