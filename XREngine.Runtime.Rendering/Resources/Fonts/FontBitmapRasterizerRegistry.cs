namespace XREngine.Rendering;

/// <summary>Selected authoring backend for bitmap font atlases.</summary>
public static class FontBitmapRasterizerRegistry
{
    private static IFontBitmapRasterizer? _current;

    public static IFontBitmapRasterizer? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public static IFontBitmapRasterizer Require()
        => Current ?? throw new InvalidOperationException(
            "No bitmap font rasterizer is registered. Register the Skia font backend in the authoring application.");
}
