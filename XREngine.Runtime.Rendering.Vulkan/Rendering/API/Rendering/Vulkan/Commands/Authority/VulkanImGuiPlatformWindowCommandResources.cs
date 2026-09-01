using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanImGuiPlatformWindowCommandResources(
    CommandPool CommandPool,
    CommandBuffer[] CommandBuffers,
    Fence[] Fences,
    bool[] FrameFenceSubmitted,
    Fence[] AcquireFences,
    bool[] AcquireFenceSubmitted,
    Silk.NET.Vulkan.Semaphore[] ImageAvailableSemaphores,
    Silk.NET.Vulkan.Semaphore[] RenderFinishedSemaphores);
