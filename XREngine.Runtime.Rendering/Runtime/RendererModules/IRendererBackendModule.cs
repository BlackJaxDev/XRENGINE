namespace XREngine.Rendering;

/// <summary>
/// Complete contract exported by a renderer leaf assembly or collectible editor module.
/// Static applications may construct the equivalent <see cref="RendererBackendRegistration"/>
/// directly without dynamic loading.
/// </summary>
public interface IRendererBackendModule : IRendererBackendLifecycle
{
    RendererBackendMetadata Metadata { get; }

    IRendererBackendFactory Factory { get; }
}
