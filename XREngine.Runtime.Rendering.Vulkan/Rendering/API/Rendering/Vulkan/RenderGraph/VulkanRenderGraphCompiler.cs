using System.Collections.Concurrent;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Compiles immutable Vulkan render-graph structures and owns the metadata compilation cache.
/// </summary>
internal sealed class VulkanRenderGraphCompiler
{
    private const string ScreenSpaceUiPassName = "VPRC_RenderScreenSpaceUI";
    private const int MaxMetadataCacheEntries = 64;
    private sealed class CompiledGraphCacheEntry(IReadOnlyCollection<RenderPassMetadata> metadata)
    {
        public VulkanCompiledRenderGraph Graph { get; } = BuildCompiledGraph(metadata);
    }

    // Do not use ConditionalWeakTable here. Its runtime dependent handles let stable
    // render-graph metadata keys retain collectible-generation cache values after the
    // owning compiler becomes unreachable. A bounded, generation-owned dictionary is
    // released atomically with the renderer and therefore unloads without ephemerons.
    private readonly ConcurrentDictionary<IReadOnlyCollection<RenderPassMetadata>, CompiledGraphCacheEntry>
        _compiledGraphCache = new(ReferenceEqualityComparer.Instance);

    internal void ReleaseCaches()
        => _compiledGraphCache.Clear();

    /// <summary>
    /// Compiles the high-level pass metadata into:
    /// 1) topological pass order,
    /// 2) compatible graphics pass batches,
    /// 3) synchronization plan.
    /// </summary>
    /// <param name="passMetadata">Per-pass metadata emitted by render graph construction.</param>
    /// <returns>A compiled graph snapshot consumed by frame op sorting and barrier emission.</returns>
    public VulkanCompiledRenderGraph Compile(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        // No metadata means there is no ordering/batching/synchronization work to perform.
        if (passMetadata is null || passMetadata.Count == 0)
            return VulkanCompiledRenderGraph.Empty;

        TrimMetadataCachesIfRequired();
        return _compiledGraphCache.GetOrAdd(
            passMetadata,
            static metadata => new CompiledGraphCacheEntry(metadata)).Graph;
    }

    private void TrimMetadataCachesIfRequired()
    {
        if (_compiledGraphCache.Count < MaxMetadataCacheEntries)
            return;

        _compiledGraphCache.Clear();
    }

    private static VulkanCompiledRenderGraph BuildCompiledGraph(IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        // Topological order ensures producers are recorded before their consumers.
        IReadOnlyList<RenderPassMetadata> orderedPasses = RenderGraphSynchronizationPlanner.TopologicallySort(passMetadata);

        // Build explicit synchronization requirements from the same metadata source.
        RenderGraphSynchronizationInfo synchronization = RenderGraphSynchronizationPlanner.Build(passMetadata);

        // passIndex -> topological order index retained for cold diagnostics.
        Dictionary<int, int> passOrder = new(orderedPasses.Count);
        int screenSpaceUiPassOrder = int.MaxValue;

        // Graphics passes with compatible attachment signatures are merged into batches.
        List<VulkanCompiledPassBatch> batches = [];

        for (int i = 0; i < orderedPasses.Count; i++)
        {
            RenderPassMetadata pass = orderedPasses[i];
            passOrder[pass.PassIndex] = i;
            if (screenSpaceUiPassOrder == int.MaxValue &&
                string.Equals(pass.Name, ScreenSpaceUiPassName, StringComparison.OrdinalIgnoreCase))
            {
                screenSpaceUiPassOrder = i;
            }

            // Signature captures the effective attachment contract for compatibility checks.
            string signature = VulkanAttachmentCompatibility.BuildSignature(pass);
            if (batches.Count > 0 && VulkanAttachmentCompatibility.AreCompatible(batches[^1], pass, signature))
            {
                // Extend current batch when stage/signature compatibility holds.
                batches[^1].AddPass(pass.PassIndex);
            }
            else
            {
                // Start a new batch when compatibility is broken or this is the first pass.
                VulkanCompiledPassBatch batch = new(batches.Count, pass.Stage, signature);
                batch.AddPass(pass.PassIndex);
                batches.Add(batch);
            }
        }

        return new VulkanCompiledRenderGraph(
            orderedPasses,
            passOrder,
            batches,
            synchronization,
            screenSpaceUiPassOrder);
    }

}
