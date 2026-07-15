using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// Holds a complete set of Vulkan handles that were owned by a
        /// <see cref="VkImageBackedTexture{T}"/> or <see cref="VulkanPhysicalImageGroup"/>
        /// and need deferred destruction.  Kept alive until the frame slot's
        /// timeline fence signals that no in-flight command buffer references them.
        /// </summary>
        internal readonly record struct RetiredImageResources(
            Image Image,
            DeviceMemory Memory,
            ImageView PrimaryView,
            ImageView[] AttachmentViews,
            Sampler Sampler,
            long AllocatedVRAMBytes);
    }
}
