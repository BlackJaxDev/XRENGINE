namespace XREngine.Rendering.Compute;

/// <summary>Why the scene requested a full GPU-BVH rebuild.</summary>
public enum GpuBvhRebuildReason : uint
{
    None = 0,
    InitialOrUnavailable = 1,
    TopologyChanged = 2,
    NormalizationDomainEscaped = 3,
    PeriodicQualityCeiling = 4,
}
