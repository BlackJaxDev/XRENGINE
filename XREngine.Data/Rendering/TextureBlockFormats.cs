namespace XREngine.Data.Rendering;

/// <summary>
/// Facts about the GPU block-compressed texture formats (BC4, BC5, and BC7) that the
/// texture cook can produce. Each format stores 4x4 pixel blocks of a fixed size.
/// </summary>
public static class TextureBlockFormats
{
    /// <summary>Width and height of one block in pixels.</summary>
    public const int BlockDimension = 4;

    /// <summary>Returns true when the sized format is block compressed.</summary>
    public static bool IsBlockCompressed(ESizedInternalFormat format)
        => format is ESizedInternalFormat.Bc7Unorm
            or ESizedInternalFormat.Bc7Srgb
            or ESizedInternalFormat.Bc5Unorm
            or ESizedInternalFormat.Bc4Unorm;

    /// <summary>Returns true when the pixel internal format is one of the supported block-compressed formats.</summary>
    public static bool IsBlockCompressed(EPixelInternalFormat format)
        => TryGetSizedFormat(format, out _);

    /// <summary>Maps a block-compressed pixel internal format to its sized format.</summary>
    public static bool TryGetSizedFormat(EPixelInternalFormat format, out ESizedInternalFormat sizedFormat)
    {
        sizedFormat = format switch
        {
            EPixelInternalFormat.CompressedRgbaBptcUnorm => ESizedInternalFormat.Bc7Unorm,
            EPixelInternalFormat.CompressedSrgbAlphaBptcUnorm => ESizedInternalFormat.Bc7Srgb,
            EPixelInternalFormat.CompressedRGRgtc2 => ESizedInternalFormat.Bc5Unorm,
            EPixelInternalFormat.CompressedRedRgtc1 => ESizedInternalFormat.Bc4Unorm,
            _ => ESizedInternalFormat.Rgba8,
        };
        return format is EPixelInternalFormat.CompressedRgbaBptcUnorm
            or EPixelInternalFormat.CompressedSrgbAlphaBptcUnorm
            or EPixelInternalFormat.CompressedRGRgtc2
            or EPixelInternalFormat.CompressedRedRgtc1;
    }

    /// <summary>Maps a block-compressed sized format to its pixel internal format.</summary>
    public static EPixelInternalFormat ToPixelInternalFormat(ESizedInternalFormat format)
        => format switch
        {
            ESizedInternalFormat.Bc7Unorm => EPixelInternalFormat.CompressedRgbaBptcUnorm,
            ESizedInternalFormat.Bc7Srgb => EPixelInternalFormat.CompressedSrgbAlphaBptcUnorm,
            ESizedInternalFormat.Bc5Unorm => EPixelInternalFormat.CompressedRGRgtc2,
            ESizedInternalFormat.Bc4Unorm => EPixelInternalFormat.CompressedRedRgtc1,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The format is not block compressed."),
        };

    /// <summary>Bytes in one 4x4 block: 8 for BC4, 16 for BC5 and BC7.</summary>
    public static int GetBlockBytes(ESizedInternalFormat format)
        => format switch
        {
            ESizedInternalFormat.Bc4Unorm => 8,
            ESizedInternalFormat.Bc5Unorm or ESizedInternalFormat.Bc7Unorm or ESizedInternalFormat.Bc7Srgb => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The format is not block compressed."),
        };

    /// <summary>Number of blocks along one axis that cover <paramref name="pixels"/> pixels.</summary>
    public static uint GetBlockCount(uint pixels)
        => (pixels + BlockDimension - 1) / BlockDimension;

    /// <summary>Bytes in one row of blocks for an image of the given width.</summary>
    public static long GetBlockRowBytes(ESizedInternalFormat format, uint width)
        => GetBlockCount(width) * (long)GetBlockBytes(format);

    /// <summary>Total bytes of one block-compressed image (one mip level).</summary>
    public static long GetImageBytes(ESizedInternalFormat format, uint width, uint height)
        => GetBlockRowBytes(format, width) * GetBlockCount(height);

    /// <summary>
    /// Swaps the sRGB and linear variants of BC7. The cook stores BC7 UNORM bits; the
    /// resident format applies the texture color space, as RGBA8 does with SRGB8_ALPHA8.
    /// Other formats return unchanged.
    /// </summary>
    public static ESizedInternalFormat WithColorSpace(ESizedInternalFormat format, bool srgb)
        => (format, srgb) switch
        {
            (ESizedInternalFormat.Bc7Unorm, true) => ESizedInternalFormat.Bc7Srgb,
            (ESizedInternalFormat.Bc7Srgb, false) => ESizedInternalFormat.Bc7Unorm,
            _ => format,
        };
}
