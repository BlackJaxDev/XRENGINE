using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal interface IVkImageDescriptorSource
    {
        Image DescriptorImage { get; }
        ImageView DescriptorView { get; }
        Sampler DescriptorSampler { get; }
        Format DescriptorFormat { get; }
        ImageAspectFlags DescriptorAspect { get; }
        SampleCountFlags DescriptorSamples { get; }
    }

    internal interface IVkFrameBufferAttachmentSource : IVkImageDescriptorSource
    {
        ImageView GetAttachmentView(int mipLevel, int layerIndex);
    }

    internal interface IVkTexelBufferDescriptorSource
    {
        BufferView DescriptorBufferView { get; }
        Format DescriptorBufferFormat { get; }
    }

    public abstract class VkTexture<T>(VulkanRenderer api, T data) : VkObject<T>(api, data) where T : XRTexture
    {
        public override VkObjectType Type => VkObjectType.Image;

        protected virtual string? ResolveLogicalResourceName()
        {
            string? name = Data.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            string describing = Data.GetDescribingName();
            return string.IsNullOrWhiteSpace(describing) ? null : describing;
        }

        internal VulkanPhysicalImageGroup? TryResolvePhysicalGroup(bool ensureAllocated = true)
        {
            string? resourceName = ResolveLogicalResourceName();
            if (string.IsNullOrWhiteSpace(resourceName))
                return null;

            if (!Renderer.ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup group))
                return null;

            if (ensureAllocated)
                group.EnsureAllocated(Renderer);

            return group;
        }

        protected bool TryResolvePhysicalImage(out Image image)
        {
            if (TryResolvePhysicalGroup(out VulkanPhysicalImageGroup? group))
            {
                image = group.Image;
                return image.Handle != 0;
            }

            image = default;
            return false;
        }

        internal bool TryResolvePhysicalGroup(out VulkanPhysicalImageGroup? group)
        {
            group = TryResolvePhysicalGroup(true);
            return group is not null;
        }
    }
}
