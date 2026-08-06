using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal KhrSurface RequireSurfaceApi()
        => _outputRuntime.SurfaceApi
            ?? throw new InvalidOperationException("The Vulkan target does not own a surface.");

    internal SurfaceKHR TargetSurface => _outputRuntime.Surface;

}
