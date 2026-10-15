namespace XREngine.Rendering;

/// <summary>
/// Cumulative times of one view kind in the collect phase.
/// </summary>
/// <remarks>
/// The view time minus the path times is the view setup: the camera, the light intersections, the render state
/// and the screen-space UI collection. A nested collection, for example a mirror, does not open a second path.
/// Its time is part of the outer path.
/// </remarks>
/// <param name="Calls">The number of view collections.</param>
/// <param name="Rows">The visible renderables that the collection paths of this view kind accepted.</param>
/// <param name="TotalMilliseconds">The full view collection time.</param>
/// <param name="GpuCollectionMilliseconds">The GPU-scene path.</param>
/// <param name="CpuCollectionMilliseconds">The CPU culling-tree path.</param>
/// <param name="MaskedFamilyMilliseconds">The exact masked main-family path, with failed attempts.</param>
/// <param name="PackagePrepareMilliseconds">The backend-ready frame package preparation.</param>
public readonly record struct CollectVisibleViewStageTimings(
    long Calls,
    long Rows,
    double TotalMilliseconds,
    double GpuCollectionMilliseconds,
    double CpuCollectionMilliseconds,
    double MaskedFamilyMilliseconds,
    double PackagePrepareMilliseconds);
