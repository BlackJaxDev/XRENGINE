using Silk.NET.Vulkan;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe class VulkanUpscaleBridgeSharedImage(
    ulong allocationGeneration,
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
    private int _vulkanResourcesDestroyed;

    /// <summary>Identifies the sidecar-device allocation transaction that owns this image.</summary>
    public ulong AllocationGeneration { get; } = allocationGeneration;
    /// <summary>Identifies the sidecar-device publication transaction that made this image visible.</summary>
    public ulong PublicationGeneration { get; private set; }
    public string Name { get; } = name;
    public EVulkanUpscaleBridgeSurfaceKind Kind { get; } = kind;
    public Image VulkanImage { get; } = vkImage;
    public DeviceMemory VulkanMemory { get; } = vkMemory;
    public ImageView VulkanImageView { get; } = vkImageView;
    public ulong VulkanImageAllocationGeneration => AllocationGeneration;
    public ulong VulkanMemoryAllocationGeneration => AllocationGeneration;
    public ulong VulkanImageViewAllocationGeneration => AllocationGeneration;
    public ulong VulkanImagePublicationGeneration => PublicationGeneration;
    public ulong VulkanMemoryPublicationGeneration => PublicationGeneration;
    public ulong VulkanImageViewPublicationGeneration => PublicationGeneration;
    public Format VulkanFormat { get; } = vkFormat;
    public ImageAspectFlags AspectMask { get; } = aspectMask;
    public ImageAspectFlags ViewAspectMask { get; } = viewAspectMask;
    public ImageUsageFlags Usage { get; } = usage;
    public XRTexture2D Texture { get; } = texture;
    public XRFrameBuffer FrameBuffer { get; } = frameBuffer;
    public ImageLayout CurrentLayout { get; set; }

    internal void Publish(ulong publicationGeneration)
    {
        if (publicationGeneration == 0 || publicationGeneration != AllocationGeneration)
            throw new InvalidOperationException("A bridge image can only be published by its owning sidecar allocation generation.");
        PublicationGeneration = publicationGeneration;
    }

    internal void DestroyVulkanResources(Vk api, Device device)
    {
        if (Interlocked.Exchange(ref _vulkanResourcesDestroyed, 1) != 0)
            return;

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
