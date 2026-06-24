using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private KhrSurface? khrSurface;
    private SurfaceKHR surface;

    private void DestroySurface()
        => khrSurface!.DestroySurface(instance, surface, null);

    private void CreateSurface()
    {
        if (!Api!.TryGetInstanceExtension<KhrSurface>(instance, out khrSurface))
            throw new NotSupportedException("KHR_surface extension not found.");
        
        surface = Window!.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
    }
}