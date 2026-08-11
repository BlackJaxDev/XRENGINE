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
