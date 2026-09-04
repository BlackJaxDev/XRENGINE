using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Captures renderer-neutral logical metadata for the 2D texture shape used by
/// the current advanced material layouts. Native objects and descriptor state
/// deliberately remain outside this boundary.
/// </summary>
public static class AdvancedGpuResourceSourceEncoder
{
    public static bool TryEncode(
        XRTexture? texture,
        EAdvancedResourceFallback fallback,
        out AdvancedGpuResourceBindingSource source,
        out EAdvancedCanonicalCompatibilityReason compatibilityReason,
        out string reason)
    {
        if (texture is null)
        {
            source = AdvancedGpuResourceBindingSource.Missing(fallback);
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
            reason = string.Empty;
            return true;
        }
        if (texture is XRTexture2DArray textureArray)
        {
            if (textureArray.Textures.Length == 0)
            {
                source = default;
                compatibilityReason = EAdvancedCanonicalCompatibilityReason.EmptyResourceTexture;
                reason = "A logical texture array must contain at least one layer.";
                return false;
            }
            if (!TryEncode(textureArray.Textures[0], fallback, out AdvancedGpuResourceBindingSource layer, out compatibilityReason, out reason))
            {
                source = default;
                return false;
            }
            AdvancedTextureRecord record = layer.TextureRecord;
            record.Dimension = EAdvancedTextureDimension.Texture2DArray;
            record.Width = textureArray.Width;
            record.Height = textureArray.Height;
            record.DepthOrLayers = textureArray.Depth;
            record.MipCount = checked((uint)Math.Max(textureArray.Mipmaps?.Length ?? 0, 1));
            source = new(textureArray, record, layer.SamplerRecord, fallback, 0u);
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
            reason = string.Empty;
            return true;
        }
        if (texture is XRTextureCube cube)
        {
            if (cube.Extent == 0u || cube.Mipmaps.Length == 0)
            {
                source = default;
                compatibilityReason = EAdvancedCanonicalCompatibilityReason.EmptyResourceTexture;
                reason = "A logical cube texture must have a nonempty base mip.";
                return false;
            }
            if (!TryTranslateFormat(cube.SizedInternalFormat, out EAdvancedTextureFormatClass cubeFormatClass, out bool cubeIsDepth, out reason) ||
                !TryTranslateAddressMode(cube.UWrap, out EAdvancedSamplerAddressMode cubeAddressU) ||
                !TryTranslateAddressMode(cube.VWrap, out EAdvancedSamplerAddressMode cubeAddressV) ||
                !TryTranslateAddressMode(cube.WWrap, out EAdvancedSamplerAddressMode cubeAddressW))
            {
                source = default;
                compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceTextureFormat;
                return false;
            }
            EAdvancedTextureRecordFlags flags = (cube.ImportedColorSpace == ETextureColorSpace.Srgb ? EAdvancedTextureRecordFlags.Srgb : EAdvancedTextureRecordFlags.None) |
                (cube.RequiresStorageUsage ? EAdvancedTextureRecordFlags.Storage : EAdvancedTextureRecordFlags.None) |
                (cubeIsDepth ? EAdvancedTextureRecordFlags.Depth : EAdvancedTextureRecordFlags.None);
            uint cubeMipCount = checked((uint)cube.Mipmaps.Length);
            AdvancedSamplerRecord sampler = CreateCubeSamplerRecord(cube, cubeAddressU, cubeAddressV, cubeAddressW, cubeMipCount);
            source = new(cube, new AdvancedTextureRecord { Dimension = EAdvancedTextureDimension.Cube, Flags = flags, Width = cube.Extent, Height = cube.Extent, DepthOrLayers = 6u, MipCount = cubeMipCount, FormatClass = (uint)cubeFormatClass, DefaultSampler = AdvancedGpuHandle.Invalid, UvScaleBias = new Vector4(1f, 1f, 0f, 0f) }, sampler, fallback, 0u);
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
            reason = string.Empty;
            return true;
        }
        if (texture is not XRTexture2D texture2D)
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceTextureType;
            reason = $"Texture type '{texture.GetType().Name}' is not a 2D material texture.";
            return false;
        }

        // A streamed texture can complete on a worker while the scene boundary
        // is capturing resources. Require one stable source generation around
        // every source-metadata read; a later boundary will retry against the
        // completed source instead of publishing a torn description.
        ulong sourceContentGeneration = texture2D.CanonicalSourceContentGeneration;

        if (texture2D.Rectangle || texture2D.MultiSample)
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceTextureShape;
            reason = "Rectangle and multisample textures are not valid in the current 2D material binding layouts.";
            return false;
        }
        if (texture2D.Width == 0u || texture2D.Height == 0u || texture2D.Mipmaps.Length == 0)
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.EmptyResourceTexture;
            reason = "A logical material texture must have at least one nonempty mip level.";
            return false;
        }
        if (!float.IsFinite(texture2D.LodBias) || !float.IsFinite(texture2D.MaxAnisotropy))
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.NonFiniteResourceSampler;
            reason = "Logical sampler LOD bias and anisotropy must be finite.";
            return false;
        }
        if (!TryTranslateFormat(
                texture2D.SizedInternalFormat,
                out EAdvancedTextureFormatClass formatClass,
                out bool isDepth,
                out reason))
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceTextureFormat;
            return false;
        }
        if (!TryTranslateAddressMode(texture2D.UWrap, out EAdvancedSamplerAddressMode addressU) ||
            !TryTranslateAddressMode(texture2D.VWrap, out EAdvancedSamplerAddressMode addressV))
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceSamplerAddressMode;
            reason = "The source texture uses an unsupported sampler address mode.";
            return false;
        }
        if (!TryTranslateCompareOperation(
                texture2D.CompareFunc,
                out EAdvancedCompareOperation compareOperation))
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceSamplerCompareOperation;
            reason = "The source texture uses an unsupported comparison operation.";
            return false;
        }
        if (texture2D.EnableComparison && !isDepth)
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.ResourceComparisonRequiresDepth;
            reason = "Comparison sampling requires a depth texture format.";
            return false;
        }

        EAdvancedTextureRecordFlags textureFlags = EAdvancedTextureRecordFlags.None;
        if (texture2D.ImportedColorSpace == ETextureColorSpace.Srgb)
            textureFlags |= EAdvancedTextureRecordFlags.Srgb;
        if (texture2D.RequiresStorageUsage)
            textureFlags |= EAdvancedTextureRecordFlags.Storage;
        if (isDepth)
            textureFlags |= EAdvancedTextureRecordFlags.Depth;

        uint mipCount = ResolveLogicalMipCount(texture2D);
        AdvancedTextureRecord textureRecord = new()
        {
            Dimension = EAdvancedTextureDimension.Texture2D,
            Flags = textureFlags,
            Width = texture2D.Width,
            Height = texture2D.Height,
            DepthOrLayers = 1u,
            MipCount = mipCount,
            FormatClass = (uint)formatClass,
            EncodedReferenceIndex = 0u,
            DefaultSampler = AdvancedGpuHandle.Invalid,
            UvScaleBias = new Vector4(1.0f, 1.0f, 0.0f, 0.0f),
        };
        AdvancedSamplerRecord samplerRecord = CreateSamplerRecord(
            texture2D,
            addressU,
            addressV,
            compareOperation,
            mipCount);
        if (sourceContentGeneration != texture2D.CanonicalSourceContentGeneration)
        {
            source = default;
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.EmptyResourceTexture;
            reason = "The texture source changed while canonical metadata was being captured.";
            return false;
        }

        source = new(texture2D, textureRecord, samplerRecord, fallback, sourceContentGeneration);
        compatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
        reason = string.Empty;
        return true;
    }

    private static AdvancedSamplerRecord CreateSamplerRecord(
        XRTexture2D texture,
        EAdvancedSamplerAddressMode addressU,
        EAdvancedSamplerAddressMode addressV,
        EAdvancedCompareOperation compareOperation,
        uint mipCount)
    {
        bool nearestMinification = texture.MinFilter is
            ETexMinFilter.Nearest or
            ETexMinFilter.NearestMipmapNearest or
            ETexMinFilter.NearestMipmapLinear;
        bool nearestMagnification = texture.MagFilter == ETexMagFilter.Nearest;
        bool usesMipmaps = texture.MinFilter is not ETexMinFilter.Nearest and not ETexMinFilter.Linear;
        bool linearMipmapInterpolation = texture.MinFilter is
            ETexMinFilter.NearestMipmapLinear or
            ETexMinFilter.LinearMipmapLinear;
        EAdvancedSamplerRecordFlags flags = EAdvancedSamplerRecordFlags.None;
        if (usesMipmaps)
            flags |= EAdvancedSamplerRecordFlags.UsesMipmaps;
        if (linearMipmapInterpolation)
            flags |= EAdvancedSamplerRecordFlags.LinearMipmapInterpolation;
        if (nearestMinification)
            flags |= EAdvancedSamplerRecordFlags.NearestMinification;
        if (nearestMagnification)
            flags |= EAdvancedSamplerRecordFlags.NearestMagnification;
        if (texture.EnableComparison)
            flags |= EAdvancedSamplerRecordFlags.ComparisonEnabled;
        if (texture.MaxAnisotropy > 1.0f)
            flags |= EAdvancedSamplerRecordFlags.AnisotropyEnabled;

        float maximumMip = mipCount - 1u;
        float minimumLod = Math.Clamp(
            Math.Max(texture.MinLOD, texture.LargestMipmapLevel),
            0.0f,
            maximumMip);
        float maximumLod = Math.Clamp(
            Math.Min(texture.MaxLOD, texture.SmallestAllowedMipmapLevel),
            minimumLod,
            maximumMip);
        return new AdvancedSamplerRecord
        {
            Filter = nearestMinification && nearestMagnification
                ? EAdvancedSamplerFilter.Nearest
                : EAdvancedSamplerFilter.Linear,
            Flags = flags,
            AddressU = addressU,
            AddressV = addressV,
            AddressW = EAdvancedSamplerAddressMode.ClampToEdge,
            CompareOperation = texture.EnableComparison
                ? compareOperation
                : EAdvancedCompareOperation.Never,
            LodBiasMinMaxAnisotropy = new Vector4(
                CanonicalizeZero(texture.LodBias),
                CanonicalizeZero(minimumLod),
                CanonicalizeZero(maximumLod),
                CanonicalizeZero(texture.MaxAnisotropy)),
            BorderColor = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
        };
    }

    private static AdvancedSamplerRecord CreateCubeSamplerRecord(XRTextureCube texture, EAdvancedSamplerAddressMode addressU, EAdvancedSamplerAddressMode addressV, EAdvancedSamplerAddressMode addressW, uint mipCount)
    {
        bool nearestMinification = texture.MinFilter is ETexMinFilter.Nearest or ETexMinFilter.NearestMipmapNearest or ETexMinFilter.NearestMipmapLinear;
        bool nearestMagnification = texture.MagFilter == ETexMagFilter.Nearest;
        bool usesMipmaps = texture.MinFilter is not ETexMinFilter.Nearest and not ETexMinFilter.Linear;
        bool linearMipmapInterpolation = texture.MinFilter is ETexMinFilter.NearestMipmapLinear or ETexMinFilter.LinearMipmapLinear;
        EAdvancedSamplerRecordFlags flags = (usesMipmaps ? EAdvancedSamplerRecordFlags.UsesMipmaps : EAdvancedSamplerRecordFlags.None) |
            (linearMipmapInterpolation ? EAdvancedSamplerRecordFlags.LinearMipmapInterpolation : EAdvancedSamplerRecordFlags.None) |
            (nearestMinification ? EAdvancedSamplerRecordFlags.NearestMinification : EAdvancedSamplerRecordFlags.None) |
            (nearestMagnification ? EAdvancedSamplerRecordFlags.NearestMagnification : EAdvancedSamplerRecordFlags.None);
        float maximumMip = mipCount - 1u;
        return new AdvancedSamplerRecord { Filter = nearestMinification && nearestMagnification ? EAdvancedSamplerFilter.Nearest : EAdvancedSamplerFilter.Linear, Flags = flags, AddressU = addressU, AddressV = addressV, AddressW = addressW, CompareOperation = EAdvancedCompareOperation.Never, LodBiasMinMaxAnisotropy = new Vector4(CanonicalizeZero(texture.LodBias), Math.Clamp(texture.MinLOD, 0, maximumMip), Math.Clamp(texture.MaxLOD, 0, maximumMip), 1f), BorderColor = new Vector4(0f, 0f, 0f, 1f) };
    }

    private static uint ResolveLogicalMipCount(XRTexture2D texture)
    {
        uint sourceMipCount = checked((uint)Math.Max(texture.Mipmaps.Length, 1));
        if (texture.RuntimeManagedProgressiveUploadActive && sourceMipCount > 1u)
            return sourceMipCount;
        if (texture.AutoGenerateMipmaps || texture.SmallestAllowedMipmapLevel < 1000)
            return checked((uint)Math.Max(1, texture.SmallestMipmapLevel + 1));
        return sourceMipCount;
    }

    private static bool TryTranslateAddressMode(
        ETexWrapMode mode,
        out EAdvancedSamplerAddressMode translated)
    {
        translated = mode switch
        {
            ETexWrapMode.Repeat => EAdvancedSamplerAddressMode.Repeat,
            ETexWrapMode.MirroredRepeat => EAdvancedSamplerAddressMode.MirroredRepeat,
            ETexWrapMode.ClampToEdge => EAdvancedSamplerAddressMode.ClampToEdge,
            ETexWrapMode.ClampToBorder => EAdvancedSamplerAddressMode.ClampToBorder,
            _ => default,
        };
        return mode is ETexWrapMode.Repeat or ETexWrapMode.MirroredRepeat or
            ETexWrapMode.ClampToEdge or ETexWrapMode.ClampToBorder;
    }

    private static bool TryTranslateCompareOperation(
        ETextureCompareFunc operation,
        out EAdvancedCompareOperation translated)
    {
        translated = operation switch
        {
            ETextureCompareFunc.Never => EAdvancedCompareOperation.Never,
            ETextureCompareFunc.Less => EAdvancedCompareOperation.Less,
            ETextureCompareFunc.Equal => EAdvancedCompareOperation.Equal,
            ETextureCompareFunc.LessOrEqual => EAdvancedCompareOperation.LessOrEqual,
            ETextureCompareFunc.Greater => EAdvancedCompareOperation.Greater,
            ETextureCompareFunc.NotEqual => EAdvancedCompareOperation.NotEqual,
            ETextureCompareFunc.GreaterOrEqual => EAdvancedCompareOperation.GreaterOrEqual,
            ETextureCompareFunc.Always => EAdvancedCompareOperation.Always,
            _ => default,
        };
        return operation is >= ETextureCompareFunc.Never and <= ETextureCompareFunc.Always;
    }

    private static bool TryTranslateFormat(
        ESizedInternalFormat format,
        out EAdvancedTextureFormatClass translated,
        out bool isDepth,
        out string reason)
    {
        translated = format switch
        {
            ESizedInternalFormat.R8 => EAdvancedTextureFormatClass.R8,
            ESizedInternalFormat.R8Snorm => EAdvancedTextureFormatClass.R8Snorm,
            ESizedInternalFormat.R16 => EAdvancedTextureFormatClass.R16,
            ESizedInternalFormat.R16Snorm => EAdvancedTextureFormatClass.R16Snorm,
            ESizedInternalFormat.R16f => EAdvancedTextureFormatClass.R16Float,
            ESizedInternalFormat.R32f => EAdvancedTextureFormatClass.R32Float,
            ESizedInternalFormat.R8i => EAdvancedTextureFormatClass.R8Sint,
            ESizedInternalFormat.R8ui => EAdvancedTextureFormatClass.R8Uint,
            ESizedInternalFormat.R16i => EAdvancedTextureFormatClass.R16Sint,
            ESizedInternalFormat.R16ui => EAdvancedTextureFormatClass.R16Uint,
            ESizedInternalFormat.R32i => EAdvancedTextureFormatClass.R32Sint,
            ESizedInternalFormat.R32ui => EAdvancedTextureFormatClass.R32Uint,
            ESizedInternalFormat.Rg8 => EAdvancedTextureFormatClass.Rg8,
            ESizedInternalFormat.Rg8Snorm => EAdvancedTextureFormatClass.Rg8Snorm,
            ESizedInternalFormat.Rg16 => EAdvancedTextureFormatClass.Rg16,
            ESizedInternalFormat.Rg16Snorm => EAdvancedTextureFormatClass.Rg16Snorm,
            ESizedInternalFormat.Rg16f => EAdvancedTextureFormatClass.Rg16Float,
            ESizedInternalFormat.Rg32f => EAdvancedTextureFormatClass.Rg32Float,
            ESizedInternalFormat.Rg8i => EAdvancedTextureFormatClass.Rg8Sint,
            ESizedInternalFormat.Rg8ui => EAdvancedTextureFormatClass.Rg8Uint,
            ESizedInternalFormat.Rg16i => EAdvancedTextureFormatClass.Rg16Sint,
            ESizedInternalFormat.Rg16ui => EAdvancedTextureFormatClass.Rg16Uint,
            ESizedInternalFormat.Rg32i => EAdvancedTextureFormatClass.Rg32Sint,
            ESizedInternalFormat.Rg32ui => EAdvancedTextureFormatClass.Rg32Uint,
            ESizedInternalFormat.R3G3B2 => EAdvancedTextureFormatClass.R3G3B2,
            ESizedInternalFormat.Rgb4 => EAdvancedTextureFormatClass.Rgb4,
            ESizedInternalFormat.Rgb5 => EAdvancedTextureFormatClass.Rgb5,
            ESizedInternalFormat.Rgb8 => EAdvancedTextureFormatClass.Rgb8,
            ESizedInternalFormat.Rgb8Snorm => EAdvancedTextureFormatClass.Rgb8Snorm,
            ESizedInternalFormat.Rgb10 => EAdvancedTextureFormatClass.Rgb10,
            ESizedInternalFormat.Rgb12 => EAdvancedTextureFormatClass.Rgb12,
            ESizedInternalFormat.Rgb16Snorm => EAdvancedTextureFormatClass.Rgb16Snorm,
            ESizedInternalFormat.Rgba2 => EAdvancedTextureFormatClass.Rgba2,
            ESizedInternalFormat.Rgba4 => EAdvancedTextureFormatClass.Rgba4,
            ESizedInternalFormat.Srgb8 => EAdvancedTextureFormatClass.Srgb8,
            ESizedInternalFormat.Rgb16f => EAdvancedTextureFormatClass.Rgb16Float,
            ESizedInternalFormat.Rgb32f => EAdvancedTextureFormatClass.Rgb32Float,
            ESizedInternalFormat.R11fG11fB10f => EAdvancedTextureFormatClass.R11G11B10Float,
            ESizedInternalFormat.Rgb9E5 => EAdvancedTextureFormatClass.Rgb9E5,
            ESizedInternalFormat.Rgb8i => EAdvancedTextureFormatClass.Rgb8Sint,
            ESizedInternalFormat.Rgb8ui => EAdvancedTextureFormatClass.Rgb8Uint,
            ESizedInternalFormat.Rgb16i => EAdvancedTextureFormatClass.Rgb16Sint,
            ESizedInternalFormat.Rgb16ui => EAdvancedTextureFormatClass.Rgb16Uint,
            ESizedInternalFormat.Rgb32i => EAdvancedTextureFormatClass.Rgb32Sint,
            ESizedInternalFormat.Rgb32ui => EAdvancedTextureFormatClass.Rgb32Uint,
            ESizedInternalFormat.Rgb5A1 => EAdvancedTextureFormatClass.Rgb5A1,
            ESizedInternalFormat.Rgba8 => EAdvancedTextureFormatClass.Rgba8,
            ESizedInternalFormat.Rgba8Snorm => EAdvancedTextureFormatClass.Rgba8Snorm,
            ESizedInternalFormat.Rgb10A2 => EAdvancedTextureFormatClass.Rgb10A2,
            ESizedInternalFormat.Rgba12 => EAdvancedTextureFormatClass.Rgba12,
            ESizedInternalFormat.Rgba16 => EAdvancedTextureFormatClass.Rgba16,
            ESizedInternalFormat.Srgb8Alpha8 => EAdvancedTextureFormatClass.Srgb8Alpha8,
            ESizedInternalFormat.Rgba16f => EAdvancedTextureFormatClass.Rgba16Float,
            ESizedInternalFormat.Rgba32f => EAdvancedTextureFormatClass.Rgba32Float,
            ESizedInternalFormat.Rgba8i => EAdvancedTextureFormatClass.Rgba8Sint,
            ESizedInternalFormat.Rgba8ui => EAdvancedTextureFormatClass.Rgba8Uint,
            ESizedInternalFormat.Rgba16i => EAdvancedTextureFormatClass.Rgba16Sint,
            ESizedInternalFormat.Rgba16ui => EAdvancedTextureFormatClass.Rgba16Uint,
            ESizedInternalFormat.Rgba32i => EAdvancedTextureFormatClass.Rgba32Sint,
            ESizedInternalFormat.Rgba32ui => EAdvancedTextureFormatClass.Rgba32Uint,
            ESizedInternalFormat.DepthComponent16 => EAdvancedTextureFormatClass.Depth16,
            ESizedInternalFormat.DepthComponent24 => EAdvancedTextureFormatClass.Depth24,
            ESizedInternalFormat.DepthComponent32f => EAdvancedTextureFormatClass.Depth32Float,
            ESizedInternalFormat.Depth24Stencil8 => EAdvancedTextureFormatClass.Depth24Stencil8,
            ESizedInternalFormat.Depth32fStencil8 => EAdvancedTextureFormatClass.Depth32FloatStencil8,
            ESizedInternalFormat.StencilIndex8 => EAdvancedTextureFormatClass.Stencil8,
            _ => EAdvancedTextureFormatClass.Unknown,
        };
        isDepth = format is ESizedInternalFormat.DepthComponent16 or
            ESizedInternalFormat.DepthComponent24 or
            ESizedInternalFormat.DepthComponent32f or
            ESizedInternalFormat.Depth24Stencil8 or
            ESizedInternalFormat.Depth32fStencil8;
        reason = translated == EAdvancedTextureFormatClass.Unknown
            ? $"Texture format '{format}' has no stable advanced-resource translation."
            : string.Empty;
        return translated != EAdvancedTextureFormatClass.Unknown;
    }

    private static float CanonicalizeZero(float value)
        => value == 0.0f ? 0.0f : value;
}
