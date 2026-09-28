namespace XREngine.Browser;

/// <summary>Immutable single-mip, sRGB RGBA8 browser texture pixels.</summary>
public sealed class BrowserTextureData
{
    private readonly byte[] _rgba;

    public BrowserTextureData(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length || rgba.Length > 64 * 1024 * 1024)
            throw new ArgumentException("Texture pixels must contain exactly width × height × four RGBA8 bytes, at most 64 MiB.");
        Width = width;
        Height = height;
        _rgba = rgba.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    internal Span<byte> RgbaBytes => _rgba.AsSpan();
}
