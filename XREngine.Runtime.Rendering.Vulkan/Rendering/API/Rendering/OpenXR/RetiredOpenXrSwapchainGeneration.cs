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
    bool RequiresGpuCompletion,
    VulkanRetirementTicket ResourceLifetimeTicket,
    bool HasResourceLifetimeAuthority,
    Image[] LifetimeImages,
    VulkanResourceSlotHandle[] DetachedLifetimeSlots,
    bool ExternalImageLifetimesDetached,
    VulkanOpenXrSwapchainChildRetirementReceipt ChildRetirementReceipt,
    bool RuntimeImagesReleased,
    long EnqueuedTimestamp,
    long RetirementGenerationId)
{
    // Destruction is independently retryable per view. A failed xrDestroySwapchain
    // leaves both the native handle and its image-array owner reachable.
    public bool[] DestroyedSwapchains { get; } = new bool[Swapchains.Length];

    // Access is serialized by VulkanXrGraphicsBinding._retiredSwapchainsGate.
    // Keep diagnostics per retirement generation so a stuck generation is visible
    // without producing a log entry on every render-thread retirement poll.
    public long LastBlockerDiagnosticTimestamp { get; set; }
}
