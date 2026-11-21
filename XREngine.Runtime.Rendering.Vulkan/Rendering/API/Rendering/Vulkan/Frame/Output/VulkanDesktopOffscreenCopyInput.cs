using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen input for one desktop offscreen copy.</summary>
internal readonly record struct VulkanDesktopOffscreenCopyInput(
    CommandBuffer CopyCommandBuffer,
    CommandBuffer SceneCommandBuffer,
    Image OffscreenImage,
    ImageLayout OffscreenLayoutAfterScene,
    Image SwapchainImage,
    Extent2D Extent,
    ImageLayout SwapchainFinalLayout);
