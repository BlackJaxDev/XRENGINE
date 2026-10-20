using XREngine.Rendering.Compute;

namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    /// <summary>Records the immutable source that the open aggregate frame will consume.</summary>
    internal void SetPhysicsOutputSource(GPUPhysicsChainDispatcher? dispatcher,
        PhysicsChainGpuOutputPageToken token)
    {
        ThrowIfFrameClosed();
        _pendingPhysicsSource = new(_frameId, dispatcher, token);
    }

    /// <summary>Checks whether retained previous vertices fit the captured physics bound.</summary>
    internal bool HasCompatiblePhysicsHistory(GPUPhysicsChainDispatcher dispatcher,
        in PhysicsChainGpuOutputPageLease page, bool previousPaletteValid)
    {
        if (!_previousOutputValid)
            return false;
        AdvancedPhysicsOutputSource prior = _slotPhysicsSources[_previousFrameSlot];
        if (prior.ProducedFrameId + 1UL != _frameId ||
            !ReferenceEquals(prior.Dispatcher, dispatcher) || !prior.PageToken.IsValid)
            return false;
        return prior.PageToken == page.Token ||
            (previousPaletteValid && prior.PageToken == page.PreviousPaletteSourceToken);
    }
}
