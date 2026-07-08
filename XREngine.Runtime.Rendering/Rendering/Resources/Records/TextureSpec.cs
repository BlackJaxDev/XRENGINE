using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Represents a logical specification for a texture resource in the render pipeline.
/// </summary>
/// <param name="Name">The name of the texture resource.</param>
/// <param name="Lifetime">The lifetime of the texture resource.</param>
/// <param name="SizePolicy">The size policy of the texture resource.</param>
/// <param name="Usage">The usage of the texture resource.</param>
/// <param name="Dependencies">The dependencies of the texture resource.</param>
/// <param name="Predicate">The predicate for the texture resource.</param>
/// <param name="HistoryPolicy">The history policy of the texture resource.</param>
/// <param name="DebugLabel">The debug label of the texture resource.</param>
/// <param name="Required">Indicates whether the texture resource is required.</param>
/// <param name="InternalFormat">The internal format of the texture resource.</param>
/// <param name="PixelFormat">The pixel format of the texture resource.</param>
/// <param name="PixelType">The pixel type of the texture resource.</param>
/// <param name="SizedInternalFormat">The sized internal format of the texture resource.</param>
/// <param name="Samples">The number of samples for the texture resource.</param>
/// <param name="Layers">The number of layers for the texture resource.</param>
/// <param name="MipPolicy">The mipmap policy for the texture resource.</param>
/// <param name="StereoCompatible">Indicates whether the texture resource is stereo compatible.</param>
/// <param name="RequiresStorageUsage">Indicates whether the texture resource requires storage usage.</param>
/// <param name="Factory">The factory function for creating the texture resource.</param>
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
    /// <summary>
    /// Converts this texture specification into a descriptor that can be used to create or manage the actual texture resource.
    /// </summary>
    /// <returns>A texture resource descriptor representing the configuration of the texture resource.</returns>
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

    /// <summary>
    /// Resolves the format label for the texture resource.
    /// </summary>
    /// <returns>The format label as a string.</returns>
    private string? ResolveFormatLabel()
        => SizedInternalFormat?.ToString()
            ?? InternalFormat?.ToString()
            ?? DebugLabel;

    /// <summary>
    /// Normalizes the mipmap policy for the texture resource, ensuring that the mip level count is at least 1.
    /// </summary>
    /// <param name="mipPolicy">The mipmap policy to normalize.</param>
    /// <returns>A normalized mipmap policy with a minimum mip level count of 1.</returns>
    private static RenderResourceMipPolicy NormalizeMipPolicy(RenderResourceMipPolicy mipPolicy)
        => mipPolicy with { MipLevelCount = Math.Max(1u, mipPolicy.MipLevelCount) };
}
