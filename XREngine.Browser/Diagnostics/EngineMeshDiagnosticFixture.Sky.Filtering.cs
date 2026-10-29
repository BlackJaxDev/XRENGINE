using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
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

    private void SetSkyFilteredImageCase(int sampleCase)
    {
        if (sampleCase is < 0 or > 14)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        SetSkyImageCase(sampleCase <= 9 ? sampleCase : sampleCase == 13 ? 7 : 0);
        _skyCase = sampleCase;
        int level = sampleCase switch
        {
            10 or 13 => 1,
            11 => 2,
            12 => 3,
            _ => 0,
        };
        _skyTexture!.MinLOD = level;
        _skyTexture.MaxLOD = level;
        if (sampleCase == 13)
            _skyComponent!.Intensity = 4;
    }
}
