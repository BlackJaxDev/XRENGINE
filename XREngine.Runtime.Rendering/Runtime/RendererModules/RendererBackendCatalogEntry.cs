namespace XREngine.Rendering;

internal sealed class RendererBackendCatalogEntry(RendererBackendRegistration registration)
{
    public RendererBackendRegistration Registration { get; } = registration;
}
