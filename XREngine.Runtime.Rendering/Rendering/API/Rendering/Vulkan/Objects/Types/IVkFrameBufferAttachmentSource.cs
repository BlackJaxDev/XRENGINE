using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal interface IVkFrameBufferAttachmentSource : IVkImageDescriptorSource
    {
        ImageView GetAttachmentView(int mipLevel, int layerIndex);
        void EnsureAttachmentLayout(bool depthStencil);

        /// <summary>
        /// Returns the effective extent of the image view that will be used for an
        /// attachment. Physical render-resource images can resize independently of
        /// the engine-side texture object during live window resize, so framebuffer
        /// render areas must prefer this Vulkan-resolved extent when available.
        /// </summary>
        bool TryGetAttachmentExtent(int mipLevel, int layerIndex, out Extent2D extent)
        {
            extent = default;
            return false;
        }

        /// <summary>
        /// Updates the internally tracked image layout.  Called by
        /// <c>UpdatePhysicalGroupLayoutsForFbo</c> after a render pass ends so the
        /// tracked layout reflects the render pass's <c>finalLayout</c>.
        /// </summary>
        void UpdateTrackedLayout(ImageLayout layout) { }

        /// <summary>
        /// Returns the tracked layout for the exact framebuffer attachment subresource.
        /// The default keeps legacy whole-image tracking for attachment types that do
        /// not expose independent mip/layer layouts.
        /// </summary>
        ImageLayout GetAttachmentTrackedLayout(int mipLevel, int layerIndex)
            => TrackedImageLayout;

        /// <summary>
        /// Updates the tracked layout for the exact framebuffer attachment subresource.
        /// The default keeps legacy whole-image tracking for attachment types that do
        /// not expose independent mip/layer layouts.
        /// </summary>
        void UpdateAttachmentTrackedLayout(ImageLayout layout, int mipLevel, int layerIndex)
            => UpdateTrackedLayout(layout);
    }
}
