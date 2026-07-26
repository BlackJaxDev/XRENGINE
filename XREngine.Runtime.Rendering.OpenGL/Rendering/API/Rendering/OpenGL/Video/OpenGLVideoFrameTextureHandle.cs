using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.OpenGL;

internal sealed class OpenGLVideoFrameTextureHandle(GLTexture2D textureHandle) :
    IOpenGLVideoFrameTextureHandle
{
    public bool IsGenerated => textureHandle.IsGenerated;
    public uint BindingId => textureHandle.BindingId;

    public void Generate() => textureHandle.Generate();
    public void ClearInvalidation() => textureHandle.ClearInvalidation();
}
