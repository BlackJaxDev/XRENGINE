using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Vulkan images and views owned by a presentation-independent frame target.</summary>
internal readonly record struct VulkanRenderFrameTarget(
    Image ColorImage,
    ImageView ColorView,
    Image DepthImage,
    ImageView DepthView,
    Extent2D Extent,
    uint Layers,
    ImageLayout InitialColorLayout,
    ImageLayout RequiredFinalColorLayout,
    ulong TargetGeneration = 0,
    uint FrameSlotIndex = 0);
