#if XRENGINE_STATIC_OPENGL
using XREngine.Rendering.OpenGL;
#endif
#if XRENGINE_STATIC_VULKAN
using XREngine.Rendering.Vulkan;
#endif

namespace XREngine.Rendering;

/// <summary>
/// Statically composes the built-in renderer leaf assemblies without reflection.
/// </summary>
public static class BuiltInRendererBackendModules
{
    /// <summary>
    /// Registers both built-in renderer backends and returns a lease that unregisters them.
    /// </summary>
    public static IDisposable RegisterAll(IRendererBackendCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        List<IDisposable> leases = new(2);
        try
        {
#if XRENGINE_STATIC_OPENGL
            leases.Add(OpenGlRendererBackendModule.Register(catalog));
#endif
#if XRENGINE_STATIC_VULKAN
            leases.Add(VulkanRendererBackendModule.Register(catalog));
#endif
            return new CompositeModuleRegistrationLease([.. leases]);
        }
        catch
        {
            for (int i = leases.Count - 1; i >= 0; i--)
                leases[i].Dispose();
            throw;
        }
    }
}
