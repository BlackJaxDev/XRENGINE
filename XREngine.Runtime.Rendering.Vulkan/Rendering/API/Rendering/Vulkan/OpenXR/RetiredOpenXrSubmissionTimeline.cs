using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures frame-owned resources retained until an OpenXR graphics-timeline
/// submission completes.
/// </summary>
internal sealed record RetiredOpenXrSubmissionTimeline(
    Semaphore TimelineSemaphore,
    ulong TimelineValue,
    bool CompletionProven,
    VulkanMappedFrameArena? Arena,
    ulong Generation,
    uint[] FrameSlots,
    int FrameSlotCount,
    VulkanFrameDataArena? FrameDataArena,
    ulong FrameDataGeneration);
