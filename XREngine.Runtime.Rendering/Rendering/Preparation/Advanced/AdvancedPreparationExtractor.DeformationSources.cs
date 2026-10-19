using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

public sealed partial class AdvancedPreparationExtractor
{
    private readonly ConditionalWeakTable<GPUScene, AdvancedDeformationSourceSet> _deformationSourceSets = new();

    /// <summary>Exact mesh-input generation selected for the last preparation attempt.</summary>
    public ulong DeformationSourceGeneration { get; private set; }

    /// <summary>Unique mesh count in the last selected deformation source set.</summary>
    public int DeformationSourceMeshCount { get; private set; }

    public EAdvancedDeformationSourceChange DeformationSourceChanges { get; private set; }
    public string? DeformationSourceChangedMesh { get; private set; }

    private ulong CaptureDeformationSourceGeneration(
        GPUScene scene, AdvancedGpuScenePublicationSnapshot publication, int drawCount)
    {
        if (!_deformationSourceSets.TryGetValue(scene, out AdvancedDeformationSourceSet? sources))
        {
            // Storage is bounded and allocated only when a scene first uses this extractor.
            sources = new AdvancedDeformationSourceSet(_options.MaximumDraws);
            _deformationSourceSets.Add(scene, sources);
        }
        EAdvancedDeformationMeshPreparationPolicy policy = AdvancedGpuDeformationResources.ResolveMeshPreparationPolicy();
        sources.Begin();
        for (int index = 0; index < drawCount; index++)
        {
            if (!RequiresAggregateDeformation(publication.Submission.Records[index].Flags, policy))
                continue;
            AdvancedManagedDeformationSourceRow source = publication.Submission.DeformationSources[index];
            if (source.Renderer is null || source.Mesh is not { VertexCount: > 0 } mesh)
                throw new InvalidOperationException("A deformation source requires a captured mesh and renderer.");
            if (!publication.Geometry.TryGet(publication.Submission.Records[index].Geometry, out AdvancedGeometryRecord geometry))
                throw new InvalidOperationException("A deformation source requires a captured geometry record.");
            AdvancedDeformationSourceWitness witness = AdvancedDeformationSourceWitness.Capture(mesh, geometry.MeshletCount, policy);
            sources.Visit(mesh, in witness);
        }
        DeformationSourceGeneration = sources.Complete();
        DeformationSourceMeshCount = sources.Count;
        DeformationSourceChanges = sources.LastChanges;
        DeformationSourceChangedMesh = sources.LastChangedMeshName;
        return DeformationSourceGeneration;
    }

    private static bool RequiresAggregateDeformation(uint flags, EAdvancedDeformationMeshPreparationPolicy policy)
        => policy == EAdvancedDeformationMeshPreparationPolicy.CanonicalSparseMorphs
            ? ((flags & (uint)GPUIndirectRenderFlags.Skinned) != 0 && RuntimeEngine.Rendering.Settings.AllowSkinning) ||
              ((flags & (uint)GPUIndirectRenderFlags.BlendShapes) != 0 && RuntimeEngine.Rendering.Settings.AllowBlendshapes)
            : (flags & (uint)GPUIndirectRenderFlags.Skinned) != 0;
}
