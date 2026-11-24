using System.IO.Compression;
using System.IO.Hashing;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Encodes and decodes the page blobs of a page file. A blob is the Brotli-compressed GPU image of
/// one page, or the raw image when compression does not make it smaller. The entry of each blob
/// stores the 32-bit xxHash of the decoded image, and the decoder rejects an image whose hash is
/// different.
/// </summary>
internal static class MeshletPageBlobCodec
{
    /// <summary>The Brotli quality of page blobs. The cook writes each blob once, and reads are frequent.</summary>
    private const int BrotliQuality = 5;

    /// <summary>The Brotli window (log2 of the window size). 2^17 bytes covers a full page.</summary>
    private const int BrotliWindow = 17;

    /// <summary>Encodes one page image. Returns the stored bytes and the hash of the image.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> image, out uint contentHash)
    {
        contentHash = XxHash32.HashToUInt32(image);
        byte[] compressed = new byte[BrotliEncoder.GetMaxCompressedLength(image.Length)];
        if (BrotliEncoder.TryCompress(image, compressed, out int written, BrotliQuality, BrotliWindow) && written < image.Length)
            return compressed.AsSpan(0, written).ToArray();
        return image.ToArray();
    }

    /// <summary>
    /// Decodes one stored blob into <paramref name="destination"/>, which must hold
    /// <see cref="MeshletPageFileEntry.UncompressedBytes"/> bytes, and checks its hash.
    /// </summary>
    /// <exception cref="InvalidDataException">The blob does not decode to an image with the stored size and hash.</exception>
    public static void Decode(ReadOnlySpan<byte> stored, MeshletPageFileEntry entry, Span<byte> destination)
    {
        if (destination.Length < entry.UncompressedBytes || stored.Length != entry.StoredBytes)
            throw new InvalidDataException("A page blob does not have the size that its entry names.");

        Span<byte> image = destination[..(int)entry.UncompressedBytes];
        if (entry.IsStoredRaw)
        {
            stored.CopyTo(image);
        }
        else if (!BrotliDecoder.TryDecompress(stored, image, out int written) || written != image.Length)
        {
            throw new InvalidDataException("A page blob does not decode to its stored size.");
        }

        if (XxHash32.HashToUInt32(image) != entry.ContentHash)
            throw new InvalidDataException("A page blob does not match its content hash.");
    }
}
