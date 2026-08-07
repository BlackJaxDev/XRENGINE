using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal struct ResourcePlannerRuntimeState
{
    public VulkanResourcePlanner ResourcePlanner;
    public VulkanResourceAllocator ResourceAllocator;
    public VulkanBarrierPlanner BarrierPlanner;
    public VulkanCompiledRenderGraph CompiledRenderGraph;
    public FrameOpContext? LastActiveFrameOpContext;
    public ulong ResourcePlannerSignature;
    public ulong ResourceAllocationSignature;
    public ulong FailedResourcePlannerSignature;
    public ulong FailedResourceAllocationSignature;
    public long FailedResourceAllocationTimestamp;
    public VulkanRenderer.ResourcePlannerFastPathKey ResourcePlannerFastPathKey;
    public bool HasResourcePlannerFastPathKey;
    public VulkanRenderer.BarrierPlanFastPathKey BarrierPlanFastPathKey;
    public bool HasBarrierPlanFastPathKey;
    public VulkanRenderer.ResourcePlannerSignatureBreakdown ResourcePlannerSignatureBreakdown;
    public ulong ResourcePlannerRevision;
    public long AllocatorOwnershipId;
    public VulkanRenderer.FrameOpResourcePlannerSwitchingState? FrameOpResourcePlannerSwitchingState;
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
            ResourcePlannerSignature = ulong.MaxValue,
            ResourceAllocationSignature = ulong.MaxValue,
            FailedResourcePlannerSignature = ulong.MaxValue,
            FailedResourceAllocationSignature = ulong.MaxValue,
            AllocatorOwnershipId = allocator.OwnershipId,
            FrameOpResourcePlannerSwitchingState = new VulkanRenderer.FrameOpResourcePlannerSwitchingState(),
        };
    }
}

/// <summary>
/// Immutable publication envelope for one coherent resource-planner runtime generation.
/// </summary>
internal sealed class ResourcePlannerRuntimeGeneration(ResourcePlannerRuntimeState state)
{
    public ResourcePlannerRuntimeState State { get; } = state;
}