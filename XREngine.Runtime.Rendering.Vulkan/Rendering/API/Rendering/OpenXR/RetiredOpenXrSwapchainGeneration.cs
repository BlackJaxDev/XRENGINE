using Silk.NET.OpenXR;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures superseded OpenXR swapchains and their native Vulkan image views/structures
/// until GPU timeline completion and OpenXR runtime release are both satisfied.
/// </summary>
internal sealed unsafe record RetiredOpenXrSwapchainGeneration(
    Swapchain[] Swapchains,
    SwapchainImageVulkan2KHR*[] SwapchainImagesVK,
    uint[] SwapchainImageCounts,
    uint ViewCount,
    ulong TombstoneTimelineValue,
    Semaphore TimelineSemaphore,
    long EnqueuedTimestamp);
