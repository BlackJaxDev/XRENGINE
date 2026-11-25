namespace XREngine.Rendering;

/// <summary>Glyph metrics for an atlas PNG produced by a font rasterizer.</summary>
public sealed class FontBitmapAtlasResult
{
    private readonly byte[]? _faceBytes;

    public FontBitmapAtlasResult(Dictionary<string, FontGlyphSet.Glyph> glyphs,
        Dictionary<string, ushort>? glyphIds = null, byte[]? faceBytes = null, int faceIndex = 0)
    {
        Glyphs = glyphs;
        GlyphIds = glyphIds;
        _faceBytes = faceBytes is null ? null : (byte[])faceBytes.Clone();
        FaceIndex = faceIndex;
    }

    public Dictionary<string, FontGlyphSet.Glyph> Glyphs { get; }
    public Dictionary<string, ushort>? GlyphIds { get; }
    public int FaceIndex { get; }
    public byte[]? CopyFaceBytes() => _faceBytes is null ? null : (byte[])_faceBytes.Clone();
}
