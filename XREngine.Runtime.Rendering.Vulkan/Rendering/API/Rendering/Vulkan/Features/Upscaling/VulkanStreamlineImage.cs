using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a Vulkan streamline image along with its associated resources and metadata.
/// <c>Owner</c> is the engine texture of the image, or null for an image without one (a
/// swapchain image). The frame operation graph uses it as the resource identity, so a DLSS
/// read or write meets the same identity as the framebuffer write of that texture.
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
    IVkFrameBufferAttachmentSource? LayoutTracker,
    XRTexture? Owner = null);
