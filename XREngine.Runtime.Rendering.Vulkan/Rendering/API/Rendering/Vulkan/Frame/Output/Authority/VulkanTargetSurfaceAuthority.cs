using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Restricts target bootstrap to the surface actions it owns. This keeps WSI
/// target policy independent of the renderer's general bootstrap surface.
/// </summary>
internal sealed unsafe class VulkanTargetSurfaceAuthority(
    Vk api,
    VulkanDeviceContext deviceContext,
    VulkanOutputRuntime outputRuntime,
    Silk.NET.Windowing.IWindow? window)
{
    internal void CreateDesktopSurface()
    {
        if (!api.TryGetInstanceExtension<KhrSurface>(deviceContext.Instance, out outputRuntime.SurfaceApi))
            throw new NotSupportedException("KHR_surface extension not found.");

        outputRuntime.Surface = window?.VkSurface?.Create<AllocationCallbacks>(deviceContext.Instance.ToHandle(), null).ToSurface()
            ?? throw new InvalidOperationException("Desktop Vulkan output requires a window surface.");
    }

    internal void DestroyDesktopSurface()
        => DestroySurface();

    internal void CreateHeadlessSurface()
    {
        if (!api.TryGetInstanceExtension<KhrSurface>(deviceContext.Instance, out outputRuntime.SurfaceApi))
            throw new NotSupportedException("VK_KHR_surface entry points are unavailable for headless WSI.");
        if (!api.TryGetInstanceExtension<ExtHeadlessSurface>(deviceContext.Instance, out ExtHeadlessSurface? headlessSurfaceApi))
            throw new NotSupportedException("VK_EXT_headless_surface was enabled but its instance entry points are unavailable.");

        HeadlessSurfaceCreateInfoEXT create = new()
        {
            SType = StructureType.HeadlessSurfaceCreateInfoExt,
        };
        Result result = headlessSurfaceApi.CreateHeadlessSurface(
            deviceContext.Instance,
            in create,
            null,
            out outputRuntime.Surface);
        if (result != Result.Success)
            throw new NotSupportedException($"vkCreateHeadlessSurfaceEXT failed: {result}.");
    }

    internal void DestroyHeadlessSurface()
        => DestroySurface();

    private void DestroySurface()
    {
        if (outputRuntime.Surface.Handle == 0)
            return;

        outputRuntime.SurfaceApi!.DestroySurface(
            deviceContext.Instance,
            outputRuntime.Surface,
            null);
        outputRuntime.Surface = default;
        outputRuntime.SurfaceApi = null;
    }
}
