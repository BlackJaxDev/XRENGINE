using XREngine.Core.Files;

namespace XREngine.Rendering;

public partial class FontGlyphSet
{
    private static readonly Lock DefaultUiFontGate = new();
    private static FontGlyphSet? _scopedDefaultUiFont;
    private static FontGlyphSet? _scopedDefaultUiIconFont;

    /// <summary>Uses a preloaded font for synchronous UI layout during one runtime session.</summary>
    public static IDisposable InstallDefaultUiFont(FontGlyphSet font)
        => InstallDefaultUiFonts(font, null);

    /// <summary>Uses caller-owned preloaded text and icon fonts during one runtime session.</summary>
    public static IDisposable InstallDefaultUiFont(FontGlyphSet font, FontGlyphSet iconFont)
    {
        ArgumentNullException.ThrowIfNull(iconFont);
        return InstallDefaultUiFonts(font, iconFont);
    }

    private static IDisposable InstallDefaultUiFonts(FontGlyphSet font, FontGlyphSet? iconFont)
    {
        ValidateDefaultUiFont(font);
        if (iconFont is not null)
            ValidateDefaultUiFont(iconFont);
        lock (DefaultUiFontGate)
        {
            if (_scopedDefaultUiFont is not null)
                throw new InvalidOperationException("BrowserFont.DefaultUiFontAlreadyInstalled.");
            _scopedDefaultUiFont = font;
            _scopedDefaultUiIconFont = iconFont;
        }
        return new DefaultUiFontScope(font, iconFont);
    }

    private static void ValidateDefaultUiFont(FontGlyphSet font)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (font.AtlasType != EFontAtlasType.Bitmap || font.Atlas is null || font.LayoutGlyphCount == 0)
            throw new NotSupportedException("BrowserFont.DefaultUiFontUnsupported: expected a cooked bitmap font with atlas and glyphs.");
    }

    private static FontGlyphSet? ScopedDefaultUiIconFont
    {
        get
        {
            lock (DefaultUiFontGate)
                return _scopedDefaultUiIconFont;
        }
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

    private sealed class DefaultUiFontScope(FontGlyphSet font, FontGlyphSet? iconFont) : IDisposable
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
                if (ReferenceEquals(_scopedDefaultUiIconFont, iconFont))
                    _scopedDefaultUiIconFont = null;
            }
        }
    }
}
