namespace XREngine.Rendering;

/// <summary>
/// Preserves an outline sampler's exact source slot, image identity, and otherwise omitted texture settings.
/// A plain image carries <see cref="Settings"/>. The source flipbook array carries <see cref="ArrayProfile"/>
/// instead, because its layer images keep their own settings.
/// </summary>
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

    public UberOutlineTextureBinding(int sourceTextureSlot, string samplerName, XRTexture2DArray texture,
        PublishedTextureArrayProfile arrayProfile)
    {
        SourceTextureSlot = sourceTextureSlot;
        SamplerName = samplerName;
        Texture = texture;
        ArrayProfile = arrayProfile;
    }

    public int SourceTextureSlot { get; init; }
    public string SamplerName { get; init; } = string.Empty;
    /// <summary>An exact <see cref="XRTexture2D"/>, or an exact <see cref="XRTexture2DArray"/> with an array profile.</summary>
    public XRTexture Texture { get; init; } = null!;
    public PublishedStandardLitTextureSettings Settings { get; init; }
    public PublishedTextureArrayProfile? ArrayProfile { get; init; }
}
