namespace XREngine.Cutscenes;

/// <summary>Registers the default binding resolver factory of <see cref="CutsceneDirectorComponent"/> for an application composition.</summary>
public static class CutsceneBindingRegistration
{
    /// <summary>
    /// Sets <see cref="CutsceneDirectorComponent.DefaultBindingResolverFactory"/> if no factory is set yet.
    /// A factory that the editor or game code sets stays in place.
    /// Dispose the result to remove the factory again, if it is still the registered one.
    /// </summary>
    /// <param name="factory">The factory that creates the binding resolver of a director.</param>
    public static IDisposable InstallDefaultResolverFactory(Func<CutsceneDirectorComponent, ICutsceneBindingResolver?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (CutsceneDirectorComponent.DefaultBindingResolverFactory is not null)
            return new Lease(null);

        CutsceneDirectorComponent.DefaultBindingResolverFactory = factory;
        return new Lease(factory);
    }

    /// <summary>
    /// Sets a default factory that resolves the bindings of a director against the root nodes of its world.
    /// The resolver gets prefabs from <paramref name="assets"/>. A director that is not in a world gets no resolver,
    /// so its open fails with a diagnostic.
    /// </summary>
    /// <param name="assets">The asset manager that provides prefabs.</param>
    public static IDisposable InstallSceneResolverFactory(AssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return InstallDefaultResolverFactory(director => CreateSceneResolver(assets, director));
    }

    /// <summary>Creates a resolver for the root nodes of the world of a director.</summary>
    /// <param name="assets">The asset manager that provides prefabs.</param>
    /// <param name="director">The director.</param>
    /// <returns>The resolver, or null if the director is not in a world.</returns>
    public static ICutsceneBindingResolver? CreateSceneResolver(AssetManager assets, CutsceneDirectorComponent director)
        => director.World is RuntimeWorld world ? new CutsceneSceneBindingResolver(assets, world.RootNodes, world) : null;

    private sealed class Lease(Func<CutsceneDirectorComponent, ICutsceneBindingResolver?>? installed) : IDisposable
    {
        private Func<CutsceneDirectorComponent, ICutsceneBindingResolver?>? _installed = installed;

        public void Dispose()
        {
            if (_installed is not null && ReferenceEquals(CutsceneDirectorComponent.DefaultBindingResolverFactory, _installed))
                CutsceneDirectorComponent.DefaultBindingResolverFactory = null;
            _installed = null;
        }
    }
}
