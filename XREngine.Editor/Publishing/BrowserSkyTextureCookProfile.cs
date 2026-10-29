using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Editor.Publishing;

/// <summary>Rejects sky images that cannot publish their authored CPU mip storage.</summary>
internal static class BrowserSkyTextureCookProfile
{
    internal static void Validate(SkyboxComponent sky)
    {
        ArgumentNullException.ThrowIfNull(sky);
        sky.ValidateWebGpuProfile();
        if (sky.Mode != ESkyboxMode.Texture)
            return;
        try
        {
            bool srgb = sky.Texture switch
            {
                XRTexture2D sourceImage => sourceImage.SizedInternalFormat == ESizedInternalFormat.Srgb8Alpha8,
                XRTextureCube sourceCube => sourceCube.SizedInternalFormat == ESizedInternalFormat.Srgb8Alpha8,
                _ => false,
            };
            if (srgb && sky.Texture!.RequiresStorageUsage)
                throw new InvalidDataException("sRGB sky textures cannot request storage-image usage.");
            if (sky.Texture is XRTexture2D image && image.GetType() == typeof(XRTexture2D))
                ValidateImage(image);
            else if (sky.Texture is XRTextureCube cube && cube.GetType() == typeof(XRTextureCube))
                ValidateCube(cube);
            else
                throw new InvalidDataException("Cooked sky pixels require an exact XRTexture2D or XRTextureCube source.");
        }
        catch (InvalidDataException error)
        {
            throw new NotSupportedException($"BrowserCook.SkyTextureStorageUnsupported: texture '{sky.Texture!.Name}' ({sky.Texture.ID}): {error.Message}", error);
        }
    }

    private static void ValidateImage(XRTexture2D image)
    {
        Mipmap2D[] mips = image.Mipmaps;
        if (mips is null || mips.Length == 0 || mips[0] is not { } first)
            throw new InvalidDataException("A sky image requires an authored base mip.");
        ValidateChain(first.Width, first.Height, mips.Length);
        // Reuse the canonical static-image contract for external, sparse,
        // grab-pass, and active upload state. No source pixels are copied.
        _ = PublishedStandardLitTextureSettings.Capture(image);
        for (int level = 0; level < mips.Length; level++)
            ValidateMip(mips[level], Math.Max(1u, first.Width >> level), Math.Max(1u, first.Height >> level),
                image.SizedInternalFormat, level, -1);
    }

    private static void ValidateCube(XRTextureCube cube)
    {
        if (cube.IsGpuWritable || cube.OpenGlExternalMemoryImportHandle != 0 || cube.OpenGlExternalMemoryImportSize != 0 ||
            cube.OpenGlExternalMemoryImportMipLevels != 1 || cube.OpenGlExternalMemoryLabel is not null)
            throw new InvalidDataException("A cooked sky cube requires static CPU pixels without GPU writes or external memory.");
        CubeMipmap[] mips = cube.Mipmaps;
        if (mips is null || mips.Length == 0 || mips[0]?.Sides is not { Length: 6 } sides || sides[0] is not { } first)
            throw new InvalidDataException("A sky cube requires six authored faces in its base mip.");
        if (first.Width != first.Height)
            throw new InvalidDataException("Sky cube faces must be square.");
        ValidateChain(first.Width, first.Height, mips.Length);
        for (int level = 0; level < mips.Length; level++)
        {
            if (mips[level]?.Sides is not { Length: 6 } faces)
                throw new InvalidDataException($"Cube mip {level} requires exactly six authored faces.");
            uint extent = Math.Max(1u, first.Width >> level);
            for (int face = 0; face < faces.Length; face++)
                ValidateMip(faces[face], extent, extent, cube.SizedInternalFormat, level, face);
        }
    }

    private static void ValidateChain(uint width, uint height, int count)
    {
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new InvalidDataException("Sky mip dimensions must be positive and fit the GPU texture descriptor.");
        int maximum = 1 + BitOperations.Log2(Math.Max(width, height));
        if (count > maximum)
            throw new InvalidDataException("The authored sky mip count exceeds the natural texture extent.");
    }

    private static void ValidateMip(Mipmap2D? mip, uint width, uint height, ESizedInternalFormat format, int level, int face)
    {
        string? reason = null;
        if (mip is null || mip.Width != width || mip.Height != height)
            reason = "missing mip or dimensions that do not match the authored mip chain";
        else if (mip.StreamingPBO is not null)
            reason = "a streaming upload buffer cannot be published as CPU pixels";
        else
        {
            int pixelBytes = (format, mip.PixelFormat, mip.PixelType) switch
            {
                (ESizedInternalFormat.Rgba8 or ESizedInternalFormat.Srgb8Alpha8, EPixelFormat.Rgba, EPixelType.UnsignedByte) => 4,
                (ESizedInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat) => 8,
                // The layered backend can encode authored float cube faces to half.
                (ESizedInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.Float) when face >= 0 => 16,
                _ => 0,
            };
            if (pixelBytes == 0)
                reason = "pixel format/type does not match the admitted upload encoding";
            else
            {
                long pixels = (long)width * height;
                if (pixels > int.MaxValue / pixelBytes)
                    reason = "pixel bytes exceed the supported upload length";
                else if (mip.Data is not { } data || data.Address == VoidPtr.Zero || data.Length != pixels * pixelBytes)
                    reason = "resident CPU pixel data is missing or has the wrong byte length";
            }
        }
        if (reason is not null)
            throw new InvalidDataException($"Mip {level}, face {face}: {reason}.");
    }
}
