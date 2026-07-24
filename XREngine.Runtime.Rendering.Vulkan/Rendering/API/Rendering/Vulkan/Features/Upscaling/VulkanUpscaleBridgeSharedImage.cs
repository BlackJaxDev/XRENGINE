using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe class VulkanUpscaleBridgeSharedImage(
    string name,
    EVulkanUpscaleBridgeSurfaceKind kind,
    Image vkImage,
    DeviceMemory vkMemory,
    ImageView vkImageView,
    Format vkFormat,
    ImageAspectFlags aspectMask,
    ImageAspectFlags viewAspectMask,
    ImageUsageFlags usage,
    XRTexture2D texture,
    XRFrameBuffer frameBuffer) : IDisposable
{
    private bool _disposed;

    public string Name { get; } = name;
    public EVulkanUpscaleBridgeSurfaceKind Kind { get; } = kind;
    public Image VulkanImage { get; } = vkImage;
    public DeviceMemory VulkanMemory { get; } = vkMemory;
    public ImageView VulkanImageView { get; } = vkImageView;
    public Format VulkanFormat { get; } = vkFormat;
    public ImageAspectFlags AspectMask { get; } = aspectMask;
    public ImageAspectFlags ViewAspectMask { get; } = viewAspectMask;
    public ImageUsageFlags Usage { get; } = usage;
    public XRTexture2D Texture { get; } = texture;
    public XRFrameBuffer FrameBuffer { get; } = frameBuffer;
    public ImageLayout CurrentLayout { get; set; }

    internal void DestroyVulkanResources(Vk api, Device device)
    {
        if (VulkanImageView.Handle != 0)
            api.DestroyImageView(device, VulkanImageView, null);
        if (VulkanImage.Handle != 0)
            api.DestroyImage(device, VulkanImage, null);
        if (VulkanMemory.Handle != 0)
            api.FreeMemory(device, VulkanMemory, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        FrameBuffer.Destroy(true);
        Texture.Destroy(true);
    }
}
