namespace XREngine.Rendering;

/// <summary>
/// Immutable global ranges cooked for one topology generation.
/// </summary>
/// <param name="HasDeformationOrder">
/// True when the influence rows of the slice store the cooked coarse-first
/// deformation order in <see cref="AdvancedSkinInfluence.DeformationOrderVertex"/>.
/// Only then can a job deform a prefix of the vertices.
/// </param>
public readonly record struct AdvancedGpuDeformationMeshSlice(
    uint SourceVertexOffset,
    uint BoneInfluenceOffset,
    uint BlendshapeRangeOffset,
    uint VertexCount,
    uint BlendshapeCount,
    uint TopologyGeneration,
    bool HasDeformationOrder = false);
