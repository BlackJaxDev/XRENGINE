namespace XREngine.Rendering;

/// <summary>Decoded sRGB RGBA8 texture on the scene wire.</summary>
internal sealed class BrowserSceneTextureDto
{
    public BrowserSceneTextureDto() { }

    public int Width { get; set; }
    public int Height { get; set; }
    public byte[]? Rgba { get; set; }
}
