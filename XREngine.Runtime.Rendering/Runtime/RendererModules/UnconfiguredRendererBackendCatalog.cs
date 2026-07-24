namespace XREngine.Rendering;

internal sealed class UnconfiguredRendererBackendCatalog : IRendererBackendCatalog
{
    public int Count => 0;

    public IDisposable Register(
        RendererBackendRegistration registration,
        RendererBackendRegistrationBehavior behavior = RendererBackendRegistrationBehavior.RejectDuplicate)
        => throw new InvalidOperationException(
            "This rendering host does not expose a writable renderer backend catalog. " +
            "Install a concrete rendering host before registering renderer modules.");

    public IDisposable Register(
        IRendererBackendModule module,
        RendererBackendRegistrationBehavior behavior = RendererBackendRegistrationBehavior.RejectDuplicate)
        => throw new InvalidOperationException(
            "This rendering host does not expose a writable renderer backend catalog. " +
            "Install a concrete rendering host before registering renderer modules.");

    public bool TryGet(RendererBackendId id, out RendererBackendRegistration registration)
    {
        registration = null!;
        return false;
    }

    public RendererBackendRegistration GetRequired(
        RendererBackendId id,
        RendererBackendCapabilities requiredCapabilities = RendererBackendCapabilities.None)
        => throw new InvalidOperationException(
            $"Required renderer backend module '{id}' is not installed. Installed backend modules: none. " +
            "Register the backend at the application composition root before creating a render window.");

    public IRuntimeRendererHost CreateRequired(
        RuntimeGraphicsApiKind graphicsApi,
        in RendererBackendCreateContext context,
        RendererBackendCapabilities requiredCapabilities = RendererBackendCapabilities.None)
        => GetRequired(RendererBackendId.FromGraphicsApi(graphicsApi), requiredCapabilities)
            .Factory.Create(context);
}
