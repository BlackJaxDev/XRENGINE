using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private static XRTextureCube CreateFilteredSkyCube()
    {
        XRTextureCube cube = new(4, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false, mipCount: 3)
        {
            Name = "Diagnostic filtered sRGB cube sky",
            SizedInternalFormat = ESizedInternalFormat.Srgb8Alpha8,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            MagFilter = ETexMagFilter.Linear,
            MinLOD = 0,
            MaxLOD = 0,
        };
        for (int level = 0; level < 3; level++)
            for (int face = 0; face < 6; face++)
            {
                int extent = 4 >> level;
                byte[] bytes = new byte[extent * extent * 4];
                for (int offset = 0; offset < bytes.Length; offset += 4)
                {
                    // The final mip is uniform across faces. It checks mip
                    // selection without a three-face corner reference.
                    bytes[offset] = level == 2 ? (byte)112 : (byte)(32 + face * 28 + level * 20);
                    bytes[offset + 1] = level == 2 ? (byte)144 : (byte)(180 - face * 20 + level * 10);
                    bytes[offset + 2] = level == 2 ? (byte)192 : (byte)(64 + face * 16 + level * 24);
                    bytes[offset + 3] = 96;
                }
                cube.Mipmaps[level].Sides[face].DataBytes = bytes;
            }
        return cube;
    }

    private void SetSkyFilteredCubeCase(int sampleCase)
    {
        if (sampleCase is < 0 or > 40)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _skyCase = sampleCase;
        int level = sampleCase is >= 6 and <= 11 or >= 25 and <= 36 ? 1 : sampleCase == 12 ? 2 : 0;
        _skyTexture!.MinLOD = level;
        _skyTexture.MaxLOD = level;
        _skyComponent!.Intensity = sampleCase == 37 ? 4 : 1;
        _skyComponent.Rotation = sampleCase == 38 ? 90 : 0;
        bool edge = sampleCase is >= 13 and <= 39;
        _skyCameraParameters!.VerticalFieldOfView = edge ? 10 : 30;
        // Keep the foreground outside every view in this sampling profile.
        _skyCameraTransform!.Translation = sampleCase == 39 ? new Vector3(500, 600, 700) : new Vector3(100, 200, 300);
        Vector3 forward = edge ? ((sampleCase <= 36 ? (sampleCase - 13) % 12 : 5) switch
        {
            0 => new Vector3(1, 1, 0),
            1 => new Vector3(1, -1, 0),
            2 => new Vector3(-1, 1, 0),
            3 => new Vector3(-1, -1, 0),
            4 => new Vector3(1, 0, 1),
            5 => new Vector3(1, 0, -1),
            6 => new Vector3(-1, 0, 1),
            7 => new Vector3(-1, 0, -1),
            8 => new Vector3(0, 1, 1),
            9 => new Vector3(0, 1, -1),
            10 => new Vector3(0, -1, 1),
            _ => new Vector3(0, -1, -1),
        }) : ((sampleCase < 12 ? sampleCase % 6 : 0) switch
        {
            1 => Vector3.UnitX,
            2 => -Vector3.UnitX,
            3 => Vector3.UnitY,
            4 => -Vector3.UnitY,
            5 => Vector3.UnitZ,
            _ => -Vector3.UnitZ,
        });
        Vector3 up = MathF.Abs(forward.Y) > 0.95f && forward.X == 0 && forward.Z == 0 ? Vector3.UnitZ : Vector3.UnitY;
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.Zero, forward, up);
        if (!Matrix4x4.Invert(view, out Matrix4x4 inverseView))
            throw new InvalidOperationException("EngineMeshDiagnostic.CubeCameraInvalid.");
        _skyCameraTransform.Rotation = Quaternion.CreateFromRotationMatrix(inverseView);
    }
}
