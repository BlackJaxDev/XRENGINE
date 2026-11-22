using System.Runtime.InteropServices;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The 32-byte header at the start of each geometry page. The shaders read the section offsets
/// from it and check every page-local offset of a cluster against them.
/// </summary>
/// <param name="Magic">Always <see cref="MeshletPageFormat.Magic"/>.</param>
/// <param name="FormatVersion">Always <see cref="MeshletPageFormat.Version"/>.</param>
/// <param name="Flags">The kind of page (static or skinned topology) and whether it is a root page.</param>
/// <param name="ClusterCount">The clusters of the page.</param>
/// <param name="VertexCount">The page-local vertices (zero for a skinned topology page).</param>
/// <param name="FirstCluster">The geometry-local index of the first cluster of the page.</param>
/// <param name="DescriptorOffset">The byte offset of the descriptor section.</param>
/// <param name="VertexIndexOffset">The byte offset of the vertex index section.</param>
/// <param name="TriangleOffset">The byte offset of the triangle section.</param>
/// <param name="VertexOffset">The byte offset of the vertex section (the end of the triangle section for a skinned topology page).</param>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct MeshletPageHeader(
    uint Magic,
    ushort FormatVersion,
    EMeshletPageFlags Flags,
    ushort ClusterCount,
    ushort VertexCount,
    uint FirstCluster,
    uint DescriptorOffset,
    uint VertexIndexOffset,
    uint TriangleOffset,
    uint VertexOffset)
{
    /// <summary>Reads the header from the start of a page image.</summary>
    /// <exception cref="InvalidDataException">The image is too short or does not start with a valid header.</exception>
    public static MeshletPageHeader Read(ReadOnlySpan<byte> page)
    {
        if (page.Length < MeshletPageFormat.HeaderBytes)
            throw new InvalidDataException("A geometry page is shorter than its header.");
        MeshletPageHeader header = MemoryMarshal.Read<MeshletPageHeader>(page);
        if (header.Magic != MeshletPageFormat.Magic || header.FormatVersion != MeshletPageFormat.Version)
            throw new InvalidDataException("A geometry page does not start with a valid header.");
        return header;
    }

    /// <summary>Writes the header to the start of a page image.</summary>
    public void Write(Span<byte> page)
    {
        MeshletPageHeader header = this;
        MemoryMarshal.Write(page, in header);
    }
}
