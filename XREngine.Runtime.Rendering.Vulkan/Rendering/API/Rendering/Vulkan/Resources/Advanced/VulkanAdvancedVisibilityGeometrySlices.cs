namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact frame-slot geometry publication consumed by the mesh-visibility ABI.
/// No placeholder buffers are valid: all slices must originate from the same
/// canonical scene publication and frame generation as the visibility payload.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityGeometrySlices(
    VulkanFrameDataSlice StaticVertices,
    VulkanFrameDataSlice CurrentVertices,
    VulkanFrameDataSlice PreviousVertices,
    VulkanFrameDataSlice MeshletDescriptors,
    VulkanFrameDataSlice MeshletVertexIndices,
    VulkanFrameDataSlice MeshletTriangleWords)
{
    internal bool IsValid
        => StaticVertices.IsValid && CurrentVertices.IsValid &&
           PreviousVertices.IsValid && MeshletDescriptors.IsValid &&
           MeshletVertexIndices.IsValid && MeshletTriangleWords.IsValid;
}
