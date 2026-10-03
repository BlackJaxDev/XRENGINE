namespace XREngine.Rendering;

/// <summary>Reads supported Unicode codepoints from a source font during import.</summary>
public interface IFontCharacterEnumerator
{
    HashSet<uint> GetSupportedCharacters(string fontPath);
}
