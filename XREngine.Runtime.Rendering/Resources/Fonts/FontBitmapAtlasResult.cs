namespace XREngine.Rendering;

/// <summary>Glyph metrics for an atlas PNG produced by a font rasterizer.</summary>
public sealed class FontBitmapAtlasResult(Dictionary<string, FontGlyphSet.Glyph> glyphs)
{
    public Dictionary<string, FontGlyphSet.Glyph> Glyphs { get; } = glyphs;
}
