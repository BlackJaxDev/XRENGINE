namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable offsets for every table packed into the shared handle lookup buffer.
/// Capacities, rather than live counts, keep offsets stable across ordinary frames.
/// </summary>
public readonly record struct AdvancedGpuSceneLookupLayout(
    AdvancedGpuLookupSegment Draws,
    AdvancedGpuLookupSegment Instances,
    AdvancedGpuLookupSegment Transforms,
    AdvancedGpuLookupSegment Deformations,
    AdvancedGpuLookupSegment RenderStates,
    AdvancedGpuLookupSegment EditorIdentities,
    AdvancedGpuLookupSegment Geometry,
    AdvancedGpuLookupSegment Materials,
    AdvancedGpuLookupSegment ShadingKernels,
    AdvancedGpuLookupSegment MaterialLayouts,
    uint TotalCount)
{
    public static AdvancedGpuSceneLookupLayout Create(
        AdvancedGpuSceneDatabase scene,
        AdvancedMaterialDatabase materials)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(materials);

        uint offset = 0u;
        AdvancedGpuLookupSegment draws = Append(scene.Draws.Capacity, ref offset);
        AdvancedGpuLookupSegment instances = Append(scene.Instances.Capacity, ref offset);
        AdvancedGpuLookupSegment transforms = Append(scene.Transforms.Capacity, ref offset);
        AdvancedGpuLookupSegment deformations = Append(scene.Deformations.Capacity, ref offset);
        AdvancedGpuLookupSegment renderStates = Append(scene.RenderStates.Capacity, ref offset);
        AdvancedGpuLookupSegment editorIdentities = Append(scene.EditorIdentities.Capacity, ref offset);
        AdvancedGpuLookupSegment geometry = Append(scene.Geometry.Records.Capacity, ref offset);
        AdvancedGpuLookupSegment materialRows = Append(materials.Materials.Capacity, ref offset);
        AdvancedGpuLookupSegment kernels = Append(materials.Kernels.Capacity, ref offset);
        AdvancedGpuLookupSegment layouts = Append(materials.Layouts.Capacity, ref offset);

        return new AdvancedGpuSceneLookupLayout(
            draws,
            instances,
            transforms,
            deformations,
            renderStates,
            editorIdentities,
            geometry,
            materialRows,
            kernels,
            layouts,
            offset);
    }

    private static AdvancedGpuLookupSegment Append(
        uint recordCapacity,
        ref uint offset)
    {
        uint count = checked(recordCapacity + 1u);
        AdvancedGpuLookupSegment segment = new(offset, count);
        offset = checked(offset + count);
        return segment;
    }
}
