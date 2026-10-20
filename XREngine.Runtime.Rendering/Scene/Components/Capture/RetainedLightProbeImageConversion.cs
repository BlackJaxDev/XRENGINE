using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Components.Capture.Lights;

/// <summary>Checks the sole derived probe representation: original RGB half bits plus a constant half-one alpha.</summary>
public static class RetainedLightProbeImageConversion
{
    public static void Validate(XRTexture2D source, XRTexture2D target)
    {
        if (ReferenceEquals(source, target)) return;
        if (source.ID == target.ID || source.SizedInternalFormat != ESizedInternalFormat.Rgb16f ||
            target.SizedInternalFormat != ESizedInternalFormat.Rgba16f || source.Mipmaps.Length != target.Mipmaps.Length ||
            source.MinFilter != target.MinFilter || source.MagFilter != target.MagFilter || source.UWrap != target.UWrap ||
            source.VWrap != target.VWrap || source.MinLOD != target.MinLOD || source.MaxLOD != target.MaxLOD ||
            source.LodBias != target.LodBias || source.LargestMipmapLevel != target.LargestMipmapLevel ||
            source.SmallestAllowedMipmapLevel != target.SmallestAllowedMipmapLevel || source.SamplerName != target.SamplerName ||
            PublishedStandardLitTextureSettings.Capture(source) != PublishedStandardLitTextureSettings.Capture(target))
            throw Invalid();
        for (int level = 0; level < source.Mipmaps.Length; level++)
        {
            Mipmap2D original = source.Mipmaps[level], derived = target.Mipmaps[level];
            if (original.Width != derived.Width || original.Height != derived.Height ||
                original.PixelFormat != EPixelFormat.Rgb || derived.PixelFormat != EPixelFormat.Rgba ||
                original.PixelType != EPixelType.HalfFloat || derived.PixelType != EPixelType.HalfFloat)
                throw Invalid();
            byte[] input = original.Data!.GetBytes(), output = derived.Data!.GetBytes();
            int pixels = checked((int)((long)original.Width * original.Height));
            if (input.Length != checked(pixels * 6) || output.Length != checked(pixels * 8)) throw Invalid();
            for (int pixel = 0; pixel < pixels; pixel++)
                if (!input.AsSpan(pixel * 6, 6).SequenceEqual(output.AsSpan(pixel * 8, 6)) ||
                    output[pixel * 8 + 6] != 0 || output[pixel * 8 + 7] != 0x3c)
                    throw Invalid();
        }
    }

    private static InvalidDataException Invalid()
        => new("WebGPU.RetainedProbe.DerivedImageMismatch: every RGB half bit, mip, source sampling state and constant alpha-one must match the independent target image.");
}
