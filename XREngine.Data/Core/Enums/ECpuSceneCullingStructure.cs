namespace XREngine;

/// <summary>
/// Selects the CPU spatial structure used for render visibility when GPU dispatch is disabled.
/// </summary>
public enum ECpuSceneCullingStructure
{
    /// <summary>
    /// Use the legacy octree.
    /// </summary>
    Octree = 0,

    /// <summary>
    /// Use a CPU bounding-volume hierarchy.
    /// </summary>
    Bvh = 1,
}
