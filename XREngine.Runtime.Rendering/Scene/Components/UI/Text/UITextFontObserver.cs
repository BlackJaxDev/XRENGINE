namespace XREngine.Rendering.UI;

/// <summary>Marks font dirtiness without changing text on the notification thread.</summary>
internal sealed class UITextFontObserver : IDisposable
{
    private readonly FontGlyphSet _font;
    private readonly Action _changed;
    private int _disposed;

    internal UITextFontObserver(FontGlyphSet font, Action changed)
    {
        _font = font;
        _changed = changed;
        font.LayoutMetricsChanged += FontChanged;
    }

    private void FontChanged(FontGlyphSet font)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _changed();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _font.LayoutMetricsChanged -= FontChanged;
    }
}
