using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Components.Capture.Lights;

/// <summary>Target-cooked image provenance and interpretation for an already-baked probe.</summary>
public sealed record RetainedLightProbeIblProfile
{
    public int Version { get; init; } = 1;
    public uint SourceGeneration { get; init; }
    public float InfluenceSphereInner { get; init; }
    public float InfluenceSphereOuter { get; init; }
    public Vector3 InfluenceBoxInner { get; init; }
    public Vector3 InfluenceBoxOuter { get; init; }
    public int SourceStreamedMipLevel { get; init; }
    public LightProbeComponent.EHdrEncoding SourceHdrEncoding { get; init; }
    public required XRTexture2D Irradiance { get; init; }
    public required XRTexture2D Prefilter { get; init; }
    public required XRTexture2D SourceIrradiance { get; init; }
    public required XRTexture2D SourcePrefilter { get; init; }
    public PublishedStandardLitTextureSettings IrradianceSettings { get; init; }
    public PublishedStandardLitTextureSettings PrefilterSettings { get; init; }

    public static RetainedLightProbeIblProfile Capture(LightProbeComponent source)
    {
        if (source.HdrEncoding != LightProbeComponent.EHdrEncoding.Rgb16f)
            throw Invalid("RGBM, RGBE and YCoCg probe decoding has no admitted canonical receiver; retained data must be direct linear RGB");
        if (source.AutoCaptureOnActivate || source.RealtimeCapture)
            throw Invalid("AutoCaptureOnActivate and RealtimeCapture must both be authored false; retained data cannot satisfy requested capture");
        if (!source.IblTexturesValid || source.CaptureVersion == 0 || source.IrradianceTexture is not { } irradiance || source.PrefilterTexture is not { } prefilter)
            throw Invalid("the source requires a valid, nonzero baked generation with both irradiance and prefiltered images");
        ValidateImage(irradiance, false, allowSourceRgb: true);
        ValidateImage(prefilter, true, allowSourceRgb: true);
        return new() { SourceGeneration = source.CaptureVersion, Irradiance = irradiance, Prefilter = prefilter,
            SourceIrradiance = irradiance, SourcePrefilter = prefilter,
            InfluenceSphereInner = source.InfluenceSphereInnerRadius, InfluenceSphereOuter = source.InfluenceSphereOuterRadius,
            InfluenceBoxInner = source.InfluenceBoxInnerExtents, InfluenceBoxOuter = source.InfluenceBoxOuterExtents,
            SourceStreamedMipLevel = source.StreamedMipLevel, SourceHdrEncoding = source.HdrEncoding,
            IrradianceSettings = PublishedStandardLitTextureSettings.Capture(irradiance),
            PrefilterSettings = PublishedStandardLitTextureSettings.Capture(prefilter) };
    }

    public void RestoreDecodedImages()
    {
        if (Version != 1 || SourceGeneration == 0 || SourceHdrEncoding != LightProbeComponent.EHdrEncoding.Rgb16f) throw Invalid("the retained target provenance version or generation is invalid");
        IrradianceSettings.ApplyTo(SourceIrradiance);
        PrefilterSettings.ApplyTo(SourcePrefilter);
        IrradianceSettings.ApplyTo(Irradiance);
        PrefilterSettings.ApplyTo(Prefilter);
        ValidateImage(Irradiance, false);
        ValidateImage(Prefilter, true);
        RetainedLightProbeImageConversion.Validate(SourceIrradiance, Irradiance);
        RetainedLightProbeImageConversion.Validate(SourcePrefilter, Prefilter);
    }

    private static void ValidateImage(XRTexture2D image, bool prefilter, bool allowSourceRgb = false)
    {
        _ = PublishedStandardLitTextureSettings.Capture(image);
        if (image.ImportedColorSpace != ETextureColorSpace.Linear)
            throw Invalid("retained probe image interpretation must be explicit direct linear RGB");
        if (image.Mipmaps.Length > BitOperations.Log2(Math.Max(image.Width, image.Height)) + 1)
            throw Invalid("retained mip count exceeds the full source image chain");
        if (!Enum.IsDefined(image.MinFilter) || !Enum.IsDefined(image.MagFilter) ||
            image.UWrap is not (ETexWrapMode.Repeat or ETexWrapMode.MirroredRepeat or ETexWrapMode.ClampToEdge) ||
            image.VWrap is not (ETexWrapMode.Repeat or ETexWrapMode.MirroredRepeat or ETexWrapMode.ClampToEdge) ||
            !prefilter && image.MaxAnisotropy != 1)
            throw Invalid("retained probe sampling requires exact WebGPU filters/wraps and non-anisotropic irradiance sampling");
        if (image.Rectangle || image.LodBias != 0 || image.LargestMipmapLevel != 0 || image.SmallestAllowedMipmapLevel < image.Mipmaps.Length - 1 ||
            image.MinLOD > 0 || image.MaxLOD < image.Mipmaps.Length - 1)
            throw Invalid("retained probe arrays require a complete canonical mip view with no rectangle coordinates, LOD bias, or clipped LOD range");
        if (image.AutoGenerateMipmaps || image.MultiSample || image.EnableComparison ||
            (image.SizedInternalFormat is not (ESizedInternalFormat.Rgba16f or ESizedInternalFormat.Rgba8) &&
                !(allowSourceRgb && image.SizedInternalFormat == ESizedInternalFormat.Rgb16f)))
            throw Invalid("retained probe images require exact RGBA16F, RGBA8, or target-convertible RGB16F HalfFloat CPU mips without automatic mips, multisampling, or comparison sampling; RGB16F has no admitted browser upload representation");
        if (prefilter && image.Mipmaps.Length < 5)
            throw Invalid("canonical prefilter sampling requires at least five retained mip levels");
        for (int level = 0; level < image.Mipmaps.Length; level++)
        {
            Mipmap2D mip = image.Mipmaps[level];
            if (image.SizedInternalFormat == ESizedInternalFormat.Rgb16f && mip.PixelType != EPixelType.HalfFloat)
                throw Invalid("RGB16F source conversion requires CPU Rgb/HalfFloat mips; Float upload rounding has no qualified lossless conversion");
            int bytes = image.SizedInternalFormat == ESizedInternalFormat.Rgb16f ? 6 : image.SizedInternalFormat == ESizedInternalFormat.Rgba16f ? 8 : 4;
            EPixelType type = bytes is 6 or 8 ? EPixelType.HalfFloat : EPixelType.UnsignedByte;
            if (mip.InternalFormat != (bytes == 6 ? EPixelInternalFormat.Rgb16f : bytes == 8 ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8) ||
                mip.PixelFormat != (bytes == 6 ? EPixelFormat.Rgb : EPixelFormat.Rgba) || mip.PixelType != type ||
                mip.Data!.Length != checked((long)mip.Width * mip.Height * bytes) ||
                level > 0 && (mip.Width != Math.Max(1u, image.Mipmaps[0].Width >> level) || mip.Height != Math.Max(1u, image.Mipmaps[0].Height >> level)))
                throw Invalid("each retained mip must contain the exact dimensions and RGBA pixel encoding");
        }
    }

    private static NotSupportedException Invalid(string reason)
        => new($"BrowserCook.RetainedProbeUnsupported: {reason}.");
}
