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

    /// <summary>Explicitly selects FreeType bitmap cooking without changing desktop startup's Skia choice.</summary>
    public static void RegisterBitmapRasterizerForCooking()
        => FontBitmapRasterizerRegistry.Current = new FreeTypeFontBitmapRasterizer();
}
