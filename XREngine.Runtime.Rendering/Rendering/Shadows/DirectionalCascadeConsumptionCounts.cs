namespace XREngine.Rendering.Shadows;

/// <summary>
/// Cumulative counts of the directional cascade collections and atlas renders.
/// </summary>
/// <remarks>
/// Each cascade source collection ends in one outcome: one of the four skip counts, a GPU package preparation, a
/// grouped collection, or the single cascade loop. The cascade view collection time is in the cascade view of
/// <see cref="CollectVisibleStageTimings"/>.
/// </remarks>
/// <param name="SourceCollectCalls">The cascade source collections. Each light collection has two sources.</param>
/// <param name="SkipDisabled">The source collections that stopped because the backend cannot render cascades.</param>
/// <param name="SkipNoCascades">The source collections that stopped because the source had no published cascade.</param>
/// <param name="SkipNoAtlasRenderRequest">The source collections that stopped because the atlas requested no cascade render.</param>
/// <param name="GpuPackages">The source collections that prepared GPU shadow frame packages instead of a CPU collection.</param>
/// <param name="GroupCollections">The grouped cascade view collections.</param>
/// <param name="GroupSkipNoCollectionRequest">The grouped collections that stopped because the atlas requested no collection.</param>
/// <param name="ViewportCollections">The single cascade view collections.</param>
/// <param name="TileRenders">The cascade atlas tile renders that succeeded.</param>
/// <param name="GroupRenders">The grouped cascade atlas renders that succeeded.</param>
/// <param name="RenderCollectNow">The cascade atlas renders that collected and swapped the light again on the render thread.</param>
public readonly record struct DirectionalCascadeConsumptionCounts(
    long SourceCollectCalls,
    long SkipDisabled,
    long SkipNoCascades,
    long SkipNoAtlasRenderRequest,
    long GpuPackages,
    long GroupCollections,
    long GroupSkipNoCollectionRequest,
    long ViewportCollections,
    long TileRenders,
    long GroupRenders,
    long RenderCollectNow);
