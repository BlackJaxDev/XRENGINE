namespace XREngine.Rendering.Commands;

/// <summary>
/// Retained per-owner dirty ranges and independent owner generations at a
/// publication boundary. This is the renderer-neutral mutation contract; it
/// intentionally names no backend upload or descriptor resource.
/// </summary>
public sealed class AdvancedCanonicalPublicationMutationSnapshot
{
    private readonly AdvancedGpuDirtyOwnerRange[] _ranges = new AdvancedGpuDirtyOwnerRange[18];

    public ulong Sequence { get; private set; }
    public int Count { get; private set; }
    public ReadOnlySpan<AdvancedGpuDirtyOwnerRange> Ranges => _ranges.AsSpan(0, Count);

    internal void Capture(ulong sequence, AdvancedSharedGpuSceneDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Sequence = sequence;
        Count = 0;
        AdvancedGpuSceneDatabase scene = database.Scene;
        Capture(EAdvancedGpuRecordOwner.Draw, scene.Draws);
        Capture(EAdvancedGpuRecordOwner.Instance, scene.Instances);
        Capture(EAdvancedGpuRecordOwner.Transform, scene.Transforms);
        Capture(EAdvancedGpuRecordOwner.Deformation, scene.Deformations);
        Capture(EAdvancedGpuRecordOwner.RenderState, scene.RenderStates);
        Capture(EAdvancedGpuRecordOwner.EditorIdentity, scene.EditorIdentities);
        Capture(EAdvancedGpuRecordOwner.Geometry, scene.Geometry.Records);
        Capture(EAdvancedGpuRecordOwner.Material, database.Materials.Materials);
        Capture(EAdvancedGpuRecordOwner.ShadingKernel, database.Materials.Kernels);
        Capture(EAdvancedGpuRecordOwner.MaterialLayout, database.Materials.Layouts);
        Capture(EAdvancedGpuRecordOwner.Texture, database.Resources.Textures);
        Capture(EAdvancedGpuRecordOwner.Sampler, database.Resources.Samplers);
        Capture(EAdvancedGpuRecordOwner.Light, database.Resources.Lights);
        Capture(EAdvancedGpuRecordOwner.Shadow, database.Resources.Shadows);
        Capture(EAdvancedGpuRecordOwner.Probe, database.Resources.Probes);
        Capture(EAdvancedGpuRecordOwner.Environment, database.Resources.Environments);
        Capture(EAdvancedGpuRecordOwner.Decal, database.Resources.Decals);
        Capture(EAdvancedGpuRecordOwner.GiResource, database.Resources.GiResources);
    }

    private void Capture<T>(EAdvancedGpuRecordOwner owner, AdvancedGpuRecordTable<T> table)
        where T : unmanaged
    {
        AdvancedGpuDirtyRange range = table.DirtyRange;
        if (range.IsEmpty)
            return;
        _ranges[Count++] = new AdvancedGpuDirtyOwnerRange(owner, range, table.Generations.Content);
    }
}
