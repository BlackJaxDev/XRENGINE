namespace XREngine.Rendering.Compute;

/// <summary>Defines the deterministic work partition used by root-down scene-BVH culling.</summary>
public static class GpuBvhCullingDispatch
{
    /// <summary>Target upper bound for commands assigned to one traversal workgroup.</summary>
    public const uint TargetCommandsPerWorkgroup = 512u;

    /// <summary>
    /// Maximum power-of-two partition count. This keeps dispatch dimensions
    /// portable while exposing enough independent subtrees to occupy the GPU.
    /// </summary>
    public const uint MaxWorkgroupCount = 256u;

    /// <summary>
    /// Returns a power-of-two workgroup count. The shader uses the workgroup
    /// identifier as a binary root-descent path, so non-power-of-two counts
    /// would not form a disjoint cover of the tree.
    /// </summary>
    public static uint CalculateWorkgroupCount(uint commandCount)
    {
        if (commandCount == 0u)
            return 1u;

        uint desired = Math.Min(
            MaxWorkgroupCount,
            1u + (commandCount - 1u) / TargetCommandsPerWorkgroup);
        uint workgroups = 1u;
        while (workgroups < desired)
            workgroups <<= 1;
        return workgroups;
    }
}
