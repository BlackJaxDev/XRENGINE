namespace XREngine.Rendering;

/// <summary>A two-dimensional texture with one array layer and an explicit color encoding.</summary>
public sealed record BrowserTextureDescription(int Width, int Height, string Format,
    BrowserTextureUsage Usage, int MipLevelCount = 1, int SampleCount = 1, string Label = "");
