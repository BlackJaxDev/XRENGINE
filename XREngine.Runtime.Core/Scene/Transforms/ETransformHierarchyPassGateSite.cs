namespace XREngine.Scene.Transforms;

/// <summary>
/// Identifies the code path that enters the pass gate of a <see cref="TransformHierarchyStore"/>.
/// The pass-gate telemetry keeps one counter block for each value.
/// </summary>
internal enum ETransformHierarchyPassGateSite
{
    /// <summary>The update-thread propagation pass in <see cref="TransformHierarchyStore.Process"/>.</summary>
    ProcessPass,

    /// <summary>The render-matrix publication that runs before visibility collection.</summary>
    PublishPreCollect,

    /// <summary>The render-matrix publication that runs in the serial buffer swap.</summary>
    PublishSwap,

    /// <summary>A render-matrix publication from the public parameterless entry, for example a benchmark or a test.</summary>
    PublishOther,

    /// <summary>The subtree-range rebuild in <see cref="TransformHierarchyStore.FlushHierarchy"/>.</summary>
    FlushHierarchy,

    /// <summary>The number of sites. This value is not a site.</summary>
    Count,
}
