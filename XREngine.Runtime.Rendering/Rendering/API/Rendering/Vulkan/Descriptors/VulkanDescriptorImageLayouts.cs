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

        // General is a stable sampling contract only for images that are also used as storage.
        // Other images can pass through General during native feature evaluation, but return to
        // their aspect-appropriate read-only layout before a descriptor-backed draw.
        return ResolveTrackedSampledDescriptorLayout(source.DescriptorUsage, trackedLayout, requestedLayout);
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

        return ResolveTrackedSampledDescriptorLayout(snapshot.Usage, trackedLayout, requestedLayout);
    }

    /// <summary>
    /// Gets the default Vulkan image layout for a sampled descriptor based on the descriptor source.
    /// </summary>
    /// <param name="source">The source of the Vulkan image descriptor.</param>
    /// <returns>The default Vulkan image layout for the sampled descriptor.</returns>
    private static ImageLayout GetDefaultSampledDescriptorLayout(IVkImageDescriptorSource source)
    {
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
    /// Uses an exact tracked read layout when one is known, preserves General only for an explicit
    /// storage-and-sampling contract, and otherwise uses the aspect-appropriate requested layout.
    /// </summary>
    internal static ImageLayout ResolveTrackedSampledDescriptorLayout(
        ImageUsageFlags usage,
        ImageLayout trackedLayout,
        ImageLayout requestedLayout)
        => trackedLayout == ImageLayout.General && UsesGeneralSampledDescriptorLayout(usage)
            ? ImageLayout.General
            : trackedLayout is
                ImageLayout.ShaderReadOnlyOptimal or
                ImageLayout.DepthStencilReadOnlyOptimal or
                ImageLayout.DepthReadOnlyOptimal or
                ImageLayout.StencilReadOnlyOptimal or
                ImageLayout.ReadOnlyOptimal
                ? trackedLayout
                : requestedLayout;

    /// <summary>
    /// Gets the default Vulkan image layout for a sampled descriptor based on the descriptor snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot of the Vulkan image descriptor.</param>
    /// <returns>The default Vulkan image layout for the sampled descriptor.</returns>
    private static ImageLayout GetDefaultSampledDescriptorLayout(in VkImageDescriptorSnapshot snapshot)
    {
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
    /// Determines whether a sampled descriptor intentionally remains in the general layout.
    /// </summary>
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
