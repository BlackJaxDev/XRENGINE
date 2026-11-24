using System.Runtime.InteropServices;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The location of one page blob in a page file (16 bytes). The CPU uses it to read and decode a
/// page. It is not uploaded to the GPU.
/// </summary>
/// <param name="FileOffsetBlocks">The offset of the blob from the start of the file, in units of <see cref="MeshletPageFormat.FileAlignment"/>.</param>
/// <param name="StoredBytes">The bytes of the blob in the file. When it equals <paramref name="UncompressedBytes"/>, the blob is stored without compression.</param>
/// <param name="UncompressedBytes">The bytes of the decoded page image.</param>
/// <param name="ContentHash">The 32-bit xxHash of the decoded page image. A reader rejects a page whose hash is different.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshletPageFileEntry(
    uint FileOffsetBlocks,
    uint StoredBytes,
    uint UncompressedBytes,
    uint ContentHash)
{
    /// <summary>True when the blob is stored without compression.</summary>
    public bool IsStoredRaw => StoredBytes == UncompressedBytes;

    /// <summary>The byte offset of the blob from the start of the file.</summary>
    public long FileOffset => (long)FileOffsetBlocks * MeshletPageFormat.FileAlignment;
}
