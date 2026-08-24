using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanVideoFrameUploadContext(VulkanBackendObjectRegistry registry) :
    IVulkanVideoFrameUploadContext
{
    public IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture)
        => registry.Get(texture) is VkTexture2D handle
            ? new VulkanVideoFrameTextureHandle(handle)
            : null;
}
