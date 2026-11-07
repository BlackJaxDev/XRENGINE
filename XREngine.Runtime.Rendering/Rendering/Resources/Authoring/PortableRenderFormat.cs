using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources.Authoring;

/// <summary>One texture format that OpenGL and Vulkan both support as a render target and as a sampled texture.</summary>
/// <param name="Sized">The sized format.</param>
/// <param name="Internal">The matching OpenGL internal format.</param>
/// <param name="PixelFormat">The pixel format for uploads and clears.</param>
/// <param name="PixelType">The pixel component type.</param>
/// <param name="IsDepth">Whether the format has a depth component.</param>
/// <param name="HasStencil">Whether the format has a stencil component.</param>
/// <param name="BytesPerPixel">The storage size of one texel.</param>
public sealed record PortableRenderFormat(
    ESizedInternalFormat Sized,
    EPixelInternalFormat Internal,
    EPixelFormat PixelFormat,
    EPixelType PixelType,
    bool IsDepth,
    bool HasStencil,
    uint BytesPerPixel)
{
    /// <summary>Gets the format name for pickers.</summary>
    public string DisplayName { get; } = Sized.ToString();
}
