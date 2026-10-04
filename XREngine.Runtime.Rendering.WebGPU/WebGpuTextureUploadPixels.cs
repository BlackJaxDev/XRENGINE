using System.Buffers.Binary;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Preserves authored upload storage while encoding the concrete GPU texture's transfer pixels.</summary>
internal static class WebGpuTextureUploadPixels
{
    public static bool RequiresFloatToHalf(string format, EPixelFormat pixel, EPixelType type)
        => format == "rgba16float" && pixel == EPixelFormat.Rgba && type == EPixelType.Float;

    public static int SourcePixelBytes(string format, EPixelFormat pixel, EPixelType type)
        => RequiresFloatToHalf(format, pixel, type) ? 16 : WebGpuTextureFormat.UploadPixelBytes(format, pixel, type);

    /// <summary>Applies the native RGBA16F conversion to little-endian authored floats without modifying the source.</summary>
    public static void EncodeRgba16Float(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty || source.Length % 16 != 0 || destination.Length != source.Length / 2)
            throw new ArgumentException("RGBA float conversion requires complete source pixels and exactly eight destination bytes per pixel.");
        for (int sourceOffset = 0, destinationOffset = 0; sourceOffset < source.Length; sourceOffset += 4, destinationOffset += 2)
        {
            float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(sourceOffset, 4)));
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(destinationOffset, 2), BitConverter.HalfToUInt16Bits((Half)value));
        }
    }
}
