using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal interface IVkImageDescriptorSource
    {
        Image DescriptorImage { get; }
        DeviceMemory DescriptorMemory { get; }
        ImageView DescriptorView { get; }
        ImageViewType DescriptorViewType { get; }
        Sampler DescriptorSampler { get; }
        Format DescriptorFormat { get; }
        ImageAspectFlags DescriptorAspect { get; }
        ImageUsageFlags DescriptorUsage { get; }
        SampleCountFlags DescriptorSamples { get; }
        uint DescriptorMipLevels => 1u;
        uint DescriptorArrayLayers => 1u;
        ulong DescriptorGeneration => 0UL;
        bool IsDescriptorReady => true;
        bool TryEnsureDescriptorReadyForUse(string reason) => IsDescriptorReady;
        bool TryEnsureDescriptorReadyForUse(string reason, bool allowSynchronousUpload)
            => allowSynchronousUpload ? TryEnsureDescriptorReadyForUse(reason) : IsDescriptorReady;

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
        /// Attempts to transition a dedicated image before descriptor binding.
        /// Render-graph allocator images should leave this as <c>false</c> and rely
        /// on the command-buffer barrier planner.
        /// </summary>
        bool TryTransitionDedicatedImageLayout(ImageLayout oldLayout, ImageLayout newLayout) => false;

        /// <summary>
        /// Returns a depth-only <see cref="ImageView"/> for combined depth-stencil textures,
        /// suitable for sampled image descriptor bindings where a single depth aspect is required.
        /// Implementations that do not support this should return <c>default</c>.
        /// </summary>
        ImageView GetDepthOnlyDescriptorView() => default;

        /// <summary>
        /// Returns a stencil-only <see cref="ImageView"/> for combined depth-stencil textures,
        /// suitable for unsigned-integer stencil sampler descriptor bindings.
        /// Implementations that do not support this should return <c>default</c>.
        /// </summary>
        ImageView GetStencilOnlyDescriptorView() => default;

        /// <summary>
        /// Returns a descriptor view with the requested dimensionality when the
        /// backing image can legally expose one.
        /// </summary>
        ImageView GetDescriptorView(ImageViewType viewType)
            => viewType == DescriptorViewType ? DescriptorView : default;
    }
}
