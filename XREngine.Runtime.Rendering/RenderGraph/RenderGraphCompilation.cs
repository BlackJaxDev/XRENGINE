namespace XREngine.Rendering.RenderGraph;

/// <summary>
/// The backend-neutral result of render graph compilation: pass order, batches, and synchronization edges.
/// Backends add physical planning on top of it. Tools use it to show the planned order without a backend.
/// </summary>
public sealed class RenderGraphCompilation
{
    /// <summary>Gets the compilation of an empty graph.</summary>
    public static RenderGraphCompilation Empty { get; } = new(
        [],
        new Dictionary<int, int>(),
        [],
        RenderGraphSynchronizationInfo.Empty,
        int.MaxValue);

    /// <summary>Initializes a compilation result.</summary>
    public RenderGraphCompilation(
        IReadOnlyList<RenderPassMetadata> orderedPasses,
        IReadOnlyDictionary<int, int> passOrder,
        IReadOnlyList<RenderGraphPassBatch> batches,
        RenderGraphSynchronizationInfo synchronization,
        int screenSpaceUiPassOrder)
    {
        OrderedPasses = orderedPasses;
        PassOrder = passOrder;
        Batches = batches;
        Synchronization = synchronization;
        ScreenSpaceUiPassOrder = screenSpaceUiPassOrder;
    }

    /// <summary>Gets the passes in execution order.</summary>
    public IReadOnlyList<RenderPassMetadata> OrderedPasses { get; }

    /// <summary>Gets the execution position of each pass index.</summary>
    public IReadOnlyDictionary<int, int> PassOrder { get; }

    /// <summary>Gets the batches in execution order.</summary>
    public IReadOnlyList<RenderGraphPassBatch> Batches { get; }

    /// <summary>Gets the synchronization edges, ordered by consumer execution position.</summary>
    public RenderGraphSynchronizationInfo Synchronization { get; }

    /// <summary>Gets the execution position of the screen-space UI pass, or <see cref="int.MaxValue"/> when the graph has none.</summary>
    public int ScreenSpaceUiPassOrder { get; }
}
