namespace XREngine.Rendering.Compute;

/// <summary>
/// GPU scan implementation selected for physics-chain active-work compaction.
/// </summary>
public enum PhysicsChainActiveWorkScanMode
{
    /// <summary>
    /// Portable shared-memory workgroup scan. This is an explicit GPU fallback;
    /// it never builds an active list on the CPU.
    /// </summary>
    PortableWorkgroup,

    /// <summary>
    /// Subgroup-arithmetic scan used when the backend reports native support.
    /// </summary>
    SubgroupArithmetic,
}
