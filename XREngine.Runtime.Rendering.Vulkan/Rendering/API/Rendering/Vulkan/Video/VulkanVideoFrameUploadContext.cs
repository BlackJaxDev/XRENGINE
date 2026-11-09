using XREngine.Rendering.VideoStreaming;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Resolves the Vulkan wrapper of a video texture from the renderer's backend object registry.
/// </summary>
/// <remarks>
/// The lookup does not create a wrapper. The first decoded frame resizes the texture to the
/// stream size; the resize destroys the image, and the registry drops the wrapper until
/// descriptor preparation for the next video draw generates it again. That frame fails and
/// a later frame succeeds. Use one instance on the render thread only.
/// </remarks>
internal sealed class VulkanVideoFrameUploadContext(VulkanBackendObjectRegistry registry) :
    IVulkanVideoFrameUploadContext
{
    private VulkanVideoFrameTextureHandle? _cachedHandle;

    public IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture)
    {
        if (registry.Get(texture) is not VkTexture2D wrapper)
            return null;

        // Keep one handle while the wrapper stays the same, so a frame upload does not allocate.
        VulkanVideoFrameTextureHandle? handle = _cachedHandle;
        if (handle is null || !handle.Wraps(wrapper))
            _cachedHandle = handle = new VulkanVideoFrameTextureHandle(wrapper);

        return handle;
    }
}
