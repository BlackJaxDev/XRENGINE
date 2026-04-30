using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
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

            if (!Renderer.ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group) || group is null)
                return null;

            if (ensureAllocated)
                group.EnsureAllocated(Renderer);

            return group;
        }

        protected bool TryResolvePhysicalImage(out Image image)
        {
            if (TryResolvePhysicalGroup(out VulkanPhysicalImageGroup? group) && group is not null)
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
