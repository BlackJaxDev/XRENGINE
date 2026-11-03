using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Engine-owned desktop color targets, one for each desktop frame slot. Worker
/// recording writes the desktop scene into the slot image instead of the acquired
/// swapchain image, so the recording does not depend on the acquired image index.
/// A copy pass on the render thread then copies the slot image into the acquired
/// image. The bundle belongs to one desktop WSI generation and retires with it.
/// </summary>
internal sealed class VulkanDesktopOffscreenColorResources(
    Image[] images,
    DeviceMemory[] memories,
    ImageView[] views,
    Format format,
    Extent2D extent,
    ulong outputGeneration)
{
    public Image[] Images { get; } = images;
    public DeviceMemory[] Memories { get; } = memories;
    public ImageView[] Views { get; } = views;
    public Format Format { get; } = format;
    public Extent2D Extent { get; } = extent;

    /// <summary>The desktop output generation that created this bundle.</summary>
    public ulong OutputGeneration { get; } = outputGeneration;

    public int SlotCount => Images.Length;
}
