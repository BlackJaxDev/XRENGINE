namespace XREngine.Rendering;

/// <summary>
/// Immutable global ranges cooked for one topology generation.
/// </summary>
public readonly record struct AdvancedGpuDeformationMeshSlice(
    uint SourceVertexOffset,
    uint BoneInfluenceOffset,
    uint BlendshapeRangeOffset,
    uint VertexCount,
    uint BlendshapeCount,
    uint TopologyGeneration);
