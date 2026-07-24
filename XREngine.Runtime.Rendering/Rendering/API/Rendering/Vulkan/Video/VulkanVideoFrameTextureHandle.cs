using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanVideoFrameTextureHandle(VulkanRenderer.VkTexture2D textureHandle) :
    IVulkanVideoFrameTextureHandle
{
    public bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height)
        => textureHandle.UploadVideoFrameData(pixelData, width, height);
}
