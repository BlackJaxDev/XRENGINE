namespace XREngine.Rendering.Compute;

/// <summary>
/// Cumulative render-thread milliseconds of the parts of the bounds and simulation stages of
/// the GPU physics-chain dispatcher. Each value is included in its parent stage in
/// <see cref="PhysicsChainDispatcherStageTimings"/>.
/// </summary>
public readonly record struct PhysicsChainDispatcherSubStageTimings(
    double BoundsRoutesMilliseconds,
    double BoundsRowsMilliseconds,
    double BoundsSceneCopyMilliseconds,
    double SimTreeParamsMilliseconds,
    double SimActiveWorkMilliseconds,
    double SimUploadRangesMilliseconds,
    double SimUploadCollidersMilliseconds,
    double SimUploadOtherMilliseconds);
