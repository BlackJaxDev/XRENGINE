using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>Preallocated resources for one headless-WSI frame slot.</summary>
internal readonly record struct VulkanHeadlessWsiFrameSlot(
    CommandPool CommandPool,
    CommandBuffer CommandBuffer,
    Fence Fence,
    Semaphore ImageAvailable,
    Image DepthImage,
    ImageView DepthView,
    VulkanMemoryAllocation DepthAllocation);
