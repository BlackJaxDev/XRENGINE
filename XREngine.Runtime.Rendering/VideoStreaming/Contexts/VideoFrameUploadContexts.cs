namespace XREngine.Rendering.VideoStreaming;

public interface IVulkanVideoFrameTextureHandle
{
    bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height);
}

public interface IVulkanVideoFrameUploadContext
{
    IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture);
}
