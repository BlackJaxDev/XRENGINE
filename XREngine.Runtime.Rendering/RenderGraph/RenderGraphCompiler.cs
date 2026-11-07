namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// Compiles render pass metadata into a backend-neutral pass order, pass batches, and synchronization edges.
/// Vulkan uses it for its graph order and batches. OpenGL and the Workbench use it to check the authored order and to
/// show a planned order when no Vulkan renderer runs.
/// </summary>
/// <remarks>
/// Independent components keep their declaration order relative to each other. Inside a component, the order is the
/// topological order of <see cref="RenderGraphSynchronizationPlanner.TopologicallySort"/>. Compilation allocates, so
/// call it only when the metadata changes.
/// </remarks>
public static class RenderGraphCompiler
{
    private const string ScreenSpaceUiPassName = "VPRC_RenderScreenSpaceUI";

    /// <summary>Compiles <paramref name="passMetadata"/> with no reuse of earlier results.</summary>
    public static RenderGraphCompilation Compile(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return RenderGraphCompilation.Empty;

        return Assemble(BuildSubgraphs(passMetadata, [], out _));
    }

    /// <summary>
    /// Splits the passes into disconnected components (passes that share no resource and no explicit dependency) and
    /// compiles each component. A component that matches an entry of <paramref name="previousSubgraphs"/> reuses it.
    /// </summary>
    /// <param name="passMetadata">The passes to compile.</param>
    /// <param name="previousSubgraphs">Subgraphs from an earlier compilation, or an empty list.</param>
    /// <param name="rebuiltSubgraphCount">Receives the number of components that the method compiled again.</param>
    /// <returns>The components, ordered by their first declaration order.</returns>
    public static RenderGraphCompiledSubgraph[] BuildSubgraphs(
        IReadOnlyCollection<RenderPassMetadata> passMetadata,
        IReadOnlyList<RenderGraphCompiledSubgraph> previousSubgraphs,
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
        var compiled = new List<RenderGraphCompiledSubgraph>(componentPasses.Count);
        rebuiltSubgraphCount = 0;
        foreach (List<RenderPassMetadata> component in componentPasses.Values)
        {
            component.Sort(static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
            RenderGraphCompiledSubgraph? match = null;
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

            compiled.Add(RenderGraphCompiledSubgraph.Compile([.. component]));
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

    /// <summary>
    /// Merges compiled components into one pass order, batches the passes, and merges the synchronization edges.
    /// </summary>
    public static RenderGraphCompilation Assemble(
        IReadOnlyList<RenderGraphCompiledSubgraph> subgraphs)
    {
        IReadOnlyList<RenderPassMetadata> orderedPasses = MergeTopologicalOrder(subgraphs);
        RenderGraphSynchronizationInfo synchronization = MergeSynchronization(subgraphs, orderedPasses);

        Dictionary<int, int> passOrder = new(orderedPasses.Count);
        int screenSpaceUiPassOrder = int.MaxValue;
        List<RenderGraphPassBatch> batches = [];

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
            string signature = RenderGraphAttachmentCompatibility.BuildSignature(pass);
            if (batches.Count > 0 && RenderGraphAttachmentCompatibility.AreCompatible(batches[^1], pass, signature))
            {
                // Extend current batch when stage/signature compatibility holds.
                batches[^1].AddPass(pass.PassIndex);
            }
            else
            {
                // Start a new batch when compatibility is broken or this is the first pass.
                RenderGraphPassBatch batch = new(batches.Count, pass.Stage, signature);
                batch.AddPass(pass.PassIndex);
                batches.Add(batch);
            }
        }

        return new RenderGraphCompilation(
            orderedPasses,
            passOrder,
            batches,
            synchronization,
            screenSpaceUiPassOrder);
    }

    private static IReadOnlyList<RenderPassMetadata> MergeTopologicalOrder(
        IReadOnlyList<RenderGraphCompiledSubgraph> subgraphs)
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
        IReadOnlyList<RenderGraphCompiledSubgraph> subgraphs,
        IReadOnlyList<RenderPassMetadata> orderedPasses)
    {
        var synchronizationByPass = new Dictionary<int, RenderGraphSynchronizationInfo>(orderedPasses.Count);
        int edgeCount = 0;
        for (int subgraphIndex = 0; subgraphIndex < subgraphs.Count; subgraphIndex++)
        {
            RenderGraphCompiledSubgraph subgraph = subgraphs[subgraphIndex];
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
