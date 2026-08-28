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
        DatabaseEpoch = database.DatabaseEpoch;
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
        GeometryPayloads = new AdvancedGeometryPublicationSnapshot(scene.Geometry);
        MaterialPayloads = materials.CreatePublicationSnapshot();
        Materials = MaterialPayloads.Materials;
        Kernels = MaterialPayloads.Kernels;
        Layouts = MaterialPayloads.Layouts;
        ResourcePayloads = new AdvancedGpuResourcePublicationSnapshot(resources);
        Textures = ResourcePayloads.Textures;
        Samplers = ResourcePayloads.Samplers;
        GlobalResources = new AdvancedGlobalSceneResourcePublicationSnapshot(resources);
    }

    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedDrawRecord> Draws { get; }

    /// <summary>Owner identity required before a Vulkan slot may reuse a resident image.</summary>
    public ulong DatabaseEpoch { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedInstanceRecord> Instances { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedTransformRecord> Transforms { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedDeformationRecord> Deformations { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedRenderStateRecord> RenderStates { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedEditorIdentityRecord> EditorIdentities { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedGeometryRecord> Geometry { get; }
    /// <summary>Exact immutable geometry stream layouts retained with this publication.</summary>
    public AdvancedGeometryPublicationSnapshot GeometryPayloads { get; }
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
    /// <summary>
    /// Immutable native-shading lights, shadows, probes, environments, decals,
    /// and GI resources captured with this publication.
    /// </summary>
    public AdvancedGlobalSceneResourcePublicationSnapshot GlobalResources { get; }

    /// <summary>Resource-table generations captured when this ring entry was sealed.</summary>
    public AdvancedGlobalResourceDatabaseGenerations ResourceGenerations
        => ResourcePayloads.Generations;

    internal bool TryCaptureResourceTableState(
        ulong sequence,
        in AdvancedGlobalResourceDatabaseGenerations generations)
        => ResourcePayloads.TryCaptureTableState(sequence, generations) &&
           GlobalResources.TryCaptureTableState(sequence, generations);
}
