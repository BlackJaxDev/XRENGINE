using System.Numerics;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The cached pass graph: nodes in topological order, hazard edges from
/// <see cref="RenderGraphSynchronizationPlanner.Build"/>, and a stable layout with one band for each pass stage.
/// </summary>
public sealed class RenderPipelinePassGraphLayout
{
    /// <summary>The node width in canvas units.</summary>
    public const float NodeWidth = 230f;
    /// <summary>The node height in canvas units.</summary>
    public const float NodeHeight = 84f;
    /// <summary>The distance between columns.</summary>
    public const float ColumnStep = 290f;
    /// <summary>The distance between rows.</summary>
    public const float RowStep = 104f;
    /// <summary>The extra space between stage bands.</summary>
    public const float BandGap = 40f;

    private static readonly ERenderGraphPassStage[] StageOrder =
        [ERenderGraphPassStage.Graphics, ERenderGraphPassStage.Compute, ERenderGraphPassStage.Transfer];

    /// <summary>Gets the nodes in topological order.</summary>
    public List<RenderPipelinePassGraphNode> Nodes { get; } = [];

    /// <summary>Gets the edges.</summary>
    public List<RenderPipelinePassGraphEdge> Edges { get; } = [];

    /// <summary>Gets the node index of each pass index.</summary>
    public Dictionary<int, int> NodeIndexByPass { get; } = [];

    /// <summary>Gets the stage bands: stage, top, and height in canvas units.</summary>
    public List<(ERenderGraphPassStage Stage, float Top, float Height, string Label)> Bands { get; } = [];

    /// <summary>Gets the error text when sorting failed (for example a dependency cycle), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Gets the canvas size that holds every node.</summary>
    public Vector2 CanvasSize { get; private set; }

    /// <summary>Gets the synchronization edges that the planner produced, for tools that need resource states.</summary>
    public RenderGraphSynchronizationInfo Synchronization { get; private set; } = RenderGraphSynchronizationInfo.Empty;

    /// <summary>
    /// Rebuilds the graph for <paramref name="passes"/>. Call it when the pass metadata or its revision changes.
    /// </summary>
    public void Build(IReadOnlyCollection<RenderPassMetadata> passes, RenderPipelineStructureSnapshot structure)
    {
        Nodes.Clear();
        Edges.Clear();
        NodeIndexByPass.Clear();
        Bands.Clear();
        Error = null;
        Synchronization = RenderGraphSynchronizationInfo.Empty;
        CanvasSize = Vector2.Zero;

        if (passes.Count == 0)
            return;

        IReadOnlyList<RenderPassMetadata> ordered;
        try
        {
            ordered = RenderGraphSynchronizationPlanner.TopologicallySort(passes);
            Synchronization = RenderGraphSynchronizationPlanner.Build(passes);
        }
        catch (InvalidOperationException ex)
        {
            // Show the passes in declaration order and report the sort failure.
            Error = ex.Message;
            List<RenderPassMetadata> fallback = [.. passes];
            fallback.Sort(static (left, right) => left.DeclarationOrder.CompareTo(right.DeclarationOrder));
            ordered = fallback;
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            RenderPassMetadata pass = ordered[i];
            int ownerId = 0;
            if (structure.TryGetOwnerCommandIndex(pass.PassIndex, out int ownerIndex))
                ownerId = RenderPipelineWorkbenchNodeIds.Get(structure.Commands[ownerIndex].Command);

            NodeIndexByPass[pass.PassIndex] = Nodes.Count;
            Nodes.Add(new RenderPipelinePassGraphNode
            {
                Metadata = pass,
                TopologicalOrder = i,
                OrderText = RenderPipelineWorkbenchText.FormatCount(i),
                Title = pass.Name,
                Subtitle = string.Concat(
                    "#", RenderPipelineWorkbenchText.FormatCount(pass.PassIndex),
                    "  ", pass.Stage.ToString(),
                    "  uses ", RenderPipelineWorkbenchText.FormatCount(pass.ResourceUsages.Count)),
                AttachmentSummary = BuildAttachmentSummary(pass),
                OwnerCommandId = ownerId,
            });
        }

        AddEdges();
        BuildNeighborText();
        LayOut();
    }

    private void BuildNeighborText()
    {
        HashSet<int> seen = [];
        List<string> names = [];
        foreach (RenderPipelinePassGraphNode node in Nodes)
        {
            node.ProducersText = JoinNeighbors(node.IncomingEdges, upstream: true, seen, names);
            node.ConsumersText = JoinNeighbors(node.OutgoingEdges, upstream: false, seen, names);
        }
    }

    private string JoinNeighbors(List<int> edges, bool upstream, HashSet<int> seen, List<string> names)
    {
        seen.Clear();
        names.Clear();
        for (int i = 0; i < edges.Count; i++)
        {
            RenderPipelinePassGraphEdge edge = Edges[edges[i]];
            int neighbor = upstream ? edge.FromNode : edge.ToNode;
            if (seen.Add(neighbor))
                names.Add(Nodes[neighbor].Title);
        }

        return names.Count == 0 ? string.Empty : string.Join(", ", names);
    }

    /// <summary>
    /// Marks every ancestor and descendant of <paramref name="nodeIndex"/> in <paramref name="related"/>.
    /// </summary>
    public void CollectRelated(int nodeIndex, bool[] related, Stack<int> scratch)
    {
        Array.Clear(related);
        if ((uint)nodeIndex >= (uint)Nodes.Count)
            return;

        related[nodeIndex] = true;
        Walk(nodeIndex, related, scratch, upstream: true);
        Walk(nodeIndex, related, scratch, upstream: false);
    }

    private void Walk(int start, bool[] related, Stack<int> scratch, bool upstream)
    {
        scratch.Clear();
        scratch.Push(start);
        while (scratch.Count > 0)
        {
            RenderPipelinePassGraphNode node = Nodes[scratch.Pop()];
            List<int> edges = upstream ? node.IncomingEdges : node.OutgoingEdges;
            for (int i = 0; i < edges.Count; i++)
            {
                RenderPipelinePassGraphEdge edge = Edges[edges[i]];
                int next = upstream ? edge.FromNode : edge.ToNode;
                if (related[next])
                    continue;

                related[next] = true;
                scratch.Push(next);
            }
        }
    }

    private void AddEdges()
    {
        HashSet<(int, int, string)> seen = [];
        foreach (RenderGraphSynchronizationEdge edge in Synchronization.Edges)
        {
            if (!NodeIndexByPass.TryGetValue(edge.ProducerPassIndex, out int from) ||
                !NodeIndexByPass.TryGetValue(edge.ConsumerPassIndex, out int to) ||
                from == to)
            {
                continue;
            }

            ERenderPipelinePassEdgeKind kind = Classify(edge);
            if (!seen.Add((from, to, edge.ResourceName)))
                continue;

            AddEdge(from, to, kind, edge.ResourceName);
        }

        // Explicit dependencies that the planner merged into a resource edge stay visible as dotted edges only when
        // the sort failed and the planner produced nothing.
        if (Error is null)
            return;

        foreach (RenderPipelinePassGraphNode node in Nodes)
        {
            int to = NodeIndexByPass[node.PassIndex];
            foreach (int dependency in node.Metadata.ExplicitDependencies)
            {
                if (NodeIndexByPass.TryGetValue(dependency, out int from) && seen.Add((from, to, string.Empty)))
                    AddEdge(from, to, ERenderPipelinePassEdgeKind.Explicit, string.Empty);
            }
        }
    }

    private void AddEdge(int from, int to, ERenderPipelinePassEdgeKind kind, string resourceName)
    {
        int edgeIndex = Edges.Count;
        Edges.Add(new RenderPipelinePassGraphEdge(from, to, kind, resourceName));
        Nodes[from].OutgoingEdges.Add(edgeIndex);
        Nodes[to].IncomingEdges.Add(edgeIndex);
    }

    private static ERenderPipelinePassEdgeKind Classify(RenderGraphSynchronizationEdge edge)
    {
        if (edge.DependencyOnly)
            return ERenderPipelinePassEdgeKind.Explicit;

        bool producerWrites = Writes(edge.ProducerState.AccessMask);
        bool consumerWrites = Writes(edge.ConsumerState.AccessMask);
        if (producerWrites && consumerWrites)
            return ERenderPipelinePassEdgeKind.WriteAfterWrite;
        return producerWrites
            ? ERenderPipelinePassEdgeKind.ReadAfterWrite
            : ERenderPipelinePassEdgeKind.WriteAfterRead;
    }

    private static bool Writes(RenderGraphAccessMask mask)
        => (mask & (RenderGraphAccessMask.MemoryWrite |
                    RenderGraphAccessMask.ShaderWrite |
                    RenderGraphAccessMask.ColorAttachmentWrite |
                    RenderGraphAccessMask.DepthStencilWrite |
                    RenderGraphAccessMask.TransferWrite)) != 0;

    private void LayOut()
    {
        // Column: the longest chain of edges that ends at the node. Nodes are in topological order when the
        // sort succeeded, so one forward pass is enough. After a failed sort, edges can point backward; those
        // edges do not move nodes.
        for (int i = 0; i < Nodes.Count; i++)
        {
            RenderPipelinePassGraphNode node = Nodes[i];
            int column = 0;
            for (int e = 0; e < node.IncomingEdges.Count; e++)
            {
                int from = Edges[node.IncomingEdges[e]].FromNode;
                if (from < i)
                    column = Math.Max(column, Nodes[from].Column + 1);
            }

            node.Column = column;
        }

        float top = 0f;
        float maxRight = 0f;
        Dictionary<int, int> rowsByColumn = [];
        foreach (ERenderGraphPassStage stage in StageOrder)
        {
            rowsByColumn.Clear();
            int bandRows = 0;
            for (int i = 0; i < Nodes.Count; i++)
            {
                RenderPipelinePassGraphNode node = Nodes[i];
                if (node.Metadata.Stage != stage)
                    continue;

                int row = rowsByColumn.GetValueOrDefault(node.Column);
                rowsByColumn[node.Column] = row + 1;
                node.Row = row;
                node.Position = new Vector2(node.Column * ColumnStep, top + 30f + (row * RowStep));
                bandRows = Math.Max(bandRows, row + 1);
                maxRight = MathF.Max(maxRight, node.Position.X + NodeWidth);
            }

            if (bandRows == 0)
                continue;

            float height = 30f + (bandRows * RowStep);
            Bands.Add((stage, top, height, stage.ToString()));
            top += height + BandGap;
        }

        CanvasSize = new Vector2(maxRight, top);
    }

    private static string BuildAttachmentSummary(RenderPassMetadata pass)
    {
        int color = 0;
        int depth = 0;
        int sampled = 0;
        int storage = 0;
        int other = 0;
        foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
        {
            switch (usage.ResourceType)
            {
                case ERenderPassResourceType.ColorAttachment:
                case ERenderPassResourceType.ResolveAttachment:
                    color++;
                    break;
                case ERenderPassResourceType.DepthAttachment:
                case ERenderPassResourceType.StencilAttachment:
                    depth++;
                    break;
                case ERenderPassResourceType.SampledTexture:
                    sampled++;
                    break;
                case ERenderPassResourceType.StorageTexture:
                case ERenderPassResourceType.StorageBuffer:
                    storage++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return string.Concat(
            "color ", RenderPipelineWorkbenchText.FormatCount(color),
            "  depth ", RenderPipelineWorkbenchText.FormatCount(depth),
            "  sampled ", RenderPipelineWorkbenchText.FormatCount(sampled),
            "  storage ", RenderPipelineWorkbenchText.FormatCount(storage),
            other > 0 ? string.Concat("  other ", RenderPipelineWorkbenchText.FormatCount(other)) : string.Empty);
    }
}
