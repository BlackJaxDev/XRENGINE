namespace XREngine.Rendering;

/// <summary>Explicit unlit color and optional decoded texture on the scene wire.</summary>
internal sealed class BrowserSceneMaterialDto
{
    public BrowserSceneMaterialDto() { }

    public float[]? Tint { get; set; }
    public BrowserSceneTextureDto? Texture { get; set; }
}
