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

    /// <summary>
    /// Structural reservations introduced by the visibility-buffer geometry slice.
    /// Later documents replace zero reservations as their stages become concrete.
    /// </summary>
    public static AdvancedRenderCapacityProfile VisibilityBuffer => new(
        DrawRecords: 65_536u,
        InstanceRecords: 65_536u,
        GeometryRecords: 65_536u,
        MaterialRecords: 65_536u,
        LightRecords: 0u,
        DecalRecords: 0u,
        DeformedVertices: 4_194_304u,
        VisiblePrimitives: 65_536u,
        MaterialWorkItems: 0u,
        Froxels: 0u,
        TransparencyNodes: 0u);

    /// <summary>
    /// Reconstruction is on demand and introduces no materialized surface rows.
    /// </summary>
    public static AdvancedRenderCapacityProfile AttributeReconstruction
        => VisibilityBuffer;
}
