namespace XREngine.Rendering;

/// <summary>Cooked mobile storage rules; no implicit decoding, transcoding or normal-axis conversion.</summary>
public static class BrowserTextureFormat
{
    public static bool IsCompressed(string format) => format is
        "astc-4x4-unorm" or "astc-4x4-unorm-srgb" or "etc2-rgba8unorm" or "etc2-rgba8unorm-srgb";

    public static void Validate(int width, int height, string format, string normalConvention, string alphaMode)
    {
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            throw new ArgumentOutOfRangeException(nameof(width), "Cooked texture dimensions must be in [1, 8192].");
        if (format is not ("rgba8unorm" or "rgba8unorm-srgb") && !IsCompressed(format))
            throw new NotSupportedException("Cooked textures require RGBA8, ASTC 4×4 or ETC2 RGBA8 encoding.");
        if (IsCompressed(format) && ((width & 3) != 0 || (height & 3) != 0))
            throw new ArgumentException("Compressed texture base dimensions must be multiples of four texels.");
        if (alphaMode != "straight")
            throw new NotSupportedException("Cooked browser textures require straight alpha.");
        if (normalConvention is not ("none" or "tangent-y-positive") ||
            (normalConvention != "none" && format.EndsWith("-srgb", StringComparison.Ordinal)))
            throw new NotSupportedException("Normal textures require linear encoding and tangent-y-positive convention.");
    }

    public static int GetMipCount(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        int count = 1;
        for (int size = Math.Max(width, height); size > 1; size >>= 1)
            count++;
        return count;
    }

    public static int GetMipByteLength(int width, int height, string format, int mip)
    {
        if ((uint)mip >= (uint)GetMipCount(width, height))
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (format is not ("rgba8unorm" or "rgba8unorm-srgb") && !IsCompressed(format))
            throw new NotSupportedException("Unsupported cooked texture encoding.");
        int mipWidth = Math.Max(1, width >> mip), mipHeight = Math.Max(1, height >> mip);
        return IsCompressed(format)
            ? checked(((mipWidth + 3) / 4) * ((mipHeight + 3) / 4) * 16)
            : checked(mipWidth * mipHeight * 4);
    }
}
