using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Immutable topology and synchronization result for one disconnected render-graph
/// component. A retained metadata builder can invalidate one component without
/// forcing independent outputs to repeat their topology and hazard compilation.
/// </summary>
internal sealed class VulkanCompiledRenderGraphSubgraph
{
    private readonly RenderPassMetadata[] _sourcePasses;
    private readonly int[] _sourceRevisions;

    private VulkanCompiledRenderGraphSubgraph(
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

    internal IReadOnlyList<RenderPassMetadata> OrderedPasses { get; }

    internal RenderGraphSynchronizationInfo Synchronization { get; }

    internal int FirstDeclarationOrder
        => _sourcePasses.Length == 0
            ? int.MaxValue
            : _sourcePasses.Min(static pass => pass.DeclarationOrder);

    internal int FirstPassIndex
        => _sourcePasses.Length == 0
            ? int.MaxValue
            : _sourcePasses[0].PassIndex;

    internal static VulkanCompiledRenderGraphSubgraph Compile(RenderPassMetadata[] sourcePasses)
    {
        Array.Sort(sourcePasses, static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
        int[] sourceRevisions = new int[sourcePasses.Length];
        for (int index = 0; index < sourcePasses.Length; index++)
            sourceRevisions[index] = sourcePasses[index].Revision;

        IReadOnlyList<RenderPassMetadata> orderedPasses =
            RenderGraphSynchronizationPlanner.TopologicallySort(sourcePasses);
        RenderGraphSynchronizationInfo synchronization =
            RenderGraphSynchronizationPlanner.Build(sourcePasses);
        return new VulkanCompiledRenderGraphSubgraph(
            sourcePasses,
            sourceRevisions,
            orderedPasses,
            synchronization);
    }

    internal bool Matches(IReadOnlyList<RenderPassMetadata> sourcePasses)
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
