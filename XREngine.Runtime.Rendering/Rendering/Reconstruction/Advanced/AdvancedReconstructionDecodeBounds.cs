namespace XREngine.Rendering;

/// <summary>
/// Shader-side table and geometry bounds used by diagnostic validation.
/// </summary>
public readonly record struct AdvancedReconstructionDecodeBounds(
    uint DrawCount,
    uint InstanceCount,
    uint GeometryCount,
    uint MaterialCount,
    uint ShadingKernelCount,
    uint TransformCount,
    uint DeformationCount,
    uint ViewCount,
    uint StaticVertexCount,
    uint PreSkinnedCurrentVertexCount,
    uint PreSkinnedPreviousVertexCount,
    uint IndexCount,
    uint MeshletCount);
