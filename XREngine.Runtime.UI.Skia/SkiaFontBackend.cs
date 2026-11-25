using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Runtime.UI.Skia;

/// <summary>Installs Skia authoring services for bitmap glyph atlases.</summary>
public static class SkiaFontBackend
{
    public static void Register()
    {
        FontBitmapRasterizerRegistry.Current = new SkiaFontBitmapRasterizer();
        UITextShapingBackendRegistry.Current = new SkiaTextShapingBackend();
    }
}
