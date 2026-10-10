using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>Exposes the buffers of one retained chain output page.</summary>
public readonly record struct PhysicsChainGpuOutputPageLease(
    PhysicsChainGpuOutputPageToken Token,
    XRDataBuffer BoundsAtlasBuffer,
    XRDataBuffer SlotMetadataBuffer,
    XRDataBuffer? CurrentPaletteBuffer,
    XRDataBuffer? PreviousPaletteBuffer,
    uint ProducerEpoch,
    uint PageGeneration,
    long RenderFrame)
{
    /// <summary>Identifies the exact page copied into the previous palette.</summary>
    public PhysicsChainGpuOutputPageToken PreviousPaletteSourceToken { get; init; }
}
