using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanVideoFrameUploadContext(VulkanRenderer renderer) :
    IVulkanVideoFrameUploadContext
{
    public IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture)
        => renderer.GenericToAPI<VulkanRenderer.VkTexture2D>(texture) is { } handle
            ? new VulkanVideoFrameTextureHandle(handle)
            : null;
}
