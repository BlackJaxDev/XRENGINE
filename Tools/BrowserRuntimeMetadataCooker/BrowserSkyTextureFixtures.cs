using System.Buffers.Binary;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Publishing;

/// <summary>Cooks authored texture fixtures for the browser asset and sky render paths.</summary>
internal static partial class BrowserSkyTextureFixtures
{
    internal static void Write(string directory)
    {
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        WriteTexture(CreateHdrCube(), "SkyCubeHdr.bin", directory, new Guid("685a5e81-1521-445c-9502-6d83e7fdaae1"));
        WriteTexture(CreateFilteredCube(), "SkyCubeFiltered.bin", directory, new Guid("685a5e81-1521-445c-9502-6d83e7fdaae2"));
        WriteTexture(CreateSkyImage(false), "SkyEquirectangularHdr.bin", directory, new Guid("685a5e81-1521-445c-9502-6d83e7fdaae3"));
        WriteTexture(CreateSkyImage(true), "SkyOctahedralHdr.bin", directory, new Guid("685a5e81-1521-445c-9502-6d83e7fdaae4"));
        WriteTexture(CreateFilteredSkyImage(false), "SkyEquirectangularFiltered.bin", directory, new Guid("685a5e81-1521-445c-9502-6d83e7fdaae5"));
        WriteTexture(CreateFilteredSkyImage(true), "SkyOctahedralFiltered.bin", directory, new Guid("685a5e81-1521-445c-9502-6d83e7fdaae6"));
    }

    private static void WriteTexture(XRTexture texture, string name, string directory, Guid identity)
    {
        try
        {
            texture.AdoptPersistentID(identity);
            byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(texture));
            byte[] envelope = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
                texture.GetType().AssemblyQualifiedName!, CookedAssetFormat.BinaryV2, payload));
            File.WriteAllBytes(Path.Combine(directory, name), envelope);
        }
        finally
        {
            if (texture is XRTextureCube cube)
                foreach (var level in cube.Mipmaps)
                    foreach (Mipmap2D face in level.Sides)
                        face.Data?.Dispose();
            if (texture is XRTexture2D image)
                foreach (Mipmap2D mip in image.Mipmaps)
                    mip.Data?.Dispose();
            texture.Destroy(true);
        }
    }

    /// <summary>Creates distinct face, column, and row values in exact half-float storage.</summary>
    private static XRTextureCube CreateHdrCube()
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

    private static XRTextureCube CreateFilteredCube()
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

    private static void WriteHalf(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));
}
