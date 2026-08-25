using System.Collections.Concurrent;

namespace XREngine.Rendering.RenderGraph;

public static class RenderGraphResourceNames
{
    private static readonly ConcurrentDictionary<string, string> FboColorNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Name, int Index), string> FboIndexedColorNames = new();
    private static readonly ConcurrentDictionary<string, string> FboDepthNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> FboStencilNames = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> TextureNames = new(StringComparer.Ordinal);

    /// <summary>
    /// The name of the output render target.
    /// </summary>
    public const string OutputRenderTarget = "__OUTPUT_FBO__";

    /// <summary>
    /// The name of the output depth buffer.
    /// </summary>
    /// <param name="fboName">The name of the framebuffer object.</param>
    /// <returns>The render graph resource name for the color attachment.</returns>
    public static string MakeFboColor(string fboName)
        => FboColorNames.GetOrAdd(fboName, static name => $"fbo::{name}::color");

    /// <summary>
    /// Returns the logical name for a specific framebuffer color attachment.
    /// Attachment zero retains the legacy unindexed name.
    /// </summary>
    public static string MakeFboColor(string fboName, int colorAttachmentIndex)
        => colorAttachmentIndex <= 0
            ? MakeFboColor(fboName)
            : FboIndexedColorNames.GetOrAdd(
                (fboName, colorAttachmentIndex),
                static key => $"fbo::{key.Name}::color{key.Index}");

    /// <summary>
    /// The name of the output depth buffer.
    /// </summary>
    /// <param name="fboName">The name of the framebuffer object.</param>
    /// <returns>The render graph resource name for the depth attachment.</returns>
    public static string MakeFboDepth(string fboName)
        => FboDepthNames.GetOrAdd(fboName, static name => $"fbo::{name}::depth");

    /// <summary>
    /// The name of the output stencil buffer.
    /// </summary>
    /// <param name="fboName">The name of the framebuffer object.</param>
    /// <returns>The render graph resource name for the stencil attachment.</returns>
    public static string MakeFboStencil(string fboName)
        => FboStencilNames.GetOrAdd(fboName, static name => $"fbo::{name}::stencil");

    /// <summary>
    /// The name of the output texture.
    /// </summary>
    /// <param name="textureName">The name of the texture.</param>
    /// <returns>The render graph resource name for the texture.</returns>
    public static string MakeTexture(string textureName)
        => TextureNames.GetOrAdd(textureName, static name => $"tex::{name}");
}
