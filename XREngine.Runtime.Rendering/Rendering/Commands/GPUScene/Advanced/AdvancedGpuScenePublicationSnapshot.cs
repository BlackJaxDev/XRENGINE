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
        Draws = scene.Draws.CreatePublicationSnapshot();
        Instances = scene.Instances.CreatePublicationSnapshot();
        Transforms = scene.Transforms.CreatePublicationSnapshot();
        Deformations = scene.Deformations.CreatePublicationSnapshot();
        RenderStates = scene.RenderStates.CreatePublicationSnapshot();
        EditorIdentities = scene.EditorIdentities.CreatePublicationSnapshot();
        Geometry = scene.Geometry.Records.CreatePublicationSnapshot();
        Materials = materials.Materials.CreatePublicationSnapshot();
        Kernels = materials.Kernels.CreatePublicationSnapshot();
        Layouts = materials.Layouts.CreatePublicationSnapshot();
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
}
