namespace XREngine.Rendering;

/// <summary>
/// Delayed aggregate of nonresident and stale resource references.
/// </summary>
public readonly record struct AdvancedResourceResidencySnapshot(
    ulong FrameId,
    ulong TextureFallbacks,
    ulong SamplerFallbacks,
    ulong StaleTextureReferences,
    ulong StaleSamplerReferences);
