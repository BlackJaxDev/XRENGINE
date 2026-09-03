using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable receipt published at vkEndCommandBuffer containing the complete set of
/// dependencies, image access range deltas, and queue ownership transfers recorded by a lane.
/// </summary>
internal sealed class VulkanSealedRecordingReceipt
{
    public CommandBuffer CommandBuffer { get; }
    public EVulkanAcceptedFrameLane Lane { get; }
    public int FrameSlot { get; }
    public ulong RecordingGeneration { get; }
    public ReadOnlyMemory<VulkanResourceLifetimeKey> Dependencies { get; }
    public ReadOnlyMemory<VulkanImageAccessRangeDelta> ImageAccessDeltas { get; }
    public ReadOnlyMemory<VulkanQueueOwnershipTransferRequirement> QueueOwnershipTransfers { get; }
    public bool IsSuccess { get; }

    public VulkanSealedRecordingReceipt(
        CommandBuffer commandBuffer,
        EVulkanAcceptedFrameLane lane,
        int frameSlot,
        ulong recordingGeneration,
        ReadOnlyMemory<VulkanResourceLifetimeKey> dependencies,
        ReadOnlyMemory<VulkanImageAccessRangeDelta> imageAccessDeltas,
        ReadOnlyMemory<VulkanQueueOwnershipTransferRequirement> queueOwnershipTransfers,
        bool isSuccess)
    {
        CommandBuffer = commandBuffer;
        Lane = lane;
        FrameSlot = frameSlot;
        RecordingGeneration = recordingGeneration;
        Dependencies = dependencies;
        ImageAccessDeltas = imageAccessDeltas;
        QueueOwnershipTransfers = queueOwnershipTransfers;
        IsSuccess = isSuccess;
    }
}
