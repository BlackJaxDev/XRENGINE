namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// The topological order and synchronization of one disconnected component of a render graph. A compiler that keeps
/// earlier subgraphs can rebuild one component without repeating the work for independent components.
/// </summary>
public sealed class RenderGraphCompiledSubgraph
{
    private readonly RenderPassMetadata[] _sourcePasses;
    private readonly int[] _sourceRevisions;

    private RenderGraphCompiledSubgraph(
        RenderPassMetadata[] sourcePasses,
        int[] sourceRevisions,
        IReadOnlyList<RenderPassMetadata> orderedPasses,
        RenderGraphSynchronizationInfo synchronization)
    {
        _sourcePasses = sourcePasses;
        _sourceRevisions = sourceRevisions;
        OrderedPasses = orderedPasses;
        Synchronization = synchronization;
    }

    /// <summary>Gets the passes of the component in topological order.</summary>
    public IReadOnlyList<RenderPassMetadata> OrderedPasses { get; }

    /// <summary>Gets the synchronization edges of the component.</summary>
    public RenderGraphSynchronizationInfo Synchronization { get; }

    /// <summary>Gets the smallest declaration order of the component, which orders components against each other.</summary>
    public int FirstDeclarationOrder
    {
        get
        {
            int first = int.MaxValue;
            for (int i = 0; i < _sourcePasses.Length; i++)
                first = Math.Min(first, _sourcePasses[i].DeclarationOrder);
            return first;
        }
    }

    /// <summary>Gets the smallest pass index of the component.</summary>
    public int FirstPassIndex
        => _sourcePasses.Length == 0 ? int.MaxValue : _sourcePasses[0].PassIndex;

    /// <summary>Compiles one component. The method sorts <paramref name="sourcePasses"/> by pass index in place.</summary>
    public static RenderGraphCompiledSubgraph Compile(RenderPassMetadata[] sourcePasses)
    {
        Array.Sort(sourcePasses, static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
        int[] sourceRevisions = new int[sourcePasses.Length];
        for (int index = 0; index < sourcePasses.Length; index++)
            sourceRevisions[index] = sourcePasses[index].Revision;

        return new RenderGraphCompiledSubgraph(
            sourcePasses,
            sourceRevisions,
            RenderGraphSynchronizationPlanner.TopologicallySort(sourcePasses),
            RenderGraphSynchronizationPlanner.Build(sourcePasses));
    }

    /// <summary>
    /// Returns true when <paramref name="sourcePasses"/> holds the same pass objects at the same revisions, sorted by pass index.
    /// </summary>
    public bool Matches(IReadOnlyList<RenderPassMetadata> sourcePasses)
    {
        if (sourcePasses.Count != _sourcePasses.Length)
            return false;

        for (int index = 0; index < sourcePasses.Count; index++)
        {
            RenderPassMetadata candidate = sourcePasses[index];
            if (!ReferenceEquals(candidate, _sourcePasses[index]) ||
                candidate.Revision != _sourceRevisions[index])
            {
                return false;
            }
        }

        return true;
    }
}
