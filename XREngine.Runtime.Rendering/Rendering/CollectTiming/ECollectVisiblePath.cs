namespace XREngine.Rendering;

/// <summary>
/// Identifies the collection path that a view uses. <see cref="CollectVisibleStageTimings"/> keeps one path time
/// for each view kind and path.
/// </summary>
internal enum ECollectVisiblePath
{
    /// <summary>The GPU-scene path, which visits every registered renderable on the CPU.</summary>
    GpuCollection = 0,

    /// <summary>The CPU culling-tree path.</summary>
    CpuCollection,

    /// <summary>The exact masked main-family path. A failed attempt records time with no rows.</summary>
    MaskedFamily,

    /// <summary>The backend-ready frame package preparation after the collection.</summary>
    PackagePrepare,

    /// <summary>The number of paths. This value is not a path.</summary>
    Count,
}
