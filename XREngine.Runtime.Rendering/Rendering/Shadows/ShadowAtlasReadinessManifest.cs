namespace XREngine.Rendering.Shadows;

/// <summary>
/// Immutable, scalar description of the published atlas plan needed by one
/// terminal output. It intentionally contains no mutable plan buffers and
/// therefore can be captured by a frame plan without retaining atlas locks.
/// </summary>
public readonly record struct ShadowAtlasReadinessManifest(
    ShadowAtlasReadinessContract Contract,
    ulong AtlasFrameId,
    ulong RenderPlanId,
    int RequiredTileCount,
    int ResidentGpuFallbackTileCount,
    int UnavailableTileCount,
    int RequestQueueOverflowCount,
    EShadowAtlasReadinessSelection Selection)
{
    public bool IsSatisfied => Selection is EShadowAtlasReadinessSelection.NotRequired or
        EShadowAtlasReadinessSelection.ExactCurrentContent or
        EShadowAtlasReadinessSelection.DeclaredResidentGpuFallback;

    public bool RequiresExactRender => Selection == EShadowAtlasReadinessSelection.ExactCurrentContent &&
        RequiredTileCount > 0;
}
