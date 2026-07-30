namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Immutable, versioned publication consumed at command-recording boundaries.
/// Structural graph data and the barrier generation are captured together.
/// </summary>
internal sealed class VulkanRenderGraphPlan
{
    public static VulkanRenderGraphPlan Empty { get; } = new(
        0,
        VulkanCompiledRenderGraph.Empty,
        VulkanBarrierPlan.Empty);

    public VulkanRenderGraphPlan(
        ulong revision,
        VulkanCompiledRenderGraph compiledGraph,
        VulkanBarrierPlan barriers)
    {
        Revision = revision;
        CompiledGraph = compiledGraph;
        Barriers = barriers;
    }

    public ulong Revision { get; }
    public ulong StructuralGeneration => CompiledGraph.Plan.Generation;
    public ulong CompatibilityIdentity => CompiledGraph.Plan.CompatibilityIdentity;
    public VulkanCompiledRenderGraph CompiledGraph { get; }
    public VulkanBarrierPlan Barriers { get; }
}
