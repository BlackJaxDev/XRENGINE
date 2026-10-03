namespace XREngine.Rendering;

/// <summary>A generation-checked view over contiguous mips and one format-compatible aspect.</summary>
public sealed record BrowserTextureViewDescription(int TextureHandle, int BaseMip = 0,
    int MipCount = 1, string Aspect = "all", string Label = "");
