namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanResourceLifetimeSnapshot(
        int LiveResourceCount,
        int RecordedResourceCount,
        int SubmittedResourceCount,
        int CompletedResourceCount,
        int ExternalResourceCount,
        int PendingRetirementCount,
        int DestroyedResourceCount,
        int TrackedCommandBufferCount,
        int TrackedDescriptorSetCount,
        int InFlightSubmissionCount,
        ulong LastGraphicsSequence,
        ulong CompletedGraphicsSequence,
        ulong LastTransferSequence,
        ulong CompletedTransferSequence,
        ulong LastOtherSequence,
        ulong CompletedOtherSequence,
        long OldestPendingRetirementAgeMilliseconds,
        ulong OldestPendingRetirementGenerationAge,
        long ForcedDestructionCount,
        bool DeviceLost);
}
