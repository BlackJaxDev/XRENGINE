using System.Buffers.Binary;

namespace XREngine.Rendering;

/// <summary>Decodes bounded native texture readback pixels without changing their linear HDR values or row order.</summary>
public static class BrowserTextureReadbackPixels
{
    /// <summary>Expands little-endian RGBA16F pixels into caller-owned RGBA32F components.</summary>
    public static void DecodeRgba16Float(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (source.IsEmpty || source.Length % 8 != 0 || source.Length > BrowserBufferReadbackDescription.MaximumByteLength)
            throw new ArgumentException("RGBA16F input must contain complete pixels within the 16 MiB readback budget.", nameof(source));
        if (destination.Length != source.Length / sizeof(ushort))
            throw new ArgumentException("RGBA32F output must contain exactly four components per source pixel.", nameof(destination));
        for (int index = 0; index < destination.Length; index++)
            destination[index] = (float)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(index * sizeof(ushort), sizeof(ushort))));
    }
}
