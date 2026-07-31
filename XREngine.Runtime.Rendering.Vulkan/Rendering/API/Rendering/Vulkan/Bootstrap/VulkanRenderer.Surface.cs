using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private KhrSurface? khrSurface;
    private SurfaceKHR surface;

    internal void CreateDesktopSurface()
        => CreateSurface();

    internal void DestroyDesktopSurface()
        => DestroySurface();

    internal void CreateHeadlessSurface()
    {
        if (!Api!.TryGetInstanceExtension<KhrSurface>(instance, out khrSurface))
            throw new NotSupportedException("VK_KHR_surface entry points are unavailable for headless WSI.");
        if (!Api.TryGetInstanceExtension<ExtHeadlessSurface>(instance, out ExtHeadlessSurface? headlessSurfaceApi))
            throw new NotSupportedException("VK_EXT_headless_surface was enabled but its instance entry points are unavailable.");

        HeadlessSurfaceCreateInfoEXT create = new()
        {
            SType = StructureType.HeadlessSurfaceCreateInfoExt,
        };
        Result result = headlessSurfaceApi.CreateHeadlessSurface(instance, in create, null, out surface);
        if (result != Result.Success)
            throw new NotSupportedException($"vkCreateHeadlessSurfaceEXT failed: {result}.");
    }

    internal void DestroyHeadlessSurface()
        => DestroySurface();

    internal KhrSurface RequireSurfaceApi()
        => khrSurface
            ?? throw new InvalidOperationException("The Vulkan target does not own a surface.");

    internal SurfaceKHR TargetSurface => surface;

    private void DestroySurface()
    {
        if (surface.Handle == 0)
            return;
        khrSurface!.DestroySurface(instance, surface, null);
        surface = default;
        khrSurface = null;
    }

    private void CreateSurface()
    {
        if (!Api!.TryGetInstanceExtension<KhrSurface>(instance, out khrSurface))
            throw new NotSupportedException("KHR_surface extension not found.");
        
        surface = Window!.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
    }
}
