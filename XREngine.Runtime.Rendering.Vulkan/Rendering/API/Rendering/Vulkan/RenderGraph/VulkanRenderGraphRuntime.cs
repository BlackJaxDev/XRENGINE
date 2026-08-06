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

    public VulkanInteractiveResizePlannerExtentCache InteractiveResizeExtentCache { get; } =
        new(MaxInteractiveResizeExtentSnapshots);
    public ulong FrozenResourcePlanRevision { get; private set; }
    public Dictionary<string, XRDataBuffer> TrackedBuffersByName { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public object PlannerReadbackGate { get; } = new();

    public VulkanRenderGraphCompiler Compiler { get; } = new();
    public VulkanFrameOperationScheduler FrameScheduler { get; } = new();
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
    public void PublishPlan(
        ulong revision,
        VulkanCompiledRenderGraph compiledGraph,
        VulkanBarrierPlanner barrierPlanner)
    {
        ulong barrierGeneration = unchecked(++_publishedBarrierGeneration);
        CurrentPlan = new VulkanRenderGraphPlan(
            revision,
            compiledGraph,
            VulkanBarrierPlan.Capture(barrierGeneration, barrierPlanner));
    }

    public void ReleaseCaches()
    {
        Compiler.ReleaseCaches();
        FrameScheduler.ReleaseCaches();
    }
}
