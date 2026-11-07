using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources.Authoring;

/// <summary>
/// A resource declaration that an authored pipeline saves as data. <see cref="AuthoredRenderResourceFactory"/> lowers
/// it into the same layout builder calls that code pipelines make. Only the fields of <see cref="Kind"/> are used.
/// </summary>
public sealed class AuthoredRenderResource
{
    /// <summary>Gets or sets the unique resource name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of resource.</summary>
    public EAuthoredRenderResourceKind Kind { get; set; }

    /// <summary>Gets or sets whether the resource lives across frames or only inside one frame.</summary>
    public RenderResourceLifetime Lifetime { get; set; } = RenderResourceLifetime.Persistent;

    /// <summary>Gets or sets how the resource keeps content from the previous frame.</summary>
    public RenderResourceHistoryPolicy History { get; set; } = RenderResourceHistoryPolicy.None;

    /// <summary>Gets or sets the size. A buffer ignores it.</summary>
    public AuthoredSizePolicy Size { get; set; } = AuthoredSizePolicy.Internal();

    /// <summary>Gets or sets the texture or view format. It must be in <see cref="PortableRenderFormats"/>.</summary>
    public ESizedInternalFormat Format { get; set; } = ESizedInternalFormat.Rgba16f;

    /// <summary>Gets or sets the sample count of a texture or render buffer.</summary>
    public uint Samples { get; set; } = 1u;

    /// <summary>
    /// Gets or sets the layer count of a texture. Zero follows the profile: two layers in stereo and one in mono, and
    /// the texture is stereo-compatible.
    /// </summary>
    public uint Layers { get; set; }

    /// <summary>Gets or sets the mip level count of a texture.</summary>
    public uint MipLevels { get; set; } = 1u;

    /// <summary>Gets or sets whether a texture samples with linear filtering.</summary>
    public bool LinearFilter { get; set; } = true;

    /// <summary>Gets or sets whether shaders write a texture as a storage image.</summary>
    public bool StorageImage { get; set; }

    /// <summary>Gets or sets whether the layout can omit the resource without an error.</summary>
    public bool Optional { get; set; }

    /// <summary>Gets or sets the source texture of a texture view.</summary>
    [Pipelines.Commands.RenderResourceName(Pipelines.Commands.ERenderResourceNameKind.Texture)]
    public string SourceTexture { get; set; } = string.Empty;

    /// <summary>Gets or sets the first mip level of a texture view.</summary>
    public uint BaseMipLevel { get; set; }

    /// <summary>Gets or sets the first layer of a texture view.</summary>
    public uint BaseLayer { get; set; }

    /// <summary>Gets or sets the layer count of a texture view. Zero views every layer of the source.</summary>
    public uint LayerCount { get; set; }

    /// <summary>Gets or sets the aspect that a depth-stencil texture view reads.</summary>
    public EDepthStencilFmt DepthStencilAspect { get; set; } = EDepthStencilFmt.None;

    /// <summary>Gets or sets the storage format of a render buffer.</summary>
    public ERenderBufferStorage RenderBufferStorage { get; set; } = ERenderBufferStorage.Depth24Stencil8;

    /// <summary>Gets or sets the element size of a buffer, in bytes.</summary>
    public uint ElementStride { get; set; } = 16u;

    /// <summary>Gets or sets the element count of a buffer.</summary>
    public uint ElementCount { get; set; } = 1u;

    /// <summary>Gets or sets the target of a buffer.</summary>
    public EBufferTarget BufferTarget { get; set; } = EBufferTarget.ShaderStorageBuffer;

    /// <summary>Gets or sets the attachments of a framebuffer.</summary>
    public List<AuthoredAttachment> Attachments { get; set; } = [];

    /// <summary>Gets or sets the engine-relative path of the fragment shader of a quad material.</summary>
    public string FragmentShader { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the textures that a quad material samples. Each name must match a sampler of the fragment shader,
    /// and the pass metadata declares each one as a sampled texture.
    /// </summary>
    [Pipelines.Commands.RenderResourceName(Pipelines.Commands.ERenderResourceNameKind.Texture)]
    public List<string> InputTextures { get; set; } = [];

    /// <summary>Returns a deep copy.</summary>
    public AuthoredRenderResource Clone()
    {
        AuthoredRenderResource copy = (AuthoredRenderResource)MemberwiseClone();
        copy.Size = Size.Clone();
        copy.Attachments = new List<AuthoredAttachment>(Attachments.Count);
        for (int i = 0; i < Attachments.Count; i++)
            copy.Attachments.Add(Attachments[i].Clone());
        copy.InputTextures = [.. InputTextures];

        return copy;
    }
}
