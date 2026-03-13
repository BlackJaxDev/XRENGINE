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

public interface IVulkanVideoFrameTextureHandle
{
    bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height);
}

public interface IVulkanVideoFrameUploadContext
{
    IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture);
}
