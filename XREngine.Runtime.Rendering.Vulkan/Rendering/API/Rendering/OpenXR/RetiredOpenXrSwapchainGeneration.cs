using Silk.NET.Vulkan;
using XREngine.Rendering.API.Rendering.OpenXR;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using SwapchainImageVulkan2KHR = Silk.NET.OpenXR.SwapchainImageVulkan2KHR;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures superseded OpenXR swapchains and their native Vulkan image views/structures
/// until GPU timeline completion and OpenXR runtime release are both satisfied.
/// </summary>
internal sealed unsafe record RetiredOpenXrSwapchainGeneration(
    ulong[] Swapchains,
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
    bool InitialExternalImageLifetimesDetached,
    VulkanOpenXrSwapchainChildRetirementReceipt InitialChildRetirementReceipt,
    bool InitialRuntimeImagesReleased,
    long EnqueuedTimestamp,
    long RetirementGenerationId,
    OpenXrRetirementToken RetirementToken)
{
    public bool ExternalImageLifetimesDetached { get; set; } = InitialExternalImageLifetimesDetached;
    public VulkanOpenXrSwapchainChildRetirementReceipt ChildRetirementReceipt { get; set; } = InitialChildRetirementReceipt;
    public bool RuntimeImagesReleased { get; set; } = InitialRuntimeImagesReleased;
    public string? PermanentRecoveryFailureReason { get; set; }

    // Destruction is independently retryable per view. A failed xrDestroySwapchain
    // leaves both the native handle and its image-array owner reachable.
    public bool[] DestroyedSwapchains { get; } = new bool[Swapchains.Length];

    // Access is serialized by VulkanXrGraphicsBinding._retiredSwapchainsGate.
    // Keep diagnostics per retirement generation so a stuck generation is visible
    // without producing a log entry on every render-thread retirement poll.
    public long LastBlockerDiagnosticTimestamp { get; set; }
    public EOpenXrSwapchainRetirementBlockers LastObservedBlockers { get; set; }
}
