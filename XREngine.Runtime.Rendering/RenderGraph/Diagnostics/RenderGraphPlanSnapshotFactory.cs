namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// Converts a <see cref="RenderGraphCompilation"/> into a <see cref="RenderGraphPlanSnapshot"/>. Backends pass their
/// physical data; the factory fills passes, batches, synchronization edges, and lifetimes from the compilation.
/// </summary>
public static class RenderGraphPlanSnapshotFactory
{
    /// <summary>Builds a snapshot from a compilation.</summary>
    /// <param name="origin">The source of the plan.</param>
    /// <param name="revision">The plan revision of the source.</param>
    /// <param name="sourcePassMetadata">The metadata that the source compiled.</param>
    /// <param name="compilation">The compiled order, batches, and synchronization.</param>
    /// <param name="executedPassIndices">The passes that executed in the last frame, or null when the source does not track execution.</param>
    /// <param name="submissions">Queue submissions, or null.</param>
    /// <param name="barriers">Physical barriers, or null.</param>
    /// <param name="lifetimes">Physical lifetimes, or null to compute logical lifetimes from the pass order.</param>
    /// <param name="aliasGroups">Memory alias groups, or null.</param>
    /// <param name="notes">Notes for tools, or null.</param>
    public static RenderGraphPlanSnapshot FromCompilation(
        EPlanSnapshotOrigin origin,
        ulong revision,
        IReadOnlyCollection<RenderPassMetadata>? sourcePassMetadata,
        RenderGraphCompilation compilation,
        IReadOnlySet<int>? executedPassIndices = null,
        IReadOnlyList<PlanSubmissionSnapshot>? submissions = null,
        IReadOnlyList<PlanBarrierSnapshot>? barriers = null,
        IReadOnlyList<PlanResourceLifetimeSnapshot>? lifetimes = null,
        IReadOnlyList<PlanAliasGroupSnapshot>? aliasGroups = null,
        IReadOnlyList<string>? notes = null)
    {
        Dictionary<int, int> batchByPass = new(compilation.OrderedPasses.Count);
        Dictionary<int, int> submissionByBatch = [];
        if (submissions is not null)
            for (int s = 0; s < submissions.Count; s++)
                for (int b = 0; b < submissions[s].BatchIndices.Count; b++)
                    submissionByBatch[submissions[s].BatchIndices[b]] = submissions[s].SubmissionIndex;

        PlanBatchSnapshot[] batches = new PlanBatchSnapshot[compilation.Batches.Count];
        for (int i = 0; i < batches.Length; i++)
        {
            RenderGraphPassBatch batch = compilation.Batches[i];
            int[] passIndices = [.. batch.PassIndices];
            for (int p = 0; p < passIndices.Length; p++)
                batchByPass[passIndices[p]] = batch.BatchIndex;
            batches[i] = new PlanBatchSnapshot(
                batch.BatchIndex,
                batch.Stage,
                batch.AttachmentSignature,
                passIndices,
                submissionByBatch.GetValueOrDefault(batch.BatchIndex, -1));
        }

        PlanPassSnapshot[] passes = new PlanPassSnapshot[compilation.OrderedPasses.Count];
        for (int i = 0; i < passes.Length; i++)
        {
            RenderPassMetadata pass = compilation.OrderedPasses[i];
            passes[i] = new PlanPassSnapshot(
                pass.PassIndex,
                pass.Name,
                pass.Stage,
                i,
                pass.DeclarationOrder,
                batchByPass.GetValueOrDefault(pass.PassIndex, -1),
                executedPassIndices?.Contains(pass.PassIndex) ?? true,
                pass);
        }

        IReadOnlyList<RenderGraphSynchronizationEdge> sourceEdges = compilation.Synchronization.Edges;
        PlanSyncEdgeSnapshot[] edges = new PlanSyncEdgeSnapshot[sourceEdges.Count];
        for (int i = 0; i < edges.Length; i++)
        {
            RenderGraphSynchronizationEdge edge = sourceEdges[i];
            edges[i] = new PlanSyncEdgeSnapshot(
                edge.ProducerPassIndex,
                edge.ConsumerPassIndex,
                edge.ResourceName ?? string.Empty,
                edge.DependencyOnly,
                edge.ProducerState,
                edge.ConsumerState);
        }

        return new RenderGraphPlanSnapshot(
            origin,
            revision,
            sourcePassMetadata,
            passes,
            batches,
            submissions ?? [],
            edges,
            barriers ?? [],
            lifetimes ?? ComputeLogicalLifetimes(compilation.OrderedPasses),
            aliasGroups ?? [],
            notes ?? []);
    }

    /// <summary>Computes the first and last plan position of each resource usage name.</summary>
    public static PlanResourceLifetimeSnapshot[] ComputeLogicalLifetimes(IReadOnlyList<RenderPassMetadata> orderedPasses)
    {
        Dictionary<string, (int First, int Last)> spans = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        for (int i = 0; i < orderedPasses.Count; i++)
        {
            foreach (RenderPassResourceUsage usage in orderedPasses[i].ResourceUsages)
            {
                if (string.IsNullOrWhiteSpace(usage.ResourceName))
                    continue;

                if (spans.TryGetValue(usage.ResourceName, out (int First, int Last) span))
                {
                    spans[usage.ResourceName] = (span.First, i);
                }
                else
                {
                    spans.Add(usage.ResourceName, (i, i));
                    order.Add(usage.ResourceName);
                }
            }
        }

        PlanResourceLifetimeSnapshot[] lifetimes = new PlanResourceLifetimeSnapshot[order.Count];
        for (int i = 0; i < lifetimes.Length; i++)
        {
            (int first, int last) = spans[order[i]];
            lifetimes[i] = new PlanResourceLifetimeSnapshot(order[i], first, last, -1, false);
        }

        return lifetimes;
    }
}
