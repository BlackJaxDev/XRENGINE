namespace XREngine.Rendering.Compute;

/// <summary>
/// Native content-reuse status of each output page buffer, or <see langword="null"/> when the
/// buffer does not exist or the producer renderer has no reuse capability.
/// </summary>
public readonly record struct PhysicsChainGpuOutputPageNativeReuse(
    EGpuBufferContentReuseStatus? BoundsAtlas,
    EGpuBufferContentReuseStatus? SlotMetadata,
    EGpuBufferContentReuseStatus? CurrentPalette,
    EGpuBufferContentReuseStatus? PreviousPalette);
