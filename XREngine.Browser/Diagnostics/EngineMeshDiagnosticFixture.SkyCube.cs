using System.Buffers.Binary;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    /// <summary>Creates distinct face, column, and row values in exact half-float storage.</summary>
    private static XRTextureCube CreateSkyCube()
    {
        XRTextureCube cube = new(4, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false)
        {
            Name = "Diagnostic six-face HDR sky",
            SizedInternalFormat = ESizedInternalFormat.Rgba16f,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
            MinLOD = 0,
            MaxLOD = 0,
        };
        for (int face = 0; face < 6; face++)
        {
            byte[] bytes = new byte[4 * 4 * 8];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    int offset = (y * 4 + x) * 8;
                    WriteHalf(bytes, offset, (face + 1) * 0.25f);
                    WriteHalf(bytes, offset + 2, (x + 0.5f) * 0.25f);
                    WriteHalf(bytes, offset + 4, (y + 0.5f) * 0.25f);
                    WriteHalf(bytes, offset + 6, 0.25f);
                }
            cube.Mipmaps[0].Sides[face].DataBytes = bytes;
        }
        return cube;
    }

    private static void WriteHalf(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));

    private void SetSkyCubeCase(int sampleCase)
    {
        if (sampleCase is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _skyCase = sampleCase;
        _skyComponent!.Intensity = sampleCase == 8 ? 2 : 1;
        _skyComponent.Rotation = sampleCase == 7 ? 90 : 0;
        _skyCameraTransform!.Translation = sampleCase == 6 ? new Vector3(100, 200, 300) : Vector3.Zero;
        _skyCameraTransform.Rotation = sampleCase switch
        {
            1 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2),
            2 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            3 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2),
            4 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2),
            5 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
            _ => Quaternion.Identity,
        };
    }
}
