namespace XREngine.Rendering;

/// <summary>
/// Describes the row/orientation convention retained in a capture texture.
/// </summary>
public enum ERenderCaptureTextureOrientation
{
    /// <summary>
    /// Keep the backend framebuffer's native texture orientation. Sampling code
    /// must use <see cref="RenderClipSpacePolicy.FramebufferTextureYDirection"/>.
    /// </summary>
    BackendFramebufferNative,
}
