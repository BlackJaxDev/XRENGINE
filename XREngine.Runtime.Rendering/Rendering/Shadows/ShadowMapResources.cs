using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Shadows;

public enum EShadowProjectionLayout
{
    Texture2D = 0,
    Texture2DArray = 1,
    TextureCube = 2,
    Texture2DArrayCubeFaces = 3,
}

public enum ShadowDepthDirection
{
    Normal = 0,
    Reversed = 1,
}

public readonly record struct ShadowMapFormatDescriptor(
    EShadowMapStorageFormat StorageFormat,
    EPixelInternalFormat InternalFormat,
    EPixelFormat PixelFormat,
    EPixelType PixelType,
    ESizedInternalFormat SizedInternalFormat,
    int ChannelCount,
    int BytesPerTexel,
    bool RequiresLinearFiltering,
    bool RequiresSignedFloat);

public readonly record struct ShadowMapClearSentinel(Vector4 Value, int ChannelCount)
{
    public float this[int index] => index switch
    {
        0 => Value.X,
        1 => Value.Y,
        2 => Value.Z,
        3 => Value.W,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

public readonly record struct ShadowMapFormatSelection(
    EShadowMapEncoding RequestedEncoding,
    EShadowMapEncoding Encoding,
    ShadowMapFormatDescriptor Format,
    ShadowMapClearSentinel ClearSentinel,
    float PositiveExponent,
    float NegativeExponent,
    SkipReason DemotionReason)
{
    public bool WasDemoted => RequestedEncoding != Encoding;
}

public readonly record struct ShadowMapResourceCreateInfo(
    EShadowProjectionLayout Layout,
    EShadowMapEncoding Encoding,
    uint Width,
    uint Height,
    int LayerCount = 1,
    int MsaaSamples = 1,
    float PositiveExponent = ShadowMapResourceFactory.DefaultEvsmPositiveExponent,
    float NegativeExponent = ShadowMapResourceFactory.DefaultEvsmNegativeExponent,
    ShadowDepthDirection DepthDirection = ShadowDepthDirection.Normal,
    IShadowMapFormatCapabilities? Capabilities = null,
    EShadowMapStorageFormat? PreferredStorageFormat = null);

public interface IShadowMapFormatCapabilities
{
    bool SupportsRenderTarget(EShadowMapStorageFormat format);
    bool SupportsLinearFiltering(EShadowMapStorageFormat format);
}

public sealed class ShadowMapFormatCapabilities : IShadowMapFormatCapabilities
{
    public static readonly ShadowMapFormatCapabilities AllSupported = new();

    public bool SupportsRenderTarget(EShadowMapStorageFormat format) => true;
    public bool SupportsLinearFiltering(EShadowMapStorageFormat format) => true;
}

public sealed class ShadowMapResource
{
    internal ShadowMapResource(
        ShadowMapFormatSelection selection,
        EShadowProjectionLayout layout,
        XRTexture samplingTexture,
        XRTexture? rasterDepthTexture,
        XRFrameBuffer[] frameBuffers,
        uint width,
        uint height,
        int layerCount,
        int msaaSamples)
    {
        RequestedEncoding = selection.RequestedEncoding;
        Encoding = selection.Encoding;
        Format = selection.Format;
        ClearSentinel = selection.ClearSentinel;
        Layout = layout;
        SamplingTexture = samplingTexture;
        RasterDepthTexture = rasterDepthTexture;
        FrameBuffers = frameBuffers;
        Width = width;
        Height = height;
        LayerCount = layerCount;
        MsaaSamples = msaaSamples;
    }

    public EShadowMapEncoding RequestedEncoding { get; }
    public EShadowMapEncoding Encoding { get; }
    public ShadowMapFormatDescriptor Format { get; }
    public ShadowMapClearSentinel ClearSentinel { get; }
    public EShadowProjectionLayout Layout { get; }
    public XRTexture SamplingTexture { get; }
    public XRTexture? RasterDepthTexture { get; }
    public XRFrameBuffer[] FrameBuffers { get; }
    public uint Width { get; }
    public uint Height { get; }
    public int LayerCount { get; }
    public int MsaaSamples { get; }
}

public static class ShadowMapResourceFactory
{
    public const float DefaultMomentMinVariance = 0.00002f;
    public const float DefaultMomentLightBleedReduction = 0.2f;
    public const float DefaultEvsmPositiveExponent = 5.0f;
    public const float DefaultEvsmNegativeExponent = 5.0f;
    public const float HalfFloatEvsmExponentClamp = 5.0f;
    public const float FloatEvsmExponentClamp = 15.0f;

    public static ShadowMapResource Create(in ShadowMapResourceCreateInfo createInfo)
    {
        ShadowMapFormatSelection selection = SelectFormat(
            createInfo.Encoding,
            createInfo.Capabilities ?? ShadowMapFormatCapabilities.AllSupported,
            createInfo.PositiveExponent,
            createInfo.NegativeExponent,
            createInfo.DepthDirection,
            createInfo.PreferredStorageFormat);

        uint width = Math.Max(1u, createInfo.Width);
        uint height = Math.Max(1u, createInfo.Height);
        int layerCount = NormalizeLayerCount(createInfo.Layout, createInfo.LayerCount);
        int msaaSamples = Math.Max(1, createInfo.MsaaSamples);

        XRTexture samplingTexture = CreateSamplingTexture(createInfo.Layout, selection.Format, width, height, layerCount);
        XRTexture? rasterDepthTexture = CreateRasterDepthTexture(createInfo.Layout, width, height, layerCount);
        XRFrameBuffer[] frameBuffers = CreateFrameBuffers(createInfo.Layout, samplingTexture, rasterDepthTexture, layerCount);

        return new ShadowMapResource(selection, createInfo.Layout, samplingTexture, rasterDepthTexture, frameBuffers, width, height, layerCount, msaaSamples);
    }

    public static ShadowMapFormatSelection SelectFormat(
        EShadowMapEncoding requestedEncoding,
        IShadowMapFormatCapabilities? capabilities = null,
        float positiveExponent = DefaultEvsmPositiveExponent,
        float negativeExponent = DefaultEvsmNegativeExponent,
        ShadowDepthDirection depthDirection = ShadowDepthDirection.Normal,
        EShadowMapStorageFormat? preferredStorageFormat = null)
    {
        capabilities ??= ShadowMapFormatCapabilities.AllSupported;
        EShadowMapEncoding encoding = requestedEncoding;
        SkipReason reason = SkipReason.None;

        while (true)
        {
            ShadowMapFormatDescriptor format = GetPreferredFormat(encoding, preferredStorageFormat);
            if (IsFormatSupported(format, capabilities))
            {
                float clampedPositive = ClampEvsmExponent(format.StorageFormat, positiveExponent);
                float clampedNegative = ClampEvsmExponent(format.StorageFormat, negativeExponent);
                ShadowMapClearSentinel clear = CalculateClearSentinel(encoding, clampedPositive, clampedNegative, depthDirection);
                return new ShadowMapFormatSelection(requestedEncoding, encoding, format, clear, clampedPositive, clampedNegative, reason);
            }

            if (encoding == EShadowMapEncoding.Depth)
            {
                ShadowMapFormatDescriptor fallback = GetFormatDescriptor(EShadowMapStorageFormat.R16Float);
                ShadowMapClearSentinel clear = CalculateClearSentinel(EShadowMapEncoding.Depth, positiveExponent, negativeExponent, depthDirection);
                return new ShadowMapFormatSelection(requestedEncoding, EShadowMapEncoding.Depth, fallback, clear, positiveExponent, negativeExponent, SkipReason.UnsupportedFormat);
            }

            encoding = DemoteEncoding(encoding);
            reason = SkipReason.UnsupportedFormat;
            preferredStorageFormat = null;
        }
    }

    public static ShadowMapFormatDescriptor GetPreferredFormat(
        EShadowMapEncoding encoding,
        EShadowMapStorageFormat? preferredStorageFormat = null)
    {
        if (preferredStorageFormat.HasValue && IsStorageFormatCompatible(encoding, preferredStorageFormat.Value))
            return GetFormatDescriptor(preferredStorageFormat.Value);

        return GetFormatDescriptor(encoding switch
        {
            EShadowMapEncoding.Variance2 => EShadowMapStorageFormat.RG16Float,
            EShadowMapEncoding.ExponentialVariance2 => EShadowMapStorageFormat.RG16Float,
            EShadowMapEncoding.ExponentialVariance4 => EShadowMapStorageFormat.RGBA16Float,
            _ => EShadowMapStorageFormat.R16Float,
        });
    }

    public static ShadowMapFormatDescriptor GetFormatDescriptor(EShadowMapStorageFormat format)
        => format switch
        {
            EShadowMapStorageFormat.R8UNorm => new(format, EPixelInternalFormat.R8, EPixelFormat.Red, EPixelType.UnsignedByte, ESizedInternalFormat.R8, 1, 1, false, false),
            EShadowMapStorageFormat.R16UNorm => new(format, EPixelInternalFormat.R16, EPixelFormat.Red, EPixelType.UnsignedShort, ESizedInternalFormat.R16, 1, 2, false, false),
            EShadowMapStorageFormat.R32Float => new(format, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f, 1, 4, false, true),
            EShadowMapStorageFormat.RG16Float => new(format, EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f, 2, 4, true, true),
            EShadowMapStorageFormat.RG32Float => new(format, EPixelInternalFormat.RG32f, EPixelFormat.Rg, EPixelType.Float, ESizedInternalFormat.Rg32f, 2, 8, true, true),
            EShadowMapStorageFormat.RGBA16Float => new(format, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, 4, 8, true, true),
            EShadowMapStorageFormat.RGBA32Float => new(format, EPixelInternalFormat.Rgba32f, EPixelFormat.Rgba, EPixelType.Float, ESizedInternalFormat.Rgba32f, 4, 16, true, true),
            EShadowMapStorageFormat.Depth16 => new(format, EPixelInternalFormat.DepthComponent16, EPixelFormat.DepthComponent, EPixelType.UnsignedShort, ESizedInternalFormat.DepthComponent16, 1, 2, false, false),
            EShadowMapStorageFormat.Depth24 => new(format, EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthComponent, EPixelType.UnsignedInt, ESizedInternalFormat.DepthComponent24, 1, 4, false, false),
            EShadowMapStorageFormat.Depth32Float => new(format, EPixelInternalFormat.DepthComponent32f, EPixelFormat.DepthComponent, EPixelType.Float, ESizedInternalFormat.DepthComponent32f, 1, 4, false, true),
            _ => new(EShadowMapStorageFormat.R16Float, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, ESizedInternalFormat.R16f, 1, 2, false, true),
        };

    public static bool IsStorageFormatCompatible(EShadowMapEncoding encoding, EShadowMapStorageFormat format)
        => encoding switch
        {
            EShadowMapEncoding.Variance2 => format is EShadowMapStorageFormat.RG16Float or EShadowMapStorageFormat.RG32Float,
            EShadowMapEncoding.ExponentialVariance2 => format is EShadowMapStorageFormat.RG16Float or EShadowMapStorageFormat.RG32Float,
            EShadowMapEncoding.ExponentialVariance4 => format is EShadowMapStorageFormat.RGBA16Float or EShadowMapStorageFormat.RGBA32Float,
            _ => format is EShadowMapStorageFormat.R8UNorm or EShadowMapStorageFormat.R16UNorm or EShadowMapStorageFormat.R16Float or EShadowMapStorageFormat.R32Float or EShadowMapStorageFormat.Depth16 or EShadowMapStorageFormat.Depth24 or EShadowMapStorageFormat.Depth32Float,
        };

    public static ShadowMapClearSentinel CalculateClearSentinel(
        EShadowMapEncoding encoding,
        float positiveExponent,
        float negativeExponent,
        ShadowDepthDirection depthDirection = ShadowDepthDirection.Normal)
    {
        float unoccludedDepth = depthDirection == ShadowDepthDirection.Reversed ? 0.0f : 1.0f;
        return encoding switch
        {
            EShadowMapEncoding.Variance2 => new(new Vector4(unoccludedDepth, unoccludedDepth * unoccludedDepth, 0.0f, 0.0f), 2),
            EShadowMapEncoding.ExponentialVariance2 => CreateEvsm2Clear(unoccludedDepth, positiveExponent),
            EShadowMapEncoding.ExponentialVariance4 => CreateEvsm4Clear(unoccludedDepth, positiveExponent, negativeExponent),
            _ => new(new Vector4(unoccludedDepth, 0.0f, 0.0f, 0.0f), 1),
        };
    }

    public static float ClampEvsmExponent(EShadowMapStorageFormat format, float exponent)
    {
        if (!float.IsFinite(exponent))
            return DefaultEvsmPositiveExponent;

        float clamp = format is EShadowMapStorageFormat.RG32Float or EShadowMapStorageFormat.RGBA32Float
            ? FloatEvsmExponentClamp
            : HalfFloatEvsmExponentClamp;
        return Math.Clamp(exponent, 0.0f, clamp);
    }

    public static EShadowMapEncoding DemoteEncoding(EShadowMapEncoding encoding)
        => EShadowMapEncoding.Depth;

    private static bool IsFormatSupported(ShadowMapFormatDescriptor format, IShadowMapFormatCapabilities capabilities)
        => capabilities.SupportsRenderTarget(format.StorageFormat)
        && (!format.RequiresLinearFiltering || capabilities.SupportsLinearFiltering(format.StorageFormat));

    private static ShadowMapClearSentinel CreateEvsm2Clear(float depth, float exponent)
    {
        float positive = MathF.Exp(exponent * depth);
        return new(new Vector4(positive, positive * positive, 0.0f, 0.0f), 2);
    }

    private static ShadowMapClearSentinel CreateEvsm4Clear(float depth, float positiveExponent, float negativeExponent)
    {
        float positive = MathF.Exp(positiveExponent * depth);
        float negativeMagnitude = MathF.Exp(-negativeExponent * depth);
        return new(new Vector4(positive, positive * positive, -negativeMagnitude, negativeMagnitude * negativeMagnitude), 4);
    }

    private static int NormalizeLayerCount(EShadowProjectionLayout layout, int requestedLayerCount)
        => layout switch
        {
            EShadowProjectionLayout.TextureCube => 6,
            EShadowProjectionLayout.Texture2DArrayCubeFaces => Math.Max(6, requestedLayerCount),
            EShadowProjectionLayout.Texture2DArray => Math.Max(1, requestedLayerCount),
            _ => 1,
        };

    private static XRTexture CreateSamplingTexture(EShadowProjectionLayout layout, ShadowMapFormatDescriptor format, uint width, uint height, int layerCount)
    {
        return layout switch
        {
            EShadowProjectionLayout.Texture2DArray or EShadowProjectionLayout.Texture2DArrayCubeFaces => ConfigureSamplingTexture(new XRTexture2DArray((uint)layerCount, width, height, format.InternalFormat, format.PixelFormat, format.PixelType, allocateData: false), format),
            EShadowProjectionLayout.TextureCube => ConfigureSamplingTexture(new XRTextureCube(Math.Max(width, height), format.InternalFormat, format.PixelFormat, format.PixelType, allocateData: false), format),
            _ => ConfigureSamplingTexture(new XRTexture2D(width, height, format.InternalFormat, format.PixelFormat, format.PixelType, allocateData: false), format),
        };
    }

    private static XRTexture? CreateRasterDepthTexture(EShadowProjectionLayout layout, uint width, uint height, int layerCount)
    {
        return layout switch
        {
            EShadowProjectionLayout.Texture2DArray or EShadowProjectionLayout.Texture2DArrayCubeFaces => ConfigureDepthTexture(new XRTexture2DArray((uint)layerCount, width, height, EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthComponent, EPixelType.UnsignedInt, allocateData: false)),
            EShadowProjectionLayout.TextureCube => ConfigureDepthTexture(new XRTextureCube(Math.Max(width, height), EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthComponent, EPixelType.UnsignedInt, allocateData: false)),
            _ => ConfigureDepthTexture(new XRTexture2D(width, height, EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthComponent, EPixelType.UnsignedInt, allocateData: false)),
        };
    }

    private static XRFrameBuffer[] CreateFrameBuffers(EShadowProjectionLayout layout, XRTexture samplingTexture, XRTexture? rasterDepthTexture, int layerCount)
    {
        int count = layout == EShadowProjectionLayout.Texture2D ? 1 : layerCount;
        XRFrameBuffer[] frameBuffers = new XRFrameBuffer[count];
        for (int i = 0; i < count; i++)
        {
            int layerIndex = layout == EShadowProjectionLayout.Texture2D ? -1 : i;
            frameBuffers[i] = rasterDepthTexture is IFrameBufferAttachement depthAttachment
                ? new XRFrameBuffer(
                    ((IFrameBufferAttachement)samplingTexture, EFrameBufferAttachment.ColorAttachment0, 0, layerIndex),
                    (depthAttachment, EFrameBufferAttachment.DepthAttachment, 0, layerIndex))
                : new XRFrameBuffer(((IFrameBufferAttachement)samplingTexture, EFrameBufferAttachment.ColorAttachment0, 0, layerIndex));
        }

        return frameBuffers;
    }

    private static T ConfigureSamplingTexture<T>(T texture, ShadowMapFormatDescriptor format)
        where T : XRTexture
    {
        texture.SamplerName = "ShadowMap";
        ApplySamplerState(texture, format.RequiresLinearFiltering);
        texture.FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0;
        return texture;
    }

    private static T ConfigureDepthTexture<T>(T texture)
        where T : XRTexture
    {
        texture.SamplerName = "ShadowRasterDepth";
        ApplySamplerState(texture, linearFiltering: false);
        texture.FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment;
        return texture;
    }

    private static void ApplySamplerState(XRTexture texture, bool linearFiltering)
    {
        ETexMinFilter minFilter = linearFiltering ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
        ETexMagFilter magFilter = linearFiltering ? ETexMagFilter.Linear : ETexMagFilter.Nearest;

        switch (texture)
        {
            case XRTexture2D texture2D:
                texture2D.MinFilter = minFilter;
                texture2D.MagFilter = magFilter;
                texture2D.UWrap = ETexWrapMode.ClampToEdge;
                texture2D.VWrap = ETexWrapMode.ClampToEdge;
                break;
            case XRTexture2DArray textureArray:
                textureArray.MinFilter = minFilter;
                textureArray.MagFilter = magFilter;
                textureArray.UWrap = ETexWrapMode.ClampToEdge;
                textureArray.VWrap = ETexWrapMode.ClampToEdge;
                break;
            case XRTextureCube textureCube:
                textureCube.MinFilter = minFilter;
                textureCube.MagFilter = magFilter;
                textureCube.UWrap = ETexWrapMode.ClampToEdge;
                textureCube.VWrap = ETexWrapMode.ClampToEdge;
                textureCube.WWrap = ETexWrapMode.ClampToEdge;
                break;
        }
    }
}