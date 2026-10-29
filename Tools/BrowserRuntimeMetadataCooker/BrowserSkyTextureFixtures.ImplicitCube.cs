using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Publishing;

internal static partial class BrowserSkyTextureFixtures
{
    /// <summary>Creates uniform sRGB mip colors for derivative-driven cube sampling.</summary>
    private static XRTextureCube CreateImplicitCube()
    {
        XRTextureCube cube = new(256, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false, mipCount: 9)
        {
            Name = "Diagnostic implicit cube sky",
            SizedInternalFormat = ESizedInternalFormat.Srgb8Alpha8,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            MagFilter = ETexMagFilter.Linear,
            MinLOD = 0,
            MaxLOD = 8,
        };
        for (int level = 0; level < 9; level++)
        {
            (int red, int green, int blue) = (level % 4) switch
            {
                0 => (32, 224, 64),
                1 => (224, 32, 192),
                2 => (64, 192, 224),
                _ => (192, 64, 32),
            };
            foreach (Mipmap2D face in cube.Mipmaps[level].Sides)
            {
                int extent = 256 >> level;
                byte[] bytes = new byte[extent * extent * 4];
                for (int offset = 0; offset < bytes.Length; offset += 4)
                {
                    bytes[offset] = (byte)red;
                    bytes[offset + 1] = (byte)green;
                    bytes[offset + 2] = (byte)blue;
                    bytes[offset + 3] = 96;
                }
                face.DataBytes = bytes;
            }
        }
        return cube;
    }
}
