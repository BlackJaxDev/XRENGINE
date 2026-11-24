using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanVideoFrameTextureHandle(VkTexture2D textureHandle) :
    IVulkanVideoFrameTextureHandle
{
    /// <summary>Returns whether this handle forwards uploads to <paramref name="wrapper"/>.</summary>
    public bool Wraps(VkTexture2D wrapper)
        => ReferenceEquals(textureHandle, wrapper);

    public bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height, out string? failureReason)
        => textureHandle.UploadVideoFrameData(pixelData, width, height, out failureReason);
}
