using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal interface IVkFrameBufferAttachmentSource : IVkImageDescriptorSource
    {
        ImageView GetAttachmentView(int mipLevel, int layerIndex);
    }
}
