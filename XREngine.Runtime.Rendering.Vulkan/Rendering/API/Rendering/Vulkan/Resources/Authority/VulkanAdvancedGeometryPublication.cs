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

    /// <summary>
    /// The geometry page pool buffer (visibility binding 73). It is a 16-byte zero sentinel when no
    /// geometry draws from geometry pages.
    /// </summary>
    internal VulkanFrameDataSlice PagePool { get; init; }

    /// <summary>
    /// The page table buffer of this publication's page table (visibility binding 74): the pool
    /// slot of each page, or not resident.
    /// </summary>
    internal VulkanFrameDataSlice PageTable { get; init; }

    /// <summary>True when this publication added the frame-slot pin of <see cref="PageTable"/>.</summary>
    internal bool PageTableNewlyPinned { get; init; }

    internal bool IsValid
        => StaticVertices.IsValid && Indices.IsValid &&
           PreSkinnedCurrent.IsValid && PreSkinnedPrevious.IsValid &&
           MeshletDescriptors.IsValid && MeshletVertexIndices.IsValid &&
           MeshletTriangleWords.IsValid &&
           PagePool.IsValid && PageTable.IsValid;
}
