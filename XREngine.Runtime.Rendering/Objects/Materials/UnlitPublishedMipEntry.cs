using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Static unlit mip metadata; pixel bytes remain owned by the image payload.</summary>
public sealed record UnlitPublishedMipEntry
{
    public uint Width { get; init; }
    public uint Height { get; init; }
    public EPixelInternalFormat InternalFormat { get; init; }
    public EPixelFormat PixelFormat { get; init; }
    public EPixelType PixelType { get; init; }
    public uint DataLength { get; init; }
}
