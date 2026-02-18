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
        ImageUsageFlags DescriptorUsage { get; }
        SampleCountFlags DescriptorSamples { get; }

        /// <summary>
        /// Returns the most recently tracked <see cref="ImageLayout"/> for the backing VkImage.
        /// Implementations that do not track layout should return <see cref="ImageLayout.Undefined"/>.
        /// </summary>
        ImageLayout TrackedImageLayout => ImageLayout.Undefined;

        /// <summary>
        /// <c>true</c> when the image is borrowed from a resource-planner physical group;
        /// <c>false</c> when it owns a dedicated image allocation.
        /// </summary>
        bool UsesAllocatorImage => false;

        /// <summary>
        /// Returns a depth-only <see cref="ImageView"/> for combined depth-stencil textures,
        /// suitable for sampled image descriptor bindings where a single depth aspect is required.
        /// Implementations that do not support this should return <c>default</c>.
        /// </summary>
        ImageView GetDepthOnlyDescriptorView() => default;
    }
}
