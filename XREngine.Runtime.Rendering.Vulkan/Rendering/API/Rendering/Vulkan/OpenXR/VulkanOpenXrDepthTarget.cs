using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanOpenXrDepthTarget(
    Image Image,
    DeviceMemory Memory,
    ImageView View,
    Format Format,
    ImageAspectFlags Aspect);
