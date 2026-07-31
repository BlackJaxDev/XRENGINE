using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Preallocated resources for one presentationless frame slot.</summary>
internal readonly record struct VulkanPresentationlessFrameSlot(
    CommandPool CommandPool,
    CommandBuffer CommandBuffer,
    Fence Fence,
    QueryPool TimestampQueryPool,
    Image ColorImage,
    ImageView ColorView,
    VulkanMemoryAllocation ColorAllocation,
    Image DepthImage,
    ImageView DepthView,
    VulkanMemoryAllocation DepthAllocation,
    Buffer ReadbackBuffer,
    VulkanMemoryAllocation ReadbackAllocation,
    ulong ReadbackByteCount);
