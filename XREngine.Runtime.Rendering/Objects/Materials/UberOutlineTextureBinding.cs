namespace XREngine.Rendering;

/// <summary>Preserves an outline sampler's exact source slot, image identity, and otherwise omitted texture settings.</summary>
public sealed record UberOutlineTextureBinding
{
    public UberOutlineTextureBinding() { }

    public UberOutlineTextureBinding(int sourceTextureSlot, string samplerName, XRTexture2D texture,
        PublishedStandardLitTextureSettings settings)
    {
        SourceTextureSlot = sourceTextureSlot;
        SamplerName = samplerName;
        Texture = texture;
        Settings = settings;
    }

    public int SourceTextureSlot { get; init; }
    public string SamplerName { get; init; } = string.Empty;
    public XRTexture2D Texture { get; init; } = null!;
    public PublishedStandardLitTextureSettings Settings { get; init; }
}
