using Silk.NET.OpenGL;
using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.OpenGL;

internal sealed class OpenGLVideoFrameUploadContext(OpenGLRenderer renderer) :
    IOpenGLVideoFrameUploadContext
{
    public GL GL => renderer.RawGL;

    public IOpenGLVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture)
        => renderer.GenericToAPI<GLTexture2D>(texture) is { } handle
            ? new OpenGLVideoFrameTextureHandle(handle)
            : null;
}
