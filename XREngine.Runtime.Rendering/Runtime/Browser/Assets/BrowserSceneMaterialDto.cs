namespace XREngine.Rendering;

/// <summary>Explicit unlit color and optional decoded texture on the scene wire.</summary>
internal sealed class BrowserSceneMaterialDto
{
    public BrowserSceneMaterialDto() { }

    public float[]? Tint { get; set; }
    public BrowserSceneTextureDto? Texture { get; set; }
    public string AlphaMode { get; set; } = "opaque";
    public string Shading { get; set; } = "unlit";
    public string CullMode { get; set; } = "none";
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool CastShadow { get; set; } = true;
    public bool ReceiveShadow { get; set; } = true;
}
