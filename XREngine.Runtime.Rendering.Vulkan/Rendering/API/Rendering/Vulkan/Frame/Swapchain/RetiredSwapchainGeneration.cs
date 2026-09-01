using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns a superseded generation through graphics and presentation-engine completion.
/// </summary>
internal sealed record RetiredSwapchainGeneration(
    SwapchainKHR Swapchain,
    Image[] Images,
    VulkanResourceSlotHandle[] ImageLifetimeSlots,
    ImageView[] ImageViews,
    Framebuffer[] Framebuffers,
    Semaphore[] PresentBridgeSemaphores,
    RenderPass ClearRenderPass,
    RenderPass LoadRenderPass,
    Fence GraphicsMarkerFence,
    VulkanWsiPresentCompletion? PresentCompletion,
    bool StreamlineProxy,
    uint Width,
    uint Height,
    long EnqueuedTimestamp);
