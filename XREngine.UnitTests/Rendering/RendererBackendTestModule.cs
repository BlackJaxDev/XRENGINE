using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

internal sealed class RendererBackendTestModule(RendererBackendRegistration registration) : IRendererBackendModule
{
    public RendererBackendMetadata Metadata => registration.Metadata;

    public IRendererBackendFactory Factory => registration.Factory;

    public int RegisteredCount { get; private set; }

    public int UnregisteredCount { get; private set; }

    public void OnRegistered()
        => RegisteredCount++;

    public void OnUnregistered()
        => UnregisteredCount++;
}
