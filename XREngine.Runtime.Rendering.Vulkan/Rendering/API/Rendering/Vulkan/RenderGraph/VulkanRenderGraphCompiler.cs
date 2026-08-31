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

    private sealed class CompiledGraphCacheEntry
    {
        private readonly object _gate = new();
        private VulkanCompiledRenderGraph _graph = VulkanCompiledRenderGraph.Empty;
        private VulkanCompiledRenderGraphSubgraph[] _subgraphs = [];
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

                VulkanCompiledRenderGraphSubgraph[] subgraphs =
                    BuildSubgraphs(metadata, _subgraphs, out int rebuiltSubgraphCount);
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

                _graph = AssembleCompiledGraph(subgraphs);
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
            static _ => new CompiledGraphCacheEntry()).GetOrCompile(passMetadata);
    }

    private void TrimMetadataCachesIfRequired()
    {
        if (_compiledGraphCache.Count < MaxMetadataCacheEntries)
            return;

        _compiledGraphCache.Clear();
    }

    private static VulkanCompiledRenderGraphSubgraph[] BuildSubgraphs(
        IReadOnlyCollection<RenderPassMetadata> passMetadata,
        IReadOnlyList<VulkanCompiledRenderGraphSubgraph> previousSubgraphs,
        out int rebuiltSubgraphCount)
    {
        RenderPassMetadata[] passes = new RenderPassMetadata[passMetadata.Count];
        int passCursor = 0;
        foreach (RenderPassMetadata pass in passMetadata)
            passes[passCursor++] = pass;

        var passSlotByIndex = new Dictionary<int, int>(passes.Length);
        for (int index = 0; index < passes.Length; index++)
        {
            if (!passSlotByIndex.TryAdd(passes[index].PassIndex, index))
            {
                throw new InvalidOperationException(
                    $"Render graph contains duplicate pass index {passes[index].PassIndex}.");
            }
        }

        int[] parents = new int[passes.Length];
        byte[] ranks = new byte[passes.Length];
        for (int index = 0; index < parents.Length; index++)
            parents[index] = index;

        var firstPassByResource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < passes.Length; index++)
        {
            RenderPassMetadata pass = passes[index];
            foreach (int dependency in pass.ExplicitDependencies)
            {
                if (passSlotByIndex.TryGetValue(dependency, out int dependencySlot))
                    Union(parents, ranks, index, dependencySlot);
            }

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (string.IsNullOrWhiteSpace(usage.ResourceName))
                    continue;

                if (firstPassByResource.TryGetValue(usage.ResourceName, out int firstPassSlot))
                    Union(parents, ranks, index, firstPassSlot);
                else
                    firstPassByResource.Add(usage.ResourceName, index);
            }
        }

        var componentPasses = new Dictionary<int, List<RenderPassMetadata>>();
        for (int index = 0; index < passes.Length; index++)
        {
            int root = FindRoot(parents, index);
            if (!componentPasses.TryGetValue(root, out List<RenderPassMetadata>? component))
            {
                component = [];
                componentPasses.Add(root, component);
            }

            component.Add(passes[index]);
        }

        bool[] reusedPrevious = new bool[previousSubgraphs.Count];
        var compiled = new List<VulkanCompiledRenderGraphSubgraph>(componentPasses.Count);
        rebuiltSubgraphCount = 0;
        foreach (List<RenderPassMetadata> component in componentPasses.Values)
        {
            component.Sort(static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
            VulkanCompiledRenderGraphSubgraph? match = null;
            for (int previousIndex = 0; previousIndex < previousSubgraphs.Count; previousIndex++)
            {
                if (reusedPrevious[previousIndex] ||
                    !previousSubgraphs[previousIndex].Matches(component))
                {
                    continue;
                }

                match = previousSubgraphs[previousIndex];
                reusedPrevious[previousIndex] = true;
                break;
            }

            if (match is not null)
            {
                compiled.Add(match);
                continue;
            }

            compiled.Add(VulkanCompiledRenderGraphSubgraph.Compile([.. component]));
            rebuiltSubgraphCount++;
        }

        compiled.Sort(static (left, right) =>
        {
            int declarationCompare = left.FirstDeclarationOrder.CompareTo(right.FirstDeclarationOrder);
            return declarationCompare != 0
                ? declarationCompare
                : left.FirstPassIndex.CompareTo(right.FirstPassIndex);
        });
        return [.. compiled];
    }

    private static VulkanCompiledRenderGraph AssembleCompiledGraph(
        IReadOnlyList<VulkanCompiledRenderGraphSubgraph> subgraphs)
    {
        IReadOnlyList<RenderPassMetadata> orderedPasses = MergeTopologicalOrder(subgraphs);
        RenderGraphSynchronizationInfo synchronization = MergeSynchronization(subgraphs, orderedPasses);

        Dictionary<int, int> passOrder = new(orderedPasses.Count);
        int screenSpaceUiPassOrder = int.MaxValue;
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

    private static IReadOnlyList<RenderPassMetadata> MergeTopologicalOrder(
        IReadOnlyList<VulkanCompiledRenderGraphSubgraph> subgraphs)
    {
        int passCount = 0;
        for (int index = 0; index < subgraphs.Count; index++)
            passCount += subgraphs[index].OrderedPasses.Count;

        int[] cursors = new int[subgraphs.Count];
        var ordered = new List<RenderPassMetadata>(passCount);
        while (ordered.Count < passCount)
        {
            int selectedSubgraph = -1;
            RenderPassMetadata? selectedPass = null;
            for (int subgraphIndex = 0; subgraphIndex < subgraphs.Count; subgraphIndex++)
            {
                IReadOnlyList<RenderPassMetadata> candidates = subgraphs[subgraphIndex].OrderedPasses;
                int cursor = cursors[subgraphIndex];
                if (cursor >= candidates.Count)
                    continue;

                RenderPassMetadata candidate = candidates[cursor];
                if (selectedPass is not null &&
                    CompareDeclarationOrder(candidate, selectedPass) >= 0)
                {
                    continue;
                }

                selectedSubgraph = subgraphIndex;
                selectedPass = candidate;
            }

            if (selectedSubgraph < 0 || selectedPass is null)
                throw new InvalidOperationException("Render-graph subgraph merge made no progress.");

            ordered.Add(selectedPass);
            cursors[selectedSubgraph]++;
        }

        return ordered;
    }

    private static RenderGraphSynchronizationInfo MergeSynchronization(
        IReadOnlyList<VulkanCompiledRenderGraphSubgraph> subgraphs,
        IReadOnlyList<RenderPassMetadata> orderedPasses)
    {
        var synchronizationByPass = new Dictionary<int, RenderGraphSynchronizationInfo>(orderedPasses.Count);
        int edgeCount = 0;
        for (int subgraphIndex = 0; subgraphIndex < subgraphs.Count; subgraphIndex++)
        {
            VulkanCompiledRenderGraphSubgraph subgraph = subgraphs[subgraphIndex];
            edgeCount += subgraph.Synchronization.Edges.Count;
            foreach (RenderPassMetadata pass in subgraph.OrderedPasses)
                synchronizationByPass.Add(pass.PassIndex, subgraph.Synchronization);
        }

        var edges = new List<RenderGraphSynchronizationEdge>(edgeCount);
        for (int passOrder = 0; passOrder < orderedPasses.Count; passOrder++)
        {
            RenderPassMetadata pass = orderedPasses[passOrder];
            IReadOnlyList<RenderGraphSynchronizationEdge> consumerEdges =
                synchronizationByPass[pass.PassIndex].GetEdgesForConsumer(pass.PassIndex);
            for (int edgeIndex = 0; edgeIndex < consumerEdges.Count; edgeIndex++)
                edges.Add(consumerEdges[edgeIndex]);
        }

        return new RenderGraphSynchronizationInfo(edges);
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
        IReadOnlyList<VulkanCompiledRenderGraphSubgraph> left,
        IReadOnlyList<VulkanCompiledRenderGraphSubgraph> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
            if (!ReferenceEquals(left[index], right[index]))
                return false;

        return true;
    }

    private static int CompareDeclarationOrder(RenderPassMetadata left, RenderPassMetadata right)
    {
        int orderCompare = left.DeclarationOrder.CompareTo(right.DeclarationOrder);
        return orderCompare != 0
            ? orderCompare
            : left.PassIndex.CompareTo(right.PassIndex);
    }

    private static int FindRoot(int[] parents, int index)
    {
        int root = index;
        while (parents[root] != root)
            root = parents[root];

        while (parents[index] != index)
        {
            int parent = parents[index];
            parents[index] = root;
            index = parent;
        }

        return root;
    }

    private static void Union(int[] parents, byte[] ranks, int left, int right)
    {
        int leftRoot = FindRoot(parents, left);
        int rightRoot = FindRoot(parents, right);
        if (leftRoot == rightRoot)
            return;

        if (ranks[leftRoot] < ranks[rightRoot])
        {
            parents[leftRoot] = rightRoot;
            return;
        }

        parents[rightRoot] = leftRoot;
        if (ranks[leftRoot] == ranks[rightRoot])
            ranks[leftRoot]++;
    }

}
