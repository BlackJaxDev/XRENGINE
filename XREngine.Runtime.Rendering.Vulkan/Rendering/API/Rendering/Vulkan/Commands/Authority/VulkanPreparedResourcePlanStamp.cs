using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable identity of the resource-plan generation that prepared a primary
/// command recording. Command execution uses this only for validation; plan
/// replacement remains a frame-loop responsibility.
/// </summary>
internal readonly record struct VulkanPreparedResourcePlanStamp(
    VulkanFramePlanningSnapshot PlanningSnapshot,
    ulong ResourcePlannerRevision,
    ulong ResourcePlannerSignature,
    ulong ResourceAllocationSignature)
{
    internal bool Matches(in VulkanPreparedResourcePlanStamp other)
        => PlanningSnapshot.RenderGraphPlan.Revision == other.PlanningSnapshot.RenderGraphPlan.Revision &&
           ResourcePlannerRevision == other.ResourcePlannerRevision &&
           ResourcePlannerSignature == other.ResourcePlannerSignature &&
           ResourceAllocationSignature == other.ResourceAllocationSignature;
}
