using System.Threading;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Owns the mutable compiler/planner workspaces and publishes immutable graph
/// generations. The renderer remains a facade over this authority.
/// </summary>
internal sealed class VulkanRenderGraphRuntime
{
    private const int MaxInteractiveResizeExtentSnapshots = 32;
    private ulong _publishedBarrierGeneration;
    private long _frameContextId;
    private int _frozenPlanReaders;

    public FrameOpContext? LastActiveFrameOpContext { get; set; }
    public VulkanInteractiveResizePlannerExtentCache InteractiveResizeExtentCache { get; } =
        new(MaxInteractiveResizeExtentSnapshots);
    public ulong FailedPlannerSignature { get; set; } = ulong.MaxValue;
    public ulong FailedAllocationSignature { get; set; } = ulong.MaxValue;
    public long FailedAllocationTimestamp { get; set; }
    public ulong FrozenResourcePlanRevision { get; private set; }
    public Dictionary<string, XRDataBuffer> TrackedBuffersByName { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public object PlannerReadbackGate { get; } = new();

    public VulkanRenderGraphCompiler Compiler { get; } = new();
    public VulkanFrameOperationScheduler FrameScheduler { get; } = new();
    public VulkanResourcePlanner ResourcePlanner { get; set; } = new();
    public VulkanResourceAllocator ResourceAllocator { get; set; } = new();
    public VulkanBarrierPlanner BarrierPlanner { get; set; } = new();
    public VulkanCompiledRenderGraph CompiledGraph { get; set; } =
        VulkanCompiledRenderGraph.Empty;
    public ulong PlannerSignature { get; set; } = ulong.MaxValue;
    public ulong AllocationSignature { get; set; } = ulong.MaxValue;
    public ulong Revision { get; set; }
    public VulkanRenderGraphPlan CurrentPlan { get; private set; } = VulkanRenderGraphPlan.Empty;
    public ulong NextFrameContextId()
        => unchecked((ulong)Interlocked.Increment(ref _frameContextId));

    public bool IsResourcePlanFrozen
        => Volatile.Read(ref _frozenPlanReaders) > 0;

    public void AddFrozenPlanReader(ulong resourcePlanRevision)
    {
        FrozenResourcePlanRevision = resourcePlanRevision;
        Interlocked.Increment(ref _frozenPlanReaders);
    }

    public void RemoveFrozenPlanReader()
    {
        if (Interlocked.Decrement(ref _frozenPlanReaders) == 0)
            FrozenResourcePlanRevision = 0;
    }

    /// <summary>
    /// Publishes copied barrier arrays only when a planner generation is rebuilt;
    /// steady-state recording reads <see cref="CurrentPlan"/> allocation-free.
    /// </summary>
    public void PublishPlan()
    {
        ulong barrierGeneration = unchecked(++_publishedBarrierGeneration);
        CurrentPlan = new VulkanRenderGraphPlan(
            Revision,
            CompiledGraph,
            VulkanBarrierPlan.Capture(barrierGeneration, BarrierPlanner));
    }

    public void ReleaseCaches()
    {
        Compiler.ReleaseCaches();
        FrameScheduler.ReleaseCaches();
    }
}
