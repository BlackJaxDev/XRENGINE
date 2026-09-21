using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Rendering;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides conversions from engine-side <see cref="ESizedInternalFormat"/> values
/// to Silk.NET Vulkan <see cref="Format"/> values. Used by both image-backed textures
/// and buffer-view textures.
/// </summary>
internal unsafe static class VkFormatConversions
{
    /// <summary>
    /// Converts an engine <see cref="ESizedInternalFormat"/> to the corresponding
    /// Vulkan <see cref="Format"/>. Covers color, SNorm, depth, depth-stencil,
    /// and stencil-only formats.
    /// </summary>
    public static Format FromSizedFormat(ESizedInternalFormat sizedFormat)
        => sizedFormat switch
        {
            // Red
            ESizedInternalFormat.R8 => Format.R8Unorm,
            ESizedInternalFormat.R8Snorm => Format.R8SNorm,
            ESizedInternalFormat.R8i => Format.R8Sint,
            ESizedInternalFormat.R8ui => Format.R8Uint,
            ESizedInternalFormat.R16 => Format.R16Unorm,
            ESizedInternalFormat.R16Snorm => Format.R16SNorm,
            ESizedInternalFormat.R16f => Format.R16Sfloat,
            ESizedInternalFormat.R16i => Format.R16Sint,
            ESizedInternalFormat.R16ui => Format.R16Uint,
            ESizedInternalFormat.R32f => Format.R32Sfloat,
            ESizedInternalFormat.R32i => Format.R32Sint,
            ESizedInternalFormat.R32ui => Format.R32Uint,

            // Red-Green
            ESizedInternalFormat.Rg8 => Format.R8G8Unorm,
            ESizedInternalFormat.Rg8Snorm => Format.R8G8SNorm,
            ESizedInternalFormat.Rg8i => Format.R8G8Sint,
            ESizedInternalFormat.Rg8ui => Format.R8G8Uint,
            ESizedInternalFormat.Rg16 => Format.R16G16Unorm,
            ESizedInternalFormat.Rg16Snorm => Format.R16G16SNorm,
            ESizedInternalFormat.Rg16f => Format.R16G16Sfloat,
            ESizedInternalFormat.Rg16i => Format.R16G16Sint,
            ESizedInternalFormat.Rg16ui => Format.R16G16Uint,
            ESizedInternalFormat.Rg32f => Format.R32G32Sfloat,
            ESizedInternalFormat.Rg32i => Format.R32G32Sint,
            ESizedInternalFormat.Rg32ui => Format.R32G32Uint,

            // RGB — promoted to RGBA equivalents because 3-channel formats
            // (VK_FORMAT_R8G8B8_*) are not supported for images on most desktop GPUs.
            // The pixel upload path must pad 3-channel data to 4 channels to match.
            ESizedInternalFormat.Rgb8 => Format.R8G8B8A8Unorm,
            ESizedInternalFormat.Rgb8Snorm => Format.R8G8B8A8SNorm,
            ESizedInternalFormat.Rgb8i => Format.R8G8B8A8Sint,
            ESizedInternalFormat.Rgb8ui => Format.R8G8B8A8Uint,
            ESizedInternalFormat.Rgb16Snorm => Format.R16G16B16A16SNorm,
            ESizedInternalFormat.Rgb16f => Format.R16G16B16A16Sfloat,
            ESizedInternalFormat.Rgb16i => Format.R16G16B16A16Sint,
            ESizedInternalFormat.Rgb16ui => Format.R16G16B16A16Uint,
            ESizedInternalFormat.Rgb32f => Format.R32G32B32A32Sfloat,
            ESizedInternalFormat.Rgb32i => Format.R32G32B32A32Sint,
            ESizedInternalFormat.Rgb32ui => Format.R32G32B32A32Uint,
            ESizedInternalFormat.Srgb8 => Format.R8G8B8A8Srgb,
            ESizedInternalFormat.R11fG11fB10f => Format.B10G11R11UfloatPack32,
            ESizedInternalFormat.Rgb9E5 => Format.E5B9G9R9UfloatPack32,

            // RGBA
            ESizedInternalFormat.Rgba8 => Format.R8G8B8A8Unorm,
            ESizedInternalFormat.Rgba8Snorm => Format.R8G8B8A8SNorm,
            ESizedInternalFormat.Rgba8i => Format.R8G8B8A8Sint,
            ESizedInternalFormat.Rgba8ui => Format.R8G8B8A8Uint,
            ESizedInternalFormat.Rgba16 => Format.R16G16B16A16Unorm,
            ESizedInternalFormat.Rgba16f => Format.R16G16B16A16Sfloat,
            ESizedInternalFormat.Rgba16i => Format.R16G16B16A16Sint,
            ESizedInternalFormat.Rgba16ui => Format.R16G16B16A16Uint,
            ESizedInternalFormat.Rgba32f => Format.R32G32B32A32Sfloat,
            ESizedInternalFormat.Rgba32i => Format.R32G32B32A32Sint,
            ESizedInternalFormat.Rgba32ui => Format.R32G32B32A32Uint,
            ESizedInternalFormat.Srgb8Alpha8 => Format.R8G8B8A8Srgb,
            ESizedInternalFormat.Rgb10A2 => Format.A2B10G10R10UnormPack32,

            // Packed
            ESizedInternalFormat.R3G3B2 => Format.Undefined, // no direct Vulkan equivalent
            ESizedInternalFormat.Rgb4 => Format.R4G4B4A4UnormPack16, // approximate
            ESizedInternalFormat.Rgb5 => Format.R5G6B5UnormPack16,   // approximate
            ESizedInternalFormat.Rgb5A1 => Format.R5G5B5A1UnormPack16,
            ESizedInternalFormat.Rgb10 => Format.A2B10G10R10UnormPack32, // approximate
            ESizedInternalFormat.Rgb12 => Format.R16G16B16Unorm,        // approximate
            ESizedInternalFormat.Rgba2 => Format.R8G8B8A8Unorm,         // no direct equivalent
            ESizedInternalFormat.Rgba4 => Format.R4G4B4A4UnormPack16,
            ESizedInternalFormat.Rgba12 => Format.R16G16B16A16Unorm,    // approximate

            // Depth
            ESizedInternalFormat.DepthComponent16 => Format.D16Unorm,
            ESizedInternalFormat.DepthComponent24 => Format.X8D24UnormPack32,
            ESizedInternalFormat.DepthComponent32f => Format.D32Sfloat,

            // Depth-Stencil
            ESizedInternalFormat.Depth24Stencil8 => Format.D24UnormS8Uint,
            ESizedInternalFormat.Depth32fStencil8 => Format.D32SfloatS8Uint,

            // Stencil
            ESizedInternalFormat.StencilIndex8 => Format.S8Uint,

            _ => Format.R8G8B8A8Unorm,
        };

    /// <summary>
    /// Converts an engine <see cref="EPixelInternalFormat"/> (typically from mip data)
    /// to a Vulkan <see cref="Format"/>.
    /// </summary>
    public static Format FromPixelInternalFormat(EPixelInternalFormat internalFormat)
        => internalFormat switch
        {
            // Red channel formats
            EPixelInternalFormat.R8 => FromSizedFormat(ESizedInternalFormat.R8),
            EPixelInternalFormat.R8SNorm => FromSizedFormat(ESizedInternalFormat.R8Snorm),
            EPixelInternalFormat.R16 => FromSizedFormat(ESizedInternalFormat.R16),
            EPixelInternalFormat.R16SNorm => FromSizedFormat(ESizedInternalFormat.R16Snorm),
            EPixelInternalFormat.R16f => FromSizedFormat(ESizedInternalFormat.R16f),
            EPixelInternalFormat.R32f => FromSizedFormat(ESizedInternalFormat.R32f),
            EPixelInternalFormat.R8i => FromSizedFormat(ESizedInternalFormat.R8i),
            EPixelInternalFormat.R8ui => FromSizedFormat(ESizedInternalFormat.R8ui),
            EPixelInternalFormat.R16i => FromSizedFormat(ESizedInternalFormat.R16i),
            EPixelInternalFormat.R16ui => FromSizedFormat(ESizedInternalFormat.R16ui),
            EPixelInternalFormat.R32i => FromSizedFormat(ESizedInternalFormat.R32i),
            EPixelInternalFormat.R32ui => FromSizedFormat(ESizedInternalFormat.R32ui),

            // RG channel formats
            EPixelInternalFormat.RG8 => FromSizedFormat(ESizedInternalFormat.Rg8),
            EPixelInternalFormat.RG8SNorm => FromSizedFormat(ESizedInternalFormat.Rg8Snorm),
            EPixelInternalFormat.RG16 => FromSizedFormat(ESizedInternalFormat.Rg16),
            EPixelInternalFormat.RG16SNorm => FromSizedFormat(ESizedInternalFormat.Rg16Snorm),
            EPixelInternalFormat.RG16f => FromSizedFormat(ESizedInternalFormat.Rg16f),
            EPixelInternalFormat.RG32f => FromSizedFormat(ESizedInternalFormat.Rg32f),
            EPixelInternalFormat.RG8i => FromSizedFormat(ESizedInternalFormat.Rg8i),
            EPixelInternalFormat.RG8ui => FromSizedFormat(ESizedInternalFormat.Rg8ui),
            EPixelInternalFormat.RG16i => FromSizedFormat(ESizedInternalFormat.Rg16i),
            EPixelInternalFormat.RG16ui => FromSizedFormat(ESizedInternalFormat.Rg16ui),
            EPixelInternalFormat.RG32i => FromSizedFormat(ESizedInternalFormat.Rg32i),
            EPixelInternalFormat.RG32ui => FromSizedFormat(ESizedInternalFormat.Rg32ui),

            // RGB formats
            EPixelInternalFormat.R3G3B2 => FromSizedFormat(ESizedInternalFormat.R3G3B2),
            EPixelInternalFormat.Rgb4 => FromSizedFormat(ESizedInternalFormat.Rgb4),
            EPixelInternalFormat.Rgb5 => FromSizedFormat(ESizedInternalFormat.Rgb5),
            EPixelInternalFormat.Rgb8 => FromSizedFormat(ESizedInternalFormat.Rgb8),
            EPixelInternalFormat.Rgb8SNorm => FromSizedFormat(ESizedInternalFormat.Rgb8Snorm),
            EPixelInternalFormat.Rgb10 => FromSizedFormat(ESizedInternalFormat.Rgb10),
            EPixelInternalFormat.Rgb12 => FromSizedFormat(ESizedInternalFormat.Rgb12),
            EPixelInternalFormat.Rgb16SNorm => FromSizedFormat(ESizedInternalFormat.Rgb16Snorm),
            EPixelInternalFormat.Srgb8 => FromSizedFormat(ESizedInternalFormat.Srgb8),
            EPixelInternalFormat.Rgb16f => FromSizedFormat(ESizedInternalFormat.Rgb16f),
            EPixelInternalFormat.Rgb32f => FromSizedFormat(ESizedInternalFormat.Rgb32f),
            EPixelInternalFormat.R11fG11fB10f => FromSizedFormat(ESizedInternalFormat.R11fG11fB10f),
            EPixelInternalFormat.Rgb9E5 => FromSizedFormat(ESizedInternalFormat.Rgb9E5),
            EPixelInternalFormat.Rgb8i => FromSizedFormat(ESizedInternalFormat.Rgb8i),
            EPixelInternalFormat.Rgb8ui => FromSizedFormat(ESizedInternalFormat.Rgb8ui),
            EPixelInternalFormat.Rgb16i => FromSizedFormat(ESizedInternalFormat.Rgb16i),
            EPixelInternalFormat.Rgb16ui => FromSizedFormat(ESizedInternalFormat.Rgb16ui),
            EPixelInternalFormat.Rgb32i => FromSizedFormat(ESizedInternalFormat.Rgb32i),
            EPixelInternalFormat.Rgb32ui => FromSizedFormat(ESizedInternalFormat.Rgb32ui),

            // RGBA formats
            EPixelInternalFormat.Rgba2 => FromSizedFormat(ESizedInternalFormat.Rgba2),
            EPixelInternalFormat.Rgba4 => FromSizedFormat(ESizedInternalFormat.Rgba4),
            EPixelInternalFormat.Rgb5A1 => FromSizedFormat(ESizedInternalFormat.Rgb5A1),
            EPixelInternalFormat.Rgba8 => FromSizedFormat(ESizedInternalFormat.Rgba8),
            EPixelInternalFormat.Rgba8SNorm => FromSizedFormat(ESizedInternalFormat.Rgba8Snorm),
            EPixelInternalFormat.Rgb10A2 => FromSizedFormat(ESizedInternalFormat.Rgb10A2),
            EPixelInternalFormat.Rgba12 => FromSizedFormat(ESizedInternalFormat.Rgba12),
            EPixelInternalFormat.Rgba16 => FromSizedFormat(ESizedInternalFormat.Rgba16),
            EPixelInternalFormat.Srgb8Alpha8 => FromSizedFormat(ESizedInternalFormat.Srgb8Alpha8),
            EPixelInternalFormat.Rgba16f => FromSizedFormat(ESizedInternalFormat.Rgba16f),
            EPixelInternalFormat.Rgba32f => FromSizedFormat(ESizedInternalFormat.Rgba32f),
            EPixelInternalFormat.Rgba8i => FromSizedFormat(ESizedInternalFormat.Rgba8i),
            EPixelInternalFormat.Rgba8ui => FromSizedFormat(ESizedInternalFormat.Rgba8ui),
            EPixelInternalFormat.Rgba16i => FromSizedFormat(ESizedInternalFormat.Rgba16i),
            EPixelInternalFormat.Rgba16ui => FromSizedFormat(ESizedInternalFormat.Rgba16ui),
            EPixelInternalFormat.Rgba32i => FromSizedFormat(ESizedInternalFormat.Rgba32i),
            EPixelInternalFormat.Rgba32ui => FromSizedFormat(ESizedInternalFormat.Rgba32ui),

            // Depth formats
            EPixelInternalFormat.DepthComponent16 => FromSizedFormat(ESizedInternalFormat.DepthComponent16),
            EPixelInternalFormat.DepthComponent24 => FromSizedFormat(ESizedInternalFormat.DepthComponent24),
            EPixelInternalFormat.DepthComponent32f => FromSizedFormat(ESizedInternalFormat.DepthComponent32f),

            // Depth-stencil formats
            EPixelInternalFormat.Depth24Stencil8 => FromSizedFormat(ESizedInternalFormat.Depth24Stencil8),
            EPixelInternalFormat.Depth32fStencil8 => FromSizedFormat(ESizedInternalFormat.Depth32fStencil8),

            // Stencil formats
            EPixelInternalFormat.StencilIndex8 => FromSizedFormat(ESizedInternalFormat.StencilIndex8),

            _ => Format.Undefined,
        };

    /// <summary>
    /// Returns the number of bytes per texel for the given Vulkan <see cref="Format"/>.
    /// Used to validate that staging buffers are large enough for the target image.
    /// Returns 0 for unrecognised or compressed formats.
    /// </summary>
    public static uint GetBytesPerTexel(Format format)
        => format switch
        {
            // 1 byte
            Format.R8Unorm or Format.R8SNorm or Format.R8Uint or Format.R8Sint
                or Format.S8Uint => 1,

            // 2 bytes
            Format.R8G8Unorm or Format.R8G8SNorm or Format.R8G8Uint or Format.R8G8Sint
                or Format.R16Unorm or Format.R16SNorm or Format.R16Sfloat or Format.R16Uint or Format.R16Sint
                or Format.R5G6B5UnormPack16 or Format.R5G5B5A1UnormPack16 or Format.R4G4B4A4UnormPack16
                or Format.D16Unorm => 2,

            // 3 bytes
            Format.R8G8B8Unorm or Format.R8G8B8SNorm or Format.R8G8B8Uint or Format.R8G8B8Sint
                or Format.R8G8B8Srgb or Format.B8G8R8Unorm or Format.B8G8R8Srgb => 3,

            // 6 bytes
            Format.R16G16B16Unorm or Format.R16G16B16SNorm => 6,

            // 4 bytes
            Format.R8G8B8A8Unorm or Format.R8G8B8A8SNorm or Format.R8G8B8A8Uint or Format.R8G8B8A8Sint
                or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb
                or Format.A2B10G10R10UnormPack32 or Format.A2R10G10B10UnormPack32
                or Format.R16G16Unorm or Format.R16G16SNorm or Format.R16G16Sfloat or Format.R16G16Uint or Format.R16G16Sint
                or Format.R32Sfloat or Format.R32Uint or Format.R32Sint
                or Format.B10G11R11UfloatPack32 or Format.E5B9G9R9UfloatPack32
                or Format.X8D24UnormPack32 or Format.D32Sfloat or Format.D24UnormS8Uint => 4,

            // 5 bytes
            Format.D16UnormS8Uint => 3, // 16-bit depth + 8-bit stencil

            // 8 bytes
            Format.R16G16B16A16Unorm or Format.R16G16B16A16SNorm or Format.R16G16B16A16Sfloat
                or Format.R16G16B16A16Uint or Format.R16G16B16A16Sint
                or Format.R32G32Sfloat or Format.R32G32Uint or Format.R32G32Sint
                or Format.D32SfloatS8Uint => 8,

            // 12 bytes
            Format.R32G32B32Sfloat or Format.R32G32B32Uint or Format.R32G32B32Sint => 12,

            // 16 bytes
            Format.R32G32B32A32Sfloat or Format.R32G32B32A32Uint or Format.R32G32B32A32Sint
                or Format.R64G64Sfloat or Format.R64G64Uint or Format.R64G64Sint => 16,

            _ => 0,
        };

    internal static unsafe DataSource? CreateNormalizedUploadData2D(
        Mipmap2D mipmap,
        Format destinationFormat,
        out bool ownsData)
    {
        ownsData = false;

        DataSource? sourceData = mipmap.Data;
        if (sourceData is null || sourceData.Length == 0)
            return sourceData;

        if (destinationFormat == Format.B10G11R11UfloatPack32 &&
            mipmap.PixelFormat == EPixelFormat.Rgb && mipmap.PixelType == EPixelType.Float)
            return CreateRgbFloatToB10G11R11Upload(mipmap, sourceData, out ownsData);

        if (destinationFormat == Format.R16Sfloat &&
            mipmap.PixelFormat == EPixelFormat.Red && mipmap.PixelType == EPixelType.Float)
            return CreateFloatTo16BitUpload(mipmap, sourceData, normalizedDepth: false, out ownsData);

        if (destinationFormat == Format.D16Unorm &&
            mipmap.PixelFormat == EPixelFormat.DepthComponent && mipmap.PixelType == EPixelType.Float)
            return CreateFloatTo16BitUpload(mipmap, sourceData, normalizedDepth: true, out ownsData);

        if (!TryGetDestinationByteColorLayout(destinationFormat, out ByteColorDestination destination))
            return sourceData;

        if (!TryGetSourceByteColorLayout(mipmap.PixelFormat, mipmap.PixelType, out ByteColorSource source))
            return sourceData;

        ulong texelCount = (ulong)mipmap.Width * mipmap.Height;
        if (texelCount == 0)
            return sourceData;

        ulong expectedSourceBytes = texelCount * (uint)source.ComponentCount;
        ulong expectedDestinationBytes = texelCount * (uint)destination.ComponentCount;
        if (expectedSourceBytes > sourceData.Length ||
            expectedSourceBytes > int.MaxValue ||
            expectedDestinationBytes > int.MaxValue)
            return sourceData;

        if (sourceData.Length == expectedDestinationBytes && LayoutsMatch(source, destination))
            return sourceData;

        if (!CanRepack(source, destination))
            return sourceData;

        DataSource repacked = new((uint)expectedDestinationBytes);
        ReadOnlySpan<byte> sourceBytes = new(
            (byte*)sourceData.Address.Pointer,
            checked((int)expectedSourceBytes));
        Span<byte> destinationBytes = new(
            (byte*)repacked.Address.Pointer,
            checked((int)expectedDestinationBytes));

        for (int i = 0; i < checked((int)texelCount); i++)
        {
            ReadOnlySpan<byte> srcTexel = sourceBytes.Slice(
                i * source.ComponentCount,
                source.ComponentCount);
            byte r = ReadSourceChannel(srcTexel, source.RedIndex, 0);
            byte g = ReadSourceChannel(srcTexel, source.GreenIndex, r);
            byte b = ReadSourceChannel(srcTexel, source.BlueIndex, r);
            byte a = ReadSourceChannel(srcTexel, source.AlphaIndex, 255);
            Span<byte> dstTexel = destinationBytes.Slice(
                i * destination.ComponentCount,
                destination.ComponentCount);

            switch (destination.Kind)
            {
                case ByteColorDestinationKind.R:
                    dstTexel[0] = r;
                    break;
                case ByteColorDestinationKind.RG:
                    dstTexel[0] = r;
                    dstTexel[1] = g;
                    break;
                case ByteColorDestinationKind.RGBA:
                    dstTexel[0] = r;
                    dstTexel[1] = g;
                    dstTexel[2] = b;
                    dstTexel[3] = a;
                    break;
                case ByteColorDestinationKind.BGRA:
                    dstTexel[0] = b;
                    dstTexel[1] = g;
                    dstTexel[2] = r;
                    dstTexel[3] = a;
                    break;
            }
        }

        ownsData = true;
        return repacked;
    }

    private static unsafe DataSource? CreateRgbFloatToB10G11R11Upload(Mipmap2D mipmap, DataSource sourceData, out bool ownsData)
    {
        ownsData = false;
        ulong texels = (ulong)mipmap.Width * mipmap.Height;
        ulong sourceBytes = texels * 12UL;
        ulong destinationBytes = texels * 4UL;
        if (texels == 0 || sourceData.Length != sourceBytes ||
            sourceBytes > int.MaxValue || destinationBytes > int.MaxValue)
            return sourceData;

        DataSource packed = new((uint)destinationBytes);
        ReadOnlySpan<byte> source = new((byte*)sourceData.Address.Pointer, checked((int)sourceBytes));
        ReadOnlySpan<float> sourceFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source);
        Span<uint> destination = new((uint*)packed.Address.Pointer, checked((int)texels));
        for (int index = 0; index < destination.Length; index++)
        {
            int sourceIndex = index * 3;
            destination[index] = PackUnsignedFloat(sourceFloats[sourceIndex], 6) |
                (PackUnsignedFloat(sourceFloats[sourceIndex + 1], 6) << 11) |
                (PackUnsignedFloat(sourceFloats[sourceIndex + 2], 5) << 22);
        }
        ownsData = true;
        return packed;
    }

    private static unsafe DataSource? CreateFloatTo16BitUpload(Mipmap2D mipmap, DataSource sourceData, bool normalizedDepth, out bool ownsData)
    {
        ownsData = false;
        ulong texels = (ulong)mipmap.Width * mipmap.Height;
        ulong sourceBytes = texels * sizeof(float);
        ulong destinationBytes = texels * sizeof(ushort);
        if (texels == 0 || sourceData.Length != sourceBytes ||
            sourceBytes > int.MaxValue || destinationBytes > int.MaxValue)
        {
            return sourceData;
        }

        DataSource packed = new((uint)destinationBytes);
        ReadOnlySpan<byte> source = new((byte*)sourceData.Address.Pointer, checked((int)sourceBytes));
        ReadOnlySpan<float> sourceFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(source);
        Span<ushort> destination = new((ushort*)packed.Address.Pointer, checked((int)texels));
        for (int index = 0; index < destination.Length; index++)
        {
            float value = sourceFloats[index];
            // Vulkan buffer-to-image copies consume native bits; unlike GL they
            // do not convert authored float depth into normalized integer pixels.
            if (normalizedDepth)
            {
                float clamped = value > 0.0f ? MathF.Min(value, 1.0f) : 0.0f;
                destination[index] = (ushort)MathF.Round(clamped * ushort.MaxValue, MidpointRounding.ToEven);
            }
            else
                destination[index] = BitConverter.HalfToUInt16Bits((Half)value);
        }

        ownsData = true;
        return packed;
    }

    // R11G11B10 uses unsigned mini-floats: exponent bias 15, with 6/6/5 mantissa bits.
    // Negative, NaN and negative infinity become zero; positive infinity and finite overflow saturate.
    private static uint PackUnsignedFloat(float value, int mantissaBits)
    {
        if (!(value > 0.0f))
            return 0u;
        uint maxMantissa = (1u << mantissaBits) - 1u;
        if (float.IsPositiveInfinity(value) || value >= MathF.ScaleB(2.0f - 1.0f / (1u << mantissaBits), 15))
            return (30u << mantissaBits) | maxMantissa;
        const float minNormal = 6.103515625e-5f;
        if (value < minNormal)
            return (uint)Math.Clamp((int)MathF.Round(value / minNormal * (1u << mantissaBits), MidpointRounding.ToEven), 0, 1 << mantissaBits);

        int exponent = Math.ILogB(value);
        float fraction = MathF.ScaleB(value, -exponent) - 1.0f;
        uint mantissa = (uint)MathF.Round(fraction * (1u << mantissaBits), MidpointRounding.ToEven);
        if (mantissa > maxMantissa)
        {
            mantissa = 0u;
            exponent++;
        }
        if (exponent + 15 >= 31)
            return (30u << mantissaBits) | maxMantissa;
        return (uint)(exponent + 15) << mantissaBits | mantissa;
    }

    private static byte ReadSourceChannel(ReadOnlySpan<byte> srcTexel, int channelIndex, byte fallback)
        => channelIndex >= 0 ? srcTexel[channelIndex] : fallback;

    private static bool TryGetDestinationByteColorLayout(Format format, out ByteColorDestination destination)
    {
        destination = default;

        destination = format switch
        {
            Format.R8Unorm or Format.R8SNorm or Format.R8Uint or Format.R8Sint
                => new(ByteColorDestinationKind.R, 1),
            Format.R8G8Unorm or Format.R8G8SNorm or Format.R8G8Uint or Format.R8G8Sint
                => new(ByteColorDestinationKind.RG, 2),
            Format.R8G8B8A8Unorm or Format.R8G8B8A8SNorm or Format.R8G8B8A8Uint or Format.R8G8B8A8Sint or Format.R8G8B8A8Srgb
                => new(ByteColorDestinationKind.RGBA, 4),
            Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb
                => new(ByteColorDestinationKind.BGRA, 4),
            _ => default
        };

        return destination.ComponentCount != 0;
    }

    private static bool TryGetSourceByteColorLayout(EPixelFormat format, EPixelType type, out ByteColorSource source)
    {
        source = default;
        if (type is not EPixelType.UnsignedByte and not EPixelType.Byte)
            return false;

        source = format switch
        {
            EPixelFormat.Red or EPixelFormat.RedInteger or EPixelFormat.Luminance
                => new(1, 0, -1, -1, -1, EPixelFormat.Red),
            EPixelFormat.Alpha or EPixelFormat.AlphaInteger
                => new(1, 0, -1, -1, 0, EPixelFormat.Alpha),
            EPixelFormat.Rg or EPixelFormat.RgInteger or EPixelFormat.LuminanceAlpha
                => new(2, 0, 1, -1, format == EPixelFormat.LuminanceAlpha ? 1 : -1, EPixelFormat.Rg),
            EPixelFormat.Rgb or EPixelFormat.RgbInteger
                => new(3, 0, 1, 2, -1, EPixelFormat.Rgb),
            EPixelFormat.Bgr or EPixelFormat.BgrInteger
                => new(3, 2, 1, 0, -1, EPixelFormat.Bgr),
            EPixelFormat.Rgba or EPixelFormat.RgbaInteger
                => new(4, 0, 1, 2, 3, EPixelFormat.Rgba),
            EPixelFormat.Bgra or EPixelFormat.BgraInteger
                => new(4, 2, 1, 0, 3, EPixelFormat.Bgra),
            _ => default
        };

        return source.ComponentCount != 0;
    }

    private static bool LayoutsMatch(ByteColorSource source, ByteColorDestination destination)
        => destination.Kind switch
        {
            ByteColorDestinationKind.R => source.CanonicalFormat is EPixelFormat.Red,
            ByteColorDestinationKind.RG => source.CanonicalFormat is EPixelFormat.Rg,
            ByteColorDestinationKind.RGBA => source.CanonicalFormat is EPixelFormat.Rgba,
            ByteColorDestinationKind.BGRA => source.CanonicalFormat is EPixelFormat.Bgra,
            _ => false
        };

    private static bool CanRepack(ByteColorSource source, ByteColorDestination destination)
        => destination.Kind switch
        {
            ByteColorDestinationKind.R => true,
            ByteColorDestinationKind.RG => source.ComponentCount >= 2,
            ByteColorDestinationKind.RGBA or ByteColorDestinationKind.BGRA => source.ComponentCount >= 3,
            _ => false
        };

    private readonly record struct ByteColorSource(
        int ComponentCount,
        int RedIndex,
        int GreenIndex,
        int BlueIndex,
        int AlphaIndex,
        EPixelFormat CanonicalFormat);

    private readonly record struct ByteColorDestination(
        ByteColorDestinationKind Kind,
        int ComponentCount);

    private enum ByteColorDestinationKind
    {
        R,
        RG,
        RGBA,
        BGRA
    }

    /// <summary>
    /// Returns <c>true</c> if the given <see cref="Format"/> is a depth or depth-stencil format.
    /// </summary>
    public static bool IsDepthStencilFormat(Format format)
        => format is Format.D16Unorm
            or Format.X8D24UnormPack32
            or Format.D32Sfloat
            or Format.D16UnormS8Uint
            or Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.S8Uint;
}
