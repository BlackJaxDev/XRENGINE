namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the mutable compiler/planner workspaces and publishes immutable graph
/// generations. The renderer remains a facade over this authority.
/// </summary>
internal sealed class VulkanRenderGraphRuntime
{
    private ulong _publishedBarrierGeneration;

    public VulkanRenderer.VulkanRenderGraphCompiler Compiler { get; } = new();
    public VulkanResourcePlanner ResourcePlanner { get; set; } = new();
    public VulkanResourceAllocator ResourceAllocator { get; set; } = new();
    public VulkanBarrierPlanner BarrierPlanner { get; set; } = new();
    public VulkanRenderer.VulkanCompiledRenderGraph CompiledGraph { get; set; } =
        VulkanRenderer.VulkanCompiledRenderGraph.Empty;
    public ulong PlannerSignature { get; set; } = ulong.MaxValue;
    public ulong AllocationSignature { get; set; } = ulong.MaxValue;
    public ulong Revision { get; set; }
    public VulkanRenderGraphPlan CurrentPlan { get; private set; } = VulkanRenderGraphPlan.Empty;

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
        => Compiler.ReleaseCaches();
}
