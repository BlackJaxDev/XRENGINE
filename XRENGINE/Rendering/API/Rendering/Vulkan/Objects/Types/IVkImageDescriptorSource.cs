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
        /// Returns a depth-only <see cref="ImageView"/> for combined depth-stencil textures,
        /// suitable for sampled image descriptor bindings where a single depth aspect is required.
        /// Implementations that do not support this should return <c>default</c>.
        /// </summary>
        ImageView GetDepthOnlyDescriptorView() => default;
    }
}
