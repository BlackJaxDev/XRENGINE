using System.Globalization;

namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// Builds a plan snapshot from the order in which the command chain entered passes. Backends that execute passes in
/// recorded order, such as OpenGL, use it as their plan. Each executed pass is its own batch, and passes that did not
/// run follow the executed passes in declaration order.
/// </summary>
public static class RenderGraphRecordedPlanner
{
    /// <summary>The note that every recorded snapshot carries.</summary>
    public const string SequentialNote = "The backend executes passes in the order that the commands record them. It has no reordering, batching, or barriers.";

    /// <summary>Builds the snapshot.</summary>
    /// <param name="origin">The source of the plan.</param>
    /// <param name="passMetadata">The pass metadata of the pipeline.</param>
    /// <param name="executedOrder">The pass indices of the newest frame, in recorded order.</param>
    /// <param name="revision">A revision that identifies the recorded order.</param>
    public static RenderGraphPlanSnapshot Plan(
        EPlanSnapshotOrigin origin,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        IReadOnlyList<int> executedOrder,
        ulong revision)
    {
        List<string> notes = [SequentialNote];
        if (passMetadata is null || passMetadata.Count == 0)
        {
            notes.Add("The pipeline has no pass metadata.");
            return RenderGraphPlanSnapshotFactory.FromCompilation(origin, revision, passMetadata, RenderGraphCompilation.Empty, notes: notes);
        }

        Dictionary<int, RenderPassMetadata> byIndex = new(passMetadata.Count);
        foreach (RenderPassMetadata pass in passMetadata)
            byIndex[pass.PassIndex] = pass;

        List<RenderPassMetadata> ordered = new(passMetadata.Count);
        HashSet<int> executed = new(executedOrder.Count);
        for (int i = 0; i < executedOrder.Count; i++)
        {
            int passIndex = executedOrder[i];
            if (!byIndex.TryGetValue(passIndex, out RenderPassMetadata? pass))
            {
                notes.Add(string.Concat("The commands entered pass #", passIndex.ToString(CultureInfo.InvariantCulture), ", which has no metadata."));
                continue;
            }

            if (executed.Add(passIndex))
                ordered.Add(pass);
        }

        int executedCount = ordered.Count;
        List<RenderPassMetadata> remaining = new(passMetadata.Count - executedCount);
        foreach (RenderPassMetadata pass in passMetadata)
            if (!executed.Contains(pass.PassIndex))
                remaining.Add(pass);
        remaining.Sort(static (a, b) =>
        {
            int order = a.DeclarationOrder.CompareTo(b.DeclarationOrder);
            return order != 0 ? order : a.PassIndex.CompareTo(b.PassIndex);
        });
        ordered.AddRange(remaining);

        Dictionary<int, int> passOrder = new(ordered.Count);
        RenderGraphPassBatch[] batches = new RenderGraphPassBatch[executedCount];
        for (int i = 0; i < ordered.Count; i++)
        {
            RenderPassMetadata pass = ordered[i];
            passOrder[pass.PassIndex] = i;
            if (i >= executedCount)
                continue;

            RenderGraphPassBatch batch = new(i, pass.Stage, RenderGraphAttachmentCompatibility.BuildSignature(pass));
            batch.AddPass(pass.PassIndex);
            batches[i] = batch;
        }

        RenderGraphSynchronizationInfo synchronization;
        try
        {
            synchronization = RenderGraphCompiler.Compile(passMetadata).Synchronization;
        }
        catch (InvalidOperationException ex)
        {
            // The metadata has a cycle or a duplicate pass index, so it has no edges to check the order against.
            synchronization = RenderGraphSynchronizationInfo.Empty;
            notes.Add(string.Concat("The compiler failed, so the order is not checked: ", ex.Message));
        }

        if (executedCount == 0)
            notes.Add("No pass executed in the newest frame.");

        RenderGraphCompilation compilation = new(ordered, passOrder, batches, synchronization, int.MaxValue);
        return RenderGraphPlanSnapshotFactory.FromCompilation(
            origin,
            revision,
            passMetadata,
            compilation,
            executedPassIndices: executed,
            lifetimes: RenderGraphPlanSnapshotFactory.ComputeLogicalLifetimes(ordered.GetRange(0, executedCount)),
            notes: notes);
    }
}
