using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Writes packed RGB24 video texels in the byte order of a Vulkan image format.
/// </summary>
/// <remarks>
/// Most desktop GPUs cannot sample three-byte formats, so an RGB8 texture resolves to a
/// four-byte image format. A buffer-to-image copy does not convert texels, so the staging
/// bytes must already match the image format. Four-byte formats receive an opaque alpha.
/// </remarks>
internal static class VulkanRgb24TexelWriter
{
    private const int SourceBytesPerTexel = 3;

    /// <summary>
    /// Gets the number of staging bytes per texel for an image format that can receive RGB24 texels.
    /// </summary>
    /// <returns><c>false</c> when the format is not an 8-bit UNORM or sRGB color format.</returns>
    public static bool TryGetDestinationBytesPerTexel(Format format, out uint bytesPerTexel)
    {
        bytesPerTexel = format switch
        {
            Format.R8G8B8Unorm or Format.R8G8B8Srgb or Format.B8G8R8Unorm or Format.B8G8R8Srgb => 3u,
            Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => 4u,
            _ => 0u,
        };
        return bytesPerTexel != 0u;
    }

    /// <summary>
    /// Writes the RGB24 texels in <paramref name="source"/> to <paramref name="destination"/>
    /// in the byte order of <paramref name="destinationFormat"/>.
    /// </summary>
    /// <param name="source">Packed RGB24 texels. The length must be a multiple of three.</param>
    /// <param name="destination">Staging bytes. The length must hold every texel in the destination format.</param>
    /// <param name="destinationFormat">The format of the image that receives the staging bytes.</param>
    /// <returns><c>false</c> when the format is not supported or a length does not match.</returns>
    public static bool TryWrite(ReadOnlySpan<byte> source, Span<byte> destination, Format destinationFormat)
    {
        if (!TryGetDestinationBytesPerTexel(destinationFormat, out uint bytesPerTexel) ||
            source.Length % SourceBytesPerTexel != 0)
            return false;

        int texelCount = source.Length / SourceBytesPerTexel;
        if ((long)destination.Length != (long)texelCount * bytesPerTexel)
            return false;

        bool swapRedBlue = destinationFormat is
            Format.B8G8R8Unorm or Format.B8G8R8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
        if (bytesPerTexel == 3u)
        {
            if (swapRedBlue)
                WriteThreeByteTexelsSwapped(source, destination, texelCount);
            else
                source.CopyTo(destination);
            return true;
        }

        WriteFourByteTexels(source, destination, texelCount, swapRedBlue);
        return true;
    }

    private static void WriteThreeByteTexelsSwapped(ReadOnlySpan<byte> source, Span<byte> destination, int texelCount)
    {
        for (int texel = 0; texel < texelCount; texel++)
        {
            int offset = texel * SourceBytesPerTexel;
            destination[offset] = source[offset + 2];
            destination[offset + 1] = source[offset + 1];
            destination[offset + 2] = source[offset];
        }
    }

    private static void WriteFourByteTexels(ReadOnlySpan<byte> source, Span<byte> destination, int texelCount, bool swapRedBlue)
    {
        ref byte sourceRef = ref MemoryMarshal.GetReference(source);
        ref byte destinationRef = ref MemoryMarshal.GetReference(destination);
        int texel = 0;

        if (Ssse3.IsSupported)
        {
            // Each step loads 16 source bytes and consumes 12 of them (four texels). A mask
            // byte with the high bit set makes the shuffle write zero, and the alpha mask then
            // sets that byte to 255. Stop while 16 source bytes remain readable.
            Vector128<byte> shuffle = swapRedBlue
                ? Vector128.Create((byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80)
                : Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
            Vector128<byte> alpha = Vector128.Create(
                (byte)0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF);
            for (; texel * SourceBytesPerTexel + Vector128<byte>.Count <= source.Length; texel += 4)
            {
                Vector128<byte> rgb = Vector128.LoadUnsafe(ref sourceRef, (nuint)(texel * SourceBytesPerTexel));
                Vector128<byte> texels = Ssse3.Shuffle(rgb, shuffle) | alpha;
                texels.StoreUnsafe(ref destinationRef, (nuint)(texel * 4));
            }
        }

        int redOffset = swapRedBlue ? 2 : 0;
        int blueOffset = swapRedBlue ? 0 : 2;
        for (; texel < texelCount; texel++)
        {
            int sourceOffset = texel * SourceBytesPerTexel;
            int destinationOffset = texel * 4;
            Unsafe.Add(ref destinationRef, destinationOffset) = Unsafe.Add(ref sourceRef, sourceOffset + redOffset);
            Unsafe.Add(ref destinationRef, destinationOffset + 1) = Unsafe.Add(ref sourceRef, sourceOffset + 1);
            Unsafe.Add(ref destinationRef, destinationOffset + 2) = Unsafe.Add(ref sourceRef, sourceOffset + blueOffset);
            Unsafe.Add(ref destinationRef, destinationOffset + 3) = byte.MaxValue;
        }
    }
}
