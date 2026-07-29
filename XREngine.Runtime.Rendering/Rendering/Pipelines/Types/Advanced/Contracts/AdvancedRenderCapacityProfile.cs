namespace XREngine.Rendering;

/// <summary>
/// Immutable reservations whose growth changes the physical advanced resource layout.
/// Runtime GPU counts are bounded contents of these reservations and are not capacities.
/// </summary>
public readonly record struct AdvancedRenderCapacityProfile(
    uint DrawRecords,
    uint InstanceRecords,
    uint GeometryRecords,
    uint MaterialRecords,
    uint LightRecords,
    uint DecalRecords,
    uint DeformedVertices,
    uint VisiblePrimitives,
    uint MaterialWorkItems,
    uint Froxels,
    uint TransparencyNodes)
{
    /// <summary>
    /// Capacity profile used while all production stages remain capability-gated.
    /// </summary>
    public static AdvancedRenderCapacityProfile Inactive => default;
}
