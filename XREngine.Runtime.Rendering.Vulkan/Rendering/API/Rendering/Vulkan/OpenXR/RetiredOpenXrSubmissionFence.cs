using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures the frame-owned resources retained until an OpenXR submission fence completes.
/// </summary>
internal sealed record RetiredOpenXrSubmissionFence(
    Fence Fence,
    VulkanMappedFrameArena? Arena,
    ulong Generation,
    uint[] FrameSlots,
    int FrameSlotCount);
