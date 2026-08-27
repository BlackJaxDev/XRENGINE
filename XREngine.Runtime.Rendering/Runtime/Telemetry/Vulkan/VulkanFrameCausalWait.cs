using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free causal payload for a frame wait whose duration exceeded the
/// detailed-capture threshold.
/// </summary>
public readonly record struct VulkanFrameCausalWait(
    EVulkanFrameWaitReason Reason,
    TimeSpan Elapsed,
    ulong FrameId,
    int FrameSlot,
    int ImageIndex,
    ulong SemaphoreTargetValue,
    ulong SemaphoreCompletedValue,
    uint QueueFamily,
    int PendingCommandCount,
    int ConcurrentWorkerActivity,
    EVulkanFrameStage Stage = EVulkanFrameStage.Count)
{
    public bool IsValid
        => Reason != EVulkanFrameWaitReason.None && Elapsed > TimeSpan.Zero;
}
