using Silk.NET.OpenGL;

namespace XREngine.Rendering.VideoStreaming;

public interface IOpenGLVideoFrameTextureHandle
{
    bool IsGenerated { get; }
    uint BindingId { get; }
    void Generate();
    void ClearInvalidation();
}

public interface IOpenGLVideoFrameUploadContext
{
    GL GL { get; }
    IOpenGLVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture);
}
