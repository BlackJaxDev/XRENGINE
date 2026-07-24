namespace XREngine.Rendering.OpenGL;

using XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Statically registers the OpenGL renderer backend without requiring the stable rendering
/// kernel to reference the concrete backend assembly.
/// </summary>
public static class OpenGlRendererBackendModule
{
    private const RendererBackendReloadLimitations ReloadLimitations =
        RendererBackendReloadLimitations.RequiresRendererTeardown |
        RendererBackendReloadLimitations.NativeLoaderIsProcessScoped |
        RendererBackendReloadLimitations.RequiresOpenXrSessionTeardown;

    private const string ReloadDescription =
        "Destroy all renderer instances and OpenXR sessions before replacing this module. " +
        "The native graphics loader remains process scoped.";

    /// <summary>
    /// Registers the statically linked OpenGL renderer and returns its catalog lease.
    /// </summary>
    public static IDisposable Register(IRendererBackendCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        TextureStreamingBackendRegistry.Register(
            RuntimeGraphicsApiKind.OpenGL,
            OpenGlTextureStreamingBackendProvider.Instance);
        IDisposable rendererLease = catalog.Register(CreateRegistration());
        try
        {
            IDisposable openXrLease = OpenXrGraphicsBindingRegistry.Register(
                RendererBackendId.OpenGL,
                static () => new OpenGlXrGraphicsBinding());
            return new CompositeModuleRegistrationLease(rendererLease, openXrLease);
        }
        catch
        {
            rendererLease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the OpenGL backend registration for composition roots that aggregate leases.
    /// </summary>
    public static RendererBackendRegistration CreateRegistration(Version? version = null)
        => new(
            new RendererBackendMetadata(
                RendererBackendId.OpenGL,
                RuntimeGraphicsApiKind.OpenGL,
                "XREngine OpenGL",
                version ?? typeof(OpenGlRendererBackendModule).Assembly.GetName().Version ?? new Version(1, 0),
                RendererBackendCapabilities.DesktopPresentation |
                RendererBackendCapabilities.HeadlessRendering |
                RendererBackendCapabilities.OpenXrPresentation |
                RendererBackendCapabilities.GpuCompute |
                RendererBackendCapabilities.EditorTextureInterop |
                RendererBackendCapabilities.SparseTextureStreaming,
                ReloadLimitations,
                ReloadDescription),
            new OpenGLRendererBackendFactory());
}
