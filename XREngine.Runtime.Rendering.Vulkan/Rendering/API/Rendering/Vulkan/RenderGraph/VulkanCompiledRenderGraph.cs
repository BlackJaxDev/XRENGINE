using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Immutable result of render graph compilation used during command recording.
/// </summary>
internal sealed class VulkanCompiledRenderGraph
{
    /// <summary>
    /// Canonical empty graph instance for frames/pipelines with no pass metadata.
    /// </summary>
    public static VulkanCompiledRenderGraph Empty { get; } = new(
        Array.Empty<RenderPassMetadata>(),
        new Dictionary<int, int>(),
        Array.Empty<VulkanCompiledPassBatch>(),
        RenderGraphSynchronizationInfo.Empty,
        int.MaxValue);

    /// <summary>
    /// Initializes a compiled graph snapshot.
    /// </summary>
    internal VulkanCompiledRenderGraph(
        IReadOnlyList<RenderPassMetadata> orderedPasses,
        IReadOnlyDictionary<int, int> passOrder,
        IReadOnlyList<VulkanCompiledPassBatch> batches,
        RenderGraphSynchronizationInfo synchronization,
        int screenSpaceUiPassOrder = int.MaxValue)
    {
        OrderedPasses = orderedPasses;
        PassOrder = passOrder;
        Batches = batches;
        Synchronization = synchronization;
        ScreenSpaceUiPassOrder = screenSpaceUiPassOrder;
        Plan = new VulkanCompiledRenderGraphPlan(orderedPasses, batches, synchronization);
    }

    /// <summary>Cold compiler/diagnostic metadata; recording must use the plan execution snapshot.</summary>
    public IReadOnlyList<RenderPassMetadata> OrderedPasses { get; }

    /// <summary>Cold compiler lookup. Hot scheduling uses the flat execution pass array.</summary>
    public IReadOnlyDictionary<int, int> PassOrder { get; }

    /// <summary>Precomputed topological order for nested screen-space UI, or int.MaxValue when absent.</summary>
    public int ScreenSpaceUiPassOrder { get; }

    /// <summary>Cold compiler batch metadata.</summary>
    public IReadOnlyList<VulkanCompiledPassBatch> Batches { get; }

    /// <summary>Cold synchronization source used to construct frozen barriers.</summary>
    public RenderGraphSynchronizationInfo Synchronization { get; }

    /// <summary>Immutable structural plan generation used for cache and recording identity.</summary>
    public VulkanCompiledRenderGraphPlan Plan { get; }
}
