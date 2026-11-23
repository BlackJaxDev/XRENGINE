namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanAdvancedVisibilityGeometrySlices(
    VulkanFrameDataSlice StaticVertices,
    VulkanVisibilityPreparedVertexSource CurrentVertices,
    VulkanVisibilityPreparedVertexSource PreviousVertices,
    VulkanFrameDataSlice Indices,
    VulkanFrameDataSlice MeshletDescriptors,
    VulkanFrameDataSlice MeshletVertexIndices,
    VulkanFrameDataSlice MeshletTriangleWords,
    VulkanFrameDataSlice DeformationOverlay,
    VulkanFrameDataSlice GeometryPagePool = default,
    VulkanFrameDataSlice GeometryPageTable = default)
{
    internal bool HasValidSources
        => StaticVertices.IsValid && CurrentVertices.IsValid &&
           PreviousVertices.IsValid && Indices.IsValid &&
           MeshletDescriptors.IsValid && MeshletVertexIndices.IsValid &&
           MeshletTriangleWords.IsValid &&
           GeometryPagePool.IsValid && GeometryPageTable.IsValid;

    internal bool IsValid => HasValidSources && DeformationOverlay.IsValid;

    internal bool MatchesSources(
        in VulkanAdvancedVisibilityGeometrySlices other)
        => StaticVertices == other.StaticVertices &&
           CurrentVertices == other.CurrentVertices &&
           PreviousVertices == other.PreviousVertices &&
           Indices == other.Indices &&
           MeshletDescriptors == other.MeshletDescriptors &&
           MeshletVertexIndices == other.MeshletVertexIndices &&
           MeshletTriangleWords == other.MeshletTriangleWords &&
           GeometryPagePool == other.GeometryPagePool &&
           GeometryPageTable == other.GeometryPageTable;
}
