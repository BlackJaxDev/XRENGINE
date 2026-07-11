using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Resolves the appropriate Vulkan image layout for a given descriptor source and descriptor type.
    /// </summary>
    /// <param name="source">The source of the Vulkan image descriptor.</param>
    /// <param name="descriptorType">The type of the Vulkan descriptor.</param>
    /// <returns>The resolved Vulkan image layout.</returns>
    internal ImageLayout ResolveDescriptorImageLayout(IVkImageDescriptorSource source, DescriptorType descriptorType)
    {
        // If the descriptor type is a storage image, it must use the general layout.
        if (descriptorType == DescriptorType.StorageImage)
            return ImageLayout.General;

        // Get the default requested layout for sampled descriptors based on the source.
        ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(source);

        // Resolve the tracked layout for the submitted descriptor.
        ImageLayout trackedLayout = ResolveSubmittedDescriptorLayout(source, source.DescriptorView);

        // If the tracked layout allows sampling from the general layout, use the general layout.
        return CanSampleFromTrackedGeneralLayout(source.DescriptorUsage, trackedLayout) 
            ? ImageLayout.General 
            : requestedLayout;
    }

    /// <summary>
    /// Resolves the appropriate Vulkan image layout for a given descriptor source, snapshot, and descriptor type.
    /// </summary>
    /// <param name="source">The source of the Vulkan image descriptor.</param>
    /// <param name="snapshot">The snapshot of the Vulkan image descriptor.</param>
    /// <param name="descriptorType">The type of the Vulkan descriptor.</param>
    /// <returns>The resolved Vulkan image layout.</returns>
    internal ImageLayout ResolveDescriptorImageLayout(
        IVkImageDescriptorSource source,
        in VkImageDescriptorSnapshot snapshot,
        DescriptorType descriptorType)
    {
        // If the descriptor type is a storage image, it must use the general layout.
        if (descriptorType == DescriptorType.StorageImage)
            return ImageLayout.General;

        // Get the default requested layout for sampled descriptors based on the snapshot.
        ImageLayout requestedLayout = GetDefaultSampledDescriptorLayout(in snapshot);

        // Resolve the tracked layout for the submitted descriptor based on the snapshot.
        ImageLayout trackedLayout = ResolveSubmittedDescriptorLayout(source, snapshot.View, snapshot.TrackedLayout);

        // If the tracked layout allows sampling from the general layout, use the general layout.
        return CanSampleFromTrackedGeneralLayout(snapshot.Usage, trackedLayout) 
            ? ImageLayout.General 
            : requestedLayout;
    }

    /// <summary>
    /// Gets the default Vulkan image layout for a sampled descriptor based on the descriptor source.
    /// </summary>
    /// <param name="source">The source of the Vulkan image descriptor.</param>
    /// <returns>The default Vulkan image layout for the sampled descriptor.</returns>
    private static ImageLayout GetDefaultSampledDescriptorLayout(IVkImageDescriptorSource source)
    {
        // If the usage flags indicate that the general layout should be used, 
        // return the general layout.
        if (UsesGeneralSampledDescriptorLayout(source.DescriptorUsage))
            return ImageLayout.General;

        // Determine the aspect flags for the descriptor to check if it includes depth or stencil components.
        ImageAspectFlags aspect = source.DescriptorAspect;
        bool depthOrStencil = (aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            IsCombinedDepthStencilFormat(source.DescriptorFormat);

        // Return the appropriate default layout based on whether the descriptor includes depth or stencil components.
        return depthOrStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>
    /// Determines whether a sampled image can be read from the tracked general layout.
    /// </summary>
    /// <param name="usage">The usage flags of the Vulkan image.</param>
    /// <param name="trackedLayout">The tracked Vulkan image layout.</param>
    /// <returns>True if the sampled image can be read from the tracked general layout; otherwise, false.</returns>
    private static bool CanSampleFromTrackedGeneralLayout(ImageUsageFlags usage, ImageLayout trackedLayout)
        => trackedLayout == ImageLayout.General &&
           (usage & ImageUsageFlags.SampledBit) != 0;

    /// <summary>
    /// Gets the default Vulkan image layout for a sampled descriptor based on the descriptor snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot of the Vulkan image descriptor.</param>
    /// <returns>The default Vulkan image layout for the sampled descriptor.</returns>
    private static ImageLayout GetDefaultSampledDescriptorLayout(in VkImageDescriptorSnapshot snapshot)
    {
        // Check if the usage flags indicate that the general layout should be used.
        if (UsesGeneralSampledDescriptorLayout(snapshot.Usage))
            return ImageLayout.General;

        // Determine the aspect flags for the descriptor to check if it includes depth or stencil components.
        bool depthOrStencil = 
            (snapshot.Aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            IsCombinedDepthStencilFormat(snapshot.Format);

        // Return the appropriate default layout based on whether the descriptor includes depth or stencil components.
        return depthOrStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    }

    /// <summary>
    /// Determines whether the sampled descriptor should use the general layout based on its usage flags.
    /// </summary>
    /// <param name="usage">The usage flags of the Vulkan image.</param>
    /// <returns>True if the sampled descriptor should use the general layout; otherwise, false.</returns>
    private static bool UsesGeneralSampledDescriptorLayout(ImageUsageFlags usage)
        => (usage & ImageUsageFlags.StorageBit) != 0 &&
           (usage & ImageUsageFlags.SampledBit) != 0;

    /// <summary>
    /// Resolves the Vulkan image layout for a submitted descriptor, 
    /// taking into account the tracked layout and a fallback layout.
    /// </summary>
    /// <param name="source">The source of the Vulkan image descriptor.</param>
    /// <param name="view">The Vulkan image view associated with the descriptor.</param>
    /// <param name="fallback">The fallback image layout to use if the tracked layout is undefined.</param>
    /// <returns>The resolved Vulkan image layout for the submitted descriptor.</returns>
    private ImageLayout ResolveSubmittedDescriptorLayout(
        IVkImageDescriptorSource source,
        ImageView view,
        ImageLayout fallback = ImageLayout.Undefined)
    {
        // Attempt to resolve the submitted descriptor layout based on the tracked layout and the fallback layout.
        if (view.Handle != 0 &&
            TryGetDescriptorHeapImageViewCreateInfo(view, out ImageViewCreateInfo viewInfo) &&
            TryGetTrackedImageLayout(viewInfo.Image, viewInfo.SubresourceRange, out ImageLayout submitted))
            return submitted;

        // If the tracked layout could not be determined, 
        // fall back to the source's tracked layout or the provided fallback layout.
        ImageLayout sourceLayout = source.TrackedImageLayout;
        return sourceLayout != ImageLayout.Undefined ? sourceLayout : fallback;
    }
}
