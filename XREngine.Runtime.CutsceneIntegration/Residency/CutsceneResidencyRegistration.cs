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
