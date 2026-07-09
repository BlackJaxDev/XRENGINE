namespace XREngine.Rendering.RenderGraph;

public static class RenderGraphResourceNames
{
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
        => $"fbo::{fboName}::color";

    /// <summary>
    /// The name of the output depth buffer.
    /// </summary>
    /// <param name="fboName">The name of the framebuffer object.</param>
    /// <returns>The render graph resource name for the depth attachment.</returns>
    public static string MakeFboDepth(string fboName)
        => $"fbo::{fboName}::depth";

    /// <summary>
    /// The name of the output stencil buffer.
    /// </summary>
    /// <param name="fboName">The name of the framebuffer object.</param>
    /// <returns>The render graph resource name for the stencil attachment.</returns>
    public static string MakeFboStencil(string fboName)
        => $"fbo::{fboName}::stencil";

    /// <summary>
    /// The name of the output texture.
    /// </summary>
    /// <param name="textureName">The name of the texture.</param>
    /// <returns>The render graph resource name for the texture.</returns>
    public static string MakeTexture(string textureName)
        => $"tex::{textureName}";
}
