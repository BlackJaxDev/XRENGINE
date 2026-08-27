using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VkImageDescriptorSnapshot(
    Image Image,
    DeviceMemory Memory,
    ImageView View,
    ImageViewType ViewType,
    Sampler Sampler,
    Format Format,
    ImageAspectFlags Aspect,
    ImageUsageFlags Usage,
    SampleCountFlags Samples,
    uint MipLevels,
    uint ArrayLayers,
    ulong Generation,
    ImageLayout TrackedLayout,
    bool UsesAllocatorImage,
    bool IsReady,
    long StreamingGeneration = 0L);
