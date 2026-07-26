using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Represents a Vulkan streamline image along with its associated resources and metadata.
    /// </summary>
    internal readonly record struct VulkanStreamlineImage(
        Image Image,
        DeviceMemory Memory,
        ImageView View,
        ImageLayout Layout,
        Format Format,
        ImageUsageFlags Usage,
        ImageAspectFlags Aspect,
        uint Width,
        uint Height,
        IVkFrameBufferAttachmentSource? LayoutTracker);
}
