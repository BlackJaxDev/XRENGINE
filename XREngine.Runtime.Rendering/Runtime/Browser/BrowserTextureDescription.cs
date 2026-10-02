namespace XREngine.Rendering;

/// <summary>A two-dimensional texture with an explicit color encoding and bounded array layers.</summary>
public sealed record BrowserTextureDescription(int Width, int Height, string Format,
    BrowserTextureUsage Usage, int MipLevelCount = 1, int SampleCount = 1, string Label = "",
    int ArrayLayerCount = 1);
