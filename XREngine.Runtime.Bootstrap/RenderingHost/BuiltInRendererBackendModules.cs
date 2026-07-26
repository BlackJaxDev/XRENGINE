using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

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

        IDisposable? openGlLease = null;
        try
        {
            openGlLease = OpenGlRendererBackendModule.Register(catalog);
            IDisposable vulkanLease = VulkanRendererBackendModule.Register(catalog);
            return new RegistrationLease(openGlLease, vulkanLease);
        }
        catch
        {
            openGlLease?.Dispose();
            throw;
        }
    }

    public static RendererBackendRegistration CreateOpenGlRegistration(Version? version = null)
        => OpenGlRendererBackendModule.CreateRegistration(version);

    public static RendererBackendRegistration CreateVulkanRegistration(Version? version = null)
        => VulkanRendererBackendModule.CreateRegistration(version);

    private sealed class RegistrationLease(IDisposable first, IDisposable second) : IDisposable
    {
        private IDisposable? _first = first;
        private IDisposable? _second = second;

        public void Dispose()
        {
            Interlocked.Exchange(ref _second, null)?.Dispose();
            Interlocked.Exchange(ref _first, null)?.Dispose();
        }
    }
}
