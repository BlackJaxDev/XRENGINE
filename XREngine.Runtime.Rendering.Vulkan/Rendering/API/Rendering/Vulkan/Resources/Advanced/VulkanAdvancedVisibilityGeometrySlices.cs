using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The canonical geometry buffers that one visibility family binds in set 1. The page pool and page
/// table hold the geometry pages. <see cref="StreamingFeedback"/> is the queue of the scene that
/// owns these buffers; the visibility runtime adds the page requests and touch bits of each
/// completed frame to it. It is not part of <see cref="MatchesSources"/>, because it never changes
/// for one scene.
/// </summary>
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
    VulkanFrameDataSlice GeometryPageTable = default,
    MeshletPageFeedbackQueue? StreamingFeedback = null)
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
