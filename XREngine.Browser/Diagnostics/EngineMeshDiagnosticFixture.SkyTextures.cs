using System.Buffers.Binary;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
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

    private static void WriteHalf(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));

}
