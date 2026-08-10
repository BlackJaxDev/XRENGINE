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

/// <summary>
/// Immutable publication envelope for one coherent resource-planner runtime generation.
/// </summary>
internal sealed class ResourcePlannerRuntimeGeneration
{
    private readonly ResourcePlannerRuntimeState _state;
    private readonly FrameOpContext _activeFrameOpContext;

    public ResourcePlannerRuntimeGeneration(ResourcePlannerRuntimeState state)
    {
        _state = state;
        if (state.LastActiveFrameOpContext is not { } context)
            return;

        _activeFrameOpContext = context;
        HasActiveFrameOpContext = true;
        DescriptorViewFamilyIdentity = HashCode.Combine(
            (int)context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity);
    }

    public ref readonly ResourcePlannerRuntimeState State => ref _state;
    public bool HasActiveFrameOpContext { get; }
    public ref readonly FrameOpContext ActiveFrameOpContext
        => ref _activeFrameOpContext;

    /// <summary>
    /// Stable descriptor-family identity derived once when this planner generation is published.
    /// Keeping this scalar in the immutable envelope avoids copying the large
    /// <see cref="FrameOpContext"/> value for every mesh draw.
    /// </summary>
    public int DescriptorViewFamilyIdentity { get; }
}
