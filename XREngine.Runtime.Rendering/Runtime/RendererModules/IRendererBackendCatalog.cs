namespace XREngine.Rendering;

/// <summary>
/// Registry and allocation-free lookup surface for installed renderer backend modules.
/// </summary>
public interface IRendererBackendCatalog
{
    int Count { get; }

    IDisposable Register(
        RendererBackendRegistration registration,
        RendererBackendRegistrationBehavior behavior = RendererBackendRegistrationBehavior.RejectDuplicate);

    IDisposable Register(
        IRendererBackendModule module,
        RendererBackendRegistrationBehavior behavior = RendererBackendRegistrationBehavior.RejectDuplicate);

    bool TryGet(RendererBackendId id, out RendererBackendRegistration registration);

    RendererBackendRegistration GetRequired(
        RendererBackendId id,
        RendererBackendCapabilities requiredCapabilities = RendererBackendCapabilities.None);

    IRuntimeRendererHost CreateRequired(
        RuntimeGraphicsApiKind graphicsApi,
        in RendererBackendCreateContext context,
        RendererBackendCapabilities requiredCapabilities = RendererBackendCapabilities.None);
}
