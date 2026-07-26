namespace XREngine.Rendering;

/// <summary>
/// A module registration shared by static/AOT composition and collectible editor loading.
/// </summary>
public sealed record RendererBackendRegistration
{
    public RendererBackendRegistration(IRendererBackendModule module)
        : this(
            module?.Metadata ?? throw new ArgumentNullException(nameof(module)),
            module.Factory,
            module)
    {
    }

    public RendererBackendRegistration(
        RendererBackendMetadata metadata,
        IRendererBackendFactory factory,
        IRendererBackendLifecycle? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(factory);
        Metadata = metadata;
        Factory = factory;
        Lifecycle = lifecycle;
    }

    public RendererBackendMetadata Metadata { get; }

    public IRendererBackendFactory Factory { get; }

    public IRendererBackendLifecycle? Lifecycle { get; }
}
