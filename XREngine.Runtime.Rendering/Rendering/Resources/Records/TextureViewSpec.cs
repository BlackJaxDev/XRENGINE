using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a logical specification for a texture view resource in the render pipeline.
/// </summary>
/// <param name="Name">The name of the texture view resource.</param>
/// <param name="Lifetime">The lifetime of the texture view resource.</param>
/// <param name="SizePolicy">The size policy of the texture view resource.</param>
/// <param name="Usage">The usage of the texture view resource.</param>
/// <param name="Dependencies">The dependencies of the texture view resource.</param>
/// <param name="Predicate">The predicate for the texture view resource.</param>
/// <param name="HistoryPolicy">The history policy of the texture view resource.</param>
/// <param name="DebugLabel">The debug label of the texture view resource.</param>
/// <param name="Required">Indicates whether the texture view resource is required.</param>
/// <param name="SourceTextureName">The name of the source texture for the texture view.</param>
/// <param name="BaseMipLevel">The base mip level for the texture view.</param>
/// <param name="MipLevelCount">The number of mip levels for the texture view.</param>
/// <param name="BaseLayer">The base layer for the texture view.</param>
/// <param name="LayerCount">The number of layers for the texture view.</param>
/// <param name="SizedInternalFormat">The sized internal format for the texture view.</param>
/// <param name="DepthStencilAspect">The depth-stencil aspect for the texture view.</param>
/// <param name="ArrayTarget">Indicates whether the texture view is an array target.</param>
/// <param name="Multisample">Indicates whether the texture view is multisampled.</param>
/// <param name="Factory">The factory function for creating the texture view resource.</param>
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
    /// <summary>
    /// Converts this texture view specification into a descriptor that can be used to create or manage the actual texture view resource.
    /// </summary>
    /// <returns>A texture resource descriptor representing the configuration of the texture view resource.</returns>
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
