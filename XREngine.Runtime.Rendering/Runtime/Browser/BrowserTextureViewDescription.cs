namespace XREngine.Rendering;

/// <summary>A generation-checked view over contiguous mips and layers with one compatible aspect.</summary>
public sealed record BrowserTextureViewDescription(int TextureHandle, int BaseMip = 0,
    int MipCount = 1, string Aspect = "all", string Label = "", int BaseArrayLayer = 0,
    int ArrayLayerCount = 1, string Dimension = "2d");
