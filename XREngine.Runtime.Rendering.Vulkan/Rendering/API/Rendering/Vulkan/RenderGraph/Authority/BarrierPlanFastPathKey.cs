namespace XREngine.Rendering.Vulkan;

internal readonly record struct BarrierPlanFastPathKey(
    VulkanCompiledRenderGraph CompiledGraph,
    ulong ResourcePlannerSignature,
    ulong ResourceAllocationSignature,
    VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership)
{
    public bool Matches(in BarrierPlanFastPathKey other)
        => ReferenceEquals(CompiledGraph, other.CompiledGraph) &&
           ResourcePlannerSignature == other.ResourcePlannerSignature &&
           ResourceAllocationSignature == other.ResourceAllocationSignature &&
           QueueOwnership.Equals(other.QueueOwnership);
}
