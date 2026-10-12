using XREngine.Core.Files;

namespace XREngine.Rendering;

public partial class FontGlyphSet
{
    private static readonly Lock DefaultUiFontGate = new();
    private static FontGlyphSet? _scopedDefaultUiFont;

    /// <summary>Uses a preloaded font for synchronous UI layout during one runtime session.</summary>
    public static IDisposable InstallDefaultUiFont(FontGlyphSet font)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (font.AtlasType != EFontAtlasType.Bitmap || font.Atlas is null || font.Glyphs is not { Count: > 0 })
            throw new NotSupportedException("BrowserFont.DefaultUiFontUnsupported: expected a cooked bitmap font with atlas and glyphs.");
        lock (DefaultUiFontGate)
        {
            if (_scopedDefaultUiFont is not null)
                throw new InvalidOperationException("BrowserFont.DefaultUiFontAlreadyInstalled.");
            _scopedDefaultUiFont = font;
        }
        return new DefaultUiFontScope(font);
    }

    private static FontGlyphSet? ScopedDefaultUiFont
    {
        get
        {
            lock (DefaultUiFontGate)
                return _scopedDefaultUiFont;
        }
    }

    private static FontGlyphSet ResolveDefaultBitmapFont()
    {
        FontGlyphSet? font = ScopedDefaultUiFont;
        if (font is not null)
            return font;
        if (DirectStorageIO.Source is { SupportsSynchronousReads: false })
            throw new NotSupportedException("BrowserFont.DefaultUiFontMissing: package a cooked default UI font before activating text.");
        return LoadEngineFont(RuntimeEngine.Rendering.Settings.DefaultFontFolder,
            RuntimeEngine.Rendering.Settings.DefaultFontFileName,
            CreateBitmapImportOptions(DefaultBitmapMipmapFontDrawSize));
    }

    private sealed class DefaultUiFontScope(FontGlyphSet font) : IDisposable
    {
        private FontGlyphSet? _font = font;

        public void Dispose()
        {
            FontGlyphSet? installed = Interlocked.Exchange(ref _font, null);
            if (installed is null)
                return;
            lock (DefaultUiFontGate)
            {
                if (ReferenceEquals(_scopedDefaultUiFont, installed))
                    _scopedDefaultUiFont = null;
            }
        }
    }
}
