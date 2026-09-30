using XREngine.Rendering;

namespace XREngine.Runtime.Text.FreeType;

/// <summary>Installs native source-font discovery and distance-field atlas generation for authoring.</summary>
public static class FreeTypeFontBackend
{
    public static void Register()
    {
        FontCharacterEnumeratorRegistry.Current = new FreeTypeFontCharacterEnumerator();
        FontDistanceFieldAtlasGeneratorRegistry.Current = new MsdfAtlasGenFontAtlasGenerator();
    }
}
