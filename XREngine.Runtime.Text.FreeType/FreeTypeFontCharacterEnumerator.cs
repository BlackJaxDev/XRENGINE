using SharpFont;
using XREngine.Rendering;

namespace XREngine.Runtime.Text.FreeType;

/// <summary>Reads Unicode coverage from a source font with FreeType.</summary>
public sealed class FreeTypeFontCharacterEnumerator : IFontCharacterEnumerator
{
    public HashSet<uint> GetSupportedCharacters(string fontPath)
    {
        using Library library = new();
        using Face face = new(library, fontPath);
        HashSet<uint> characters = [];
        face.SetCharmap(face.CharMaps[0]);
        uint code = face.GetFirstChar(out uint glyphIndex);
        while (glyphIndex != 0)
        {
            characters.Add(code);
            code = face.GetNextChar(code, out glyphIndex);
        }
        return characters;
    }
}
