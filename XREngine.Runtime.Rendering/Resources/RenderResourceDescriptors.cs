using XREngine.Data.Rendering;
using static XREngine.Rendering.XRTexture;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Specifies the lifetime of a render resource, indicating how long it will be retained and managed by the rendering system.
/// </summary>
public enum RenderResourceLifetime
{
    /// <summary>
    /// The resource will be retained and managed by the rendering system for the entire lifetime of the application.
    /// </summary>
    Persistent,
    /// <summary>
    /// The resource will be retained and managed by the rendering system only for a short duration, typically within a single frame or a few frames.
    /// </summary>
    Transient,
    /// <summary>
    /// The resource is managed externally and the rendering system does not take ownership of its lifetime.
    /// </summary>
    External
}

/// <summary>
/// Specifies the size classification of a render resource, indicating how its dimensions are determined.
/// </summary>
public enum RenderResourceSizeClass
{
    /// <summary>
    /// The resource has an absolute size specified in pixels.
    /// </summary>
    AbsolutePixels,
    /// <summary>
    /// The resource size is determined relative to the internal resolution of the rendering system.
    /// </summary>
    InternalResolution,
    /// <summary>
    /// The resource size is determined relative to the window resolution.
    /// </summary>
    WindowResolution,
    /// <summary>
    /// The resource size is determined by a custom scale factor.
    /// </summary>
    Custom
}

/// <summary>
/// Describes the intended GPU-side access pattern for a buffer resource.
/// </summary>
public enum EBufferAccessPattern
{
    /// <summary>
    /// The buffer resource is intended to be read-only from the GPU's perspective.
    /// </summary>
    ReadOnly,
    /// <summary>
    /// The buffer resource is intended to be written to only from the GPU's perspective.
    /// </summary>
    WriteOnly,
    /// <summary>
    /// The buffer resource is intended to be both read from and written to from the GPU's perspective.
    /// </summary>
    ReadWrite
}

/// <summary>
/// Represents the size policy for a render resource, including its size classification and scaling factors or absolute dimensions.
/// </summary>
/// <param name="SizeClass">The size classification of the resource.</param>
/// <param name="ScaleX">The horizontal scaling factor for the resource.</param>
/// <param name="ScaleY">The vertical scaling factor for the resource.</param>
/// <param name="Width">The absolute width of the resource in pixels.</param>
/// <param name="Height">The absolute height of the resource in pixels.</param>
public readonly record struct RenderResourceSizePolicy(
    RenderResourceSizeClass SizeClass,
    float ScaleX = 1f,
    float ScaleY = 1f,
    uint Width = 0,
    uint Height = 0)
{
    /// <summary>
    /// Creates a new render resource size policy with an absolute size specified in pixels.
    /// </summary>
    /// <param name="width">The absolute width of the resource in pixels.</param>
    /// <param name="height">The absolute height of the resource in pixels.</param>
    /// <returns>A new instance of <see cref="RenderResourceSizePolicy"/> with the specified absolute size.</returns>
    public static RenderResourceSizePolicy Absolute(uint width, uint height)
        => new(RenderResourceSizeClass.AbsolutePixels, 1f, 1f, width, height);

    /// <summary>
    /// Creates a new render resource size policy based on the internal resolution with an optional scaling factor.
    /// </summary>
    /// <param name="scale">The scaling factor relative to the internal resolution.</param>
    /// <returns>A new instance of <see cref="RenderResourceSizePolicy"/> with the specified internal resolution scale.</returns>
    public static RenderResourceSizePolicy Internal(float scale = 1f)
        => new(RenderResourceSizeClass.InternalResolution, scale, scale);

    /// <summary>
    /// Creates a new render resource size policy based on the window resolution with an optional scaling factor.
    /// </summary>
    /// <param name="scale">The scaling factor relative to the window resolution.</param>
    /// <returns>A new instance of <see cref="RenderResourceSizePolicy"/> with the specified window resolution scale.</returns>
    public static RenderResourceSizePolicy Window(float scale = 1f)
        => new(RenderResourceSizeClass.WindowResolution, scale, scale);

    /// <summary>
    /// Creates a new render resource size policy with custom scaling factors.
    /// </summary>
    /// <param name="scaleX">The horizontal scaling factor for the resource.</param>
    /// <param name="scaleY">The vertical scaling factor for the resource.</param>
    /// <returns>A new instance of <see cref="RenderResourceSizePolicy"/> with the specified custom scaling factors.</returns>
    public static RenderResourceSizePolicy Custom(float scaleX, float scaleY)
        => new(RenderResourceSizeClass.Custom, scaleX, scaleY);
}

/// <summary>
/// Describes a generic render resource used in the rendering pipeline.
/// </summary>
/// <param name="Name">The name of the render resource.</param>
/// <param name="Lifetime">The lifetime of the render resource.</param>
/// <param name="SizePolicy">The size policy of the render resource.</param>
public abstract record RenderResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy);

/// <summary>
/// Describes a texture resource used in the rendering pipeline.
/// </summary>
/// <param name="Name">The name of the texture resource.</param>
/// <param name="Lifetime">The lifetime of the texture resource.</param>
/// <param name="SizePolicy">The size policy of the texture resource.</param>
/// <param name="FormatLabel">The format label of the texture resource.</param>
/// <param name="StereoCompatible">Indicates if the texture is stereo compatible.</param>
/// <param name="ArrayLayers">The number of array layers in the texture.</param>
/// <param name="SupportsAliasing">Indicates if the texture supports aliasing.</param>
/// <param name="RequiresStorageUsage">Indicates if the texture requires storage usage.</param>
/// <param name="Kind">The kind of the texture resource.</param>
/// <param name="Usage">The usage flags for the texture resource.</param>
/// <param name="InternalFormat">The internal format of the texture.</param>
/// <param name="PixelFormat">The pixel format of the texture.</param>
/// <param name="PixelType">The pixel type of the texture.</param>
/// <param name="SizedInternalFormat">The sized internal format of the texture.</param>
/// <param name="Samples">The number of samples for multisampling.</param>
/// <param name="MipPolicy">The mipmap policy for the texture.</param>
/// <param name="SourceTextureName">The name of the source texture, if any.</param>
/// <param name="BaseMipLevel">The base mip level of the texture.</param>
/// <param name="MipLevelCount">The number of mip levels in the texture.</param>
/// <param name="BaseLayer">The base layer of the texture.</param>
/// <param name="LayerCount">The number of layers in the texture.</param>
/// <param name="DepthStencilAspect">The depth-stencil aspect of the texture.</param>
/// <param name="ArrayTarget">The array target of the texture.</param>
/// <param name="Multisample">Indicates if the texture uses multisampling.</param>
/// <param name="DepthStencilAspect">The depth-stencil aspect of the texture.</param>
/// <param name="ArrayTarget">Indicates if the texture is an array target.</param>
/// <param name="Multisample">Indicates if the texture uses multisampling.</param>
public sealed record TextureResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    string? FormatLabel = null,
    bool StereoCompatible = false,
    uint ArrayLayers = 1,
    bool SupportsAliasing = false,
    bool RequiresStorageUsage = false,
    RenderPipelineResourceKind Kind = RenderPipelineResourceKind.Texture,
    RenderPipelineResourceUsage Usage = RenderPipelineResourceUsage.None,
    EPixelInternalFormat? InternalFormat = null,
    EPixelFormat? PixelFormat = null,
    EPixelType? PixelType = null,
    ESizedInternalFormat? SizedInternalFormat = null,
    uint Samples = 1,
    RenderResourceMipPolicy MipPolicy = default,
    string? SourceTextureName = null,
    uint BaseMipLevel = 0,
    uint MipLevelCount = 1,
    uint BaseLayer = 0,
    uint LayerCount = 1,
    EDepthStencilFmt DepthStencilAspect = EDepthStencilFmt.None,
    bool ArrayTarget = false,
    bool Multisample = false)
    : RenderResourceDescriptor(Name, Lifetime, SizePolicy);

/// <summary>
/// Describes an attachment of a framebuffer resource used in the rendering pipeline.
/// </summary>
/// <param name="ResourceName">The name of the framebuffer resource.</param>
/// <param name="Attachment">The type of attachment for the framebuffer resource.</param>
/// <param name="MipLevel">The mip level of the attachment.</param>
/// <param name="LayerIndex">The layer index of the attachment.</param>
public readonly record struct FrameBufferAttachmentDescriptor(
    string ResourceName,
    EFrameBufferAttachment Attachment,
    int MipLevel,
    int LayerIndex);

/// <summary>
/// Describes a framebuffer resource used in the rendering pipeline.
/// </summary>
/// <param name="Name">The name of the framebuffer resource.</param>
/// <param name="Lifetime">The lifetime of the framebuffer resource.</param>
/// <param name="SizePolicy">The size policy of the framebuffer resource.</param>
/// <param name="Attachments">The list of attachments for the framebuffer resource.</param>
public sealed record FrameBufferResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    IReadOnlyList<FrameBufferAttachmentDescriptor> Attachments)
    : RenderResourceDescriptor(Name, Lifetime, SizePolicy);

/// <summary>
/// Describes a render buffer resource used in the rendering pipeline.
/// </summary>
/// <param name="Name">The name of the render buffer resource.</param>
/// <param name="Lifetime">The lifetime of the render buffer resource.</param>
/// <param name="SizePolicy">The size policy of the render buffer resource.</param>
/// <param name="StorageFormat">The storage format of the render buffer.</param>
/// <param name="MultisampleCount">The number of samples for multisampling.</param>
/// <param name="DefaultAttachment">The default framebuffer attachment for the render buffer.</param>
public sealed record RenderBufferResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    RenderResourceSizePolicy SizePolicy,
    ERenderBufferStorage StorageFormat,
    uint MultisampleCount = 1,
    EFrameBufferAttachment? DefaultAttachment = null)
    : RenderResourceDescriptor(Name, Lifetime, SizePolicy);

/// <summary>
/// Describes a buffer resource used in the rendering pipeline.
/// </summary>
/// <param name="Name">The name of the buffer resource.</param>
/// <param name="Lifetime">The lifetime of the buffer resource.</param>
/// <param name="SizeInBytes">The size of the buffer resource in bytes.</param>
/// <param name="Target">The target type of the buffer resource.</param>
/// <param name="Usage">The usage pattern of the buffer resource.</param>
/// <param name="SupportsAliasing">Indicates whether the buffer resource supports aliasing.</param>
/// <param name="ElementStride">The stride of each element in the buffer.</param>
/// <param name="ElementCount">The number of elements in the buffer.</param>
/// <param name="AccessPattern">The access pattern of the buffer resource.</param>
public sealed record BufferResourceDescriptor(
    string Name,
    RenderResourceLifetime Lifetime,
    ulong SizeInBytes,
    EBufferTarget Target,
    EBufferUsage Usage,
    bool SupportsAliasing = true,
    uint ElementStride = 0,
    uint ElementCount = 0,
    EBufferAccessPattern AccessPattern = EBufferAccessPattern.ReadWrite);
