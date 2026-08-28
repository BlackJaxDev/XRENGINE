namespace XREngine.Rendering.Commands;

/// <summary>
/// Ring-owned immutable table images for one sealed canonical-scene publication.
/// The contained spans remain valid while the corresponding publication lease is
/// retained; the next reuse of the ring entry replaces their contents.
/// </summary>
public sealed class AdvancedGpuScenePublicationSnapshot
{
    internal AdvancedGpuScenePublicationSnapshot(AdvancedSharedGpuSceneDatabase database)
    {
        AdvancedGpuSceneDatabase scene = database.Scene;
        AdvancedMaterialDatabase materials = database.Materials;
        AdvancedGlobalResourceDatabase resources = database.Resources;
        Draws = scene.Draws.CreatePublicationSnapshot(includeRecordImage: true);
        Instances = scene.Instances.CreatePublicationSnapshot();
        Transforms = scene.Transforms.CreatePublicationSnapshot();
        Deformations = scene.Deformations.CreatePublicationSnapshot();
        RenderStates = scene.RenderStates.CreatePublicationSnapshot();
        EditorIdentities = scene.EditorIdentities.CreatePublicationSnapshot();
        Geometry = scene.Geometry.Records.CreatePublicationSnapshot();
        MaterialPayloads = materials.CreatePublicationSnapshot();
        Materials = MaterialPayloads.Materials;
        Kernels = MaterialPayloads.Kernels;
        Layouts = MaterialPayloads.Layouts;
        ResourcePayloads = new AdvancedGpuResourcePublicationSnapshot(resources);
        Textures = ResourcePayloads.Textures;
        Samplers = ResourcePayloads.Samplers;
    }

    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedDrawRecord> Draws { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedInstanceRecord> Instances { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedTransformRecord> Transforms { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedDeformationRecord> Deformations { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedRenderStateRecord> RenderStates { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedEditorIdentityRecord> EditorIdentities { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedGeometryRecord> Geometry { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedMaterialRecord> Materials { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedShadingKernelRecord> Kernels { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedMaterialLayoutRecord> Layouts { get; }
    /// <summary>
    /// Immutable packed material constants, texture/sampler references, and
    /// material-to-layout handles captured with the record-table publication.
    /// </summary>
    public AdvancedMaterialPublicationSnapshot MaterialPayloads { get; }
    /// <summary>
    /// Immutable logical resource records, lookups, and strong source closure
    /// captured for this publication.
    /// </summary>
    public AdvancedGpuResourcePublicationSnapshot ResourcePayloads { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedTextureRecord> Textures { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedSamplerRecord> Samplers { get; }

    /// <summary>Resource-table generations captured when this ring entry was sealed.</summary>
    public AdvancedGlobalResourceDatabaseGenerations ResourceGenerations
        => ResourcePayloads.Generations;

    internal bool TryCaptureResourceTableState(
        ulong sequence,
        in AdvancedGlobalResourceDatabaseGenerations generations)
        => ResourcePayloads.TryCaptureTableState(sequence, generations);
}
