using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>One distinct unlit image; DirectImage is populated only for a plain Texture0 image.</summary>
public sealed record UnlitPublishedImageEntry
{
    public int FirstLayer { get; init; }
    public Guid ImageId { get; init; }
    public XRTexture2D? DirectImage { get; init; }
    public ESizedInternalFormat SizedInternalFormat { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public UnlitPublishedMipEntry[] Mips { get; init; } = [];
    public PublishedStandardLitTextureSettings Settings { get; init; }
    public ETexMinFilter MinFilter { get; init; }
    public ETexMagFilter MagFilter { get; init; }
    public ETexWrapMode UWrap { get; init; }
    public ETexWrapMode VWrap { get; init; }
    public float LodBias { get; init; }
}
