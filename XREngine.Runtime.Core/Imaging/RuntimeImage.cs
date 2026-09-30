using System.Buffers;
using XREngine.Data.Rendering;

namespace XREngine.Imaging;

/// <summary>
/// One image mip with owned or borrowed row-major pixels and explicit origin.
/// The owner is released on disposal; borrowed memory remains the caller's responsibility.
/// </summary>
public sealed class RuntimeImage : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private ReadOnlyMemory<byte> _pixels;
    private int _disposed;

    public RuntimeImage(uint width, uint height, RuntimePixelFormat pixelFormat, ReadOnlyMemory<byte> pixels, int mipLevel = 0)
        : this(width, height, MapFormat(pixelFormat), MapType(pixelFormat), pixels, null, 0, RuntimeImageOrigin.TopLeft, mipLevel)
    {
    }

    public RuntimeImage(uint width, uint height, RuntimePixelFormat pixelFormat, IMemoryOwner<byte> owner, int mipLevel = 0)
        : this(width, height, MapFormat(pixelFormat), MapType(pixelFormat), owner?.Memory ?? throw new ArgumentNullException(nameof(owner)), owner, 0, RuntimeImageOrigin.TopLeft, mipLevel)
    {
    }

    public RuntimeImage(uint width, uint height, EPixelFormat format, EPixelType type,
        ReadOnlyMemory<byte> pixels, int rowStrideBytes = 0,
        RuntimeImageOrigin origin = RuntimeImageOrigin.TopLeft, int mipLevel = 0)
        : this(width, height, format, type, pixels, null, rowStrideBytes, origin, mipLevel)
    {
    }

    public RuntimeImage(uint width, uint height, EPixelFormat format, EPixelType type,
        IMemoryOwner<byte> owner, int rowStrideBytes = 0,
        RuntimeImageOrigin origin = RuntimeImageOrigin.TopLeft, int mipLevel = 0)
        : this(width, height, format, type, owner?.Memory ?? throw new ArgumentNullException(nameof(owner)), owner, rowStrideBytes, origin, mipLevel)
    {
    }

    private RuntimeImage(uint width, uint height, EPixelFormat format, EPixelType type,
        ReadOnlyMemory<byte> pixels, IMemoryOwner<byte>? owner, int rowStrideBytes,
        RuntimeImageOrigin origin, int mipLevel)
    {
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if (mipLevel < 0)
            throw new ArgumentOutOfRangeException(nameof(mipLevel));

        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin));
        int rowBytes = checked((int)((long)width * GetBytesPerPixel(format, type)));
        if (rowStrideBytes == 0)
            rowStrideBytes = rowBytes;
        if (rowStrideBytes < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(rowStrideBytes), "Row stride must cover every pixel in a row.");
        int requiredBytes = checked((int)(((long)height - 1) * rowStrideBytes + rowBytes));
        if (pixels.Length < requiredBytes)
            throw new ArgumentException($"Expected at least {requiredBytes} pixel bytes; received {pixels.Length}.", nameof(pixels));

        Width = width;
        Height = height;
        Format = format;
        Type = type;
        PixelFormat = MapPixelFormat(format, type);
        Origin = origin;
        _pixels = pixels[..requiredBytes];
        MipLevel = mipLevel;
        RowStrideBytes = rowStrideBytes;
        _owner = owner;
    }

    public uint Width { get; }
    public uint Height { get; }
    public int MipLevel { get; }
    public int RowStrideBytes { get; }
    public RuntimePixelFormat PixelFormat { get; }
    public EPixelFormat Format { get; }
    public EPixelType Type { get; }
    public RuntimeImageOrigin Origin { get; }
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _pixels;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _pixels = default;
        Interlocked.Exchange(ref _owner, null)?.Dispose();
    }

    /// <summary>Copies a rectangular region into independent top-first, tightly packed memory.</summary>
    public RuntimeImage CopyRegion(uint x, uint y, uint width, uint height)
    {
        if (width == 0 || height == 0 || x > Width || y > Height || width > Width - x || height > Height - y)
            throw new ArgumentOutOfRangeException(nameof(width), "Region must be inside the image.");
        int pixelBytes = GetBytesPerPixel(Format, Type);
        int rowBytes = checked((int)((long)width * pixelBytes));
        byte[] copied = new byte[checked(rowBytes * (int)height)];
        ReadOnlySpan<byte> source = Pixels.Span;
        for (int row = 0; row < height; row++)
        {
            int sourceRow = checked((int)y + row);
            if (Origin == RuntimeImageOrigin.BottomLeft)
                sourceRow = checked((int)Height - sourceRow - 1);
            int sourceOffset = checked(sourceRow * RowStrideBytes + (int)x * pixelBytes);
            source.Slice(sourceOffset, rowBytes).CopyTo(copied.AsSpan(row * rowBytes, rowBytes));
        }
        return new RuntimeImage(width, height, Format, Type, copied, mipLevel: MipLevel);
    }

    /// <summary>Returns independent, top-first RGBA8 pixels for ordinary color images.</summary>
    public byte[] CopyRgba8Pixels()
    {
        if (Type != EPixelType.UnsignedByte ||
            Format is not (EPixelFormat.Rgb or EPixelFormat.Rgba or EPixelFormat.Bgr or EPixelFormat.Bgra))
            throw new NotSupportedException($"Cannot convert {Format}/{Type} to RGBA8 pixels.");
        int channels = Format is EPixelFormat.Rgb or EPixelFormat.Bgr ? 3 : 4;
        byte[] rgba = new byte[checked((int)((long)Width * Height * 4))];
        ReadOnlySpan<byte> source = Pixels.Span;
        for (int row = 0; row < Height; row++)
        {
            int sourceRow = Origin == RuntimeImageOrigin.BottomLeft
                ? checked((int)Height - row - 1)
                : row;
            for (int column = 0; column < Width; column++)
            {
                int sourceOffset = checked(sourceRow * RowStrideBytes + column * channels);
                int outputOffset = checked((row * (int)Width + column) * 4);
                bool bgr = Format is EPixelFormat.Bgr or EPixelFormat.Bgra;
                rgba[outputOffset] = source[sourceOffset + (bgr ? 2 : 0)];
                rgba[outputOffset + 1] = source[sourceOffset + 1];
                rgba[outputOffset + 2] = source[sourceOffset + (bgr ? 0 : 2)];
                rgba[outputOffset + 3] = channels == 4 ? source[sourceOffset + 3] : (byte)255;
            }
        }
        return rgba;
    }

    private static EPixelFormat MapFormat(RuntimePixelFormat format) => format switch
    {
        RuntimePixelFormat.Rgb8 => EPixelFormat.Rgb,
        RuntimePixelFormat.Rgba8 or RuntimePixelFormat.RgbaFloat32 => EPixelFormat.Rgba,
        RuntimePixelFormat.Bgr8 => EPixelFormat.Bgr,
        RuntimePixelFormat.Bgra8 => EPixelFormat.Bgra,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static EPixelType MapType(RuntimePixelFormat format)
        => format == RuntimePixelFormat.RgbaFloat32 ? EPixelType.Float : EPixelType.UnsignedByte;

    private static RuntimePixelFormat MapPixelFormat(EPixelFormat format, EPixelType type)
        => (format, type) switch
        {
            (EPixelFormat.Rgb, EPixelType.UnsignedByte) => RuntimePixelFormat.Rgb8,
            (EPixelFormat.Rgba, EPixelType.UnsignedByte) => RuntimePixelFormat.Rgba8,
            (EPixelFormat.Bgr, EPixelType.UnsignedByte) => RuntimePixelFormat.Bgr8,
            (EPixelFormat.Bgra, EPixelType.UnsignedByte) => RuntimePixelFormat.Bgra8,
            (EPixelFormat.Rgba, EPixelType.Float) => RuntimePixelFormat.RgbaFloat32,
            _ => RuntimePixelFormat.Unknown,
        };

    public static int GetBytesPerPixel(EPixelFormat format, EPixelType type)
    {
        int componentCount = format switch
        {
            EPixelFormat.Red or EPixelFormat.Green or EPixelFormat.Blue or EPixelFormat.Alpha or
                EPixelFormat.RedInteger or EPixelFormat.GreenInteger or EPixelFormat.BlueInteger or
                EPixelFormat.AlphaInteger or EPixelFormat.Luminance or EPixelFormat.DepthComponent or
                EPixelFormat.StencilIndex or EPixelFormat.ColorIndex or
                EPixelFormat.UnsignedShort or EPixelFormat.UnsignedInt => 1,
            EPixelFormat.Rg or EPixelFormat.RgInteger or EPixelFormat.LuminanceAlpha or EPixelFormat.DepthStencil => 2,
            EPixelFormat.Rgb or EPixelFormat.Bgr or EPixelFormat.RgbInteger or EPixelFormat.BgrInteger => 3,
            EPixelFormat.Rgba or EPixelFormat.Bgra or EPixelFormat.RgbaInteger or EPixelFormat.BgraInteger => 4,
            _ => throw new NotSupportedException($"Unsupported image pixel format {format}."),
        };
        int packedSize = type switch
        {
            EPixelType.UnsignedByte332 or EPixelType.UnsignedByte233Reversed => 1,
            EPixelType.UnsignedShort4444 or EPixelType.UnsignedShort5551 or EPixelType.UnsignedShort565 or
                EPixelType.UnsignedShort565Reversed or EPixelType.UnsignedShort4444Reversed or
                EPixelType.UnsignedShort1555Reversed => 2,
            EPixelType.UnsignedInt8888 or EPixelType.UnsignedInt1010102 or EPixelType.UnsignedInt8888Reversed or
                EPixelType.UnsignedInt2101010Reversed or EPixelType.UnsignedInt248 or
                EPixelType.UnsignedInt10F11F11FRev or EPixelType.UnsignedInt5999Rev => 4,
            EPixelType.Float32UnsignedInt248Rev => 8,
            _ => 0,
        };
        if (packedSize != 0)
            return packedSize;
        int componentSize = type switch
        {
            EPixelType.Byte or EPixelType.UnsignedByte => 1,
            EPixelType.Short or EPixelType.UnsignedShort or EPixelType.HalfFloat => 2,
            EPixelType.Int or EPixelType.UnsignedInt or EPixelType.Float => 4,
            _ => throw new NotSupportedException($"Unsupported image pixel type {type}."),
        };
        return checked(componentCount * componentSize);
    }
}
