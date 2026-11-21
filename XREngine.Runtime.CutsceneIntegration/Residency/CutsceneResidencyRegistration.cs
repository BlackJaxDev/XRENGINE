namespace XREngine.Cutscenes;

/// <summary>Registers the default residency backend factory of <see cref="CutsceneDirectorComponent"/> for an application composition.</summary>
public static class CutsceneResidencyRegistration
{
    /// <summary>
    /// Sets <see cref="CutsceneDirectorComponent.DefaultResidencyBackendFactory"/> if no factory is set yet.
    /// Dispose the result to remove the factory again, if it is still the registered one.
    /// </summary>
    /// <param name="factory">The factory that creates the texture residency backend of a director.</param>
    public static IDisposable InstallDefaultBackendFactory(Func<CutsceneDirectorComponent, IResidencyBackend?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (CutsceneDirectorComponent.DefaultResidencyBackendFactory is not null)
            return new Lease(null);

        CutsceneDirectorComponent.DefaultResidencyBackendFactory = factory;
        return new Lease(factory);
    }

    /// <summary>
    /// True while a host composition says that residency is not available, for example a headless host with no renderer.
    /// A director with no backend then opens without residency and adds no diagnostic.
    /// </summary>
    public static bool IsUnavailableOnHost => Volatile.Read(ref _unavailableCount) > 0;

    private static int _unavailableCount;

    /// <summary>
    /// Marks residency as not available on this host, so a director with no backend factory opens quietly.
    /// Dispose the result to remove the mark.
    /// </summary>
    public static IDisposable InstallUnavailable()
    {
        Interlocked.Increment(ref _unavailableCount);
        return new UnavailableLease();
    }

    private sealed class UnavailableLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _unavailableCount);
        }
    }

    private sealed class Lease(Func<CutsceneDirectorComponent, IResidencyBackend?>? installed) : IDisposable
    {
        private Func<CutsceneDirectorComponent, IResidencyBackend?>? _installed = installed;

        public void Dispose()
        {
            if (_installed is not null && ReferenceEquals(CutsceneDirectorComponent.DefaultResidencyBackendFactory, _installed))
                CutsceneDirectorComponent.DefaultResidencyBackendFactory = null;
            _installed = null;
        }
    }
}
