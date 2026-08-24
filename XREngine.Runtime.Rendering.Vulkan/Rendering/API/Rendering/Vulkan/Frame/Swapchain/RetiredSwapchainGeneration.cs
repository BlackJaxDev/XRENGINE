using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns one superseded desktop swapchain generation until its marker fences complete.
/// </summary>
internal sealed record RetiredSwapchainGeneration(
    SwapchainKHR Swapchain,
    Image[] Images,
    ulong[] ImageLifetimeGenerations,
    ImageView[] ImageViews,
    Framebuffer[] Framebuffers,
    Semaphore[] PresentBridgeSemaphores,
    RenderPass ClearRenderPass,
    RenderPass LoadRenderPass,
    Fence GraphicsMarkerFence,
    Fence PresentMarkerFence,
    bool StreamlineProxy,
    uint Width,
    uint Height,
    long EnqueuedTimestamp);
