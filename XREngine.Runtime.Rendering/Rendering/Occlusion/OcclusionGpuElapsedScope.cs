namespace XREngine.Rendering.Occlusion;

/// <summary>Allocation-free ownership token for one renderer-owned timestamp query pair.</summary>
public readonly struct OcclusionGpuElapsedScope : IDisposable
{
    private readonly OcclusionGpuElapsedTiming? _owner;
    private readonly OcclusionGpuElapsedTiming.RendererTimingState? _state;
    private readonly EOcclusionGpuElapsedStage _stage;
    private readonly ulong _frameId;
    private readonly int _pairSlot;
    private readonly ulong _generation;

    internal OcclusionGpuElapsedScope(OcclusionGpuElapsedTiming owner, OcclusionGpuElapsedTiming.RendererTimingState state, EOcclusionGpuElapsedStage stage, ulong frameId, int pairSlot, ulong generation)
        => (_owner, _state, _stage, _frameId, _pairSlot, _generation) = (owner, state, stage, frameId, pairSlot, generation);

    public void Dispose()
    {
        if (_owner is not null && _state is not null)
            _owner.End(_state, _stage, _frameId, _pairSlot, _generation);
    }
}
