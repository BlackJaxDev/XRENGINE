using System.Collections.Concurrent;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Compiles immutable Vulkan render-graph structures and owns the metadata compilation cache.
/// </summary>
internal sealed class VulkanRenderGraphCompiler
{
    private const int MaxMetadataCacheEntries = 64;

    private sealed class CompiledGraphCacheEntry
    {
        private readonly object _gate = new();
        private VulkanCompiledRenderGraph _graph = VulkanCompiledRenderGraph.Empty;
        private RenderGraphCompiledSubgraph[] _subgraphs = [];
        private int _revisionStamp = int.MinValue;
        private int _passCount = -1;

        internal VulkanCompiledRenderGraph GetOrCompile(
            IReadOnlyCollection<RenderPassMetadata> metadata)
        {
            lock (_gate)
            {
                int revisionStamp = ReadRevisionStamp(metadata);
                if (metadata is RenderPassMetadataSnapshot &&
                    metadata.Count == _passCount &&
                    revisionStamp == _revisionStamp)
                {
                    return _graph;
                }

                RenderGraphCompiledSubgraph[] subgraphs =
                    RenderGraphCompiler.BuildSubgraphs(metadata, _subgraphs, out int rebuiltSubgraphCount);
                int completedRevisionStamp = ReadRevisionStamp(metadata);
                if (completedRevisionStamp != revisionStamp)
                {
                    throw new InvalidOperationException(
                        "Render-pass metadata mutated while its Vulkan graph was being compiled. " +
                        "Publish graph mutations at a frame boundary and retry.");
                }

                // Mutable collection implementations do not expose the O(1) revision
                // source used by RenderPassMetadataSnapshot. Exact subgraph matching
                // still lets them reuse the prior immutable publication safely.
                if (rebuiltSubgraphCount == 0 &&
                    metadata.Count == _passCount &&
                    SubgraphSequenceMatches(subgraphs, _subgraphs))
                {
                    _revisionStamp = completedRevisionStamp;
                    return _graph;
                }

                _graph = new VulkanCompiledRenderGraph(RenderGraphCompiler.Assemble(subgraphs));
                _subgraphs = subgraphs;
                _revisionStamp = completedRevisionStamp;
                _passCount = metadata.Count;
                return _graph;
            }
        }
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
    /// Compiles the high-level pass metadata with the backend-neutral <see cref="RenderGraphCompiler"/> into:
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
            static _ => new CompiledGraphCacheEntry()).GetOrCompile(passMetadata);
    }

    private void TrimMetadataCachesIfRequired()
    {
        if (_compiledGraphCache.Count < MaxMetadataCacheEntries)
            return;

        _compiledGraphCache.Clear();
    }

    private static int ReadRevisionStamp(IReadOnlyCollection<RenderPassMetadata> metadata)
    {
        if (metadata is RenderPassMetadataSnapshot snapshot)
            return snapshot.RevisionStamp;

        HashCode hash = new();
        hash.Add(metadata.Count);
        foreach (RenderPassMetadata pass in metadata)
        {
            hash.Add(pass.PassIndex);
            hash.Add(pass.Revision);
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pass));
        }

        return hash.ToHashCode();
    }

    private static bool SubgraphSequenceMatches(
        IReadOnlyList<RenderGraphCompiledSubgraph> left,
        IReadOnlyList<RenderGraphCompiledSubgraph> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
            if (!ReferenceEquals(left[index], right[index]))
                return false;

        return true;
    }
}
