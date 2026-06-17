using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

public sealed record TextureSpec(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    RenderPipelineResourceUsage Usage,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    RenderResourceHistoryPolicy HistoryPolicy,
    string? DebugLabel,
    bool Required,
    EPixelInternalFormat? InternalFormat,
    EPixelFormat? PixelFormat,
    EPixelType? PixelType,
    ESizedInternalFormat? SizedInternalFormat,
    uint Samples,
    uint Layers,
    RenderResourceMipPolicy MipPolicy,
    bool StereoCompatible,
    bool RequiresStorageUsage,
    Func<XRTexture>? Factory)
    : RenderPipelineResourceSpec(
        Name,
        RenderPipelineResourceKind.Texture,
        Lifetime,
        SizePolicy,
        Usage,
        Dependencies,
        Predicate,
        HistoryPolicy,
        DebugLabel,
        Required)
{
    public TextureResourceDescriptor ToDescriptor()
        => new(
            Name,
            Lifetime,
            SizePolicy,
            ResolveFormatLabel(),
            StereoCompatible,
            Math.Max(1u, Layers),
            SupportsAliasing: Lifetime == RenderResourceLifetime.Transient,
            RequiresStorageUsage,
            Kind: RenderPipelineResourceKind.Texture,
            Usage: Usage,
            InternalFormat: InternalFormat,
            PixelFormat: PixelFormat,
            PixelType: PixelType,
            SizedInternalFormat: SizedInternalFormat,
            Samples: Math.Max(1u, Samples),
            MipPolicy: NormalizeMipPolicy(MipPolicy),
            BaseMipLevel: MipPolicy.BaseMipLevel,
            MipLevelCount: Math.Max(1u, MipPolicy.MipLevelCount),
            BaseLayer: 0u,
            LayerCount: Math.Max(1u, Layers));

    private string? ResolveFormatLabel()
        => SizedInternalFormat?.ToString()
            ?? InternalFormat?.ToString()
            ?? DebugLabel;

    private static RenderResourceMipPolicy NormalizeMipPolicy(RenderResourceMipPolicy mipPolicy)
        => mipPolicy with { MipLevelCount = Math.Max(1u, mipPolicy.MipLevelCount) };
}
