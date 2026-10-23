namespace XREngine.Rendering.UI;

/// <summary>Owns one small RGBA image for a fixed UI atlas generation.</summary>
public sealed class UIImageAtlasSource
{
    private readonly byte[] _pixels;

    public UIImageAtlasSource(string key, int width, int height, ReadOnlySpan<byte> rgba, bool srgb = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (width is < 1 or > 512 || height is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(width), "UI.AtlasImageSize: each dimension must be 1..512 pixels.");
        if (rgba.Length != checked(width * height * 4))
            throw new ArgumentException("UI.AtlasPixels: expected tightly packed RGBA8 pixels.", nameof(rgba));
        Key = key;
        Width = width;
        Height = height;
        Srgb = srgb;
        _pixels = rgba.ToArray();
    }

    public string Key { get; }
    public int Width { get; }
    public int Height { get; }
    public bool Srgb { get; }
    internal ReadOnlySpan<byte> Pixels => _pixels;
}
