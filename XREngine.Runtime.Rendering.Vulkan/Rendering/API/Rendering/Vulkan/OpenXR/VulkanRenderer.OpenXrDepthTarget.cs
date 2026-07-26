using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct OpenXrDepthTarget(
        Image Image,
        DeviceMemory Memory,
        ImageView View,
        Format Format,
        ImageAspectFlags Aspect);
}
