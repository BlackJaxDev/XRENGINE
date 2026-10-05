using System.Runtime.InteropServices;
using System.Buffers.Binary;
using ImageMagick;
using XREngine.Imaging;

namespace XREngine.Runtime.Imaging.Magick;

/// <summary>Converts encoded source images and cooked pixels with ImageMagick.</summary>
public sealed class MagickRuntimeImageCodec : IRuntimeImageCodec
{
    public RuntimeImage Decode(ReadOnlyMemory<byte> encodedImage)
    {
        using MagickImage source = new(GetWholeArray(encodedImage));
        return DecodePixels(source);
    }

    /// <summary>
    /// The array behind <paramref name="encodedImage"/> when it spans the whole
    /// array, so decoding a file read into one array does not copy it again.
    /// </summary>
    private static byte[] GetWholeArray(ReadOnlyMemory<byte> encodedImage)
        => MemoryMarshal.TryGetArray(encodedImage, out ArraySegment<byte> segment) &&
           segment.Array is { } array &&
           segment.Offset == 0 &&
           segment.Count == array.Length
            ? array
            : encodedImage.ToArray();

    public IReadOnlyList<RuntimeImage> DecodeFrames(ReadOnlyMemory<byte> encodedImage)
    {
        using MagickImageCollection collection = new(GetWholeArray(encodedImage));
        RuntimeImage[] frames = new RuntimeImage[collection.Count];
        int completed = 0;
        try
        {
            for (; completed < frames.Length; completed++)
            {
                if (collection[completed] is not MagickImage frame)
                    throw new InvalidDataException("ImageMagick returned an unsupported image frame.");
                frames[completed] = DecodePixels(frame);
            }
            return frames;
        }
        catch
        {
            for (int i = 0; i < completed; i++)
                frames[i].Dispose();
            throw;
        }
    }

    private static RuntimeImage DecodePixels(MagickImage source)
    {
        if (source.Format is MagickFormat.Exr or MagickFormat.Hdr or MagickFormat.Pfm)
            return DecodeHighDynamicRange(source);

        using MagickImage? converted = RequiresSrgbConversion(source.ColorSpace)
            ? (MagickImage)source.Clone()
            : null;
        MagickImage pixelsImage = converted ?? source;
        if (converted is not null)
            converted.ColorSpace = ColorSpace.sRGB;
        using IPixelCollection<float> pixels = pixelsImage.GetPixels()
            ?? throw new InvalidDataException("ImageMagick returned no decoded pixels.");
        byte[] rgba = pixels.ToByteArray(0, 0, source.Width, source.Height, PixelMapping.RGBA)
            ?? throw new InvalidDataException("ImageMagick returned no RGBA pixel bytes.");
        return new RuntimeImage(source.Width, source.Height, RuntimePixelFormat.Rgba8, rgba);
    }

    public byte[] EncodePng(RuntimeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelFormat == RuntimePixelFormat.Unknown)
            return EncodeDiagnosticPng(image);
        (StorageType storage, string mapping) = image.PixelFormat switch
        {
            RuntimePixelFormat.Rgb8 => (StorageType.Char, "RGB"),
            RuntimePixelFormat.Rgba8 => (StorageType.Char, "RGBA"),
            RuntimePixelFormat.Bgr8 => (StorageType.Char, "BGR"),
            RuntimePixelFormat.Bgra8 => (StorageType.Char, "BGRA"),
            RuntimePixelFormat.RgbaFloat32 => (StorageType.Float, "RGBA"),
            _ => throw new NotSupportedException($"Cannot encode {image.PixelFormat} pixels."),
        };
        int rowBytes = checked((int)(image.Width * (uint)RuntimeImage.GetBytesPerPixel(image.Format, image.Type)));
        byte[] topFirstPixels = new byte[checked(rowBytes * (int)image.Height)];
        ReadOnlySpan<byte> source = image.Pixels.Span;
        for (int row = 0; row < image.Height; row++)
        {
            int sourceRow = image.Origin == RuntimeImageOrigin.BottomLeft
                ? checked((int)image.Height - row - 1)
                : row;
            source.Slice(sourceRow * image.RowStrideBytes, rowBytes)
                .CopyTo(topFirstPixels.AsSpan(row * rowBytes, rowBytes));
        }
        using MagickImage encoded = new(topFirstPixels,
            new PixelReadSettings(image.Width, image.Height, storage, mapping));
        return encoded.ToByteArray(MagickFormat.Png);
    }

    public byte[] Encode(RuntimeImage image, RuntimeImageFileFormat format, int quality, bool srgb)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (quality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(quality));
        MagickFormat magickFormat = format switch
        {
            RuntimeImageFileFormat.Png => MagickFormat.Png,
            RuntimeImageFileFormat.Jpeg => MagickFormat.Jpeg,
            RuntimeImageFileFormat.Exr => MagickFormat.Exr,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        using MagickImage encoded = CreateMagickImage(image);
        encoded.ColorSpace = srgb ? ColorSpace.sRGB : ColorSpace.RGB;
        encoded.Quality = (uint)quality;
        return encoded.ToByteArray(magickFormat);
    }

    private static byte[] EncodeDiagnosticPng(RuntimeImage image)
    {
        int pixelBytes = RuntimeImage.GetBytesPerPixel(image.Format, image.Type);
        byte[] rgba = new byte[checked((int)((long)image.Width * image.Height * 4))];
        ReadOnlySpan<byte> source = image.Pixels.Span;
        for (int y = 0; y < image.Height; y++)
        {
            int sourceRow = image.Origin == RuntimeImageOrigin.BottomLeft
                ? checked((int)image.Height - y - 1)
                : y;
            for (int x = 0; x < image.Width; x++)
            {
                ReadOnlySpan<byte> pixel = source.Slice(
                    checked(sourceRow * image.RowStrideBytes + x * pixelBytes), pixelBytes);
                Span<byte> output = rgba.AsSpan(checked((y * (int)image.Width + x) * 4), 4);
                if (image.Format == XREngine.Data.Rendering.EPixelFormat.DepthStencil)
                {
                    float depth = image.Type switch
                    {
                        XREngine.Data.Rendering.EPixelType.UnsignedInt248 =>
                            (BinaryPrimitives.ReadUInt32LittleEndian(pixel) >> 8) / 16777215.0f,
                        XREngine.Data.Rendering.EPixelType.Float32UnsignedInt248Rev =>
                            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pixel)),
                        _ => throw new NotSupportedException($"Unsupported depth-stencil type {image.Type}."),
                    };
                    byte intensity = ToByte(depth);
                    output[0] = output[1] = output[2] = intensity;
                    output[3] = 255;
                    continue;
                }

                int components = image.Format switch
                {
                    XREngine.Data.Rendering.EPixelFormat.DepthComponent or
                        XREngine.Data.Rendering.EPixelFormat.StencilIndex or
                        XREngine.Data.Rendering.EPixelFormat.Red or
                        XREngine.Data.Rendering.EPixelFormat.Luminance => 1,
                    XREngine.Data.Rendering.EPixelFormat.Rg or
                        XREngine.Data.Rendering.EPixelFormat.LuminanceAlpha => 2,
                    XREngine.Data.Rendering.EPixelFormat.Rgb or XREngine.Data.Rendering.EPixelFormat.Bgr => 3,
                    XREngine.Data.Rendering.EPixelFormat.Rgba or XREngine.Data.Rendering.EPixelFormat.Bgra => 4,
                    _ => throw new NotSupportedException($"Cannot encode {image.Format}/{image.Type} pixels."),
                };
                int componentBytes = pixelBytes / components;
                if (componentBytes * components != pixelBytes)
                    throw new NotSupportedException($"Cannot encode packed {image.Format}/{image.Type} pixels.");
                byte c0 = ToByte(ReadComponent(pixel, 0, image.Type, componentBytes));
                byte c1 = components > 1 ? ToByte(ReadComponent(pixel, 1, image.Type, componentBytes)) : c0;
                byte c2 = components > 2 ? ToByte(ReadComponent(pixel, 2, image.Type, componentBytes)) : c0;
                byte c3 = components > 3 ? ToByte(ReadComponent(pixel, 3, image.Type, componentBytes)) : (byte)255;
                if (image.Format is XREngine.Data.Rendering.EPixelFormat.Bgr or XREngine.Data.Rendering.EPixelFormat.Bgra)
                    (c0, c2) = (c2, c0);
                output[0] = c0;
                output[1] = c1;
                output[2] = c2;
                output[3] = c3;
            }
        }
        using MagickImage encoded = new(rgba,
            new PixelReadSettings(image.Width, image.Height, StorageType.Char, "RGBA"));
        return encoded.ToByteArray(MagickFormat.Png);
    }

    private static float ReadComponent(ReadOnlySpan<byte> pixel, int index,
        XREngine.Data.Rendering.EPixelType type, int componentBytes)
    {
        ReadOnlySpan<byte> component = pixel.Slice(index * componentBytes, componentBytes);
        return type switch
        {
            XREngine.Data.Rendering.EPixelType.UnsignedByte => component[0] / 255.0f,
            XREngine.Data.Rendering.EPixelType.Byte => Math.Max(0, (sbyte)component[0] / 127.0f),
            XREngine.Data.Rendering.EPixelType.UnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(component) / 65535.0f,
            XREngine.Data.Rendering.EPixelType.Short => Math.Max(0, BinaryPrimitives.ReadInt16LittleEndian(component) / 32767.0f),
            XREngine.Data.Rendering.EPixelType.UnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(component) / (float)uint.MaxValue,
            XREngine.Data.Rendering.EPixelType.Int => Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(component) / (float)int.MaxValue),
            XREngine.Data.Rendering.EPixelType.Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(component)),
            XREngine.Data.Rendering.EPixelType.HalfFloat => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(component)),
            _ => throw new NotSupportedException($"Cannot encode {type} pixel components."),
        };
    }

    private static byte ToByte(float value)
        => float.IsFinite(value) ? (byte)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f) : (byte)0;

    public RuntimeImage Resize(RuntimeImage image, uint width, uint height, RuntimeImageResizeMode mode)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Resized image dimensions must be positive.");
        (StorageType storage, string mapping) = image.PixelFormat switch
        {
            RuntimePixelFormat.Rgb8 => (StorageType.Char, "RGB"),
            RuntimePixelFormat.Rgba8 => (StorageType.Char, "RGBA"),
            RuntimePixelFormat.Bgr8 => (StorageType.Char, "BGR"),
            RuntimePixelFormat.Bgra8 => (StorageType.Char, "BGRA"),
            RuntimePixelFormat.RgbaFloat32 => (StorageType.Float, "RGBA"),
            _ => throw new NotSupportedException($"Cannot resize {image.Format}/{image.Type} pixels."),
        };
        int rowBytes = checked((int)(image.Width * (uint)RuntimeImage.GetBytesPerPixel(image.Format, image.Type)));
        byte[] topFirstPixels = new byte[checked(rowBytes * (int)image.Height)];
        ReadOnlySpan<byte> source = image.Pixels.Span;
        for (int row = 0; row < image.Height; row++)
        {
            int sourceRow = image.Origin == RuntimeImageOrigin.BottomLeft
                ? checked((int)image.Height - row - 1)
                : row;
            source.Slice(sourceRow * image.RowStrideBytes, rowBytes)
                .CopyTo(topFirstPixels.AsSpan(row * rowBytes, rowBytes));
        }

        using MagickImage resized = new(topFirstPixels,
            new PixelReadSettings(image.Width, image.Height, storage, mapping));
        if (image.Type == XREngine.Data.Rendering.EPixelType.Float)
            resized.Format = MagickFormat.Exr;
        // The runtime codec promises exact dimensions. Aspect-preserving containment
        // belongs to the caller and otherwise leaves the pixel readback out of bounds.
        MagickGeometry geometry = new(width, height) { IgnoreAspectRatio = true };
        switch (mode)
        {
            case RuntimeImageResizeMode.Standard:
                resized.Resize(geometry);
                break;
            case RuntimeImageResizeMode.Bilinear:
                resized.InterpolativeResize(geometry, PixelInterpolateMethod.Bilinear);
                break;
            case RuntimeImageResizeMode.Lanczos:
                resized.FilterType = FilterType.Lanczos;
                resized.Resize(geometry);
                break;
            case RuntimeImageResizeMode.Adaptive:
                resized.AdaptiveResize(geometry);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (image.Type == XREngine.Data.Rendering.EPixelType.Float)
            return DecodeHighDynamicRange(resized);
        using IPixelCollection<float> pixels = resized.GetPixels()
            ?? throw new InvalidDataException("ImageMagick returned no resized pixels.");
        byte[] data = pixels.ToByteArray(0, 0, width, height,
            mapping switch
            {
                "RGB" => PixelMapping.RGB,
                "RGBA" => PixelMapping.RGBA,
                "BGR" => PixelMapping.BGR,
                "BGRA" => PixelMapping.BGRA,
                _ => throw new InvalidOperationException("Unsupported image channel mapping."),
            }) ?? throw new InvalidDataException("ImageMagick returned no resized pixel bytes.");
        return new RuntimeImage(width, height, image.Format, image.Type, data);
    }

    public RuntimeImage ReprojectEquirectangularToCubeCross(RuntimeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 4 || image.Width % 4 != 0)
            throw new ArgumentException("Equirectangular image width must be divisible by four.", nameof(image));
        using MagickImage source = CreateMagickImage(image);
        uint width = image.Width;
        uint height = checked(width * 3 / 4);
        using MagickImage destination = new(MagickColors.Black, width, height)
        {
            Format = source.Format,
            ColorSpace = source.ColorSpace,
            Depth = source.Depth,
        };
        ConvertEquirectangularToCross(source, destination);
        return DecodePixels(destination);
    }

    private static MagickImage CreateMagickImage(RuntimeImage image)
    {
        (StorageType storage, string mapping) = image.PixelFormat switch
        {
            RuntimePixelFormat.Rgb8 => (StorageType.Char, "RGB"),
            RuntimePixelFormat.Rgba8 => (StorageType.Char, "RGBA"),
            RuntimePixelFormat.Bgr8 => (StorageType.Char, "BGR"),
            RuntimePixelFormat.Bgra8 => (StorageType.Char, "BGRA"),
            RuntimePixelFormat.RgbaFloat32 => (StorageType.Float, "RGBA"),
            _ => throw new NotSupportedException($"Cannot reproject {image.Format}/{image.Type} pixels."),
        };
        int rowBytes = checked((int)((long)image.Width * RuntimeImage.GetBytesPerPixel(image.Format, image.Type)));
        byte[] topFirst = new byte[checked(rowBytes * (int)image.Height)];
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        for (int row = 0; row < image.Height; row++)
        {
            int sourceRow = image.Origin == RuntimeImageOrigin.BottomLeft
                ? checked((int)image.Height - row - 1)
                : row;
            pixels.Slice(sourceRow * image.RowStrideBytes, rowBytes)
                .CopyTo(topFirst.AsSpan(row * rowBytes, rowBytes));
        }
        MagickImage source = new(topFirst, new PixelReadSettings(image.Width, image.Height, storage, mapping));
        if (image.Type == XREngine.Data.Rendering.EPixelType.Float)
            source.Format = MagickFormat.Exr;
        return source;
    }

    private static void ConvertEquirectangularToCross(MagickImage source, MagickImage destination)
    {
        uint inWidth = source.Width;
        uint inHeight = source.Height;
        uint outWidth = destination.Width;
        uint edge = inWidth / 4;
        using IPixelCollection<float> inPixels = source.GetPixels()
            ?? throw new InvalidDataException("ImageMagick returned no equirectangular pixels.");
        using IPixelCollection<float> outPixels = destination.GetPixels()
            ?? throw new InvalidDataException("ImageMagick returned no cubemap pixels.");
        float[] outputPixel = new float[3];
        bool preserveHdrRange = source.Format is MagickFormat.Exr or MagickFormat.Hdr or MagickFormat.Pfm;
        for (uint i = 0; i < outWidth; i++)
        {
            uint face = i / edge;
            int startRow = face == 2 ? 0 : (int)edge;
            int endRow = face == 2 ? (int)edge * 3 : (int)edge * 2;
            for (int j = startRow; j < endRow; j++)
            {
                int faceIndex = j < edge ? 4 : j >= 2 * edge ? 5 : (int)face;
                OutImgToXYZ((int)i, j, faceIndex, (int)edge, out double x, out double y, out double z);
                double theta = Math.Atan2(y, x);
                double phi = Math.Atan2(z, Math.Sqrt(x * x + y * y));
                double u = 2.0 * edge * (theta + Math.PI) / Math.PI;
                double v = 2.0 * edge * (Math.PI / 2 - phi) / Math.PI;
                int ui = (int)Math.Floor(u);
                int vi = (int)Math.Floor(v);
                double mu = u - ui;
                double nu = v - vi;
                int x0 = ((ui % (int)inWidth) + (int)inWidth) % (int)inWidth;
                int x1 = (((ui + 1) % (int)inWidth) + (int)inWidth) % (int)inWidth;
                int y0 = Math.Clamp(vi, 0, (int)inHeight - 1);
                int y1 = Math.Clamp(vi + 1, 0, (int)inHeight - 1);
                var a = inPixels.GetPixel(x0, y0);
                var b = inPixels.GetPixel(x1, y0);
                var c = inPixels.GetPixel(x0, y1);
                var d = inPixels.GetPixel(x1, y1);
                for (uint channel = 0; channel < 3; channel++)
                {
                    double value = a.GetChannel(channel) * (1 - mu) * (1 - nu)
                        + b.GetChannel(channel) * mu * (1 - nu)
                        + c.GetChannel(channel) * (1 - mu) * nu
                        + d.GetChannel(channel) * mu * nu;
                    outputPixel[channel] = (float)(preserveHdrRange ? value : Math.Clamp(value, 0.0, Quantum.Max));
                }
                outPixels.SetPixel((int)i, j, outputPixel);
            }
        }
    }

    private static void OutImgToXYZ(int i, int j, int face, int edge, out double x, out double y, out double z)
    {
        double a = 2.0 * i / edge;
        double b = 2.0 * j / edge;
        x = y = z = 0;
        switch (face)
        {
            case 0: x = -1.0; y = 1.0 - a; z = 3.0 - b; break;
            case 1: x = a - 3.0; y = -1.0; z = 3.0 - b; break;
            case 2: x = 1.0; y = a - 5.0; z = 3.0 - b; break;
            case 3: x = 7.0 - a; y = 1.0; z = 3.0 - b; break;
            case 4: x = b - 1.0; y = a - 5.0; z = 1.0; break;
            case 5: x = 5.0 - b; y = a - 5.0; z = -1.0; break;
        }
    }

    private static RuntimeImage DecodeHighDynamicRange(MagickImage image)
    {
        using IPixelCollection<float> pixels = image.GetPixels()
            ?? throw new InvalidDataException("ImageMagick returned no HDR pixels.");
        int channelCount = checked((int)pixels.Channels);
        int redIndex = checked((int)(pixels.GetChannelIndex(PixelChannel.Red)
            ?? pixels.GetChannelIndex(PixelChannel.Gray) ?? 0u));
        int greenIndex = checked((int)(pixels.GetChannelIndex(PixelChannel.Green) ?? (uint)redIndex));
        int blueIndex = checked((int)(pixels.GetChannelIndex(PixelChannel.Blue) ?? (uint)redIndex));
        uint? alphaChannel = pixels.GetChannelIndex(PixelChannel.Alpha);
        int alphaIndex = alphaChannel.HasValue ? checked((int)alphaChannel.Value) : -1;
        int pixelCount = checked((int)((long)image.Width * image.Height));
        byte[] destination = new byte[checked(pixelCount * 4 * sizeof(float))];
        Span<float> rgba = MemoryMarshal.Cast<byte, float>(destination.AsSpan());
        float inverseQuantum = 1.0f / Quantum.Max;
        int rowValues = checked((int)image.Width * channelCount);
        int rowsPerChunk = Math.Max(1, 1024 * 1024 / Math.Max(1, rowValues * sizeof(float)));
        for (int y = 0; y < image.Height; y += rowsPerChunk)
        {
            uint chunkHeight = (uint)Math.Min(rowsPerChunk, (int)image.Height - y);
            float[] chunk = pixels.GetArea(0, y, image.Width, chunkHeight)
                ?? throw new InvalidDataException("ImageMagick returned no HDR channel values.");
            int chunkPixels = checked((int)image.Width * (int)chunkHeight);
            if (chunk.Length != checked(chunkPixels * channelCount))
                throw new InvalidDataException("ImageMagick returned an incomplete HDR image.");
            for (int i = 0; i < chunkPixels; i++)
            {
                int input = i * channelCount;
                int output = (checked(y * (int)image.Width) + i) * 4;
                rgba[output] = chunk[input + redIndex] * inverseQuantum;
                rgba[output + 1] = chunk[input + greenIndex] * inverseQuantum;
                rgba[output + 2] = chunk[input + blueIndex] * inverseQuantum;
                rgba[output + 3] = alphaIndex >= 0 ? chunk[input + alphaIndex] * inverseQuantum : 1.0f;
            }
        }
        return new RuntimeImage(image.Width, image.Height, RuntimePixelFormat.RgbaFloat32, destination);
    }

    private static bool RequiresSrgbConversion(ColorSpace colorSpace)
        => colorSpace is not ColorSpace.RGB
            and not ColorSpace.sRGB
            and not ColorSpace.scRGB
            and not ColorSpace.Gray
            and not ColorSpace.LinearGray;
}
