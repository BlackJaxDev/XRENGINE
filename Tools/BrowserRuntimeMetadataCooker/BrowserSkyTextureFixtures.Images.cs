using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Publishing;

internal static partial class BrowserSkyTextureFixtures
{
    /// <summary>Creates an asymmetric half-float image for both direction-to-UV projections.</summary>
    private static XRTexture2D CreateSkyImage(bool octahedral)
    {
        uint height = octahedral ? 8u : 4u;
        XRTexture2D image = new(8, height, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false)
        {
            Name = "Diagnostic HDR sky image",
            SizedInternalFormat = ESizedInternalFormat.Rgba16f,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
            UWrap = octahedral ? ETexWrapMode.ClampToEdge : ETexWrapMode.Repeat,
            VWrap = ETexWrapMode.ClampToEdge,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = 0,
        };
        byte[] bytes = new byte[8 * height * 8];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < 8; x++)
            {
                int offset = (y * 8 + x) * 8;
                WriteHalf(bytes, offset, (x + 1) * 0.25f);
                WriteHalf(bytes, offset + 2, (y + 0.5f) * 0.125f);
                WriteHalf(bytes, offset + 4, 0.25f + (x + y) * 0.0625f);
                WriteHalf(bytes, offset + 6, 0.25f);
            }
        image.Mipmaps[0].DataBytes = bytes;
        return image;
    }

    private static XRTexture2D CreateFilteredSkyImage(bool octahedral)
    {
        uint height = octahedral ? 8u : 4u;
        XRTexture2D image = new(8, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false)
        {
            Name = "Diagnostic filtered sRGB sky image",
            SizedInternalFormat = ESizedInternalFormat.Srgb8Alpha8,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = octahedral ? ETexWrapMode.ClampToEdge : ETexWrapMode.Repeat,
            VWrap = ETexWrapMode.ClampToEdge,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = 0,
        };
        Mipmap2D[] mips = new Mipmap2D[4];
        for (int level = 0; level < mips.Length; level++)
        {
            uint width = 8u >> level;
            uint mipHeight = Math.Max(1u, height >> level);
            byte[] bytes = new byte[width * mipHeight * 4];
            for (int y = 0; y < mipHeight; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * (int)width + x) * 4;
                    bytes[offset] = (byte)(32 + x * 24 + level * 40);
                    bytes[offset + 1] = (byte)(40 + y * 20 + level * 24);
                    bytes[offset + 2] = (byte)(80 + x * 6 + y * 10 + level * 24);
                    bytes[offset + 3] = 96;
                }
            mips[level] = new Mipmap2D(width, mipHeight, bytes);
        }
        image.Mipmaps = mips;
        return image;
    }

}
