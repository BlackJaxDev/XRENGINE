using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Freezes one Vulkan deformation output for a queued draw and retains its producer receipt.
/// </summary>
internal readonly record struct DeformationOutputWitness(
    SkinningPrepassDispatcher.VulkanOutputReceipt Receipt,
    AbstractRenderer BackendOwner,
    ulong AttemptId,
    ulong OutputVersion,
    int PoseHash,
    bool DoesSkinning,
    bool DoesBlendshapes,
    bool UsesPrecombinedBlendshapes,
    SkinningPrepassDispatcher.OutputAllocationIdentity AllocationIdentity)
{
    internal bool IsValid => Receipt is not null && BackendOwner is not null && AttemptId != 0UL;
}
