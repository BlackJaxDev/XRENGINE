namespace XREngine.Rendering;

/// <summary>Preserves one canonical Uber sampler's source slot, image, and physical sampling interpretation.</summary>
public sealed record UberBaseTextureBinding
{
    public int SourceTextureSlot { get; init; }
    public string SamplerName { get; init; } = string.Empty;
    public XRTexture2D Texture { get; init; } = null!;
    public PublishedStandardLitTextureSettings Settings { get; init; }
}
