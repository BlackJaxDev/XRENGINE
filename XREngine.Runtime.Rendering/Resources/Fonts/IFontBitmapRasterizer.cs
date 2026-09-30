namespace XREngine.Rendering;

/// <summary>Rasterizes a source font into a cooked bitmap atlas and glyph metrics.</summary>
public interface IFontBitmapRasterizer
{
    FontBitmapAtlasResult Rasterize(string fontPath, IReadOnlyList<string> characters, string outputAtlasPath, float textSize);
}
