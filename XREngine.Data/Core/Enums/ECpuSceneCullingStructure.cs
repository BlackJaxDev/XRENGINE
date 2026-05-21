namespace XREngine;

/// <summary>
/// Selects the CPU spatial structure used for render visibility when GPU dispatch is disabled.
/// </summary>
public enum ECpuSceneCullingStructure
{
    Octree = 0,
    Bvh = 1,
}
