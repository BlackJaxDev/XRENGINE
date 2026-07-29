namespace XREngine.Rendering.Vulkan;

using XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Explicit Vulkan backend registration entry point for production and
/// native-AOT composition roots.
/// </summary>
public static class VulkanRendererBackendModule
{
    /// <summary>Creates the registration without mutating a catalog.</summary>
    public static RendererBackendRegistration CreateRegistration(Version? version = null)
        => new(new VulkanRendererBackendModuleEntry(version));

    /// <summary>Registers the built-in Vulkan backend and returns its catalog lease.</summary>
    public static IDisposable Register(IRendererBackendCatalog catalog, Version? version = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        VulkanRendererBackendModuleEntry module = new(version);
        try
        {
            return catalog.Register(module);
        }
        catch
        {
            module.Dispose();
            throw;
        }
    }
}
