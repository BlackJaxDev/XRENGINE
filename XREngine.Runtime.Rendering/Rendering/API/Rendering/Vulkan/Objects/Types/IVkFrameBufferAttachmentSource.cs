using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal interface IVkFrameBufferAttachmentSource : IVkImageDescriptorSource
    {
        ImageView GetAttachmentView(int mipLevel, int layerIndex);
        void EnsureAttachmentLayout(bool depthStencil);

        /// <summary>
        /// Updates the internally tracked image layout.  Called by
        /// <c>UpdatePhysicalGroupLayoutsForFbo</c> after a render pass ends so the
        /// tracked layout reflects the render pass's <c>finalLayout</c>.
        /// </summary>
        void UpdateTrackedLayout(ImageLayout layout) { }
    }
}
