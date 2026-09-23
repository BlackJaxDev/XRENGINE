namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable native bindings for the exact geometry image used by one advanced
/// scene publication. The backing buffers are resident across frame slots;
/// each slice exposes only the prefix captured by that publication.
/// </summary>
internal readonly record struct VulkanAdvancedGeometryPublication(
    VulkanFrameDataSlice StaticVertices,
    VulkanFrameDataSlice Indices,
    VulkanFrameDataSlice PreSkinnedCurrent,
    VulkanFrameDataSlice PreSkinnedPrevious,
    VulkanFrameDataSlice MeshletDescriptors,
    VulkanFrameDataSlice MeshletVertexIndices,
    VulkanFrameDataSlice MeshletTriangleWords,
    byte NewlyPinnedStreams)
{
    internal const byte StaticVerticesPin = 1 << 0;
    internal const byte IndicesPin = 1 << 1;
    internal const byte PreSkinnedCurrentPin = 1 << 2;
    internal const byte PreSkinnedPreviousPin = 1 << 3;
    internal const byte MeshletDescriptorsPin = 1 << 4;
    internal const byte MeshletVertexIndicesPin = 1 << 5;
    internal const byte MeshletTriangleWordsPin = 1 << 6;

    internal bool IsValid
        => StaticVertices.IsValid && Indices.IsValid &&
           PreSkinnedCurrent.IsValid && PreSkinnedPrevious.IsValid &&
           MeshletDescriptors.IsValid && MeshletVertexIndices.IsValid &&
           MeshletTriangleWords.IsValid;
}
