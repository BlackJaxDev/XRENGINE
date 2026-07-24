namespace XREngine.Rendering;

/// <summary>
/// Explicit static registration path used by production and native-AOT composition roots.
/// It intentionally performs no assembly scanning or dynamic loading.
/// </summary>
public static class BuiltInRendererBackendModules
{
    private const RendererBackendReloadLimitations BuiltInReloadLimitations =
        RendererBackendReloadLimitations.RequiresRendererTeardown |
        RendererBackendReloadLimitations.NativeLoaderIsProcessScoped |
        RendererBackendReloadLimitations.RequiresOpenXrSessionTeardown;

    private const string BuiltInReloadDescription =
        "Destroy all renderer instances and OpenXR sessions before replacing this module. " +
        "The native graphics loader remains process scoped.";

    /// <summary>
    /// Registers the statically linked OpenGL and Vulkan factories through the same contract
    /// used by editor-provided collectible modules.
    /// </summary>
    public static IDisposable RegisterAll(IRendererBackendCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        IDisposable? openGlLease = null;
        try
        {
            Version version = typeof(BuiltInRendererBackendModules).Assembly.GetName().Version ?? new Version(1, 0);
            openGlLease = catalog.Register(CreateOpenGlRegistration(version));
            IDisposable vulkanLease = catalog.Register(CreateVulkanRegistration(version));
            return new CompositeRendererBackendRegistrationLease(openGlLease, vulkanLease);
        }
        catch
        {
            openGlLease?.Dispose();
            throw;
        }
    }

    public static RendererBackendRegistration CreateOpenGlRegistration(Version? version = null)
        => new(
            new RendererBackendMetadata(
                RendererBackendId.OpenGL,
                RuntimeGraphicsApiKind.OpenGL,
                "XREngine OpenGL",
                version ?? typeof(BuiltInRendererBackendModules).Assembly.GetName().Version ?? new Version(1, 0),
                RendererBackendCapabilities.DesktopPresentation |
                RendererBackendCapabilities.HeadlessRendering |
                RendererBackendCapabilities.OpenXrPresentation |
                RendererBackendCapabilities.GpuCompute |
                RendererBackendCapabilities.EditorTextureInterop |
                RendererBackendCapabilities.SparseTextureStreaming,
                BuiltInReloadLimitations,
                BuiltInReloadDescription),
            new OpenGLRendererBackendFactory());

    public static RendererBackendRegistration CreateVulkanRegistration(Version? version = null)
        => new(
            new RendererBackendMetadata(
                RendererBackendId.Vulkan,
                RuntimeGraphicsApiKind.Vulkan,
                "XREngine Vulkan",
                version ?? typeof(BuiltInRendererBackendModules).Assembly.GetName().Version ?? new Version(1, 0),
                RendererBackendCapabilities.DesktopPresentation |
                RendererBackendCapabilities.HeadlessRendering |
                RendererBackendCapabilities.OpenXrPresentation |
                RendererBackendCapabilities.GpuCompute |
                RendererBackendCapabilities.EditorTextureInterop,
                BuiltInReloadLimitations,
                BuiltInReloadDescription),
            new VulkanRendererBackendFactory());

}
