using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Global planner identity captured around prepared command-chain encoding.
/// Command-buffer-local tracking is intentionally excluded.
/// </summary>
internal readonly record struct VulkanPreparedWorkerPlannerStamp(
    VulkanResourcePlanner ResourcePlanner,
    VulkanResourceAllocator ResourceAllocator,
    VulkanBarrierPlanner BarrierPlanner,
    VulkanCompiledRenderGraph CompiledRenderGraph,
    ulong PlannerSignature,
    ulong AllocationSignature,
    ulong PlannerRevision,
    ulong FailedPlannerSignature,
    ulong FailedAllocationSignature,
    long FailedAllocationTimestamp,
    bool HasPlannerFastPathKey,
    bool HasBarrierPlanFastPathKey,
    FrameOpContext? LastActiveFrameOpContext);
