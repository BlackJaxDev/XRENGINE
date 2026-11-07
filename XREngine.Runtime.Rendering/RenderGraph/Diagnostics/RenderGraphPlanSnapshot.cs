using System.Globalization;

namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// A backend-neutral, immutable description of a render graph plan: pass order, batches, submissions, synchronization
/// edges, barriers, resource lifetimes, and alias groups. Sources fill only the parts they know. Tools compare
/// snapshots from different sources to show how a backend changes the authored order.
/// </summary>
public sealed class RenderGraphPlanSnapshot
{
    private readonly Dictionary<int, PlanPassSnapshot> _passByIndex;

    /// <summary>Initializes a snapshot. The lists must not change after the call.</summary>
    public RenderGraphPlanSnapshot(
        EPlanSnapshotOrigin origin,
        ulong revision,
        IReadOnlyCollection<RenderPassMetadata>? sourcePassMetadata,
        IReadOnlyList<PlanPassSnapshot> passes,
        IReadOnlyList<PlanBatchSnapshot> batches,
        IReadOnlyList<PlanSubmissionSnapshot> submissions,
        IReadOnlyList<PlanSyncEdgeSnapshot> syncEdges,
        IReadOnlyList<PlanBarrierSnapshot> barriers,
        IReadOnlyList<PlanResourceLifetimeSnapshot> lifetimes,
        IReadOnlyList<PlanAliasGroupSnapshot> aliasGroups,
        IReadOnlyList<string> notes)
    {
        Origin = origin;
        Revision = revision;
        SourcePassMetadata = sourcePassMetadata;
        Passes = passes;
        Batches = batches;
        Submissions = submissions;
        SyncEdges = syncEdges;
        Barriers = barriers;
        Lifetimes = lifetimes;
        AliasGroups = aliasGroups;
        Notes = notes;
        _passByIndex = new Dictionary<int, PlanPassSnapshot>(passes.Count);
        for (int i = 0; i < passes.Count; i++)
            _passByIndex[passes[i].PassIndex] = passes[i];
        Reorders = BuildReorders(passes, syncEdges, _passByIndex);
        OrderViolations = BuildOrderViolations(passes, syncEdges, _passByIndex);
    }

    /// <summary>Gets the source of the snapshot.</summary>
    public EPlanSnapshotOrigin Origin { get; }

    /// <summary>Gets the plan revision of the source. A new revision means a new plan.</summary>
    public ulong Revision { get; }

    /// <summary>Gets the metadata that the source planned, when the source knows it.</summary>
    public IReadOnlyCollection<RenderPassMetadata>? SourcePassMetadata { get; }

    /// <summary>Gets the passes in plan order.</summary>
    public IReadOnlyList<PlanPassSnapshot> Passes { get; }

    /// <summary>Gets the batches in plan order.</summary>
    public IReadOnlyList<PlanBatchSnapshot> Batches { get; }

    /// <summary>Gets the queue submissions in plan order. The list is empty when the source has no submissions.</summary>
    public IReadOnlyList<PlanSubmissionSnapshot> Submissions { get; }

    /// <summary>Gets the logical synchronization edges.</summary>
    public IReadOnlyList<PlanSyncEdgeSnapshot> SyncEdges { get; }

    /// <summary>Gets the physical barriers. The list is empty for sources with no barriers.</summary>
    public IReadOnlyList<PlanBarrierSnapshot> Barriers { get; }

    /// <summary>Gets the resource lifetimes in plan positions.</summary>
    public IReadOnlyList<PlanResourceLifetimeSnapshot> Lifetimes { get; }

    /// <summary>Gets the memory alias groups. The list is empty for sources with no aliasing.</summary>
    public IReadOnlyList<PlanAliasGroupSnapshot> AliasGroups { get; }

    /// <summary>Gets notes from the source, such as the ordering rule of the backend.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Gets the passes whose plan position differs from their authored position, with the cause.</summary>
    public IReadOnlyList<PlanReorderEntry> Reorders { get; }

    /// <summary>
    /// Gets the synchronization edges whose consumer runs before its producer in plan order. Only executed passes
    /// count. A backend plan never has violations; a recorded order can have them when the metadata does not match
    /// the command order.
    /// </summary>
    public IReadOnlyList<PlanOrderViolation> OrderViolations { get; }

    /// <summary>Finds a pass by its pass index.</summary>
    public bool TryGetPass(int passIndex, out PlanPassSnapshot? pass)
        => _passByIndex.TryGetValue(passIndex, out pass);

    /// <summary>
    /// Finds passes whose plan position differs from their authored position. For each moved pass, the cause is the
    /// first edge that ties it to a pass on the other side of its authored position.
    /// </summary>
    private static PlanReorderEntry[] BuildReorders(
        IReadOnlyList<PlanPassSnapshot> passes,
        IReadOnlyList<PlanSyncEdgeSnapshot> edges,
        Dictionary<int, PlanPassSnapshot> byIndex)
    {
        List<PlanPassSnapshot> executed = new(passes.Count);
        for (int i = 0; i < passes.Count; i++)
            if (passes[i].Executed)
                executed.Add(passes[i]);
        if (executed.Count == 0)
            return [];

        // Compare only executed passes. A pass that did not run has no execution position.
        PlanPassSnapshot[] authored = [.. executed];
        Array.Sort(authored, static (a, b) =>
        {
            int order = a.AuthoredOrder.CompareTo(b.AuthoredOrder);
            return order != 0 ? order : a.PassIndex.CompareTo(b.PassIndex);
        });

        Dictionary<int, int> authoredPosition = new(authored.Length);
        for (int i = 0; i < authored.Length; i++)
            authoredPosition[authored[i].PassIndex] = i;

        List<PlanReorderEntry> reorders = [];
        for (int executionPosition = 0; executionPosition < executed.Count; executionPosition++)
        {
            PlanPassSnapshot pass = executed[executionPosition];
            int authoredAt = authoredPosition[pass.PassIndex];
            if (authoredAt == executionPosition)
                continue;

            reorders.Add(new PlanReorderEntry(pass.PassIndex, authoredAt, executionPosition, FindCause(pass, authoredAt, edges, authoredPosition, byIndex)));
        }

        return [.. reorders];
    }

    private static PlanOrderViolation[] BuildOrderViolations(
        IReadOnlyList<PlanPassSnapshot> passes,
        IReadOnlyList<PlanSyncEdgeSnapshot> edges,
        Dictionary<int, PlanPassSnapshot> byIndex)
    {
        List<PlanOrderViolation>? violations = null;
        for (int i = 0; i < edges.Count; i++)
        {
            PlanSyncEdgeSnapshot edge = edges[i];
            if (!byIndex.TryGetValue(edge.ProducerPassIndex, out PlanPassSnapshot? producer) ||
                !byIndex.TryGetValue(edge.ConsumerPassIndex, out PlanPassSnapshot? consumer) ||
                !producer.Executed ||
                !consumer.Executed ||
                consumer.ExecutionOrder > producer.ExecutionOrder)
            {
                continue;
            }

            violations ??= [];
            violations.Add(new PlanOrderViolation(
                edge.ProducerPassIndex,
                edge.ConsumerPassIndex,
                edge.ResourceName,
                string.Concat(
                    Describe(edge.ConsumerPassIndex, byIndex), " runs before ",
                    Describe(edge.ProducerPassIndex, byIndex), ", but the metadata says that it waits for it",
                    DescribeResource(edge), ".")));
        }

        return violations is null ? [] : [.. violations];
    }

    private static string FindCause(
        PlanPassSnapshot pass,
        int authoredAt,
        IReadOnlyList<PlanSyncEdgeSnapshot> edges,
        Dictionary<int, int> authoredPosition,
        Dictionary<int, PlanPassSnapshot> byIndex)
    {
        for (int i = 0; i < edges.Count; i++)
        {
            PlanSyncEdgeSnapshot edge = edges[i];
            if (edge.ConsumerPassIndex == pass.PassIndex &&
                authoredPosition.TryGetValue(edge.ProducerPassIndex, out int producerAt) &&
                producerAt > authoredAt)
            {
                return string.Concat("waits for ", Describe(edge.ProducerPassIndex, byIndex), DescribeResource(edge), " declared later");
            }

            if (edge.ProducerPassIndex == pass.PassIndex &&
                authoredPosition.TryGetValue(edge.ConsumerPassIndex, out int consumerAt) &&
                consumerAt < authoredAt)
            {
                return string.Concat("feeds ", Describe(edge.ConsumerPassIndex, byIndex), DescribeResource(edge), " declared earlier");
            }
        }

        return "a pass declared before it moved, or independent components merged by declaration order";
    }

    private static string Describe(int passIndex, Dictionary<int, PlanPassSnapshot> byIndex)
        => byIndex.TryGetValue(passIndex, out PlanPassSnapshot? pass)
            ? string.Concat(pass.Name, " #", passIndex.ToString(CultureInfo.InvariantCulture))
            : string.Concat("#", passIndex.ToString(CultureInfo.InvariantCulture));

    private static string DescribeResource(PlanSyncEdgeSnapshot edge)
        => edge.DependencyOnly || edge.ResourceName.Length == 0
            ? " (explicit dependency)"
            : string.Concat(" (", edge.ResourceName, ")");
}
