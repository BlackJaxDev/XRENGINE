using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable, atomically published ownership bundle for the desktop swapchain depth target.
/// </summary>
internal sealed class VulkanSwapchainDepthResources(
    Image image,
    DeviceMemory memory,
    ImageView view,
    Format format,
    ImageAspectFlags aspect,
    Extent2D extent)
{
    public Image Image { get; } = image;
    public DeviceMemory Memory { get; } = memory;
    public ImageView View { get; } = view;
    public Format Format { get; } = format;
    public ImageAspectFlags Aspect { get; } = aspect;
    public Extent2D Extent { get; } = extent;
}
