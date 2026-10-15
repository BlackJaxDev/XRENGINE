namespace XREngine.Rendering.Compute;

/// <summary>
/// Reads the renderer output states of one retained chain output page without the page lock.
/// Open it with <see cref="GPUPhysicsChainDispatcher.TryOpenRendererStateReader"/> after the page
/// is retained, and stop using it before the page is released: a retained page is never rewritten.
/// </summary>
public readonly struct PhysicsChainGpuRendererStateReader
{
    internal PhysicsChainGpuRendererStateReader(
        GPUPhysicsChainDispatcher dispatcher,
        PhysicsChainGpuRendererOutputState[] statesBySlot,
        List<PhysicsChainMorphWeight> morphWeights)
    {
        Dispatcher = dispatcher;
        StatesBySlot = statesBySlot;
        MorphWeights = morphWeights;
    }

    internal GPUPhysicsChainDispatcher? Dispatcher { get; }

    /// <summary>The page's renderer states, indexed by bounds slot.</summary>
    internal PhysicsChainGpuRendererOutputState[]? StatesBySlot { get; }

    internal List<PhysicsChainMorphWeight>? MorphWeights { get; }

    /// <summary>True when the reader belongs to an open page.</summary>
    public bool IsOpen => StatesBySlot is not null;
}
