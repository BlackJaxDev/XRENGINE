using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

public sealed record TextureViewSpec(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    RenderPipelineResourceUsage Usage,
    IReadOnlyList<string> Dependencies,
    RenderPipelineResourcePredicate? Predicate,
    RenderResourceHistoryPolicy HistoryPolicy,
    string? DebugLabel,
    bool Required,
    string SourceTextureName,
    uint BaseMipLevel,
    uint MipLevelCount,
    uint BaseLayer,
    uint LayerCount,
    ESizedInternalFormat? SizedInternalFormat,
    EDepthStencilFmt DepthStencilAspect,
    bool ArrayTarget,
    bool Multisample,
    Func<XRTexture>? Factory)
    : RenderPipelineResourceSpec(
        Name,
        RenderPipelineResourceKind.TextureView,
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
            SizedInternalFormat?.ToString() ?? DebugLabel,
            StereoCompatible: LayerCount > 1u,
            ArrayLayers: Math.Max(1u, LayerCount),
            SupportsAliasing: false,
            RequiresStorageUsage: false,
            Kind: RenderPipelineResourceKind.TextureView,
            Usage: Usage,
            SizedInternalFormat: SizedInternalFormat,
            Samples: Multisample ? 2u : 1u,
            MipPolicy: new RenderResourceMipPolicy(BaseMipLevel, Math.Max(1u, MipLevelCount)),
            SourceTextureName: SourceTextureName,
            BaseMipLevel: BaseMipLevel,
            MipLevelCount: Math.Max(1u, MipLevelCount),
            BaseLayer: BaseLayer,
            LayerCount: Math.Max(1u, LayerCount),
            DepthStencilAspect: DepthStencilAspect,
            ArrayTarget: ArrayTarget,
            Multisample: Multisample);
}
