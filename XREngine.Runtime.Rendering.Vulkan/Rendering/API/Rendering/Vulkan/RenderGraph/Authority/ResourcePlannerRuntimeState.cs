using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal struct ResourcePlannerRuntimeState
{
    public VulkanResourcePlanner ResourcePlanner;
    public VulkanResourceAllocator ResourceAllocator;
    public VulkanBarrierPlanner BarrierPlanner;
    public VulkanCompiledRenderGraph CompiledRenderGraph;
    public VulkanRenderGraphPlan RenderGraphPlan;
    public FrameOpContext? LastActiveFrameOpContext;
    public ulong ResourcePlannerSignature;
    public ulong ResourceAllocationSignature;
    public ulong FailedResourcePlannerSignature;
    public ulong FailedResourceAllocationSignature;
    public long FailedResourceAllocationTimestamp;
    public ResourcePlannerFastPathKey ResourcePlannerFastPathKey;
    public bool HasResourcePlannerFastPathKey;
    public BarrierPlanFastPathKey BarrierPlanFastPathKey;
    public bool HasBarrierPlanFastPathKey;
    public ResourcePlannerSignatureBreakdown ResourcePlannerSignatureBreakdown;
    public ulong ResourcePlannerRevision;
    public long AllocatorOwnershipId;
    public FrameOpResourcePlannerSwitchingState? FrameOpResourcePlannerSwitchingState;
    public VulkanPreparedResourceGenerationManifest? PreparedGenerationManifest;

    /// <summary>
    /// Gets whether this snapshot still owns the allocator generation captured
    /// when the planner state was published. Retirement is shared by every
    /// shallow copy of the state, so it is the authoritative publication
    /// tombstone after a resource-generation replacement.
    /// </summary>
    public readonly bool HasLiveAllocatorOwnership
        => ResourceAllocator is not null &&
           !ResourceAllocator.IsRetired &&
           ResourceAllocator.OwnershipId == AllocatorOwnershipId;

    public static ResourcePlannerRuntimeState CreateEmpty()
    {
        VulkanResourceAllocator allocator = new();
        return new()
        {
            ResourcePlanner = new VulkanResourcePlanner(),
            ResourceAllocator = allocator,
            BarrierPlanner = new VulkanBarrierPlanner(),
            CompiledRenderGraph = VulkanCompiledRenderGraph.Empty,
            RenderGraphPlan = VulkanRenderGraphPlan.Empty,
            ResourcePlannerSignature = ulong.MaxValue,
            ResourceAllocationSignature = ulong.MaxValue,
            FailedResourcePlannerSignature = ulong.MaxValue,
            FailedResourceAllocationSignature = ulong.MaxValue,
            AllocatorOwnershipId = allocator.OwnershipId,
            FrameOpResourcePlannerSwitchingState = new FrameOpResourcePlannerSwitchingState(),
        };
    }
}
