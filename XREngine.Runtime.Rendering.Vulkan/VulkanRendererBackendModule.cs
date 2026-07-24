namespace XREngine.Rendering.Vulkan;

using XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Explicit Vulkan backend registration entry point for production and
/// native-AOT composition roots.
/// </summary>
public static class VulkanRendererBackendModule
{
    private const RendererBackendReloadLimitations ReloadLimitations =
        RendererBackendReloadLimitations.RequiresRendererTeardown |
        RendererBackendReloadLimitations.NativeLoaderIsProcessScoped |
        RendererBackendReloadLimitations.RequiresOpenXrSessionTeardown;

    private const string ReloadDescription =
        "Destroy all Vulkan renderer instances and OpenXR sessions before replacing this module. " +
        "The Vulkan native loader remains process scoped.";

    /// <summary>Creates the registration without mutating a catalog.</summary>
    public static RendererBackendRegistration CreateRegistration(Version? version = null)
        => new(
            new RendererBackendMetadata(
                RendererBackendId.Vulkan,
                RuntimeGraphicsApiKind.Vulkan,
                "XREngine Vulkan",
                version ?? typeof(VulkanRendererBackendModule).Assembly.GetName().Version ?? new Version(1, 0),
                RendererBackendCapabilities.DesktopPresentation |
                RendererBackendCapabilities.HeadlessRendering |
                RendererBackendCapabilities.OpenXrPresentation |
                RendererBackendCapabilities.GpuCompute |
                RendererBackendCapabilities.EditorTextureInterop,
                ReloadLimitations,
                ReloadDescription),
            new VulkanRendererBackendFactory());

    /// <summary>Registers the built-in Vulkan backend and returns its catalog lease.</summary>
    public static IDisposable Register(IRendererBackendCatalog catalog, Version? version = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        TextureStreamingBackendRegistry.Register(
            RuntimeGraphicsApiKind.Vulkan,
            VulkanTextureStreamingBackendProvider.Instance);
        IDisposable rendererLease = catalog.Register(CreateRegistration(version));
        IDisposable? vendorUpscaleLease = null;
        try
        {
            vendorUpscaleLease = RuntimeVendorUpscaleService.Register(
                VulkanVendorUpscaleService.Instance);
            IDisposable openXrLease = OpenXrGraphicsBindingRegistry.Register(
                RendererBackendId.Vulkan,
                static () => new VulkanXrGraphicsBinding());
            return new CompositeModuleRegistrationLease(
                new CompositeModuleRegistrationLease(rendererLease, vendorUpscaleLease),
                openXrLease);
        }
        catch
        {
            vendorUpscaleLease?.Dispose();
            rendererLease.Dispose();
            throw;
        }
    }
}
