namespace XREngine.Rendering.Shadows;

/// <summary>Completion telemetry for a captured shadow readiness manifest.</summary>
public readonly record struct ShadowAtlasReadinessResult(
    ShadowAtlasReadinessManifest Manifest,
    int RenderedTileCount,
    int FailedTileCount,
    EShadowAtlasReadinessSelection Selection)
{
    public bool IsSatisfied => FailedTileCount == 0 && Selection is
        EShadowAtlasReadinessSelection.NotRequired or
        EShadowAtlasReadinessSelection.ExactCurrentContent or
        EShadowAtlasReadinessSelection.DeclaredResidentGpuFallback;
}
